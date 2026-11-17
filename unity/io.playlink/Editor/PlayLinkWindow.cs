using UnityEditor;
using UnityEngine;

// PlayLink 창.
// 맨 위: 폰 연결(Start → 목록에서 폰 고르기 → 연결, 페어링, USB, 앱 다운로드. PlayLinkLinkGUI).
// 그 아래 접어 둔 구역: 화질과 고급 설정, 상태와 통계.
public class PlayLinkWindow : EditorWindow
{
    // 접어 둔 구역의 열림 상태(에디터를 켜 둔 동안만 기억)
    private const string KEY_SETTINGS_OPEN = "PlayLink_WindowSettingsOpen";
    private const string KEY_STATUS_OPEN = "PlayLink_WindowStatusOpen";

    private Vector2 scroll;

    [MenuItem("Tools/PlayLink/Window", false, 0)]
    public static void Open()
    {
        GetWindow<PlayLinkWindow>("PlayLink").Show();
    }

    private void OnEnable()
    {
        PlayLinkLink.Changed += Repaint;
    }

    private void OnDisable()
    {
        PlayLinkLink.Changed -= Repaint;
        PlayLinkLinkGUI.ReleaseTextures();
    }

    private void OnInspectorUpdate()
    {
        Repaint();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        // 창이 좁아도 내용이 오른쪽으로 밀려 잘리지 않게 너비를 창에 맞춥니다(세로 스크롤 막대 자리 제외).
        EditorGUILayout.BeginVertical(GUILayout.MaxWidth(Mathf.Max(200f, position.width - 16f)));
        DrawWarnings();
        PlayLinkLinkGUI.Draw(this);
        EditorGUILayout.Space();
        if (Foldout(KEY_SETTINGS_OPEN, "화질과 고급 설정 (이 PC 공통)", false))
        {
            DrawControls();
        }

        if (Foldout(KEY_STATUS_OPEN, "상태와 통계", false))
        {
            DrawStatus();
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
        if (!string.IsNullOrEmpty(PlayLink.LastError))
        {
            EditorGUILayout.HelpBox(PlayLink.LastError, MessageType.Error);
        }

        if (PlayLink.StaleReason != null)
        {
            EditorGUILayout.HelpBox($"폰 화면이 멈춰 있습니다: {PlayLink.StaleReason}", MessageType.Warning);
            if (PlayLinkCapture.IsGameViewHidden() && GUILayout.Button("Game 뷰 보이기"))
            {
                PlayLinkCapture.ShowGameView();
            }
        }
    }

    private void DrawControls()
    {
        EditorGUILayout.LabelField("여기 값은 이 PC 의 모든 Unity 프로젝트가 함께 씁니다. 평소에는 바꿀 필요가 없습니다.", EditorStyles.wordWrappedMiniLabel);
        PlayLink.UseH264 = EditorGUILayout.Toggle(new GUIContent("H.264 사용(가능하면)",
            "기본 켬. ffmpeg 가 있으면 H.264 로 보냅니다(Wi-Fi 에서 MJPEG 보다 훨씬 부드러움). 안 되면 자동으로 MJPEG."),
            PlayLink.UseH264);
        if (PlayLink.UseH264)
        {
            PlayLink.H264Kbps = EditorGUILayout.IntSlider(new GUIContent("H.264 비트레이트(kbps)", "Wi-Fi 4000~8000, USB 10000 이상"), PlayLink.H264Kbps, 1000, 30000);
            using (new EditorGUILayout.HorizontalScope())
            {
                string path = EditorGUILayout.DelayedTextField(new GUIContent("ffmpeg 경로", "비우면 PATH에서 찾습니다(8.x 권장)."), PlayLink.FfmpegPath);
                if (path != PlayLink.FfmpegPath)
                {
                    PlayLink.FfmpegPath = path;
                }

                if (GUILayout.Button("찾기", GUILayout.Width(44)))
                {
                    string picked = EditorUtility.OpenFilePanel("ffmpeg.exe", string.Empty, "exe");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        PlayLink.FfmpegPath = picked;
                    }
                }
            }

            DrawEncoder();
        }

        PlayLink.JpegQuality = EditorGUILayout.IntSlider(new GUIContent("JPEG Quality", "MJPEG로 보낼 때만 씁니다."), PlayLink.JpegQuality, 10, 100);
        PlayLink.FpsCap = EditorGUILayout.IntSlider("FPS Cap", PlayLink.FpsCap, 1, 120);
        int scaleIndex = System.Array.IndexOf(PlayLink.STREAM_SCALES, PlayLink.StreamScalePercent);
        int pickedScale = EditorGUILayout.Popup(new GUIContent("전송 해상도",
            "Game 뷰 해상도 대비. 느린 Wi-Fi 나 느린 폰에서는 50%가 부드럽습니다. 화질은 떨어집니다."),
            Mathf.Max(0, scaleIndex), new[] { "100% (원본)", "75%", "50%" });
        PlayLink.StreamScalePercent = PlayLink.STREAM_SCALES[pickedScale];
        PlayLinkCapture.TestPattern = EditorGUILayout.Toggle(new GUIContent("테스트 패턴(끊김 확인용)",
            "진단용. 일정한 속도로 가로지르는 네모를 화면에 그려 보냅니다. 게임 화면이 멈춰 있어도 60fps로 보내지고, 폰에서 네모가 고르게 움직이는지로 끊김을 눈으로 확인합니다. Play 를 끝내면 꺼집니다."),
            PlayLinkCapture.TestPattern);
        PlayLink.AutoPacing = EditorGUILayout.Toggle(new GUIContent("자동 프레임 간격(끊김 방지)",
            "기본 켬. 전송이나 폰 디코딩이 Unity fps를 못 따라가면 프레임이 불규칙하게 버려져 끊겨 보입니다. 그럴 때 2~4프레임마다 1번씩 고르게 보냅니다."),
            PlayLink.AutoPacing);
        PlayLink.LowLatencyReadback = EditorGUILayout.Toggle(new GUIContent("저지연 리드백",
            "기본 켬. 캡처를 같은 프레임 안에 받아 화면 지연을 약 1프레임 줄입니다. 대신 메인 스레드가 GPU 복사를 잠깐(평균 약 1~3ms) 기다립니다."),
            PlayLink.LowLatencyReadback);
    }

    // H.264 인코더: 자동(되는 것 중 첫 번째) 또는 하나로 고정
    private static void DrawEncoder()
    {
        string[] names = PlayLinkH264.ENCODERS;
        string[] labels = new string[names.Length + 1];
        labels[0] = "자동 (NVIDIA → AMD → Intel → CPU)";
        for (int i = 0; i < names.Length; i++)
        {
            labels[i + 1] = PlayLinkH264.EncoderLabel(names[i]) + " (" + names[i] + ")";
        }

        int current = System.Array.IndexOf(names, PlayLink.EncoderChoice) + 1;
        int picked = EditorGUILayout.Popup(new GUIContent("H.264 인코더",
            "자동이면 그래픽 카드 인코더를 차례로 시험해 실제로 되는 첫 번째를 쓰고, 다 안 되면 CPU(libx264)로 인코딩합니다. 고정한 인코더가 안 되면 MJPEG 로 보냅니다."), current, labels);
        if (picked != current)
        {
            PlayLink.EncoderChoice = picked == 0 ? string.Empty : names[picked - 1];
        }
    }

    private void DrawStatus()
    {
        PlayLink.Snapshot stats = PlayLink.Stats;
        EditorGUILayout.LabelField("Streaming", PlayLink.IsRunning ? "예" : "아니오 (Play 중이고 폰이 연결돼 있을 때만 보냄)");
        EditorGUILayout.LabelField("Codec", PlayLinkCapture.CodecStatus);
        EditorGUILayout.LabelField("Game view", $"{Screen.width}x{Screen.height}  (streamed {stats.width}x{stats.height})");
        EditorGUILayout.LabelField("FPS", $"unity {stats.unityFps:0.0} / sent {stats.sentFps:0.0}  (identical skipped {stats.skippedIdentical}/s)");
        EditorGUILayout.LabelField("Bitrate", $"{stats.kbps:0} kbps  (avg frame {stats.avgFrameKb:0.0} KB)");
        string wait = PlayLinkCapture.LowLatencySuspended
            ? $"저지연 잠시 중지(대기 김, {PlayLinkCapture.LowLatencySuspendCount}회)"
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
            EditorGUILayout.HelpBox($"FPS Cap({PlayLink.FpsCap})이 Unity fps({stats.unityFps:0})보다 낮아 {stats.captureStride}프레임마다 1번 보냅니다(간격을 고르게 하려고). 부드럽게 보려면 FPS Cap을 Unity fps 이상으로 두세요.", MessageType.None);
        }

        EditorGUILayout.LabelField("Input", $"recv→inject {stats.injectLatencyMs:0.0} ms ({stats.injectLatencyFrames:0.0} frames), touches {PlayLinkInput.ActiveTouches}");
        EditorGUILayout.LabelField("Last input", PlayLinkInput.LastInput);
        EditorGUILayout.LabelField("Frame check", $"checked {PlayLinkCapture.CheckedFrames}, black {PlayLinkCapture.BlackFrames}, size {PlayLinkCapture.SizeMismatchFrames}, held {PlayLinkCapture.GuardedFrames}, no render {PlayLinkCapture.NoRenderCaptures}");
        EditorGUILayout.LabelField("Client check", PlayLinkInput.ClientReport);
    }
}
