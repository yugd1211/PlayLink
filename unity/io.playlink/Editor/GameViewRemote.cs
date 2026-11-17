using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.LowLevel;
using Debug = UnityEngine.Debug;
using PlayerLoopPhase = UnityEngine.PlayerLoop;

// Game View Remote 2단계: 에디터 Game 뷰를 폰 브라우저로 보내고(MJPEG over WebSocket), 폰 터치를 레거시 Input 터치로 넣습니다.
// 서버는 Play 중에만 돕니다(Play 진입 시 도메인 리로드로 정적 상태가 사라지므로). Play 종료, 도메인 리로드, 에디터 종료 때 반드시 닫습니다.
// Editor 폴더의 MonoBehaviour는 런타임에 붙일 수 없어서(1단계 2.1) PlayerLoop에 단계를 끼워 넣어 구동합니다.
//   EarlyUpdate.UpdateInputManager 직전: 끝난 리드백 확인 → 캡처 요청 → 입력 주입 → 1초 통계
//   PostLateUpdate 맨 끝: 저지연 리드백(기본 켬)일 때 이번 프레임 리드백을 기다림
[InitializeOnLoad]
public static class GameViewRemote
{
    public const string LOG_PREFIX = "[GVR]";
    public const int DEFAULT_PORT = 7300;
    public const int DEFAULT_QUALITY = 80;
    public const int DEFAULT_FPS_CAP = 60;

    private const string PREF_PORT = "GVR_Port";
    private const string PREF_QUALITY = "GVR_JpegQuality";
    private const string PREF_FPS_CAP = "GVR_FpsCap";
    private const string PREF_AUTO_START = "GVR_AutoStart";
    private const string PREF_LOW_LATENCY_READBACK = "GVR_LowLatencyReadback";
    private const string PREF_ALLOW_LAN = "GVR_AllowLan";
    private const string PREF_AUTO_ADB_REVERSE = "GVR_AutoAdbReverse";
    private const string PREF_AUTO_PACING = "GVR_AutoPacing";
    private const string PREF_PREFER_WIRELESS = "GVR_PreferWireless";
    private const string PREF_STREAM_SCALE = "GVR_StreamScale";
    private const string PREF_VIDEO_CODEC = "GVR_VideoCodec";
    private const string PREF_H264_KBPS = "GVR_H264Kbps";
    private const string PREF_FFMPEG_PATH = "GVR_FfmpegPath";
    public const int DEFAULT_H264_KBPS = 6000;
    public static readonly int[] STREAM_SCALES = { 100, 75, 50 };
    private const string PREF_WIRELESS_ADDRESS = "GVR_WirelessAddress";
    // 자동 프레임 간격: 한 장을 보내는 비용(수신 확인 시간과 폰 디코딩 시간 중 큰 값)이 보내는 간격의 이 비율을 넘으면 간격을 늘리고,
    // 한 단계 줄인 간격의 이 비율보다 작게 STEP_DOWN_SECONDS초 이어지면 줄입니다. 사이를 띄워 오르내림을 막습니다.
    private const float PACING_UP_RATIO = 0.85f;
    private const float PACING_DOWN_RATIO = 0.6f;
    private const int PACING_STEP_DOWN_SECONDS = 10;
    // 수신 확인 시간(보내기 → 확인)의 1초 평균이 이 값을 넘으면 전송이 밀린 것으로 봅니다. 왕복이 프레임 간격보다 긴 것(무선 adb 20~80ms)은
    // 정상이라 근거로 쓰지 않고, 역압 기준(MAX_H264_BACKLOG_MS)에 가까워질 만큼 계속 밀릴 때만 근거로 씁니다.
    private const float PACING_BACKLOG_MS = 100f;
    // 간격을 줄였다가 이 시간 안에 다시 늘리게 되면(링크가 실제로 못 따라감) 다음에 줄이기까지 기다리는 시간을 2배로 늘립니다(최대 2^4배).
    private const int PACING_PROBE_SECONDS = 15;
    // 영상 클라이언트가 접속한 직후 이 시간(초)은 간격을 늘리는 판정을 하지 않습니다. 접속 · 인코더 시작 · 폰 디코더 준비 동안의 일시적인 버림과
    // 긴 디코딩 시간 때문에(실측: 페이지를 새로 열 때마다) 30fps로 내려가 10초 넘게 머물렀습니다.
    private const int PACING_CONNECT_GRACE_SECONDS = 4;
    private const int MAX_PACING_BACKOFF = 4;
    // 간격을 늘리는 조건이 이 초만큼 이어져야 늘립니다. 1초 표본 하나(잠깐의 전송 정체)로 30fps로 떨어지지 않게 합니다.
    private const int PACING_STEP_UP_SECONDS = 2;
    private const int MAX_TRANSPORT_STRIDE = 4;
    // 게임 카메라가 이 시간 넘게 그리지 않으면 화면이 멈춘 것으로 보고 창과 폰에 알립니다.
    private const double STALE_MS = 700.0;
    private const string PAGE_ASSET_NAME = "GameViewRemotePage";
    private const string SESSION_RESTART_AFTER_RELOAD = "GVR_RestartAfterReload";

    private struct RemoteStep { }
    private struct RemoteLateStep { }

    // 화면 표시용 1초 통계. 메인 스레드에서만 갱신합니다.
    public struct Snapshot
    {
        public float unityFps;
        public float sentFps;
        public float kbps;
        public float avgFrameKb;
        public float encodeMs;
        public float captureToSendMs;
        public float readbackMs;
        public float readbackWaitMs;
        public float readbackMaxWaitMs;
        public float injectLatencyMs;
        public float injectLatencyFrames;
        public int width;
        public int height;
        public int skippedIdentical;
        // 끊김 진단(1초): 캡처 수, 리드백 3개가 차서 건너뛴 캡처, 인코더 덮어씀, 전송 슬롯 덮어씀, 수신 확인 평균/최대, 확인 1초 초과,
        // 새 픽셀 버퍼 할당, GC 횟수, Unity 프레임 시간 최대, 튐(직전 평균의 2배 넘고 25ms 넘는 프레임) 수, 전송 간격 최대, 캡처 간격(N프레임)
        public float captureFps;
        public int inflightSkips;
        public int encoderDrops;
        public int sendDrops;
        public float ackMs;
        public float ackMaxMs;
        // 1초 동안 온 수신 확인 수. 보내는 중인데 0이면 폰이 읽지 않는 것(화면 꺼짐, 다른 앱으로 전환)입니다.
        public int ackCount;
        // 링크가 바쁠 때 수신 확인 간격 평균(= 한 장당 전송 처리 시간). 표본이 없으면 0
        public float sendCostMs;
        public int ackTimeouts;
        public int bufferAllocs;
        public int gcCount;
        public float unityFrameMaxMs;
        public int unityHitches;
        public float sendGapMaxMs;
        public int captureStride;
        public float clientDecodeMs;
        public float clientDrops;
        public int transportStride;
    }

    // 백그라운드 스레드가 누적하는 카운터. Interlocked로만 바꿉니다.
    private static long sentFrames;
    private static long sentBytes;
    private static long sentLatencyUs;
    private static long encodedFrames;
    private static long encodeUs;
    private static long skippedFrames;

    private static Snapshot snapshot;
    // 끊김 진단: Unity 프레임 시간(메인 스레드), 전송 간격과 수신 확인 시간(전송/수신 스레드 → pacingLock)
    private static readonly object pacingLock = new object();
    private static float smoothedFrameMs = 16.7f;
    private static float frameMaxMs;
    private static int hitches;
    private static long lastSentTicks;
    private static float sendGapMaxMs;
    private static double ackSumMs;
    private static int ackCount;
    private static float ackMaxMs;
    private static double busySumMs;
    private static int busyCount;
    private static int gcBase;
    private static long[] pacingBase = new long[6];
    private static long statsTicks;
    private static long statsFrameCount;
    private static long[] statsBase = new long[6];
    private static GameViewRemoteServer server;
    private static bool loopInstalled;
    private static volatile int jpegQuality;
    private static volatile int fpsCap;
    // 인코더 스레드가 읽는 H.264 설정(EditorPrefs는 메인 스레드 전용이라 캐시)
    private static volatile bool useH264;
    private static volatile int h264Kbps;
    private static volatile string ffmpegPath;
    private static volatile string pagePath;
    private static volatile bool preferWireless;

    public static int Port
    {
        get { return EditorPrefs.GetInt(PREF_PORT, DEFAULT_PORT); }
        set { EditorPrefs.SetInt(PREF_PORT, Mathf.Clamp(value, 1024, 65535)); }
    }

    // 인코더 스레드가 읽으므로 EditorPrefs(메인 스레드 전용) 값을 정적 필드에 둡니다.
    public static int JpegQuality
    {
        get { return jpegQuality; }
        set
        {
            jpegQuality = Mathf.Clamp(value, 10, 100);
            EditorPrefs.SetInt(PREF_QUALITY, jpegQuality);
        }
    }

    public static int FpsCap
    {
        get { return fpsCap; }
        set
        {
            fpsCap = Mathf.Clamp(value, 1, 120);
            EditorPrefs.SetInt(PREF_FPS_CAP, fpsCap);
        }
    }

    // 전송 해상도(Game 뷰 해상도 대비 %). 무선 adb(실측 약 8Mbps)에서는 50%를 권장합니다. 터치 좌표는 비율이라 그대로 맞습니다.
    public static int StreamScalePercent
    {
        get { return EditorPrefs.GetInt(PREF_STREAM_SCALE, 100); }
        set { EditorPrefs.SetInt(PREF_STREAM_SCALE, Mathf.Clamp(value, 25, 100)); }
    }

    // 영상 형식: 켜면(기본) 페이지가 H.264(WebCodecs)를 지원하고 ffmpeg가 있을 때 H.264, 아니면 MJPEG. 끄면 항상 MJPEG
    public static bool UseH264
    {
        get { return useH264; }
        set
        {
            useH264 = value;
            EditorPrefs.SetInt(PREF_VIDEO_CODEC, value ? 0 : 1);
            GameViewRemoteCapture.RetryH264();
        }
    }

    // H.264 비트레이트(kbps, CBR). 무선 adb(실측 약 8~15Mbps)는 4000~6000, USB는 10000 이상도 됩니다.
    public static int H264Kbps
    {
        get { return h264Kbps; }
        set
        {
            h264Kbps = Mathf.Clamp(value, 500, 50000);
            EditorPrefs.SetInt(PREF_H264_KBPS, h264Kbps);
        }
    }

    // ffmpeg.exe 경로(비우면 PATH에서 찾음). PC마다 다르므로 EditorPrefs에 둡니다.
    public static string FfmpegPath
    {
        get { return ffmpegPath ?? string.Empty; }
        set
        {
            ffmpegPath = value ?? string.Empty;
            EditorPrefs.SetString(PREF_FFMPEG_PATH, ffmpegPath);
            GameViewRemoteCapture.RetryH264();
        }
    }

    public static string FfmpegPathForEncoder { get { return ffmpegPath; } }

    public static bool AutoStart
    {
        get { return EditorPrefs.GetBool(PREF_AUTO_START, false); }
        set { EditorPrefs.SetBool(PREF_AUTO_START, value); }
    }

    // 캡처 지연을 1프레임 줄이는 대신 메인 스레드가 GPU 복사를 잠깐(실측 평균 1~3ms) 기다립니다. 기본은 켬(실측 Unity fps 차이 없음).
    public static bool LowLatencyReadback
    {
        get { return EditorPrefs.GetBool(PREF_LOW_LATENCY_READBACK, true); }
        set { EditorPrefs.SetBool(PREF_LOW_LATENCY_READBACK, value); }
    }

    // 인증 없는 LAN 리슨("Wi-Fi 접속 허용", 예전의 AllowLan)은 없앴습니다(브리프 3.3 의 1번). 서버는 항상 127.0.0.1 에만 열고,
    // 같은 Wi-Fi 연결은 앞단(GameViewRemoteHost)의 QR 인증 경로로만 받습니다.

    // Start 때 USB 기기가 정확히 1대면 adb reverse를 자동으로 합니다.
    public static bool AutoAdbReverse
    {
        get { return EditorPrefs.GetBool(PREF_AUTO_ADB_REVERSE, true); }
        set { EditorPrefs.SetBool(PREF_AUTO_ADB_REVERSE, value); }
    }

    // 전송(USB 대역폭)이나 폰 디코딩이 Unity fps를 못 따라가면 불규칙하게 버려지는 대신 N프레임마다 고르게 보냅니다(끊김 방지). 기본 켬
    public static bool AutoPacing
    {
        get { return EditorPrefs.GetBool(PREF_AUTO_PACING, true); }
        set { EditorPrefs.SetBool(PREF_AUTO_PACING, value); }
    }

    // 같은 폰이 USB와 무선(adb 무선)으로 함께 연결돼 있을 때 영상 터널(adb reverse)을 어느 쪽에 걸지. 끄면(기본) USB, 켜면 무선.
    // 폰의 localhost:포트는 하나뿐이라 한쪽으로만 갑니다. 한쪽만 연결돼 있으면 그쪽으로 갑니다. adb 스레드가 읽으므로 정적 필드에 둡니다.
    public static bool PreferWireless
    {
        get { return preferWireless; }
        set
        {
            if (preferWireless != value)
            {
                preferWireless = value;
                EditorPrefs.SetBool(PREF_PREFER_WIRELESS, value);
            }
        }
    }

    // 자동 프레임 간격이 정한 값(1 = 매 프레임). 캡처는 이 값과 FPS Cap 간격 중 큰 쪽을 씁니다.
    public static int TransportStride { get; private set; } = 1;
    private static int pacingCalmSeconds;
    private static int pacingHotSeconds;
    private static int pacingBackoff;
    private static volatile int pacingGraceSeconds;
    private static int pacingSecondsSinceStepDown;

    // 마지막으로 무선 adb 연결에 성공한 주소(IP:포트). Start 때 연결된 기기가 없으면 이 주소로 다시 연결합니다.
    public static string WirelessAddress
    {
        get { return EditorPrefs.GetString(PREF_WIRELESS_ADDRESS, string.Empty); }
        set { EditorPrefs.SetString(PREF_WIRELESS_ADDRESS, value ?? string.Empty); }
    }

    // 폰 화면이 멈춘 이유(Game 뷰 가려짐, 일시정지 등). 정상이면 null
    public static string StaleReason { get; private set; }
    public static bool IsRunning { get { return server != null && server.IsRunning; } }
    public static int RunningPort { get { return IsRunning ? server.Port : 0; } }
    public static int VideoClients { get { return IsRunning ? server.CountConnections(GameViewRemoteServer.ChannelKind.Video) : 0; } }
    public static int InputClients { get { return IsRunning ? server.CountConnections(GameViewRemoteServer.ChannelKind.Input) : 0; } }
    public static Snapshot Stats { get { return snapshot; } }
    // Unity 프레임 속도(지수 평균). 캡처 간격(N프레임마다 1번)을 정할 때 씁니다.
    public static float SmoothedUnityFps { get { return 1000f / Mathf.Max(1f, smoothedFrameMs); } }
    public static string LastError { get; private set; }

    static GameViewRemote()
    {
        jpegQuality = EditorPrefs.GetInt(PREF_QUALITY, DEFAULT_QUALITY);
        preferWireless = EditorPrefs.GetBool(PREF_PREFER_WIRELESS, false);
        fpsCap = EditorPrefs.GetInt(PREF_FPS_CAP, DEFAULT_FPS_CAP);
        useH264 = EditorPrefs.GetInt(PREF_VIDEO_CODEC, 0) == 0;
        h264Kbps = EditorPrefs.GetInt(PREF_H264_KBPS, DEFAULT_H264_KBPS);
        ffmpegPath = EditorPrefs.GetString(PREF_FFMPEG_PATH, string.Empty);
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.quitting += Stop;
        // 예전에 저장된 "Wi-Fi 접속 허용" 값은 더 이상 쓰지 않으므로 지웁니다.
        if (EditorPrefs.HasKey(PREF_ALLOW_LAN))
        {
            EditorPrefs.DeleteKey(PREF_ALLOW_LAN);
        }

        // Play 중 스크립트 재컴파일("Recompile And Continue Playing")로 리로드됐으면 서버를 다시 엽니다. 폰 페이지는 자동 재접속합니다.
        if (SessionState.GetBool(SESSION_RESTART_AFTER_RELOAD, false))
        {
            SessionState.EraseBool(SESSION_RESTART_AFTER_RELOAD);
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlaying)
                {
                    Start();
                }
            };
        }
    }

    public static void Start()
    {
        if (IsRunning)
        {
            return;
        }

        LastError = null;
        if (!EditorApplication.isPlaying)
        {
            LastError = "Play 모드에서만 시작할 수 있습니다.";
            Debug.LogWarning($"{LOG_PREFIX} {LastError}");
            return;
        }

        byte[] page = LoadPage();
        if (page == null)
        {
            LastError = $"{PAGE_ASSET_NAME}.html을 찾을 수 없습니다.";
            Debug.LogError($"{LOG_PREFIX} {LastError}");
            return;
        }

        server = new GameViewRemoteServer(page);
        server.onConnected = OnConnected;
        server.onDisconnected = OnDisconnected;
        server.onInputText = GameViewRemoteInput.HandleMessage;
        server.onFrameSent = OnFrameSent;
        server.onAck = OnAck;
        server.onStatsRequest = BuildStatsJson;
        server.onPageRequest = ReadPage;
        server.onDebugRequest = HandleDebugRequest;
        try
        {
            server.Start(Port);
        }
        catch (Exception e)
        {
            LastError = $"포트 {Port} 열기 실패: {e.Message}";
            Debug.LogError($"{LOG_PREFIX} {LastError}");
            server = null;
            return;
        }

        ResetStats();
        GameViewRemoteTrace.Reset();
        TransportStride = 1;
        pacingCalmSeconds = 0;
        pacingHotSeconds = 0;
        pacingBackoff = 0;
        pacingSecondsSinceStepDown = int.MaxValue / 2;
        StaleReason = null;
        GameViewRemoteCapture.Start(server);
        InstallLoop();
        EditorApplication.update += CheckStale;
        Debug.Log($"{LOG_PREFIX} server started on 127.0.0.1:{server.Port} (game view {Screen.width}x{Screen.height}, quality {JpegQuality}, fps cap {FpsCap}, low-latency readback {LowLatencyReadback})");
        if (AutoAdbReverse)
        {
            GameViewRemoteAdb.AutoReverseAsync(server.Port, WirelessAddress);
        }

        // Play 밖에서 기다리던 앱(앞단의 대기실)이 곧바로 다시 붙어 이 서버로 넘어오게 합니다.
        GameViewRemoteHost.OnServerStarted();
    }

    public static void Stop()
    {
        if (server == null)
        {
            return;
        }

        int port = server.Port;
        GameViewRemoteServer stopping = server;
        server = null;
        EditorApplication.update -= CheckStale;
        StaleReason = null;
        UninstallLoop();
        stopping.Stop();
        GameViewRemoteCapture.Stop();
        GameViewRemoteInput.Reset();
        Debug.Log($"{LOG_PREFIX} server stopped (port {port} closed)");
    }

    // 앞단(GameViewRemoteHost)이 인증을 마친 앱 소켓을 넘깁니다(앞단의 연결 스레드에서 불림).
    // 서버가 돌고 있으면 그 연결이 끝날 때까지 돌아오지 않고 true, 돌고 있지 않으면 소켓을 건드리지 않고 false.
    public static bool AdoptClient(System.Net.Sockets.TcpClient client, GameViewRemoteServer.ChannelKind kind, object tag)
    {
        GameViewRemoteServer target = server;
        return target != null && target.Adopt(client, kind, tag);
    }

    // 인증된 앱 연결을 전부 이 닫기 코드로 닫습니다([새 QR 발급]: 4403).
    public static void CloseAppClients(ushort code)
    {
        GameViewRemoteServer target = server;
        if (target != null)
        {
            target.CloseTagged(code);
        }
    }

    public static double ElapsedMs(long stopwatchTicks)
    {
        return (Stopwatch.GetTimestamp() - stopwatchTicks) * 1000.0 / Stopwatch.Frequency;
    }

    public static void RecordEncode(double ms, bool skipped)
    {
        if (skipped)
        {
            Interlocked.Increment(ref skippedFrames);
            return;
        }

        Interlocked.Increment(ref encodedFrames);
        Interlocked.Add(ref encodeUs, (long)(ms * 1000.0));
    }

    private static void OnFrameSent(int bytes, double captureToSendMs)
    {
        Interlocked.Increment(ref sentFrames);
        Interlocked.Add(ref sentBytes, bytes);
        Interlocked.Add(ref sentLatencyUs, (long)(captureToSendMs * 1000.0));
        lock (pacingLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (lastSentTicks != 0)
            {
                sendGapMaxMs = Mathf.Max(sendGapMaxMs, (float)((now - lastSentTicks) * 1000.0 / Stopwatch.Frequency));
            }

            lastSentTicks = now;
        }
    }

    private static void OnAck(double ms, double busyIntervalMs)
    {
        lock (pacingLock)
        {
            ackSumMs += ms;
            ackCount++;
            ackMaxMs = Mathf.Max(ackMaxMs, (float)ms);
            if (busyIntervalMs >= 0)
            {
                busySumMs += busyIntervalMs;
                busyCount++;
            }
        }
    }

    // 메인 스레드, 매 프레임. 직전 평균의 2배 넘고 25ms 넘는 프레임을 "튐"으로 셉니다(GC, 에디터 작업 등으로 Unity 자체가 멈춘 프레임).
    private static void RecordFrameTime()
    {
        float ms = Time.unscaledDeltaTime * 1000f;
        if (ms > smoothedFrameMs * 2f && ms > 25f)
        {
            hitches++;
        }
        else
        {
            smoothedFrameMs = Mathf.Lerp(smoothedFrameMs, ms, 0.05f);
        }

        frameMaxMs = Mathf.Max(frameMaxMs, ms);
    }

    private static void OnConnected(GameViewRemoteServer.Connection connection)
    {
        Debug.Log($"{LOG_PREFIX} {connection.kind} client #{connection.id} connected from {connection.remote}");
        if (connection.kind == GameViewRemoteServer.ChannelKind.Video)
        {
            pacingGraceSeconds = PACING_CONNECT_GRACE_SECONDS;
            GameViewRemoteCapture.SeedClient(connection);
        }

        // 입력 연결 수는 자리 배정(GameViewRemoteSeats)이 셉니다: 대기 중인 클라이언트의 입력은 세지 않아야 해서 한 곳에서만 부릅니다.
    }

    private static void OnDisconnected(GameViewRemoteServer.Connection connection)
    {
        Debug.Log($"{LOG_PREFIX} {connection.kind} client #{connection.id} disconnected");
    }

    private static void OnBeforeAssemblyReload()
    {
        if (IsRunning && EditorApplication.isPlaying)
        {
            SessionState.SetBool(SESSION_RESTART_AFTER_RELOAD, true);
        }

        Stop();
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            Stop();
        }
        else if (state == PlayModeStateChange.EnteredPlayMode && AutoStart)
        {
            Start();
        }
    }

    // 에디터 업데이트(일시정지 중에도 돎)에서 게임 화면이 멈췄는지 보고, 바뀔 때만 창/폰에 알립니다.
    private static void CheckStale()
    {
        if (!IsRunning)
        {
            return;
        }

        string reason = null;
        if (ElapsedMs(GameViewRemoteCapture.LastRenderTicks) > STALE_MS)
        {
            if (EditorApplication.isPaused)
            {
                reason = "Unity Play가 일시정지되어 화면이 멈췄습니다.";
            }
            else if (GameViewRemoteCapture.IsGameViewHidden())
            {
                reason = "Unity Game 뷰 탭이 다른 탭에 가려져 화면이 갱신되지 않습니다. Game 뷰 탭을 앞으로 꺼내 주세요.";
            }
            else
            {
                reason = "Unity Game 뷰가 그려지지 않고 있습니다(Unity 창 최소화 등).";
            }
        }

        if (reason == StaleReason)
        {
            return;
        }

        StaleReason = reason;
        server.PublishStatus(reason == null ? "{\"t\":\"s\",\"stale\":false}" : "{\"t\":\"s\",\"stale\":true,\"msg\":\"" + reason + "\"}");
        if (reason != null)
        {
            Debug.LogWarning($"{LOG_PREFIX} {reason}");
        }
        else
        {
            Debug.Log($"{LOG_PREFIX} game view rendering again");
        }
    }

    private static byte[] LoadPage()
    {
        foreach (string guid in AssetDatabase.FindAssets(PAGE_ASSET_NAME))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                pagePath = Path.GetFullPath(path);
                return File.ReadAllBytes(path);
            }
        }

        return null;
    }

    // 서버 스레드: 페이지 파일을 다시 읽습니다(AssetDatabase는 메인 스레드 전용이라 Start 때 찾아 둔 경로를 씀). 실패하면 null
    private static byte[] ReadPage()
    {
        try
        {
            return pagePath != null ? File.ReadAllBytes(pagePath) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // GET /debug?pattern=1|0 (서버 스레드): 테스트 패턴을 켜고 끕니다.
    private static string HandleDebugRequest(string query)
    {
        if (query.Contains("pattern=1"))
        {
            GameViewRemoteCapture.TestPattern = true;
        }
        else if (query.Contains("pattern=0"))
        {
            GameViewRemoteCapture.TestPattern = false;
        }

        return "{\"pattern\":" + (GameViewRemoteCapture.TestPattern ? "true" : "false") + "}";
    }

    #region PlayerLoop

    private static void RemoteUpdate()
    {
        if (!IsRunning)
        {
            return;
        }

        // 캡처를 주입보다 먼저 요청합니다. 이 캡처는 직전 프레임 화면이므로, 헤더의 inputSeq도 주입 전 값이어야 맞습니다.
        RecordFrameTime();
        GameViewRemoteTrace.BeginFrame(Time.frameCount, Time.unscaledDeltaTime * 1000f);
        GameViewRemoteCapture.CaptureUpdate();
        GameViewRemoteInput.InjectUpdate();
        UpdateStats();
    }

    private static void RemoteLateUpdate()
    {
        if (IsRunning && GameViewRemoteCapture.UseLowLatency)
        {
            GameViewRemoteCapture.FinishReadbacks();
        }
    }

    private static void InstallLoop()
    {
        if (loopInstalled)
        {
            return;
        }

        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            Type phase = loop.subSystemList[i].type;
            if (phase != typeof(PlayerLoopPhase.EarlyUpdate) && phase != typeof(PlayerLoopPhase.PostLateUpdate))
            {
                continue;
            }

            List<PlayerLoopSystem> children = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList);
            if (phase == typeof(PlayerLoopPhase.EarlyUpdate))
            {
                int index = children.FindIndex(s => s.type == typeof(PlayerLoopPhase.EarlyUpdate.UpdateInputManager));
                children.Insert(Mathf.Max(0, index), new PlayerLoopSystem { type = typeof(RemoteStep), updateDelegate = RemoteUpdate });
            }
            else
            {
                children.Add(new PlayerLoopSystem { type = typeof(RemoteLateStep), updateDelegate = RemoteLateUpdate });
            }

            loop.subSystemList[i].subSystemList = children.ToArray();
        }

        PlayerLoop.SetPlayerLoop(loop);
        loopInstalled = true;
    }

    // 저장해 둔 루프로 되돌리지 않고 우리 단계만 뺍니다(1단계 PoC 등 다른 도구가 바꾼 루프를 덮어쓰지 않게).
    private static void UninstallLoop()
    {
        if (!loopInstalled)
        {
            return;
        }

        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            PlayerLoopSystem[] children = loop.subSystemList[i].subSystemList;
            if (children == null)
            {
                continue;
            }

            List<PlayerLoopSystem> kept = new List<PlayerLoopSystem>(children);
            kept.RemoveAll(s => s.type == typeof(RemoteStep) || s.type == typeof(RemoteLateStep));
            loop.subSystemList[i].subSystemList = kept.ToArray();
        }

        PlayerLoop.SetPlayerLoop(loop);
        loopInstalled = false;
    }

    #endregion

    #region Stats

    // GET /stats 응답(서버 스레드에서 호출): 창에 보이는 1초 통계 + 누적 카운터 + 최근 프레임 기록(GameViewRemoteTrace)
    private static string BuildStatsJson()
    {
        string extra = "{\"codec\":\"" + GameViewRemoteCapture.CodecStatus.Replace("\\", "/").Replace("\"", "'") + "\""
            + ",\"transportStride\":" + TransportStride
            + ",\"captures\":" + GameViewRemoteCapture.Captures
            + ",\"encoderDrops\":" + GameViewRemoteCapture.EncoderDrops
            + ",\"inflightSkips\":" + GameViewRemoteCapture.InflightSkips
            + ",\"noRenderCaptures\":" + GameViewRemoteCapture.NoRenderCaptures
            + ",\"guardedFrames\":" + GameViewRemoteCapture.GuardedFrames + "}";
        return GameViewRemoteTrace.BuildJson(JsonUtility.ToJson(snapshot), extra);
    }

    private static void ResetStats()
    {
        snapshot = new Snapshot();
        statsTicks = Stopwatch.GetTimestamp();
        statsFrameCount = Time.frameCount;
        statsBase = ReadCounters();
        pacingBase = ReadPacingCounters();
        gcBase = GC.CollectionCount(0);
        smoothedFrameMs = 16.7f;
        frameMaxMs = 0f;
        hitches = 0;
        lock (pacingLock)
        {
            lastSentTicks = 0;
            sendGapMaxMs = 0f;
            ackSumMs = 0;
            ackCount = 0;
            ackMaxMs = 0f;
            busySumMs = 0;
            busyCount = 0;
        }
    }

    private static long[] ReadPacingCounters()
    {
        GameViewRemoteServer target = server;
        return new[]
        {
            GameViewRemoteCapture.Captures,
            GameViewRemoteCapture.InflightSkips,
            GameViewRemoteCapture.EncoderDrops,
            target != null ? target.SlotOverwrites : 0,
            target != null ? target.AckTimeouts : 0,
            GameViewRemoteCapture.BufferAllocs,
        };
    }

    private static long[] ReadCounters()
    {
        return new[]
        {
            Interlocked.Read(ref sentFrames),
            Interlocked.Read(ref sentBytes),
            Interlocked.Read(ref sentLatencyUs),
            Interlocked.Read(ref encodedFrames),
            Interlocked.Read(ref encodeUs),
            Interlocked.Read(ref skippedFrames),
        };
    }

    private static void UpdateStats()
    {
        double seconds = ElapsedMs(statsTicks) / 1000.0;
        if (seconds < 1.0)
        {
            return;
        }

        long[] now = ReadCounters();
        long frames = now[0] - statsBase[0];
        long encoded = now[3] - statsBase[3];
        Snapshot next = new Snapshot();
        next.unityFps = (float)((Time.frameCount - statsFrameCount) / seconds);
        next.sentFps = (float)(frames / seconds);
        next.kbps = (float)((now[1] - statsBase[1]) * 8 / 1000.0 / seconds);
        next.avgFrameKb = frames > 0 ? (now[1] - statsBase[1]) / 1024f / frames : 0f;
        next.captureToSendMs = frames > 0 ? (now[2] - statsBase[2]) / 1000f / frames : 0f;
        next.encodeMs = encoded > 0 ? (now[4] - statsBase[4]) / 1000f / encoded : 0f;
        next.skippedIdentical = (int)(now[5] - statsBase[5]);
        next.readbackMs = GameViewRemoteCapture.AvgReadbackMs;
        next.readbackWaitMs = GameViewRemoteCapture.AvgWaitMs;
        next.readbackMaxWaitMs = GameViewRemoteCapture.TakeMaxWaitMs();
        next.injectLatencyMs = GameViewRemoteInput.AvgInjectLatencyMs;
        next.injectLatencyFrames = GameViewRemoteInput.AvgInjectLatencyFrames;
        next.width = GameViewRemoteCapture.LastWidth;
        next.height = GameViewRemoteCapture.LastHeight;

        long[] pacing = ReadPacingCounters();
        next.captureFps = (float)((pacing[0] - pacingBase[0]) / seconds);
        next.inflightSkips = (int)(pacing[1] - pacingBase[1]);
        next.encoderDrops = (int)(pacing[2] - pacingBase[2]);
        next.sendDrops = (int)(pacing[3] - pacingBase[3]);
        next.ackTimeouts = (int)(pacing[4] - pacingBase[4]);
        next.bufferAllocs = (int)(pacing[5] - pacingBase[5]);
        pacingBase = pacing;
        int gc = GC.CollectionCount(0);
        next.gcCount = gc - gcBase;
        gcBase = gc;
        next.unityFrameMaxMs = frameMaxMs;
        next.unityHitches = hitches;
        frameMaxMs = 0f;
        hitches = 0;
        next.captureStride = GameViewRemoteCapture.CaptureStride;
        next.clientDecodeMs = GameViewRemoteInput.ClientDecodeMs;
        next.clientDrops = GameViewRemoteInput.ClientDropsPerSecond;
        lock (pacingLock)
        {
            next.ackMs = ackCount > 0 ? (float)(ackSumMs / ackCount) : 0f;
            next.ackCount = ackCount;
            next.ackMaxMs = ackMaxMs;
            next.sendCostMs = busyCount >= 3 ? (float)(busySumMs / busyCount) : 0f;
            busySumMs = 0;
            busyCount = 0;
            next.sendGapMaxMs = sendGapMaxMs;
            ackSumMs = 0;
            ackCount = 0;
            ackMaxMs = 0f;
            sendGapMaxMs = 0f;
        }

        UpdatePacing(next);
        next.transportStride = TransportStride;
        snapshot = next;
        statsTicks = Stopwatch.GetTimestamp();
        statsFrameCount = Time.frameCount;
        statsBase = now;
    }

    // 1초마다: 못 따라간다는 근거가 PACING_STEP_UP_SECONDS초 이어지면 간격을 1 늘리고,
    // 여유가 PACING_STEP_DOWN_SECONDS초(되돌렸다가 실패하면 2배씩) 이어지면 1 줄입니다(조건이 풀리면 다시 매 프레임으로 돌아감).
    // 근거는 실제 손실이나 계속되는 밀림만 씁니다: 전송/폰에서 버려진 프레임이 10% 초과, 수신 확인 시간 평균이 PACING_BACKLOG_MS 초과,
    // 폰 한 장당 디코딩 비용이 보내는 간격의 85% 초과. "수신 확인 간격"(예전 전송 비용)은 쓰지 않습니다. 포화되지 않은 링크에서도
    // 왕복이 프레임 간격보다 길면 보내는 간격과 같게 나와서, 무선에서 30fps와 60fps를 오가게 만들었습니다.
    // 비용 = 한 장당 전송 처리 시간과 폰 한 장당 디코딩 비용 중 큰 값. 전송 처리 시간은 링크가 바쁠 때(앞 프레임이 가는 중에 다음 프레임을 보냄)의
    // 수신 확인 간격입니다. 링크가 한 번도 바쁘지 않았으면 여유가 있다는 뜻이라 0으로 봅니다. 수신 확인 시간 자체(왕복 지연)는 무선에서 길지만
    // 2장을 겹쳐 보내므로 처리량을 제한하지 않아 비용으로 쓰지 않습니다. 전송 슬롯이나 폰 디코더 대기에서 버려진 프레임이 10%를 넘어도 늘립니다.
    private static void UpdatePacing(Snapshot stats)
    {
        if (!AutoPacing || stats.sentFps <= 0f)
        {
            TransportStride = 1;
            pacingCalmSeconds = 0;
            pacingHotSeconds = 0;
            return;
        }

        // 수신 확인이 1초 동안 하나도 없으면 폰이 읽지 않는 중입니다(화면 꺼짐, 설정 등 다른 앱으로 전환해 탭이 가려짐). 링크가 느린 것이 아니므로
        // 간격을 늘리지 않고, 다시 읽기 시작하면 매 프레임부터 봅니다. 실측: 폰에서 설정 앱을 다녀오는 동안 버림으로 세어져 4프레임 간격(15fps)까지
        // 올라갔고, 돌아온 뒤 10초에 한 단계씩 30초 넘게 걸려 풀렸습니다(개발 문서 12.9).
        if (stats.ackCount == 0)
        {
            if (TransportStride != 1)
            {
                Debug.Log($"{LOG_PREFIX} auto pacing: every 1 frame(s) (no acks from the phone for 1 s: the page is not reading, not a slow link)");
            }

            TransportStride = 1;
            pacingCalmSeconds = 0;
            pacingHotSeconds = 0;
            pacingGraceSeconds = PACING_CONNECT_GRACE_SECONDS;
            return;
        }

        float frameMs = 1000f / Mathf.Max(1f, stats.unityFps);
        float decodeMs = stats.clientDecodeMs;
        bool dropping = stats.captureFps > 0f && stats.sendDrops + stats.clientDrops > stats.captureFps * 0.1f;
        bool backlogged = stats.ackMs > PACING_BACKLOG_MS;
        int previous = TransportStride;
        if (pacingSecondsSinceStepDown < int.MaxValue / 2)
        {
            pacingSecondsSinceStepDown++;
        }

        // 늘리는 조건은 연속으로 이어질 때만 인정합니다. 실측: 전송이 잠깐 40ms 묶인 1초 표본 하나로(버린 프레임 0) 30fps로 내려간 적이 있습니다(문서 12.7).
        bool overloaded = dropping || backlogged || decodeMs > TransportStride * frameMs * PACING_UP_RATIO;
        if (pacingGraceSeconds > 0)
        {
            pacingGraceSeconds--;
            overloaded = false;
        }

        pacingHotSeconds = overloaded ? pacingHotSeconds + 1 : 0;
        if (overloaded && TransportStride < MAX_TRANSPORT_STRIDE)
        {
            pacingCalmSeconds = 0;
            if (pacingHotSeconds >= PACING_STEP_UP_SECONDS)
            {
                TransportStride++;
                pacingHotSeconds = 0;
                if (pacingSecondsSinceStepDown <= PACING_PROBE_SECONDS)
                {
                    pacingBackoff = Mathf.Min(MAX_PACING_BACKOFF, pacingBackoff + 1);
                }
            }
        }
        else if (TransportStride > 1 && !overloaded && stats.ackMs < PACING_BACKLOG_MS * 0.5f && decodeMs < (TransportStride - 1) * frameMs * PACING_DOWN_RATIO)
        {
            pacingCalmSeconds++;
            if (pacingCalmSeconds >= PACING_STEP_DOWN_SECONDS << pacingBackoff)
            {
                TransportStride--;
                pacingCalmSeconds = 0;
                pacingSecondsSinceStepDown = 0;
            }
        }
        else
        {
            pacingCalmSeconds = 0;
        }

        // 매 프레임으로 돌아와 한동안 문제가 없으면 되돌리기 대기 시간을 처음 값으로 돌립니다.
        if (TransportStride == 1 && !overloaded && pacingSecondsSinceStepDown > PACING_PROBE_SECONDS * 4)
        {
            pacingBackoff = 0;
        }

        if (TransportStride != previous)
        {
            Debug.Log($"{LOG_PREFIX} auto pacing: every {TransportStride} frame(s) (send drops {stats.sendDrops}/s, phone drops {stats.clientDrops:0}/s, ack {stats.ackMs:0.0} ms avg / {stats.ackMaxMs:0} max, phone decode {decodeMs:0.0} ms, unity frame {frameMs:0.0} ms, next step back after {PACING_STEP_DOWN_SECONDS << pacingBackoff}s calm)");
        }
    }

    #endregion
}
