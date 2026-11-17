using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

// Game 뷰 원본 해상도 캡처(Game 뷰 렌더 타깃 PlayModeView.m_TargetTexture) → AsyncGPUReadback(메인 스레드 대기 없음) → 백그라운드 스레드 JPEG 인코딩 → 영상 클라이언트로 발행.
// 지연을 쌓지 않으려고 인코딩 대기 1장, 클라이언트당 전송 대기 1장만 두고 최신 것이 이전 것을 덮어씁니다.
// 리드백 완료 콜백은 Unity가 "다음 프레임 끝"에야 확인해서 그냥 두면 2프레임(약 55ms) 늦게 옵니다. GPU 복사 자체는 3~5ms면 끝나므로
//   - 저지연 리드백(기본 켬): 같은 프레임 PostLateUpdate 끝에서 WaitForCompletion → 같은 프레임 안에 받음, 메인 스레드 대기 평균 1~3ms
//   - 끄면: 다음 프레임 시작(RemoteStep)에서 request.Update()로 직접 확인 → 1프레임 지연, 메인 스레드 대기 없음
// 인코딩 전에 프레임을 검사해(거의 검은 픽셀 비율, Game 뷰 크기) 이상 프레임은 짧은 동안 보내지 않습니다(안전장치, CheckFrame).
// 리드백이 끝나기 전에 다음 캡처가 올 수 있어서 최대 3개까지 겹쳐 요청합니다.
// 프레임 메시지 = 헤더 28바이트(little-endian) + JPEG (프로토콜 2: 24 → 28바이트, 캡처 시각 추가)
//   0 u32 frameId | 4 u16 width | 6 u16 height | 8 i32 inputSeq(이 화면에 반영된 마지막 입력 번호)
//   12 u32 encodeUs | 16 u32 captureToEncodedUs | 20 u32 flags
//   24 u32 captureTime(서버 시작 뒤 흐른 시간, 0.1ms 단위. 폰 페이지가 이 시각표에 맞춰 고르게 표시하는 데 씀)
//   flags: 1 = 이전 프레임과 같은 화면 재전송(JPEG), 2 = H.264, 4 = 키프레임, 8 = H.264 설정(avcC, 프레임 아님)
// H.264: 영상 클라이언트가 1개이고 그 페이지가 WebCodecs로 디코딩할 수 있으면 ffmpeg(h264_nvenc)로 보냅니다(GameViewRemoteH264).
// 프레임이 앞 프레임에 기대므로 인코딩한 뒤에는 버리지 않고(서버 순서 큐), 밀리면 인코딩 전에 건너뜁니다. 안 되면 MJPEG로 돌아갑니다.
public static class GameViewRemoteCapture
{
    public const int HEADER_BYTES = 28;
    // 프레임 헤더/메시지 형식 번호. 페이지(GameViewRemotePage.html의 PROTOCOL_VERSION)와 같아야 하고, 다르면 페이지가 스스로 새로 고칩니다.
    public const int PROTOCOL_VERSION = 2;
    private const uint FLAG_IDENTICAL = 1;
    private const uint FLAG_H264 = 2;
    private const uint FLAG_KEY = 4;
    private const uint FLAG_CONFIG = 8;
    // H.264에서 가장 오래된 미확인(또는 아직 못 보낸) 프레임이 이 시간(ms)만큼 밀려 있으면 다음 프레임은 인코딩하지 않고 건너뜁니다(지연이 쌓이지 않게).
    // 서버가 더 보내지 않는 기준과 같게 둡니다.
    private const int MAX_H264_BACKLOG_MS = GameViewRemoteServer.MAX_H264_BACKLOG_MS;
    // 화면이 안 바뀌어도 이 간격으로는 다시 보냅니다(유휴 하한 5fps).
    private const double IDLE_INTERVAL_MS = 200.0;
    private const int MAX_READBACKS_IN_FLIGHT = 3;
    // 픽셀 버퍼(720x1280이면 3.7MB) 풀 크기. 겹친 리드백 3 + 인코더 1 + 대기 1을 새로 할당 없이 돌립니다.
    private const int MAX_POOLED_BUFFERS = 6;
    // 프레임 검사: 이 간격(픽셀)마다 표본을 봅니다(720x1280 기준 약 13만 개, 0.3ms 안팎).
    private const int CHECK_STRIDE = 7;
    // 표본 중 RGB 합이 이 값보다 작으면 "거의 검은 픽셀"
    private const int DARK_RGB_SUM = 24;
    // 거의 검은 픽셀이 이 비율 이상이면 검은 프레임으로 봅니다.
    private const float BLACK_FRACTION = 0.97f;
    // 캡처 이상으로 의심되는 프레임은 이 시간까지 보내지 않고 마지막 정상 프레임을 둡니다. 더 길게 이어지면 실제 화면으로 보고 보냅니다.
    private const double GUARD_HOLD_MS = 300.0;
    // 저지연 리드백 대기가 평균 이 값을 넘으면 LOW_LATENCY_RETRY_MS 동안 기다리지 않는 방식으로 돌아갔다가 다시 시도합니다
    // (GPU가 바쁜 순간에 에디터가 끌리지 않게).
    private const float LOW_LATENCY_SUSPEND_WAIT_MS = 6f;
    private const int LOW_LATENCY_MIN_SAMPLES = 30;
    private const double LOW_LATENCY_RETRY_MS = 10000.0;

    private sealed class RawFrame
    {
        public byte[] pixels;
        public int width;
        public int height;
        public int inputSeq;
        public long captureTicks;
        public int captureFrame;
        public int sourceId;
        public bool sourceRecreated;
        public int cameraRenders;
        public bool editorFocused;
        public int screenWidth;
        public int screenHeight;
        public bool black;
        // 픽셀 행 순서: true = 맨 위 행부터(ffmpeg rawvideo), false = 맨 아래 행부터(Unity EncodeArrayToJPG)
        public bool topDown;
    }

    private static readonly Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
    private static readonly FieldInfo targetTextureField = typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView")
        ?.GetField("m_TargetTexture", BindingFlags.Instance | BindingFlags.NonPublic);
    // 창이 들어 있는 DockArea와 그 영역에서 지금 보이는 탭(actualView)
    private static readonly FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo actualViewProperty = typeof(EditorWindow).Assembly.GetType("UnityEditor.HostView")
        ?.GetProperty("actualView", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static GameViewRemoteServer server;
    private static RenderTexture flipTexture;
    private static UnityEngine.Object cachedGameView;
    private static readonly List<AsyncGPUReadbackRequest> pendingRequests = new List<AsyncGPUReadbackRequest>();
    private static int readbacksInFlight;
    // Stop → Start 사이에 늦게 도착한 이전 세션의 리드백 콜백을 걸러 냅니다.
    private static int session;
    private static float avgReadbackMs;
    private static float avgWaitMs;

    private static readonly object frameLock = new object();
    private static RawFrame pendingFrame;
    private static readonly Stack<byte[]> bufferPool = new Stack<byte[]>();
    private static Thread encoderThread;
    private static AutoResetEvent encoderWake;
    private static volatile bool encoderRunning;
    private static volatile GameViewRemoteServer.VideoMessage lastMessage;
    private static int frameId;
    // H.264 인코더(인코더 스레드가 만들고 닫음)와 그 스트림을 받는 클라이언트
    private static GameViewRemoteH264 h264;
    private static GameViewRemoteServer.Connection h264Client;
    private static int h264Kbps;
    private static volatile bool h264Failed;
    private static volatile string h264Error;
    private static volatile string codecStatus = "-";

    // 진단: 캡처 사이에 게임 카메라가 몇 번 그렸는지, 직전 캡처의 소스 텍스처
    private static int cameraRenders;
    // 그중 화면(Game 뷰)에 그리는 카메라만 센 수. 렌더 텍스처로 그리는 카메라가 있어도 Game 뷰가 실제로 다시 그려졌는지 가릅니다.
    private static int screenCameraRenders;
    private static long lastRenderTicks;
    private static int lastSourceId;
    private static long checkedFrames;
    private static long blackFrames;
    private static long sizeMismatchFrames;
    private static long noRenderCaptures;
    // 끊김 진단 카운터(누적): 캡처 수, 리드백 3개가 차서 건너뛴 캡처, 인코더가 못 가져가 덮어쓴 프레임, 새로 할당한 픽셀 버퍼
    private static long captures;
    private static long inflightSkips;
    private static long encoderDrops;
    private static long bufferAllocs;
    private static long guardedFrames;
    private static int invalidLogs;
    private static bool lastBlack;
    private static bool lastSizeMismatch;
    private static bool lastSentBlack;
    private static long suspectSinceTicks;
    private static int waitSamples;
    private static long suspendedSinceTicks;
    private static float maxWaitMs;

    public static int LastWidth { get; private set; }
    public static int LastHeight { get; private set; }
    public static float AvgReadbackMs { get { return avgReadbackMs; } }
    public static float AvgWaitMs { get { return avgWaitMs; } }
    // 저지연 리드백 대기가 길어 잠시 자동으로 멈췄는지(LOW_LATENCY_RETRY_MS 뒤 다시 시도)
    public static bool LowLatencySuspended { get { return suspendedSinceTicks != 0 && GameViewRemote.ElapsedMs(suspendedSinceTicks) < LOW_LATENCY_RETRY_MS; } }
    public static int LowLatencySuspendCount { get; private set; }
    // 게임 카메라가 마지막으로 그린 시각(Stopwatch). Game 뷰가 가려지거나 Play가 일시정지되면 멈춥니다.
    public static long LastRenderTicks { get { return lastRenderTicks; } }
    public static long CheckedFrames { get { return Interlocked.Read(ref checkedFrames); } }
    // 지금 쓰는 영상 형식과 이유(창 표시용)
    public static string CodecStatus { get { return codecStatus; } }
    public static long BlackFrames { get { return Interlocked.Read(ref blackFrames); } }
    public static long SizeMismatchFrames { get { return Interlocked.Read(ref sizeMismatchFrames); } }
    // 직전 캡처 뒤로 게임 카메라가 한 번도 그리지 않은 캡처(Game 뷰가 갱신되지 않은 화면)
    public static long NoRenderCaptures { get { return Interlocked.Read(ref noRenderCaptures); } }
    // 안전장치로 보내지 않은 이상 프레임 수
    public static long GuardedFrames { get { return Interlocked.Read(ref guardedFrames); } }
    public static long Captures { get { return Interlocked.Read(ref captures); } }
    public static long InflightSkips { get { return Interlocked.Read(ref inflightSkips); } }
    public static long EncoderDrops { get { return Interlocked.Read(ref encoderDrops); } }
    public static long BufferAllocs { get { return Interlocked.Read(ref bufferAllocs); } }
    // 지금 실제로 쓰는 캡처 간격(N프레임마다 1번). FPS Cap이 Unity fps 이상이면 1
    public static int CaptureStride { get; private set; } = 1;

    // 행 순서: 렌더 타깃을 그대로 읽으면 D3D/Vulkan/Metal(graphicsUVStartsAtTop)은 맨 위 행부터, GL은 맨 아래 행부터 나옵니다.
    // Unity EncodeArrayToJPG는 맨 아래 행부터, ffmpeg rawvideo(H.264)는 맨 위 행부터 받습니다. 그래서 지금 쓰는 형식에 맞게 GPU에서 뒤집을지 정하고
    // (D3D11 + JPEG = 뒤집음, D3D11 + H.264 = 그대로), 형식이 바뀌는 순간 방향이 안 맞는 프레임은 인코더 스레드가 행을 바꿔 맞춥니다(FlipRows).
    private static volatile bool h264Active;
    private static volatile bool testPattern;
    // 테스트 패턴이 화면을 가로지르는 시간(ms)과 크기(화면 너비 대비)
    private const double TEST_PATTERN_PERIOD_MS = 1500.0;
    private const float TEST_PATTERN_SIZE = 0.12f;

    // 진단용: 캡처 직전에 Game 뷰 렌더 타깃에 일정한 속도로 가로지르는 네모를 그립니다(기본 꺼짐, Stop 때 꺼짐).
    // 게임 화면이 멈춰 있어도 매 프레임 다른 그림이 되어 60fps로 보내지고, 속도가 일정해서 폰에서 끊김이 눈에 바로 보입니다.
    // 위치는 캡처 시각으로 정하므로 헤더의 캡처 시각과 정확히 맞습니다. PC의 Game 뷰에도 보일 수 있습니다.
    public static bool TestPattern
    {
        get { return testPattern; }
        set { testPattern = value; }
    }
    private static byte[] flipRow;

    private static bool NeedsFlip { get { return SystemInfo.graphicsUVStartsAtTop != h264Active; } }

    public static void Start(GameViewRemoteServer target)
    {
        server = target;
        session++;
        pendingRequests.Clear();
        readbacksInFlight = 0;
        avgWaitMs = 0f;
        maxWaitMs = 0f;
        waitSamples = 0;
        suspendedSinceTicks = 0;
        LowLatencySuspendCount = 0;
        lastMessage = null;
        pendingFrame = null;
        frameId = 0;
        h264Failed = false;
        codecStatus = "-";
        cameraRenders = 0;
        lastRenderTicks = Stopwatch.GetTimestamp();
        lastSourceId = 0;
        Interlocked.Exchange(ref checkedFrames, 0);
        Interlocked.Exchange(ref blackFrames, 0);
        Interlocked.Exchange(ref sizeMismatchFrames, 0);
        Interlocked.Exchange(ref noRenderCaptures, 0);
        Interlocked.Exchange(ref guardedFrames, 0);
        Interlocked.Exchange(ref captures, 0);
        Interlocked.Exchange(ref inflightSkips, 0);
        Interlocked.Exchange(ref encoderDrops, 0);
        Interlocked.Exchange(ref bufferAllocs, 0);
        CaptureStride = 1;
        invalidLogs = 0;
        lastBlack = false;
        lastSizeMismatch = false;
        lastSentBlack = false;
        suspectSinceTicks = 0;
        // Stop 없이 Start가 두 번 불려도 두 번 구독하지 않게 합니다.
        Camera.onPostRender -= OnCameraPostRender;
        Camera.onPostRender += OnCameraPostRender;
        encoderWake = new AutoResetEvent(false);
        encoderRunning = true;
        encoderThread = new Thread(EncodeLoop) { IsBackground = true, Name = "GVR Encoder" };
        encoderThread.Start();
    }

    public static void Stop()
    {
        server = null;
        testPattern = false;
        Camera.onPostRender -= OnCameraPostRender;
        encoderRunning = false;
        if (encoderThread != null)
        {
            encoderWake.Set();
            encoderThread.Join(1000);
            encoderThread = null;
        }

        StopH264();

        pendingRequests.Clear();
        ReleaseTexture(ref flipTexture);
        lastMessage = null;
        pendingFrame = null;
        lock (frameLock)
        {
            bufferPool.Clear();
        }
    }

    // 설정(형식, ffmpeg 경로, 비트레이트)을 바꾸면 부릅니다. H.264 실패 표시를 지워 다음 프레임에 다시 시도합니다.
    public static void RetryH264()
    {
        h264Failed = false;
    }

    public static void SeedClient(GameViewRemoteServer.Connection connection)
    {
        GameViewRemoteServer target = server;
        if (target != null)
        {
            target.Seed(connection, lastMessage);
        }
    }

    // PlayerLoop(메인 스레드)에서 매 프레임 호출. 이 시점의 Game 뷰 화면은 직전 프레임 렌더 결과입니다.
    // ScreenCapture.CaptureScreenshotIntoRenderTexture는 프레임 끝(WaitForEndOfFrame)이 아니면 에디터 창 백버퍼(Game 뷰 툴바 포함)를
    // 잡아서 쓰지 않습니다. 대신 Game 뷰가 게임을 그려 둔 렌더 타깃을 그대로 읽습니다(Game 뷰 배율/여백과 무관한 원본 해상도).
    public static void CaptureUpdate()
    {
        PollReadbacks();
        long start = Stopwatch.GetTimestamp();
        int renders = cameraRenders;
        int screenRenders = screenCameraRenders;
        int result = RequestCapture();
        GameViewRemoteTrace.SetCapture(result, renders, screenRenders, GameViewRemote.ElapsedMs(start));
    }

    // 이번 프레임 캡처를 요청합니다. 반환값은 GameViewRemoteTrace.CAPTURE_*(요청함 / 건너뛴 이유)
    private static int RequestCapture()
    {
        if (server == null || server.CountConnections(GameViewRemoteServer.ChannelKind.Video) == 0)
        {
            return GameViewRemoteTrace.CAPTURE_NONE;
        }

        // FPS Cap이 Unity fps보다 낮으면 N프레임마다 1번 캡처해 간격을 고르게 둡니다. 시간으로 거르면(예: 60fps에 Cap 37)
        // 16.7ms와 33.3ms 간격이 섞여 폰에서 끊겨 보입니다. 0.05는 Cap = Unity fps 근처에서 흔들리지 않게 하는 여유입니다.
        // 전송(USB 대역폭)이나 폰 디코딩이 못 따라가면 GameViewRemote.TransportStride가 커져 같은 방식으로 고르게 줄입니다.
        CaptureStride = Mathf.Max(GameViewRemote.TransportStride, Mathf.CeilToInt(GameViewRemote.SmoothedUnityFps / GameViewRemote.FpsCap - 0.05f));
        if (CaptureStride > 1 && Time.frameCount % CaptureStride != 0)
        {
            return GameViewRemoteTrace.CAPTURE_STRIDE_SKIP;
        }

        if (readbacksInFlight >= MAX_READBACKS_IN_FLIGHT)
        {
            Interlocked.Increment(ref inflightSkips);
            return GameViewRemoteTrace.CAPTURE_INFLIGHT_SKIP;
        }

        RenderTexture source = FindGameViewTexture();
        if (source == null || !source.IsCreated())
        {
            return GameViewRemoteTrace.CAPTURE_NO_TEXTURE;
        }

        if (testPattern)
        {
            DrawTestPattern(source);
        }

        // 전송 해상도: GPU에서 줄인 뒤 읽습니다(50%면 리드백 · 인코딩 · 전송 · 폰 디코딩이 약 1/4). 크기는 짝수로 맞춥니다.
        float scale = GameViewRemote.StreamScalePercent / 100f;
        int width = Mathf.Max(2, Mathf.RoundToInt(source.width * scale) & ~1);
        int height = Mathf.Max(2, Mathf.RoundToInt(source.height * scale) & ~1);
        bool resized = width != source.width || height != source.height;
        int sourceId = source.GetInstanceID();
        bool sourceRecreated = lastSourceId != 0 && sourceId != lastSourceId;
        lastSourceId = sourceId;
        int renders = cameraRenders;
        cameraRenders = 0;
        screenCameraRenders = 0;
        if (renders == 0)
        {
            Interlocked.Increment(ref noRenderCaptures);
        }

        // 인코더 스레드가 형식을 바꿀 수 있으므로 이번 캡처의 뒤집기 여부는 한 번만 읽습니다.
        bool flip = NeedsFlip;
        if (flip || resized)
        {
            EnsureTexture(ref flipTexture, width, height, source.graphicsFormat);
            // Blit은 대상 텍스처를 RenderTexture.active로 남깁니다. 이 단계는 프레임 맨 앞(EarlyUpdate)이라
            // 그대로 두면 이번 프레임의 다른 렌더링(게임 OnGUI, 에디터 그리기)이 뒤집기 텍스처를 대상으로 삼을 수 있어서 원래대로 돌립니다.
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(source, flipTexture, new Vector2(1f, flip ? -1f : 1f), new Vector2(0f, flip ? 1f : 0f));
            }
            finally
            {
                RenderTexture.active = previousActive;
            }

            source = flipTexture;
        }

        long captureTicks = Stopwatch.GetTimestamp();
        int inputSeq = GameViewRemoteInput.LastInjectedSeq;
        int requestSession = session;
        RawFrame info = new RawFrame
        {
            width = width,
            height = height,
            inputSeq = inputSeq,
            captureTicks = captureTicks,
            captureFrame = Time.frameCount,
            sourceId = sourceId,
            sourceRecreated = sourceRecreated,
            cameraRenders = renders,
            editorFocused = EditorApplication.isFocused,
            screenWidth = resized ? width : Screen.width,
            screenHeight = resized ? height : Screen.height,
            topDown = SystemInfo.graphicsUVStartsAtTop != flip,
        };
        readbacksInFlight++;
        Interlocked.Increment(ref captures);
        pendingRequests.Add(AsyncGPUReadback.Request(source, 0, TextureFormat.RGBA32, request => OnReadback(request, requestSession, info)));
        if (UseLowLatency)
        {
            // 복사 명령을 바로 GPU에 제출해야 PostLateUpdate에서 기다리는 시간이 짧습니다.
            GL.Flush();
        }

        return GameViewRemoteTrace.CAPTURE_REQUESTED;
    }

    private static void DrawTestPattern(RenderTexture target)
    {
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture.active = target;
        GL.PushMatrix();
        try
        {
            GL.LoadPixelMatrix(0f, target.width, target.height, 0f);
            float size = target.width * TEST_PATTERN_SIZE;
            float progress = (float)(GameViewRemoteTrace.NowMs % TEST_PATTERN_PERIOD_MS / TEST_PATTERN_PERIOD_MS);
            float x = progress * (target.width + size) - size;
            float y = target.height * 0.3f;
            Graphics.DrawTexture(new Rect(x, y, size, size), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x + size * 0.2f, y + size * 0.2f, size * 0.6f, size * 0.6f), Texture2D.blackTexture);
        }
        finally
        {
            GL.PopMatrix();
            RenderTexture.active = previousActive;
        }
    }

    // 끝난 리드백의 콜백을 지금 부르게 합니다(Unity 자체 확인은 한 프레임 더 늦음). 끝나지 않은 것은 기다리지 않습니다.
    private static void PollReadbacks()
    {
        for (int i = pendingRequests.Count - 1; i >= 0; i--)
        {
            AsyncGPUReadbackRequest request = pendingRequests[i];
            request.Update();
            if (request.done)
            {
                pendingRequests.RemoveAt(i);
            }
        }
    }

    public static bool UseLowLatency { get { return GameViewRemote.LowLatencyReadback && !LowLatencySuspended; } }

    // 저지연 리드백: PostLateUpdate 끝(PlayerLoop, 메인 스레드)에서 이번 프레임에 요청한 리드백을 받습니다.
    // 이미 끝난 것은 기다리지 않고, 끝나지 않은 것만 기다립니다. 평균 대기가 길면 이번 세션은 저지연을 멈춥니다.
    public static void FinishReadbacks()
    {
        if (pendingRequests.Count == 0)
        {
            return;
        }

        long start = Stopwatch.GetTimestamp();
        foreach (AsyncGPUReadbackRequest request in pendingRequests)
        {
            request.Update();
            if (!request.done)
            {
                request.WaitForCompletion();
            }
        }

        pendingRequests.Clear();
        float waited = (float)GameViewRemote.ElapsedMs(start);
        GameViewRemoteTrace.SetReadbackWait(waited);
        avgWaitMs = waitSamples == 0 ? waited : Mathf.Lerp(avgWaitMs, waited, 0.1f);
        maxWaitMs = Mathf.Max(maxWaitMs, waited);
        waitSamples++;
        if (waitSamples >= LOW_LATENCY_MIN_SAMPLES && avgWaitMs > LOW_LATENCY_SUSPEND_WAIT_MS)
        {
            LowLatencySuspendCount++;
            if (LowLatencySuspendCount <= 3)
            {
                UnityEngine.Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} low-latency readback paused for {LOW_LATENCY_RETRY_MS / 1000:0}s: main-thread wait avg {avgWaitMs:0.0} ms > {LOW_LATENCY_SUSPEND_WAIT_MS} ms (#{LowLatencySuspendCount})");
            }

            suspendedSinceTicks = Stopwatch.GetTimestamp();
            waitSamples = 0;
            avgWaitMs = 0f;
        }
    }

    // 직전 호출 뒤로 가장 길었던 저지연 대기(ms)를 돌려주고 지웁니다(1초 통계용).
    public static float TakeMaxWaitMs()
    {
        float value = maxWaitMs;
        maxWaitMs = 0f;
        return value;
    }

    // Game 뷰 탭이 같은 도킹 영역의 다른 탭(Scene 등)에 가려졌는지. 가려진 Game 뷰는 그려지지 않아 화면이 멈춥니다.
    // PlayModeView.RenderView를 밖에서 불러도 카메라가 그려지지 않아서(실측) 대신 경고하고 앞으로 꺼내는 버튼을 둡니다.
    public static bool IsGameViewHidden()
    {
        if (cachedGameView == null && gameViewType != null)
        {
            UnityEngine.Object[] views = Resources.FindObjectsOfTypeAll(gameViewType);
            cachedGameView = views.Length > 0 ? views[0] : null;
        }

        EditorWindow view = cachedGameView as EditorWindow;
        if (view == null || parentField == null || actualViewProperty == null)
        {
            return false;
        }

        object parent = parentField.GetValue(view);
        return parent != null && actualViewProperty.DeclaringType.IsInstanceOfType(parent) && !ReferenceEquals(actualViewProperty.GetValue(parent), view);
    }

    public static void ShowGameView()
    {
        EditorWindow view = cachedGameView as EditorWindow;
        if (view == null && gameViewType != null)
        {
            view = EditorWindow.GetWindow(gameViewType);
        }

        if (view != null)
        {
            view.Show();
            view.Focus();
        }
    }

    // Game 뷰가 여러 개면 렌더 타깃 크기가 Screen.width/height와 같은 것을 고릅니다.
    private static RenderTexture FindGameViewTexture()
    {
        if (gameViewType == null || targetTextureField == null)
        {
            return null;
        }

        // 매 프레임 FindObjectsOfTypeAll을 부르지 않도록 고른 창을 기억해 둡니다.
        if (cachedGameView != null)
        {
            RenderTexture cached = targetTextureField.GetValue(cachedGameView) as RenderTexture;
            if (cached != null && cached.width == Screen.width && cached.height == Screen.height)
            {
                return cached;
            }
        }

        RenderTexture fallback = null;
        foreach (UnityEngine.Object view in Resources.FindObjectsOfTypeAll(gameViewType))
        {
            RenderTexture texture = targetTextureField.GetValue(view) as RenderTexture;
            if (texture == null)
            {
                continue;
            }

            cachedGameView = view;
            if (texture.width == Screen.width && texture.height == Screen.height)
            {
                return texture;
            }

            fallback = texture;
        }

        return fallback;
    }

    private static void OnCameraPostRender(Camera camera)
    {
        if (camera.cameraType == CameraType.Game)
        {
            cameraRenders++;
            if (camera.targetTexture == null)
            {
                screenCameraRenders++;
            }

            lastRenderTicks = Stopwatch.GetTimestamp();
        }
    }

    private static void OnReadback(AsyncGPUReadbackRequest request, int requestSession, RawFrame frame)
    {
        if (requestSession != session)
        {
            return;
        }

        readbacksInFlight--;
        if (server == null || request.hasError)
        {
            return;
        }

        long captureTicks = frame.captureTicks;
        avgReadbackMs = Mathf.Lerp(avgReadbackMs, (float)GameViewRemote.ElapsedMs(captureTicks), 0.1f);
        int length = frame.width * frame.height * 4;
        byte[] pixels = RentBuffer(length);
        request.GetData<byte>().CopyTo(pixels);
        frame.pixels = pixels;
        lock (frameLock)
        {
            // 인코더가 아직 못 가져간 이전 프레임은 버리고 최신 것만 둡니다.
            if (pendingFrame != null && pendingFrame.captureTicks > captureTicks)
            {
                ReturnBufferLocked(pixels);
                Interlocked.Increment(ref encoderDrops);
                return;
            }

            if (pendingFrame != null)
            {
                ReturnBufferLocked(pendingFrame.pixels);
                Interlocked.Increment(ref encoderDrops);
            }

            pendingFrame = frame;
        }

        encoderWake.Set();
    }

    // 같은 크기 버퍼를 풀에서 꺼냅니다. 없으면 새로 만들고 수를 셉니다(정상 상태에서는 시작 직후 몇 개만 생겨야 함).
    private static byte[] RentBuffer(int length)
    {
        lock (frameLock)
        {
            while (bufferPool.Count > 0)
            {
                byte[] pooled = bufferPool.Pop();
                if (pooled.Length == length)
                {
                    return pooled;
                }
            }
        }

        Interlocked.Increment(ref bufferAllocs);
        return new byte[length];
    }

    // frameLock 안에서 호출합니다. 풀이 차면 버립니다.
    private static void ReturnBufferLocked(byte[] buffer)
    {
        if (buffer != null && bufferPool.Count < MAX_POOLED_BUFFERS)
        {
            bufferPool.Push(buffer);
        }
    }

    private static void EncodeLoop()
    {
        ulong lastHash = 0;
        long lastSentTicks = 0;
        int lastWidth = 0;
        int lastHeight = 0;
        while (encoderRunning)
        {
            encoderWake.WaitOne(250);
            RawFrame frame;
            lock (frameLock)
            {
                frame = pendingFrame;
                pendingFrame = null;
            }

            if (frame == null || !encoderRunning)
            {
                continue;
            }

            try
            {
                if (CheckFrame(frame))
                {
                    GameViewRemoteTrace.NoteEncode(frame.captureFrame, GameViewRemoteTrace.ENCODE_GUARD_SKIP);
                    continue;
                }

                ulong hash = HashPixels(frame.pixels);
                GameViewRemoteServer publisher = server;
                GameViewRemoteServer.Connection h264Target = publisher != null && GameViewRemote.UseH264 && !h264Failed ? publisher.SingleH264Client() : null;
                if (h264Target == null && h264 != null)
                {
                    StopH264();
                }

                if (h264Target != null)
                {
                    // H.264: 같은 화면은 보내지 않습니다(폰 디코더가 마지막 화면을 그대로 보여 줌).
                    bool same = hash == lastHash && frame.width == lastWidth && frame.height == lastHeight;
                    // 키프레임 요청이 와 있으면 같은 화면이어도 보냅니다(멈춘 화면에서 디코더를 다시 만든 폰이 다음 변화까지 기다리지 않게).
                    if (same && h264Client == h264Target && h264 != null && !h264Target.keyframeRequested)
                    {
                        GameViewRemote.RecordEncode(0, true);
                        GameViewRemoteTrace.NoteEncode(frame.captureFrame, GameViewRemoteTrace.ENCODE_IDENTICAL_SKIP);
                        continue;
                    }

                    if (!frame.topDown)
                    {
                        FlipRows(frame);
                    }

                    if (EncodeH264(publisher, h264Target, frame))
                    {
                        lastHash = hash;
                        lastWidth = frame.width;
                        lastHeight = frame.height;
                        continue;
                    }
                }

                if (frame.topDown)
                {
                    FlipRows(frame);
                }

                codecStatus = h264Failed ? $"MJPEG (H.264 실패: {h264Error})"
                    : GameViewRemote.UseH264 ? "MJPEG (페이지가 H.264를 지원하지 않거나 영상 클라이언트가 여러 개)" : "MJPEG";
                bool identical = hash == lastHash && frame.width == lastWidth && frame.height == lastHeight && lastMessage != null;
                if (identical && GameViewRemote.ElapsedMs(lastSentTicks) < IDLE_INTERVAL_MS)
                {
                    GameViewRemote.RecordEncode(0, true);
                    GameViewRemoteTrace.NoteEncode(frame.captureFrame, GameViewRemoteTrace.ENCODE_IDENTICAL_SKIP);
                    continue;
                }

                long encodeStart = Stopwatch.GetTimestamp();
                // 같은 화면이면 다시 인코딩하지 않고 이전 JPEG를 새 헤더로 보냅니다. JPEG 배열은 복사하지 않고 헤더(24B)만 따로 만듭니다.
                byte[] jpeg = identical
                    ? lastMessage.jpeg
                    : ImageConversion.EncodeArrayToJPG(frame.pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)frame.width, (uint)frame.height, 0, GameViewRemote.JpegQuality);
                double encodeMs = GameViewRemote.ElapsedMs(encodeStart);
                byte[] header = new byte[HEADER_BYTES];
                WriteHeader(header, frame.width, frame.height, frame.inputSeq, encodeMs, frame.captureTicks, identical ? FLAG_IDENTICAL : 0u);
                GameViewRemote.RecordEncode(encodeMs, false);

                lastHash = hash;
                lastWidth = frame.width;
                lastHeight = frame.height;
                lastSentTicks = Stopwatch.GetTimestamp();
                LastWidth = frame.width;
                LastHeight = frame.height;

                GameViewRemoteServer.VideoMessage message = new GameViewRemoteServer.VideoMessage { header = header, jpeg = jpeg, captureTicks = frame.captureTicks, frameId = Volatile.Read(ref frameId) };
                lastMessage = message;
                lastSentBlack = frame.black;
                GameViewRemoteTrace.NoteEncode(frame.captureFrame, GameViewRemoteTrace.ENCODE_JPEG);
                if (publisher != null)
                {
                    publisher.PublishFrame(message);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"{GameViewRemote.LOG_PREFIX} encode failed: {e}");
            }
            finally
            {
                lock (frameLock)
                {
                    ReturnBufferLocked(frame.pixels);
                }
            }
        }
    }

    // 검은/크기가 맞지 않는 프레임을 세고, 상태가 바뀔 때 원인 추적용 정보를 남깁니다(인코더 스레드).
    // 반환값 true = 안전장치로 이 프레임을 보내지 않음(폰에는 마지막 정상 프레임이 남음).
    private static bool CheckFrame(RawFrame frame)
    {
        int samples = 0;
        int dark = 0;
        int transparent = 0;
        byte[] pixels = frame.pixels;
        for (int i = 0; i + 3 < pixels.Length; i += 4 * CHECK_STRIDE)
        {
            samples++;
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] < DARK_RGB_SUM)
            {
                dark++;
            }

            if (pixels[i + 3] == 0)
            {
                transparent++;
            }
        }

        Interlocked.Increment(ref checkedFrames);
        float blackRatio = samples > 0 ? (float)dark / samples : 1f;
        bool black = blackRatio >= BLACK_FRACTION;
        bool sizeMismatch = frame.width != frame.screenWidth || frame.height != frame.screenHeight;
        if (black)
        {
            Interlocked.Increment(ref blackFrames);
        }

        if (sizeMismatch)
        {
            Interlocked.Increment(ref sizeMismatchFrames);
        }

        if ((black != lastBlack || sizeMismatch != lastSizeMismatch) && invalidLogs < 200)
        {
            invalidLogs++;
            UnityEngine.Debug.Log($"{GameViewRemote.LOG_PREFIX} frame check: {(black ? "BLACK" : "ok")} dark={blackRatio:0.000} alpha0={(float)transparent / Mathf.Max(1, samples):0.000} "
                + $"size={frame.width}x{frame.height} screen={frame.screenWidth}x{frame.screenHeight} unityFrame={frame.captureFrame} nextFrameId={frameId + 1} "
                + $"src={frame.sourceId} recreated={frame.sourceRecreated} cameraRenders={frame.cameraRenders} editorFocused={frame.editorFocused}");
        }

        lastBlack = black;
        lastSizeMismatch = sizeMismatch;
        frame.black = black;

        // 캡처 쪽 이상의 증거가 있는 프레임만 막습니다: Game 뷰 크기와 다른 프레임, 또는 직전에 보낸 프레임은 정상인데
        // 렌더 타깃이 새로 만들어졌거나 그 사이 게임 카메라가 그리지 않은 상태에서 읽은 검은 프레임.
        // 게임이 실제로 검게 페이드하는 프레임(카메라가 그렸고 타깃도 그대로)은 막지 않습니다. 보낸 적이 없으면(접속 직후) 막지 않습니다.
        bool suspect = sizeMismatch || (black && !lastSentBlack && (frame.sourceRecreated || frame.cameraRenders == 0));
        if (!suspect || lastMessage == null)
        {
            suspectSinceTicks = 0;
            return false;
        }

        if (suspectSinceTicks == 0)
        {
            suspectSinceTicks = frame.captureTicks;
        }

        if (GameViewRemote.ElapsedMs(suspectSinceTicks) >= GUARD_HOLD_MS)
        {
            return false;
        }

        Interlocked.Increment(ref guardedFrames);
        return true;
    }

    // 픽셀 행 순서를 위아래로 바꿉니다(인코더 스레드, 720x1280 약 1ms). 형식이 바뀐 직후 몇 프레임만 거칩니다.
    private static void FlipRows(RawFrame frame)
    {
        int stride = frame.width * 4;
        if (flipRow == null || flipRow.Length != stride)
        {
            flipRow = new byte[stride];
        }

        byte[] pixels = frame.pixels;
        for (int top = 0, bottom = (frame.height - 1) * stride; top < bottom; top += stride, bottom -= stride)
        {
            Buffer.BlockCopy(pixels, top, flipRow, 0, stride);
            Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
            Buffer.BlockCopy(flipRow, 0, pixels, bottom, stride);
        }

        frame.topDown = !frame.topDown;
    }

    // H.264로 1장 보냅니다(인코더 스레드). 인코더가 없거나, 받는 클라이언트·크기·비트레이트가 바뀌었거나, 키프레임 요청이 오면 다시 시작합니다
    // (시작 직후 설정 + 키프레임이 나옴). 보낼 프레임이 밀려 있으면 인코딩하지 않고 건너뜁니다. 쓸 수 없으면 false(MJPEG로 보냄).
    private static bool EncodeH264(GameViewRemoteServer publisher, GameViewRemoteServer.Connection client, RawFrame frame)
    {
        if (h264 != null && h264.HasExited)
        {
            FailH264(h264.LastError ?? "ffmpeg가 종료됨");
            return false;
        }

        bool keyframe = publisher.TakeKeyframeRequest(client);
        if (h264 == null || h264Client != client || h264.Width != frame.width || h264.Height != frame.height || h264Kbps != GameViewRemote.H264Kbps || keyframe)
        {
            StopH264();
            string ffmpeg = GameViewRemoteH264.FindFfmpeg(GameViewRemote.FfmpegPathForEncoder);
            if (ffmpeg == null)
            {
                FailH264("ffmpeg.exe를 찾지 못함(창에서 경로 지정)");
                return false;
            }

            GameViewRemoteH264 encoder = new GameViewRemoteH264();
            encoder.onOutput = (payload, key, config, info) => OnH264Output(encoder, payload, key, config, info);
            int kbps = GameViewRemote.H264Kbps;
            if (!encoder.Start(ffmpeg, frame.width, frame.height, kbps))
            {
                FailH264(encoder.LastError);
                encoder.Dispose();
                return false;
            }

            h264 = encoder;
            h264Client = client;
            h264Active = true;
            h264Kbps = kbps;
            codecStatus = $"H.264 (NVENC) {kbps / 1000f:0.0} Mbps, {frame.width}x{frame.height}";
            UnityEngine.Debug.Log($"{GameViewRemote.LOG_PREFIX} H.264 encoder started: {codecStatus}{(keyframe ? " (keyframe requested)" : string.Empty)}, {ffmpeg}");
        }

        if (publisher.BacklogMs(client) >= MAX_H264_BACKLOG_MS)
        {
            publisher.NoteDroppedFrame();
            GameViewRemoteTrace.NoteEncode(frame.captureFrame, GameViewRemoteTrace.ENCODE_BACKLOG_SKIP);
            return true;
        }

        GameViewRemoteH264.FrameInfo frameInfo = new GameViewRemoteH264.FrameInfo
        {
            width = frame.width,
            height = frame.height,
            inputSeq = frame.inputSeq,
            captureTicks = frame.captureTicks,
        };
        if (!h264.Encode(frame.pixels, frameInfo))
        {
            FailH264(h264.LastError);
            return false;
        }

        LastWidth = frame.width;
        LastHeight = frame.height;
        GameViewRemoteTrace.NoteEncode(frame.captureFrame, GameViewRemoteTrace.ENCODE_H264);
        return true;
    }

    // ffmpeg 출력 1개(리더 스레드). 지금 인코더의 것이 아니면(다시 시작 전 것) 버립니다.
    private static void OnH264Output(GameViewRemoteH264 encoder, byte[] payload, bool key, bool config, GameViewRemoteH264.FrameInfo info)
    {
        GameViewRemoteServer publisher = server;
        GameViewRemoteServer.Connection client = h264Client;
        if (encoder != h264 || publisher == null || client == null)
        {
            return;
        }

        double encodeMs = config ? 0.0 : GameViewRemote.ElapsedMs(info.feedTicks);
        long captureTicks = config ? Stopwatch.GetTimestamp() : info.captureTicks;
        byte[] header = new byte[HEADER_BYTES];
        uint flags = FLAG_H264 | (key ? FLAG_KEY : 0u) | (config ? FLAG_CONFIG : 0u);
        int id = WriteHeader(header, encoder.Width, encoder.Height, info.inputSeq, encodeMs, captureTicks, flags);
        if (!config)
        {
            GameViewRemote.RecordEncode(encodeMs, false);
        }

        publisher.PublishOrdered(client, new GameViewRemoteServer.VideoMessage { header = header, jpeg = payload, captureTicks = captureTicks, frameId = id });
    }

    private static void FailH264(string reason)
    {
        h264Failed = true;
        h264Error = reason;
        StopH264();
        UnityEngine.Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} H.264 unavailable, using MJPEG: {reason}");
    }

    private static void StopH264()
    {
        GameViewRemoteH264 encoder = h264;
        h264 = null;
        h264Client = null;
        h264Active = false;
        encoder?.Dispose();
    }

    // 헤더 28바이트를 쓰고 frameId를 돌려줍니다(인코더 스레드와 H.264 리더 스레드가 함께 씀).
    private static int WriteHeader(byte[] payload, int width, int height, int inputSeq, double encodeMs, long captureTicks, uint flags)
    {
        int id = Interlocked.Increment(ref frameId);
        WriteUInt(payload, 0, (uint)id);
        payload[4] = (byte)width;
        payload[5] = (byte)(width >> 8);
        payload[6] = (byte)height;
        payload[7] = (byte)(height >> 8);
        WriteUInt(payload, 8, (uint)inputSeq);
        WriteUInt(payload, 12, (uint)(encodeMs * 1000.0));
        WriteUInt(payload, 16, (uint)(GameViewRemote.ElapsedMs(captureTicks) * 1000.0));
        WriteUInt(payload, 20, flags);
        WriteUInt(payload, 24, (uint)(long)(GameViewRemoteTrace.SessionMs(captureTicks) * 10.0));
        return id;
    }

    private static void WriteUInt(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    // 전체 픽셀을 8바이트 단위로 섞는 해시. 작은 글자 하나가 바뀌어도 잡히도록 표본 추출은 하지 않습니다.
    private static ulong HashPixels(byte[] pixels)
    {
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(pixels);
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < words.Length; i++)
        {
            hash = (hash ^ words[i]) * 1099511628211UL;
        }

        return hash;
    }

    private static void EnsureTexture(ref RenderTexture texture, int width, int height, GraphicsFormat format)
    {
        if (texture != null && texture.width == width && texture.height == height && texture.graphicsFormat == format)
        {
            return;
        }

        ReleaseTexture(ref texture);
        // 원본(Game 뷰 렌더 타깃)과 같은 형식이라 Blit이 색 공간 변환을 하지 않습니다(이 프로젝트: R8G8B8A8_UNorm, sRGB 아님).
        texture = new RenderTexture(width, height, 0, format);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.Create();
    }

    private static void ReleaseTexture(ref RenderTexture texture)
    {
        if (texture == null)
        {
            return;
        }

        // Graphics.Blit이 뒤집기 텍스처를 active로 남겨 두므로 풀기 전에 해제합니다.
        if (RenderTexture.active == texture)
        {
            RenderTexture.active = null;
        }

        texture.Release();
        UnityEngine.Object.DestroyImmediate(texture);
        texture = null;
    }
}
