using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// PlayLink 창의 맨 위 구역: Start → QR → 폰으로 찍어 연결.
// 주소나 포트를 손으로 넣는 곳이 없습니다. 서버 쪽 일은 전부 PlayLinkHost, PlayLinkFirewall, PlayLinkAppAdb 에 맡기고(직접 호출) 여기서는 그리기만 합니다.
public static class PlayLinkHostGUI
{
    // QR 한 변의 화면 크기(포인트). 모니터에서 30~50cm 떨어진 폰 카메라가 읽는 크기입니다.
    private const int QR_POINTS = 300;
    private const int QR_POINTS_LARGE = 520;
    private const string QR_PNG_PATH = "Temp/PlayLink/qr.png";
    private const string KEY_LARGE = "PlayLink_QrLarge";
    private const string KEY_SHOW_WHILE_CONNECTED = "PlayLink_QrShowWhileConnected";
    private const string KEY_FIREWALL_OPEN = "PlayLink_FirewallOpen";
    private const string KEY_USB_OPEN = "PlayLink_UsbOpen";

    private static Texture2D qrTexture;
    private static string qrText;
    private static int qrPixelsPerModule;
    private static PlayLinkQr.Code qrCode;
    private static string qrError;
    private static string savedPath;
    // 방화벽 상태를 이 에디터 세션에서 한 번이라도 읽었는가. SessionState 에 둡니다: 정적 필드면 재컴파일할 때마다 지워져
    // 창이 열려 있는 동안 리로드마다 PowerShell 을 띄웠습니다(코드 리뷰 m16). 다시 읽는 때는 [Start] 와 [다시 확인] 뿐입니다.
    private const string KEY_FIREWALL_ASKED = "PlayLink_FirewallAsked";
    private static GUIStyle bigButton;
    private static GUIStyle wrapLabel;
    private static GUIStyle stepLabel;

    // 창이 그리는 QR 과 같은 텍스처(검증용). 아직 그린 적이 없으면 null.
    public static Texture2D CurrentQrTexture { get { return qrTexture; } }
    public static PlayLinkQr.Code CurrentQrCode { get { return qrCode; } }

    public static void Draw(EditorWindow window)
    {
        EnsureStyles();
        EditorGUILayout.LabelField("폰으로 연결 (QR)", EditorStyles.boldLabel);
        bool armed = PlayLinkHost.Armed;
        DrawStartStop(armed);
        string error = PlayLinkHost.LastError;
        if (!string.IsNullOrEmpty(error))
        {
            EditorGUILayout.HelpBox(error, MessageType.Error);
        }

        if (!armed)
        {
            EditorGUILayout.LabelField("Start 를 누르면 QR 이 생깁니다. 폰 카메라로 그 QR 을 찍으면 연결됩니다(주소를 입력할 필요가 없습니다).", wrapLabel);
            ReleaseTexture();
            DrawAutoArm();
            return;
        }

        string status = PlayLinkHost.StatusText;
        if (!string.IsNullOrEmpty(status))
        {
            EditorGUILayout.LabelField("상태: " + status, wrapLabel);
        }

        List<PlayLinkSeats.Viewer> viewers = PlayLinkHost.Viewers;
        DrawViewers(viewers);
        DrawQr(window, viewers);
        DrawFirewall();
        DrawUsb();
        DrawAutoArm();
    }

    // 창이 닫히거나 도메인이 다시 로드되기 전에 텍스처를 지웁니다.
    public static void ReleaseTexture()
    {
        if (qrTexture != null)
        {
            UnityEngine.Object.DestroyImmediate(qrTexture);
        }

        qrTexture = null;
        qrText = null;
        qrCode = null;
        qrError = null;
    }

    private static void DrawStartStop(bool armed)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(armed))
            {
                if (GUILayout.Button(new GUIContent("Start", "연결을 받기 시작하고 QR 을 만듭니다. Play 를 누르지 않아도 됩니다."), bigButton))
                {
                    // Arm 이 방화벽 상태도 다시 읽습니다.
                    SessionState.SetBool(KEY_FIREWALL_ASKED, true);
                    PlayLinkHost.Arm();
                }
            }

            using (new EditorGUI.DisabledScope(!armed))
            {
                if (GUILayout.Button(new GUIContent("Stop", "연결을 모두 끊고 QR 을 닫습니다. USB 의 Chrome 페이지로 보내던 화면 전송도 함께 멈춥니다(다시 보려면 아래의 예전 Start)."), bigButton))
                {
                    PlayLinkHost.Disarm();
                }
            }
        }
    }

    private static void DrawViewers(List<PlayLinkSeats.Viewer> viewers)
    {
        if (viewers.Count == 0)
        {
            EditorGUILayout.LabelField("연결된 기기: 없음", EditorStyles.miniLabel);
            return;
        }

        EditorGUILayout.LabelField("연결된 기기", EditorStyles.miniBoldLabel);
        foreach (PlayLinkSeats.Viewer viewer in viewers)
        {
            string name = string.IsNullOrEmpty(viewer.deviceName) ? (viewer.group == "page" ? "Chrome 페이지" : "폰") : viewer.deviceName;
            string state;
            string mark;
            switch (viewer.state)
            {
                case "active":
                    mark = "●";
                    state = "화면을 보는 중";
                    break;
                case "standby":
                    mark = "○";
                    state = "기다리는 중(다른 기기가 보고 있음)";
                    break;
                case "lobby":
                    mark = "○";
                    state = "연결됨. Unity 에서 Play 를 누르면 화면이 나갑니다";
                    break;
                default:
                    mark = "○";
                    state = viewer.state ?? string.Empty;
                    break;
            }

            string path = viewer.usb ? "USB" : "Wi-Fi";
            string version = viewer.appVersion > 0 ? $", 앱 버전 {viewer.appVersion}" : string.Empty;
            EditorGUILayout.LabelField($"  {mark} {name} — {state} ({path}{version})", wrapLabel);
        }

        if (viewers.Count > 1)
        {
            EditorGUILayout.LabelField("  화면은 한 기기에만 나갑니다. 다른 기기에서 보려면 그 기기 화면의 [여기서 보기]를 누르세요.", EditorStyles.miniLabel);
        }
    }

    private static void DrawQr(EditorWindow window, List<PlayLinkSeats.Viewer> viewers)
    {
        string payload = PlayLinkHost.BuildQrPayload();
        if (string.IsNullOrEmpty(payload))
        {
            ReleaseTexture();
            if (string.IsNullOrEmpty(PlayLinkHost.LastError))
            {
                EditorGUILayout.HelpBox("QR 을 만들 수 없습니다. 이 PC 가 Wi-Fi 나 유선 랜(공유기)에 연결돼 있는지 확인하세요.", MessageType.Warning);
            }

            DrawAddressChoice();
            return;
        }

        bool appConnected = false;
        foreach (PlayLinkSeats.Viewer viewer in viewers)
        {
            appConnected |= viewer.group != "page";
        }

        // 폰이 이미 붙어 있으면 QR 을 접어 둡니다(화면을 지나가는 사람이 찍지 않게).
        if (appConnected && !SessionState.GetBool(KEY_SHOW_WHILE_CONNECTED, false))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("폰이 연결돼 있어 QR 을 접어 두었습니다.", EditorStyles.miniLabel);
                if (GUILayout.Button("QR 보기", GUILayout.Width(80)))
                {
                    SessionState.SetBool(KEY_SHOW_WHILE_CONNECTED, true);
                }
            }

            return;
        }

        bool large = SessionState.GetBool(KEY_LARGE, false);
        float pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
        // 창이 좁으면 QR 을 창 너비에 맞춥니다(모듈당 4픽셀 아래로는 줄이지 않음).
        float available = window != null ? window.position.width - 28f : QR_POINTS;
        float wanted = Mathf.Min(large ? QR_POINTS_LARGE : QR_POINTS, available);
        bool cramped = available < QR_POINTS;
        int targetPixels = Mathf.RoundToInt(wanted * pixelsPerPoint);
        EnsureTexture(payload, targetPixels);
        if (qrTexture == null)
        {
            EditorGUILayout.HelpBox("QR 을 만들지 못했습니다: " + qrError, MessageType.Error);
            return;
        }

        // 텍스처 1픽셀 = 화면 1픽셀이 되게 그립니다(모듈 경계가 번지지 않음).
        float points = qrTexture.width / pixelsPerPoint;
        bool sideBySide = !large && window != null && window.position.width >= points + 300;
        if (sideBySide)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawQrImage(points);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawQrHelp();
                }
            }
        }
        else
        {
            DrawQrImage(points);
            if (cramped)
            {
                EditorGUILayout.LabelField("창이 좁아 QR 을 작게 그렸습니다. 폰이 잘 못 읽으면 이 창을 넓혀 주세요.", EditorStyles.wordWrappedMiniLabel);
            }

            DrawQrHelp();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(large ? "QR 작게" : "QR 크게"))
            {
                SessionState.SetBool(KEY_LARGE, !large);
            }

            if (GUILayout.Button(new GUIContent("QR 이미지 저장", "지금 보이는 QR 을 PNG 파일로 저장합니다. 이 그림을 가진 사람은 누구나 접속할 수 있으니 함부로 보내지 마세요.")))
            {
                SaveQr();
            }

            if (GUILayout.Button(new GUIContent("새 QR 발급", "지금 QR 을 못 쓰게 만들고 새 QR 을 만듭니다. 연결돼 있던 폰은 끊기고 새 QR 을 다시 찍어야 합니다.")))
            {
                if (EditorUtility.DisplayDialog("새 QR 발급",
                    "지금 QR 은 더 이상 쓸 수 없게 되고, 연결돼 있던 폰은 모두 끊깁니다.\n폰에서 새 QR 을 다시 찍어야 합니다.\n\nQR 이 다른 사람에게 알려졌을 때 쓰세요.", "새로 발급", "취소"))
                {
                    PlayLinkHost.RenewToken();
                    savedPath = null;
                }
            }

            if (appConnected && GUILayout.Button("QR 접기"))
            {
                SessionState.SetBool(KEY_SHOW_WHILE_CONNECTED, false);
            }
        }

        if (!string.IsNullOrEmpty(savedPath))
        {
            EditorGUILayout.LabelField("저장함: " + savedPath, EditorStyles.miniLabel);
        }

        DrawAddressChoice();
        EditorGUILayout.HelpBox("이 QR 을 찍은 사람은 누구나 이 PC 의 게임 화면을 보고 조작할 수 있습니다. 회사나 집 Wi-Fi 에서만 쓰고, "
            + "카페 같은 공용 Wi-Fi 에서는 쓰지 마세요. QR 이 화면 공유나 사진으로 알려졌다면 [새 QR 발급]을 누르세요.", MessageType.None);
    }

    private static void DrawQrImage(float points)
    {
        Rect rect = GUILayoutUtility.GetRect(points, points, GUILayout.Width(points), GUILayout.Height(points));
        float pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
        rect.x = Mathf.Round(rect.x * pixelsPerPoint) / pixelsPerPoint;
        rect.y = Mathf.Round(rect.y * pixelsPerPoint) / pixelsPerPoint;
        rect.width = points;
        rect.height = points;
        if (Event.current.type == EventType.Repaint)
        {
            GUI.DrawTexture(rect, qrTexture, ScaleMode.StretchToFill, false);
        }
    }

    private static void DrawQrHelp()
    {
        EditorGUILayout.LabelField("① 폰과 이 PC 가 같은 Wi-Fi(같은 공유기)인지 확인합니다.", stepLabel);
        EditorGUILayout.LabelField("② 폰의 기본 카메라로 이 QR 을 비추고, 뜨는 링크를 누릅니다.", stepLabel);
        EditorGUILayout.LabelField("③ 처음이면 앱 설치 안내가 나옵니다. 설치한 뒤 [앱으로 열기]를 누릅니다.", stepLabel);
        EditorGUILayout.LabelField("④ 다음부터는 폰에서 앱만 열면 자동으로 다시 연결됩니다.", stepLabel);
        string version = PlayLinkHost.ApkExists
            ? $"설치될 앱: {PlayLinkHost.ApkVersionName} ({PlayLinkHost.ApkVersionCode})"
            : "설치용 앱 파일(APK)이 도구 폴더에 없습니다. QR 로 설치는 안 되고, 이미 앱이 깔린 폰의 연결만 됩니다.";
        EditorGUILayout.LabelField(version, EditorStyles.wordWrappedMiniLabel);
        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.LabelField("게임 화면은 Unity 에서 Play 를 누른 뒤에 나갑니다. 연결은 지금 해 두어도 됩니다.", EditorStyles.wordWrappedMiniLabel);
        }

        if (qrCode != null)
        {
            EditorGUILayout.LabelField($"QR 정보: 버전 {qrCode.version} ({qrCode.size}x{qrCode.size}), 오류 정정 {qrCode.errorCorrection}, 모듈당 {qrPixelsPerModule}px",
                EditorStyles.wordWrappedMiniLabel);
        }
    }

    // PC 에 네트워크가 여러 개일 때만 고르는 목록을 보입니다. 입력 칸은 없습니다.
    private static void DrawAddressChoice()
    {
        string[] choices = PlayLinkHost.SelectableAddresses;
        string primary = PlayLinkHost.PrimaryAddress;
        if (choices == null || choices.Length <= 1)
        {
            if (!string.IsNullOrEmpty(primary))
            {
                EditorGUILayout.LabelField($"이 PC 주소: {primary} (자동)", EditorStyles.miniLabel);
            }

            return;
        }

        string selected = PlayLinkHost.SelectedAddress ?? string.Empty;
        string[] labels = new string[choices.Length + 1];
        labels[0] = string.IsNullOrEmpty(primary) || !string.IsNullOrEmpty(selected) ? "자동으로 고르기" : $"자동으로 고르기 (지금 {primary})";
        int index = 0;
        for (int i = 0; i < choices.Length; i++)
        {
            labels[i + 1] = choices[i];
            if (choices[i] == selected)
            {
                index = i + 1;
            }
        }

        int picked = EditorGUILayout.Popup(new GUIContent("이 PC 주소",
            "이 PC 에 네트워크가 여러 개 있습니다. 폰에서 연결이 안 되면 폰과 같은 공유기에 연결된 쪽 주소를 골라 보세요. 고르면 QR 이 새로 그려집니다."), index, labels);
        if (picked != index)
        {
            PlayLinkHost.SelectedAddress = picked == 0 ? string.Empty : choices[picked - 1];
            savedPath = null;
        }
    }

    private static void DrawFirewall()
    {
        if (!SessionState.GetBool(KEY_FIREWALL_ASKED, false))
        {
            SessionState.SetBool(KEY_FIREWALL_ASKED, true);
            PlayLinkFirewall.RefreshAsync();
        }

        PlayLinkFirewall.Verdict state = PlayLinkFirewall.State;
        bool problem = state == PlayLinkFirewall.Verdict.Blocked || state == PlayLinkFirewall.Verdict.WillPrompt;
        bool open = EditorGUILayout.Foldout(SessionState.GetBool(KEY_FIREWALL_OPEN, false) || problem, problem ? "Windows 방화벽 (확인 필요)" : "Windows 방화벽", true);
        SessionState.SetBool(KEY_FIREWALL_OPEN, open);
        if (!open)
        {
            return;
        }

        string message = PlayLinkFirewall.Message;
        if (string.IsNullOrEmpty(message))
        {
            message = state == PlayLinkFirewall.Verdict.Checking ? "Windows 방화벽 상태를 확인하는 중입니다." : "Windows 방화벽 상태를 아직 확인하지 않았습니다.";
        }

        EditorGUILayout.HelpBox(message, state == PlayLinkFirewall.Verdict.Blocked ? MessageType.Warning : MessageType.Info);
        if (PlayLinkFirewall.IsPublicProfile)
        {
            // 코드 리뷰 M3-a: 어느 프로필에 규칙이 걸리는지 누르기 전에 알 수 있게 합니다.
            EditorGUILayout.LabelField("지금 네트워크는 Windows 에 '공용'으로 등록돼 있습니다. [방화벽 허용…]은 이 네트워크의 주소 대역에서 오는 연결만 엽니다. "
                + "믿는 네트워크(집, 사무실)라면 Windows 설정 > 네트워크 및 인터넷에서 프로필을 '개인'으로 바꾼 뒤 누르는 편이 좋습니다.", wrapLabel);
        }

        if (problem)
        {
            EditorGUILayout.LabelField("폰에서 QR 을 찍었는데 페이지가 열리지 않으면 대개 이 방화벽 때문입니다. [방화벽 허용…]을 누르면 무엇을 바꾸는지 먼저 보여 주고, "
                + "Windows 가 \"이 앱이 디바이스를 변경하도록 허용하시겠어요?\"라고 한 번 묻습니다. [예]를 누르면 끝납니다(이 PC 의 관리자 권한이 필요합니다).", wrapLabel);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("방화벽 허용…", "이 Unity 가 같은 네트워크의 폰에서 오는 연결을 받을 수 있게 Windows 방화벽에 규칙을 넣습니다.")))
            {
                string detail = PlayLinkFirewall.DescribeAllow();
                if (string.IsNullOrEmpty(detail))
                {
                    detail = "이 Unity 에디터가 같은 네트워크의 폰에서 오는 연결(이 도구의 포트 하나)을 받을 수 있게 Windows 방화벽 규칙을 넣습니다.";
                }

                if (EditorUtility.DisplayDialog("Windows 방화벽 허용",
                    detail + "\n\n[계속]을 누르면 Windows 가 \"이 앱이 디바이스를 변경하도록 허용하시겠어요?\" 창을 한 번 띄웁니다. 거기서 [예]를 눌러야 적용됩니다. "
                    + "나중에 [되돌리기]로 원래대로 돌릴 수 있습니다.", "계속", "취소"))
                {
                    PlayLinkFirewall.RequestAllow();
                }
            }

            using (new EditorGUI.DisabledScope(!PlayLinkFirewall.HasBackup))
            {
                if (GUILayout.Button(new GUIContent("되돌리기", "[방화벽 허용…]으로 넣은 규칙을 지우고 원래 규칙으로 되돌립니다. Windows 가 한 번 더 묻습니다.")))
                {
                    if (EditorUtility.DisplayDialog("Windows 방화벽 되돌리기",
                        "이 도구가 넣은 방화벽 규칙을 지우고 원래대로 되돌립니다.\n되돌리면 폰에서 Wi-Fi 로 연결할 수 없게 될 수 있습니다(USB 연결은 그대로 됩니다).\n\n"
                        + "Windows 가 \"이 앱이 디바이스를 변경하도록 허용하시겠어요?\" 창을 한 번 띄웁니다.", "되돌리기", "취소"))
                    {
                        PlayLinkFirewall.RequestRevert();
                    }
                }
            }

            if (GUILayout.Button(new GUIContent("다시 확인", "방화벽 상태를 다시 읽습니다. 아무것도 바꾸지 않습니다.")))
            {
                PlayLinkFirewall.RefreshAsync();
            }

            using (new EditorGUI.DisabledScope(PlayLinkAppAdb.Busy))
            {
                if (GUILayout.Button(new GUIContent("연결 점검", "USB 로 연결된 폰이 Wi-Fi 로 이 PC 에 실제로 닿는지 확인합니다(폰이 USB 로 꽂혀 있어야 합니다). 아무것도 바꾸지 않습니다.")))
                {
                    PlayLinkAppAdb.CheckReachAsync();
                }
            }
        }
    }

    private static void DrawUsb()
    {
        bool open = EditorGUILayout.Foldout(SessionState.GetBool(KEY_USB_OPEN, false), "QR 이 안 될 때: USB 케이블로 설치하고 연결", true);
        SessionState.SetBool(KEY_USB_OPEN, open);
        if (open)
        {
            EditorGUILayout.LabelField("폰을 USB 로 꽂고(USB 디버깅이 켜져 있어야 합니다) 아래 버튼을 누르세요. 방화벽을 바꾸지 않아도 됩니다.", wrapLabel);
            if (PlayLinkAppAdb.EmulatorOnly)
            {
                EditorGUILayout.HelpBox("시험 기간 설정: 이 구역의 버튼과 [연결 점검]은 지금 PC 의 안드로이드 가상 기기(에뮬레이터)에만 동작하고 실제 폰은 건드리지 않습니다. "
                    + "실제 폰에서 쓰려면 README 0장의 'USB 버튼을 실제 폰에 쓰기'를 보세요.", MessageType.Info);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(PlayLinkAppAdb.Busy))
                {
                    using (new EditorGUI.DisabledScope(!PlayLinkHost.ApkExists))
                    {
                        if (GUILayout.Button(new GUIContent("USB 로 앱 설치", "도구에 들어 있는 앱을 케이블로 폰에 설치합니다.")))
                        {
                            PlayLinkAppAdb.InstallAsync();
                        }
                    }

                    if (GUILayout.Button(new GUIContent("USB 로 연결", "케이블로 연결하고 폰에서 앱을 엽니다. Unity 가 Play 중이어야 화면이 나옵니다.")))
                    {
                        PlayLinkAppAdb.ConnectUsbAsync();
                    }
                }
            }
        }

        string output = PlayLinkAppAdb.LastOutput;
        if (!string.IsNullOrEmpty(output))
        {
            EditorGUILayout.HelpBox(output, MessageType.None);
        }
    }

    private static void DrawAutoArm()
    {
        bool auto = PlayLinkHost.AutoArm;
        bool picked = EditorGUILayout.ToggleLeft(new GUIContent("Unity 를 켤 때 자동으로 Start (이 프로젝트만)",
            "켜 두면 이 프로젝트를 열 때마다 Start 를 누른 상태로 시작합니다. 이 프로젝트의 UserSettings 폴더에 저장되고 다른 프로젝트에는 영향을 주지 않습니다."), auto);
        if (picked != auto)
        {
            PlayLinkHost.AutoArm = picked;
        }
    }

    private static void EnsureTexture(string payload, int targetPixels)
    {
        if (qrTexture != null && qrText == payload && qrCode != null
            && qrPixelsPerModule == PlayLinkQr.PixelsPerModule(qrCode.size, targetPixels))
        {
            return;
        }

        ReleaseTexture();
        try
        {
            qrCode = PlayLinkQr.Encode(payload);
            qrPixelsPerModule = PlayLinkQr.PixelsPerModule(qrCode.size, targetPixels);
            qrTexture = PlayLinkQr.CreateTexture(qrCode, qrPixelsPerModule);
            qrText = payload;
            savedPath = null;
        }
        catch (Exception exception)
        {
            // 내용(토큰 포함)은 적지 않습니다.
            qrError = exception.GetType().Name;
            qrCode = null;
            qrTexture = null;
        }
    }

    private static void SaveQr()
    {
        if (qrTexture == null)
        {
            return;
        }

        try
        {
            PlayLinkQr.SavePng(qrTexture, QR_PNG_PATH);
            savedPath = Path.GetFullPath(QR_PNG_PATH);
            EditorUtility.RevealInFinder(savedPath);
        }
        catch (Exception exception)
        {
            savedPath = "저장 실패(" + exception.GetType().Name + ")";
        }
    }

    private static void EnsureStyles()
    {
        if (bigButton != null)
        {
            return;
        }

        bigButton = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold, fixedHeight = 34 };
        wrapLabel = new GUIStyle(EditorStyles.label) { wordWrap = true };
        stepLabel = new GUIStyle(EditorStyles.label) { wordWrap = true, fontSize = 12 };
    }
}
