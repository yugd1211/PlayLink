using UnityEditor;
using UnityEngine;

// Game View Remote 창.
// 맨 위: 폰으로 연결(Start → QR → 폰 카메라로 찍기). 주소나 포트를 넣는 곳이 없습니다(GameViewRemoteHostGUI).
// 그 아래 접어 둔 구역: 화질과 고급 설정, 통계, Chrome 페이지로 연결(USB / 무선 adb. 예전 방식 그대로).
// 같은 Wi-Fi 연결은 QR(인증 있음) 하나뿐입니다. 예전의 인증 없는 "Wi-Fi 접속 허용" 토글은 없앴습니다.
public class GameViewRemoteWindow : EditorWindow
{
    // adb 상태(기기, unauthorized, reverse)를 다시 확인하는 간격
    private const double ADB_REFRESH_SECONDS = 5.0;

    // 접어 둔 구역의 열림 상태(에디터를 켜 둔 동안만 기억)
    private const string KEY_SETTINGS_OPEN = "GVR_WindowSettingsOpen";
    private const string KEY_STATUS_OPEN = "GVR_WindowStatusOpen";
    private const string KEY_PAGE_OPEN = "GVR_WindowPageOpen";

    private string connectAddress = string.Empty;
    private string pairAddress = string.Empty;
    private string pairCode = string.Empty;
    private Vector2 scroll;
    private double nextAdbRefresh;

    [MenuItem("Tools/Game View Remote/Window", false, 0)]
    public static void Open()
    {
        GetWindow<GameViewRemoteWindow>("Game View Remote").Show();
    }

    private void OnEnable()
    {
        connectAddress = GameViewRemote.WirelessAddress;
        nextAdbRefresh = 0;
    }

    private void OnDisable()
    {
        GameViewRemoteHostGUI.ReleaseTexture();
    }

    private void OnInspectorUpdate()
    {
        // 무선 연결에 성공한 주소를 기억합니다(EditorPrefs는 메인 스레드에서만 씀).
        string connected = GameViewRemoteAdb.ConnectedAddress;
        if (!string.IsNullOrEmpty(connected) && connected != GameViewRemote.WirelessAddress)
        {
            GameViewRemote.WirelessAddress = connected;
            connectAddress = connected;
        }

        if (EditorApplication.timeSinceStartup >= nextAdbRefresh)
        {
            nextAdbRefresh = EditorApplication.timeSinceStartup + ADB_REFRESH_SECONDS;
            GameViewRemoteAdb.RefreshStatusAsync(CurrentPort, GameViewRemote.IsRunning && GameViewRemote.AutoAdbReverse);
        }

        Repaint();
    }

    private static int CurrentPort { get { return GameViewRemote.IsRunning ? GameViewRemote.RunningPort : GameViewRemote.Port; } }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        // 창이 좁아도 내용이 오른쪽으로 밀려 잘리지 않게 너비를 창에 맞춥니다(세로 스크롤 막대 자리 제외).
        EditorGUILayout.BeginVertical(GUILayout.MaxWidth(Mathf.Max(200f, position.width - 16f)));
        DrawWarnings();
        GameViewRemoteHostGUI.Draw(this);
        EditorGUILayout.Space();
        if (Foldout(KEY_SETTINGS_OPEN, "화질과 고급 설정 (이 PC 공통)", false))
        {
            DrawControls();
        }

        if (Foldout(KEY_STATUS_OPEN, "상태와 통계", false))
        {
            DrawStatus();
        }

        if (Foldout(KEY_PAGE_OPEN, "Chrome 페이지로 연결 (USB / 무선 adb, 예전 방식)", false))
        {
            DrawPageServer();
            DrawConnection();
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();
    }

    private static bool Foldout(string key, string title, bool openByDefault)
    {
        bool open = SessionState.GetBool(key, openByDefault);
        bool picked = EditorGUILayout.Foldout(open, title, true, EditorStyles.foldoutHeader);
        if (picked != open)
        {
            SessionState.SetBool(key, picked);
        }

        return picked;
    }

    // 지금 폰에 화면이 안 나오거나 멈출 이유를 맨 위에 보여 줍니다.
    private void DrawWarnings()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("Play 모드가 아닙니다. 게임 화면은 Play 중에만 폰으로 나갑니다(연결은 Play 전에 해 두어도 됩니다).", MessageType.Info);
        }
        else if (!GameViewRemote.IsRunning)
        {
            EditorGUILayout.HelpBox("화면 전송이 꺼져 있습니다. 위의 Start 를 누르세요.", MessageType.Warning);
        }

        if (!string.IsNullOrEmpty(GameViewRemote.LastError))
        {
            EditorGUILayout.HelpBox(GameViewRemote.LastError, MessageType.Error);
        }

        if (GameViewRemote.StaleReason != null)
        {
            EditorGUILayout.HelpBox($"폰 화면이 멈춰 있습니다: {GameViewRemote.StaleReason}", MessageType.Warning);
            if (GameViewRemoteCapture.IsGameViewHidden() && GUILayout.Button("Game 뷰 보이기"))
            {
                GameViewRemoteCapture.ShowGameView();
            }
        }

        GameViewRemoteAdb.Status adb = GameViewRemoteAdb.LastStatus;
        if (adb.level != GameViewRemoteAdb.Level.Ok)
        {
            EditorGUILayout.HelpBox($"USB: {adb.message}", adb.level == GameViewRemoteAdb.Level.Warning ? MessageType.Warning : MessageType.Info);
        }
    }

    private void DrawControls()
    {
        EditorGUILayout.LabelField("여기 값은 이 PC 의 모든 Unity 프로젝트가 함께 씁니다. 평소에는 바꿀 필요가 없습니다.", EditorStyles.wordWrappedMiniLabel);
        using (new EditorGUI.DisabledScope(GameViewRemote.IsRunning || GameViewRemoteHost.Armed))
        {
            GameViewRemote.Port = EditorGUILayout.IntField(new GUIContent("Port",
                "도구가 쓰는 포트(기본 7300). 다른 프로그램과 겹칠 때만 바꾸세요. Stop 한 상태에서만 바꿀 수 있고, 바꾸면 QR 이 달라져 폰에서 다시 찍어야 합니다."), GameViewRemote.Port);
        }

        GameViewRemote.UseH264 = EditorGUILayout.Toggle(new GUIContent("H.264 사용(가능하면)",
            "기본 켬. 폰 페이지가 H.264(WebCodecs)를 지원하고 ffmpeg가 있으면 NVENC로 보냅니다(무선에서 MJPEG보다 훨씬 부드러움). 안 되면 자동으로 MJPEG."),
            GameViewRemote.UseH264);
        if (GameViewRemote.UseH264)
        {
            GameViewRemote.H264Kbps = EditorGUILayout.IntSlider(new GUIContent("H.264 비트레이트(kbps)", "무선 adb 4000~6000, USB 10000 이상"), GameViewRemote.H264Kbps, 1000, 30000);
            using (new EditorGUILayout.HorizontalScope())
            {
                string path = EditorGUILayout.DelayedTextField(new GUIContent("ffmpeg 경로", "비우면 PATH에서 찾습니다. NVENC가 되는 ffmpeg(8.x 권장)"), GameViewRemote.FfmpegPath);
                if (path != GameViewRemote.FfmpegPath)
                {
                    GameViewRemote.FfmpegPath = path;
                }

                if (GUILayout.Button("찾기", GUILayout.Width(44)))
                {
                    string picked = EditorUtility.OpenFilePanel("ffmpeg.exe", string.Empty, "exe");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        GameViewRemote.FfmpegPath = picked;
                    }
                }
            }
        }

        GameViewRemote.JpegQuality = EditorGUILayout.IntSlider(new GUIContent("JPEG Quality", "MJPEG로 보낼 때만 씁니다."), GameViewRemote.JpegQuality, 10, 100);
        GameViewRemote.FpsCap = EditorGUILayout.IntSlider("FPS Cap", GameViewRemote.FpsCap, 1, 120);
        int scaleIndex = System.Array.IndexOf(GameViewRemote.STREAM_SCALES, GameViewRemote.StreamScalePercent);
        int pickedScale = EditorGUILayout.Popup(new GUIContent("전송 해상도",
            "Game 뷰 해상도 대비. 무선(adb 무선, 약 8Mbps)이나 느린 폰에서는 50%가 부드럽습니다. 화질은 떨어집니다."),
            Mathf.Max(0, scaleIndex), new[] { "100% (원본)", "75%", "50% (무선 권장)" });
        GameViewRemote.StreamScalePercent = GameViewRemote.STREAM_SCALES[pickedScale];
        GameViewRemote.AutoStart = EditorGUILayout.Toggle("Play 진입 시 자동 시작", GameViewRemote.AutoStart);
        GameViewRemoteCapture.TestPattern = EditorGUILayout.Toggle(new GUIContent("테스트 패턴(끊김 확인용)",
            "진단용. 일정한 속도로 가로지르는 네모를 화면에 그려 보냅니다. 게임 화면이 멈춰 있어도 60fps로 보내지고, 폰에서 네모가 고르게 움직이는지로 끊김을 눈으로 확인합니다. Stop 하면 꺼집니다."),
            GameViewRemoteCapture.TestPattern);
        GameViewRemote.PreferWireless = EditorGUILayout.Toggle(new GUIContent("영상 경로: 무선 우선",
            "같은 폰이 USB와 무선(adb 무선)으로 함께 연결돼 있을 때 영상을 어느 쪽으로 보낼지. 끄면(기본) USB, 켜면 무선(Wi-Fi). 한쪽만 연결돼 있으면 그쪽으로 갑니다. 바꾸면 몇 초 안에 터널을 옮기고 폰 페이지가 다시 접속합니다."),
            GameViewRemote.PreferWireless);
        GameViewRemote.AutoAdbReverse = EditorGUILayout.Toggle(new GUIContent("Start 때 adb reverse 자동",
            "서버를 시작할 때 USB 기기가 정확히 1대면 adb reverse tcp:포트 tcp:포트를 자동으로 합니다."), GameViewRemote.AutoAdbReverse);
        GameViewRemote.AutoPacing = EditorGUILayout.Toggle(new GUIContent("자동 프레임 간격(끊김 방지)",
            "기본 켬. USB 전송이나 폰 디코딩이 Unity fps를 못 따라가면 프레임이 불규칙하게 버려져 끊겨 보입니다. 그럴 때 2~4프레임마다 1번씩 고르게 보냅니다."),
            GameViewRemote.AutoPacing);
        GameViewRemote.LowLatencyReadback = EditorGUILayout.Toggle(new GUIContent("저지연 리드백",
            "기본 켬. 캡처를 같은 프레임 안에 받아 화면 지연을 약 1프레임 줄입니다. 대신 메인 스레드가 GPU 복사를 잠깐(평균 약 1~3ms) 기다립니다."),
            GameViewRemote.LowLatencyReadback);
    }

    // Chrome 페이지가 붙는 서버(이 PC 안에서만 열림, Play 중에만)를 QR 과 따로 켜고 끕니다. 맨 위 Start 는 Play 중이면 이것도 같이 켭니다.
    private void DrawPageServer()
    {
        EditorGUILayout.LabelField("Chrome 페이지는 USB(adb reverse)나 무선 adb 로만 열립니다. 같은 Wi-Fi 로 바로 붙는 것은 맨 위의 QR(앱)로 합니다.", EditorStyles.wordWrappedMiniLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(GameViewRemote.IsRunning || !EditorApplication.isPlaying))
            {
                if (GUILayout.Button(new GUIContent("화면 전송 시작 (Play 중)", "QR 없이 Chrome 페이지만 쓸 때. Play 중에만 누를 수 있습니다.")))
                {
                    GameViewRemote.Start();
                }
            }

            using (new EditorGUI.DisabledScope(!GameViewRemote.IsRunning))
            {
                if (GUILayout.Button("화면 전송 중지"))
                {
                    GameViewRemote.Stop();
                }
            }
        }
    }

    private void DrawStatus()
    {
        GameViewRemote.Snapshot stats = GameViewRemote.Stats;
        EditorGUILayout.LabelField("Running", GameViewRemote.IsRunning ? $"예 (port {GameViewRemote.RunningPort}, 127.0.0.1 = 이 PC 안에서만)" : "아니오");
        EditorGUILayout.LabelField("Clients", $"video {GameViewRemote.VideoClients} / input {GameViewRemote.InputClients}");
        EditorGUILayout.LabelField("Codec", GameViewRemoteCapture.CodecStatus);
        EditorGUILayout.LabelField("Game view", $"{Screen.width}x{Screen.height}  (streamed {stats.width}x{stats.height})");
        EditorGUILayout.LabelField("FPS", $"unity {stats.unityFps:0.0} / sent {stats.sentFps:0.0}  (identical skipped {stats.skippedIdentical}/s)");
        EditorGUILayout.LabelField("Bitrate", $"{stats.kbps:0} kbps  (avg frame {stats.avgFrameKb:0.0} KB)");
        string wait = GameViewRemoteCapture.LowLatencySuspended
            ? $"저지연 잠시 중지(대기 김, {GameViewRemoteCapture.LowLatencySuspendCount}회)"
            : $"wait avg {stats.readbackWaitMs:0.00} max {stats.readbackMaxWaitMs:0.0}";
        EditorGUILayout.LabelField("Latency", $"readback {stats.readbackMs:0.0} ms ({wait}) / encode {stats.encodeMs:0.0} ms / capture→sent {stats.captureToSendMs:0.0} ms");
        EditorGUILayout.LabelField("Pacing", $"capture {stats.captureFps:0}/s (every {stats.captureStride} frame) · drops: readback-full {stats.inflightSkips} encoder {stats.encoderDrops} send {stats.sendDrops} phone {stats.clientDrops:0}/s");
        EditorGUILayout.LabelField(" ", $"ack {stats.ackMs:0.0}/{stats.ackMaxMs:0} ms (timeout {stats.ackTimeouts}) · send/frame {stats.sendCostMs:0.0} ms · phone decode/frame {stats.clientDecodeMs:0.0} ms · send gap max {stats.sendGapMaxMs:0} ms · unity frame max {stats.unityFrameMaxMs:0} ms, hitches {stats.unityHitches} · GC {stats.gcCount} · new buffers {stats.bufferAllocs}");
        if (stats.transportStride > 1)
        {
            EditorGUILayout.HelpBox($"전송/폰 디코딩이 따라가지 못해 {stats.transportStride}프레임마다 고르게 보냅니다(한 장 비용: 수신 확인 {stats.ackMs:0} ms, 폰 디코딩 {stats.clientDecodeMs:0} ms). JPEG Quality를 낮추면 더 자주 보낼 수 있습니다.", MessageType.None);
        }
        else if (stats.captureStride > 1)
        {
            EditorGUILayout.HelpBox($"FPS Cap({GameViewRemote.FpsCap})이 Unity fps({stats.unityFps:0})보다 낮아 {stats.captureStride}프레임마다 1번 보냅니다(간격을 고르게 하려고). 부드럽게 보려면 FPS Cap을 Unity fps 이상으로 두세요.", MessageType.None);
        }

        EditorGUILayout.LabelField("Input", $"recv→inject {stats.injectLatencyMs:0.0} ms ({stats.injectLatencyFrames:0.0} frames), touches {GameViewRemoteInput.ActiveTouches}");
        EditorGUILayout.LabelField("Last input", GameViewRemoteInput.LastInput);
        EditorGUILayout.LabelField("Frame check", $"checked {GameViewRemoteCapture.CheckedFrames}, black {GameViewRemoteCapture.BlackFrames}, size {GameViewRemoteCapture.SizeMismatchFrames}, held {GameViewRemoteCapture.GuardedFrames}, no render {GameViewRemoteCapture.NoRenderCaptures}");
        EditorGUILayout.LabelField("Client check", GameViewRemoteInput.ClientReport);
    }

    private void DrawConnection()
    {
        int port = CurrentPort;
        EditorGUILayout.LabelField("USB (adb reverse)", EditorStyles.miniBoldLabel);
        EditorGUILayout.SelectableLabel($"http://localhost:{port}/", GUILayout.Height(EditorGUIUtility.singleLineHeight));
        GameViewRemoteAdb.Status adb = GameViewRemoteAdb.LastStatus;
        if (adb.level == GameViewRemoteAdb.Level.Ok)
        {
            EditorGUILayout.LabelField(adb.message, EditorStyles.miniLabel);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(GameViewRemoteAdb.Busy))
            {
                if (GUILayout.Button(new GUIContent("폰에서 열기", "adb reverse 후 폰 Chrome에서 http://localhost:포트/ 를 엽니다.")))
                {
                    GameViewRemoteAdb.OpenOnPhoneAsync(port);
                }

                if (GUILayout.Button($"adb reverse tcp:{port}"))
                {
                    GameViewRemoteAdb.ReverseAsync(port);
                }

                if (GUILayout.Button("adb devices"))
                {
                    GameViewRemoteAdb.DevicesAsync(port);
                }
            }
        }

        DrawWireless(port);
        if (!string.IsNullOrEmpty(GameViewRemoteAdb.LastOutput))
        {
            EditorGUILayout.HelpBox(GameViewRemoteAdb.LastOutput, MessageType.None);
        }

        EditorGUILayout.LabelField("같은 Wi-Fi", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox("같은 Wi-Fi 연결은 맨 위의 QR(Start)로 합니다. 예전의 'Wi-Fi 접속 허용'(인증 없이 PC 주소로 페이지 열기)은 없앴습니다. "
            + "Chrome 페이지는 USB(adb reverse)나 아래 무선 adb 로만 열립니다.", MessageType.None);
    }

    // 무선 adb: 방화벽을 바꾸지 않고 Wi-Fi로 쓰는 방법. 폰은 USB와 똑같이 http://localhost:포트/ 로 엽니다.
    private void DrawWireless(int port)
    {
        EditorGUILayout.LabelField("무선 (adb, 방화벽 설정 불필요)", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox("PC와 폰이 같은 Wi-Fi여야 합니다. ① USB로 연결된 상태에서 'USB 폰을 무선으로 전환'을 누른 뒤 케이블을 뽑거나, "
            + "② Android 11 이상이면 폰 개발자 옵션 > 무선 디버깅에서 페어링(코드) → 연결. 연결되면 '폰에서 열기'로 똑같이 씁니다. "
            + "마지막 주소를 기억해 두었다가 Start 때 자동으로 다시 연결합니다(폰을 재부팅하면 ①을 다시 해야 함).", MessageType.None);
        using (new EditorGUI.DisabledScope(GameViewRemoteAdb.Busy))
        {
            if (GUILayout.Button("USB 폰을 무선으로 전환"))
            {
                GameViewRemoteAdb.SwitchToWirelessAsync(port);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                connectAddress = EditorGUILayout.TextField("주소 (IP:포트)", connectAddress);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(connectAddress)))
                {
                    if (GUILayout.Button("연결", GUILayout.Width(50)))
                    {
                        GameViewRemoteAdb.ConnectAsync(connectAddress, port);
                    }
                }

                if (GUILayout.Button("끊기", GUILayout.Width(50)))
                {
                    GameViewRemoteAdb.DisconnectWirelessAsync(port);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                pairAddress = EditorGUILayout.TextField("페어링 (IP:포트, 코드)", pairAddress);
                pairCode = EditorGUILayout.TextField(pairCode, GUILayout.Width(70));
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(pairAddress) || string.IsNullOrWhiteSpace(pairCode)))
                {
                    if (GUILayout.Button("페어링", GUILayout.Width(50)))
                    {
                        GameViewRemoteAdb.PairAsync(pairAddress, pairCode, port);
                    }
                }
            }
        }
    }
}
