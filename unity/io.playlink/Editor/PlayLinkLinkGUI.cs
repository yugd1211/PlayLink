using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// PlayLink 창의 맨 위 구역: Start → 폰 목록에서 고르기 → 연결. 처음 보는 폰은 QR 이나 6자리 숫자 확인으로 페어링합니다.
// 연결 쪽 일은 전부 PlayLinkLink 에 맡기고 여기서는 그리기만 합니다. 키와 QR 비밀은 글자로 보이지 않습니다.
public static class PlayLinkLinkGUI
{
    // QR 한 변의 화면 크기(포인트). 모니터에서 30~50cm 떨어진 폰 카메라가 읽는 크기입니다.
    private const int QR_POINTS = 260;
    private const int QR_POINTS_LARGE = 480;
    private const int DOWNLOAD_QR_POINTS = 200;
    private const string KEY_LARGE = "PlayLink_QrLarge";
    private const string KEY_QR_OPEN = "PlayLink_PairQrOpen";
    private const string KEY_MANUAL_OPEN = "PlayLink_ManualOpen";
    private const string KEY_USB_OPEN = "PlayLink_UsbOpen";
    private const string KEY_DOWNLOAD_OPEN = "PlayLink_DownloadOpen";
    private const double USB_REFRESH_SECONDS = 5.0;

    // 그려 둔 QR 하나(내용이나 크기가 바뀌면 다시 만듦)
    private sealed class QrView
    {
        public Texture2D texture;
        public string text;
        public int pixelsPerModule;
        public PlayLinkQr.Code code;
        public string error;

        public void Release()
        {
            if (texture != null)
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            texture = null;
            text = null;
            code = null;
            error = null;
        }

        public void Ensure(string payload, int targetPixels)
        {
            if (texture != null && text == payload && code != null && pixelsPerModule == PlayLinkQr.PixelsPerModule(code.size, targetPixels))
            {
                return;
            }

            Release();
            try
            {
                code = PlayLinkQr.Encode(payload);
                pixelsPerModule = PlayLinkQr.PixelsPerModule(code.size, targetPixels);
                texture = PlayLinkQr.CreateTexture(code, pixelsPerModule);
                text = payload;
            }
            catch (Exception exception)
            {
                // 내용(비밀 포함)은 적지 않습니다.
                error = exception.GetType().Name;
                code = null;
                texture = null;
            }
        }
    }

    private static readonly QrView pairQr = new QrView();
    private static readonly QrView downloadQr = new QrView();
    private static string manualAddress;
    private static double nextUsbRefresh;
    private static GUIStyle bigButton;
    private static GUIStyle wrapLabel;
    private static GUIStyle digitsLabel;

    // 창이 그리는 페어링 QR 과 같은 텍스처(검증용). 아직 그린 적이 없으면 null.
    public static Texture2D CurrentQrTexture { get { return pairQr.texture; } }

    public static void Draw(EditorWindow window)
    {
        EnsureStyles();
        EditorGUILayout.LabelField("폰 연결", EditorStyles.boldLabel);
        bool armed = PlayLinkLink.Armed;
        DrawStartStop(armed);
        string storeError = PlayLinkIdentity.LastError;
        if (!string.IsNullOrEmpty(storeError))
        {
            EditorGUILayout.HelpBox(storeError, MessageType.Warning);
        }

        if (!armed)
        {
            EditorGUILayout.LabelField("Start 를 누르면 같은 Wi-Fi 에서 PlayLink 앱이 켜져 있는 폰을 찾습니다. 이 PC 의 방화벽 설정은 필요 없습니다.", wrapLabel);
            pairQr.Release();
            DrawDownload(window);
            DrawAutoArm();
            return;
        }

        EditorGUILayout.LabelField("상태: " + StatusText(), wrapLabel);
        string discoveryError = PlayLinkDiscovery.LastError;
        if (!string.IsNullOrEmpty(discoveryError))
        {
            EditorGUILayout.HelpBox(discoveryError, MessageType.Error);
        }

        DrawLinkProblem();
        DrawPairing();
        DrawPcName();
        List<PlayLinkDiscovery.Phone> seen = PlayLinkDiscovery.Snapshot();
        DrawPairedPhones(seen);
        DrawNewPhones(seen);
        string notice = PlayLinkLink.Notice;
        if (!string.IsNullOrEmpty(notice))
        {
            EditorGUILayout.HelpBox(notice, MessageType.None);
        }

        EditorGUILayout.Space();
        DrawPairQr(window);
        DrawManual();
        DrawUsb();
        DrawDownload(window);
        DrawAutoArm();
    }

    // 창이 닫히거나 도메인이 다시 로드되기 전에 텍스처를 지웁니다.
    public static void ReleaseTextures()
    {
        pairQr.Release();
        downloadQr.Release();
    }

    private static void DrawStartStop(bool armed)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(armed))
            {
                if (GUILayout.Button(new GUIContent("Start", "폰을 찾기 시작합니다. Play 를 누르지 않아도 됩니다."), bigButton))
                {
                    PlayLinkLink.Arm();
                }
            }

            using (new EditorGUI.DisabledScope(!armed))
            {
                if (GUILayout.Button(new GUIContent("Stop", "폰과의 연결을 끊고 폰 찾기를 멈춥니다."), bigButton))
                {
                    PlayLinkLink.Disarm();
                }
            }
        }
    }

    private static string TargetLabel()
    {
        string id = PlayLinkLink.TargetId;
        PlayLinkIdentity.Phone phone = id != null ? PlayLinkIdentity.Current.Find(id) : null;
        return phone != null ? phone.label : "폰";
    }

    private static string StatusText()
    {
        switch (PlayLinkLink.Current)
        {
            case PlayLinkLink.State.Connecting:
                return TargetLabel() + " 에 연결하는 중… (폰이 다른 PC 화면을 보고 있으면 폰에서 바꿀지 묻습니다)";
            case PlayLinkLink.State.Connected:
                if (!EditorApplication.isPlaying)
                {
                    return TargetLabel() + " 연결됨. Unity 에서 Play 를 누르면 화면이 나갑니다.";
                }

                return PlayLink.IsRunning ? TargetLabel() + " 에 화면을 보내는 중" : TargetLabel() + " 연결됨";
            case PlayLinkLink.State.Retrying:
                return TargetLabel() + " 연결이 끊겨 다시 연결하는 중…";
            case PlayLinkLink.State.Failed:
                return TargetLabel() + " 연결 안 됨";
            default:
                return PlayLinkIdentity.Current.phones.Length == 0
                    ? "페어링된 폰이 없습니다. 아래에서 새 폰을 페어링하세요."
                    : "폰을 찾는 중입니다. 아래 목록에서 연결할 폰을 고르세요.";
        }
    }

    // 다시 걸어도 소용없는 이유로 멈췄을 때: 이유와 할 수 있는 일
    private static void DrawLinkProblem()
    {
        if (PlayLinkLink.Current != PlayLinkLink.State.Failed)
        {
            return;
        }

        string code = PlayLinkLink.ErrorCode;
        EditorGUILayout.HelpBox(PlayLinkLink.ErrorMessage, code == "phone_closed" ? MessageType.Info : MessageType.Warning);
        using (new EditorGUILayout.HorizontalScope())
        {
            bool needsPairing = code == "unknown_pc" || code == "auth_failed" || code == "unknown_phone";
            if (needsPairing && GUILayout.Button(new GUIContent("다시 페어링", "이 폰과 6자리 숫자 확인으로 다시 페어링합니다. 폰에서 [허용]을 눌러야 합니다.")))
            {
                PlayLinkLink.RePairTarget();
            }

            if (!needsPairing && GUILayout.Button("다시 연결"))
            {
                PlayLinkLink.Reconnect();
            }

            if (GUILayout.Button("닫기"))
            {
                PlayLinkLink.Disconnect();
            }
        }
    }

    // 진행 중인 페어링: 6자리 숫자와 [확인]
    private static void DrawPairing()
    {
        PlayLinkLink.PairingInfo info = PlayLinkLink.Pairing;
        if (info == null)
        {
            return;
        }

        string name = string.IsNullOrEmpty(info.phoneName) ? "폰" : info.phoneName;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("페어링: " + name, EditorStyles.boldLabel);
            switch (info.stage)
            {
                case PlayLinkLink.PairStage.Dialing:
                    EditorGUILayout.LabelField(info.qr ? "QR 을 찍은 폰과 페어링하는 중…" : "폰에 접속하는 중…", wrapLabel);
                    break;
                case PlayLinkLink.PairStage.Digits:
                case PlayLinkLink.PairStage.WaitingPhone:
                    EditorGUILayout.LabelField("폰 화면에 같은 숫자가 보이는지 확인하세요.", wrapLabel);
                    EditorGUILayout.LabelField(info.digits ?? string.Empty, digitsLabel, GUILayout.Height(44));
                    EditorGUILayout.LabelField(info.stage == PlayLinkLink.PairStage.Digits
                        ? "숫자가 같으면 여기서 [확인]을 누르고, 폰에서 [허용]을 누르세요. 둘 다 눌러야 페어링됩니다. 숫자가 다르면 [취소]하세요."
                        : "여기서는 확인했습니다. 폰에서 [허용]을 누르세요.", wrapLabel);
                    break;
                case PlayLinkLink.PairStage.Done:
                    EditorGUILayout.LabelField("페어링했습니다. 연결합니다.", wrapLabel);
                    break;
                default:
                    EditorGUILayout.LabelField("페어링하지 못했습니다: " + info.error, wrapLabel);
                    break;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (info.stage == PlayLinkLink.PairStage.Digits && GUILayout.Button("확인", bigButton))
                {
                    PlayLinkLink.ConfirmPair();
                }

                bool finished = info.stage == PlayLinkLink.PairStage.Failed || info.stage == PlayLinkLink.PairStage.Done;
                if (GUILayout.Button(finished ? "닫기" : "취소", bigButton))
                {
                    PlayLinkLink.CancelPair();
                }
            }
        }
    }

    private static void DrawPcName()
    {
        PlayLinkIdentity.Snapshot identity = PlayLinkIdentity.Current;
        string name = EditorGUILayout.DelayedTextField(new GUIContent("이 PC 이름",
            "폰의 PC 목록에 보이는 이름입니다. 글자, 숫자, 공백, _-.() 만 쓸 수 있고 32자까지입니다. 이 PC 의 모든 프로젝트가 함께 씁니다."), identity.pcName);
        if (name != identity.pcName)
        {
            PlayLinkIdentity.SetPcName(name);
        }
    }

    private static void DrawPairedPhones(List<PlayLinkDiscovery.Phone> seen)
    {
        PlayLinkIdentity.Phone[] phones = PlayLinkIdentity.Current.phones;
        EditorGUILayout.LabelField("페어링된 폰", EditorStyles.miniBoldLabel);
        if (phones.Length == 0)
        {
            EditorGUILayout.LabelField("  아직 없습니다.", EditorStyles.miniLabel);
            return;
        }

        string targetId = PlayLinkLink.TargetId;
        PlayLinkLink.State state = PlayLinkLink.Current;
        foreach (PlayLinkIdentity.Phone phone in phones)
        {
            PlayLinkDiscovery.Phone wifi = seen.Find(p => p.verified && p.id == phone.id && !p.IsUsb);
            PlayLinkDiscovery.Phone usb = seen.Find(p => p.verified && p.id == phone.id && p.IsUsb);
            bool isTarget = targetId == phone.id && state != PlayLinkLink.State.Idle;
            using (new EditorGUILayout.HorizontalScope())
            {
                string mark = isTarget && state == PlayLinkLink.State.Connected ? "●" : wifi != null || usb != null ? "○" : "·";
                GUILayout.Label(mark, GUILayout.Width(14));
                string label = EditorGUILayout.DelayedTextField(phone.label, GUILayout.MinWidth(80), GUILayout.MaxWidth(160));
                if (label != phone.label)
                {
                    PlayLinkIdentity.SetPhoneLabel(phone.id, label);
                }

                if (isTarget)
                {
                    string how = PlayLinkLink.TargetIsUsb ? "USB" : "Wi-Fi " + PlayLinkLink.TargetHost;
                    PlayLinkPeer peer = PlayLinkLink.Peer;
                    string version = state == PlayLinkLink.State.Connected && peer != null && peer.appVersion > 0 ? ", 앱 버전 " + peer.appVersion : string.Empty;
                    string text = state == PlayLinkLink.State.Connected ? "연결됨" : state == PlayLinkLink.State.Failed ? "연결 안 됨" : "연결하는 중";
                    GUILayout.Label(text + " (" + how + version + ")", wrapLabel);
                    if (GUILayout.Button("끊기", GUILayout.Width(64)))
                    {
                        PlayLinkLink.Disconnect();
                    }
                }
                else if (wifi != null || usb != null)
                {
                    GUILayout.Label(wifi != null ? "앱이 켜져 있음 (Wi-Fi " + wifi.host + ")" : "앱이 켜져 있음 (USB)", wrapLabel);
                    if (wifi != null && GUILayout.Button("연결", GUILayout.Width(64)))
                    {
                        PlayLinkLink.Connect(wifi);
                    }

                    if (usb != null && GUILayout.Button("USB 로 연결", GUILayout.Width(90)))
                    {
                        PlayLinkLink.Connect(usb);
                    }
                }
                else
                {
                    GUILayout.Label("보이지 않음 (폰에서 앱을 켜세요)", wrapLabel);
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(phone.host)))
                    {
                        if (GUILayout.Button(new GUIContent("연결", "마지막으로 연결했던 주소(" + phone.host + ")로 걸어 봅니다."), GUILayout.Width(64)))
                        {
                            PlayLinkLink.ConnectLastAddress(phone);
                        }
                    }
                }

                if (GUILayout.Button(new GUIContent("페어링 해제", "이 PC 에서 이 폰을 지웁니다. 다시 쓰려면 다시 페어링해야 합니다."), GUILayout.Width(84)))
                {
                    if (EditorUtility.DisplayDialog("페어링 해제", phone.label + " 의 페어링을 지울까요?\n다시 연결하려면 QR 이나 숫자 확인으로 다시 페어링해야 합니다.\n폰의 PC 목록에서도 이 PC 를 지워 주세요.", "지우기", "취소"))
                    {
                        PlayLinkLink.Unpair(phone.id);
                    }

                    GUIUtility.ExitGUI();
                }
            }
        }

        EditorGUILayout.LabelField("  폰은 한 번에 하나만 연결됩니다. 다른 폰을 고르면 지금 연결은 끊깁니다.", EditorStyles.miniLabel);
    }

    private static void DrawNewPhones(List<PlayLinkDiscovery.Phone> seen)
    {
        EditorGUILayout.LabelField("새 폰 (같은 Wi-Fi 에서 앱이 켜져 있는 폰)", EditorStyles.miniBoldLabel);
        int shown = 0;
        bool pairing = PlayLinkLink.Pairing != null;
        foreach (PlayLinkDiscovery.Phone phone in seen)
        {
            // 증명이 맞는 응답이 함께 보이는 폰은 위 목록에 있습니다.
            if (phone.verified || seen.Exists(p => p.verified && p.id == phone.id))
            {
                continue;
            }

            shown++;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("○", GUILayout.Width(14));
                string name = string.IsNullOrEmpty(phone.name) ? "이름 없는 폰" : phone.name;
                bool known = PlayLinkIdentity.Current.Find(phone.id) != null;
                GUILayout.Label(name + " (" + (phone.IsUsb ? "USB" : phone.host) + ")" + (known ? " — 폰이 이 PC 를 기억하지 못합니다. 다시 페어링하세요." : string.Empty), wrapLabel);
                using (new EditorGUI.DisabledScope(pairing))
                {
                    if (GUILayout.Button(new GUIContent("페어링", "이 폰과 페어링합니다. 양쪽에 같은 6자리 숫자가 뜨면 여기서 [확인], 폰에서 [허용]을 누릅니다."), GUILayout.Width(64)))
                    {
                        PlayLinkLink.BeginPair(phone);
                    }
                }
            }
        }

        if (shown == 0)
        {
            EditorGUILayout.LabelField("  보이는 새 폰이 없습니다. 폰에서 PlayLink 앱을 켜 두세요(같은 Wi-Fi).", EditorStyles.miniLabel);
        }
        else
        {
            EditorGUILayout.LabelField("  같은 Wi-Fi 의 다른 기기가 폰인 척할 수 있습니다. 숫자가 폰 화면과 같은지 꼭 확인하세요.", EditorStyles.miniLabel);
        }
    }

    private static bool Foldout(string key, string title)
    {
        bool open = SessionState.GetBool(key, false);
        bool picked = EditorGUILayout.Foldout(open, title, true);
        if (picked != open)
        {
            SessionState.SetBool(key, picked);
        }

        return picked;
    }

    // 페어링 QR: 앱의 [QR 찍기]로 찍으면 숫자 확인 없이 페어링되고 바로 연결됩니다.
    private static void DrawPairQr(EditorWindow window)
    {
        if (!Foldout(KEY_QR_OPEN, "새 폰 페어링: QR 찍기"))
        {
            pairQr.Release();
            return;
        }

        bool large = SessionState.GetBool(KEY_LARGE, false);
        float points = DrawQr(window, pairQr, PlayLinkLink.BuildQrText(), large ? QR_POINTS_LARGE : QR_POINTS);
        if (points <= 0f)
        {
            return;
        }

        EditorGUILayout.LabelField("① 폰과 이 PC 가 같은 Wi-Fi(같은 공유기)인지 확인합니다.", wrapLabel);
        EditorGUILayout.LabelField("② 폰에서 PlayLink 앱을 열고 [QR 찍기]로 이 QR 을 찍습니다.", wrapLabel);
        EditorGUILayout.LabelField("③ 잠시 뒤 페어링되고 바로 연결됩니다. 다음부터는 목록에서 고르기만 하면 됩니다.", wrapLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(large ? "QR 작게" : "QR 크게"))
            {
                SessionState.SetBool(KEY_LARGE, !large);
            }

            if (GUILayout.Button(new GUIContent("새 QR", "지금 QR 을 못 쓰게 만들고 새 QR 을 만듭니다. 이미 페어링된 폰은 그대로입니다.")))
            {
                PlayLinkIdentity.RenewQrSecret();
            }
        }

        EditorGUILayout.LabelField("이 QR 은 한 번 쓰면 새로 바뀝니다. QR 을 찍은 사람은 이 PC 의 게임 화면을 보고 조작할 수 있으니 화면 공유나 사진으로 내보내지 마세요.",
            EditorStyles.wordWrappedMiniLabel);
    }

    // 브로드캐스트가 안 되는 망: 앱 대기 화면에 보이는 폰 주소를 직접 넣습니다.
    private static void DrawManual()
    {
        if (!Foldout(KEY_MANUAL_OPEN, "폰이 목록에 안 보일 때: 폰 주소 직접 입력"))
        {
            return;
        }

        EditorGUILayout.LabelField("회사 망처럼 자동으로 찾지 못하는 Wi-Fi 에서는 앱 대기 화면에 보이는 폰 주소를 넣으세요. 찾으면 위 목록에 나타납니다.", wrapLabel);
        if (manualAddress == null)
        {
            manualAddress = PlayLinkLink.ManualAddress;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            manualAddress = EditorGUILayout.TextField(new GUIContent("폰 주소", "예: 192.168.0.12 또는 192.168.0.12:7300"), manualAddress);
            if (GUILayout.Button("찾기", GUILayout.Width(50)))
            {
                PlayLinkLink.SetManualAddress(manualAddress);
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(PlayLinkLink.ManualAddress)))
            {
                if (GUILayout.Button("지우기", GUILayout.Width(56)))
                {
                    manualAddress = string.Empty;
                    PlayLinkLink.SetManualAddress(string.Empty);
                    GUI.FocusControl(null);
                }
            }
        }
    }

    private static void DrawUsb()
    {
        if (!Foldout(KEY_USB_OPEN, "USB 케이블로 연결"))
        {
            return;
        }

        EditorGUILayout.LabelField("Wi-Fi 로 안 될 때 케이블로 연결합니다. 폰의 USB 디버깅이 켜져 있어야 하고, 폰에서 PlayLink 앱을 켜 둔 채로 누르세요. "
            + "처음이면 6자리 숫자 확인으로 페어링한 뒤 연결됩니다.", wrapLabel);
        // 이 구역이 열려 있는 동안에만 기기 목록을 다시 읽습니다.
        if (EditorApplication.timeSinceStartup >= nextUsbRefresh)
        {
            nextUsbRefresh = EditorApplication.timeSinceStartup + USB_REFRESH_SECONDS;
            PlayLinkAdb.RefreshAsync();
        }

        PlayLinkAdb.Device[] devices = PlayLinkAdb.Devices;
        foreach (PlayLinkAdb.Device device in devices)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string kind = device.IsWireless ? "무선 adb" : "USB";
                GUILayout.Label("  " + device.serial + " (" + kind + (device.IsReady ? string.Empty : ", " + device.state) + ")", wrapLabel);
                using (new EditorGUI.DisabledScope(!device.IsReady || PlayLinkLink.Pairing != null))
                {
                    if (GUILayout.Button("USB 로 연결", GUILayout.Width(90)))
                    {
                        PlayLinkLink.ConnectUsb(device.serial);
                    }
                }
            }
        }

        string message = PlayLinkAdb.Message;
        if (!string.IsNullOrEmpty(message))
        {
            EditorGUILayout.LabelField("  " + message, EditorStyles.wordWrappedMiniLabel);
        }
    }

    // 앱 설치: 설정한 주소(기본 GitHub Releases)를 QR 로 보여 줍니다. Unity 가 파일을 내주지는 않습니다.
    private static void DrawDownload(EditorWindow window)
    {
        if (!Foldout(KEY_DOWNLOAD_OPEN, "앱 다운로드"))
        {
            downloadQr.Release();
            return;
        }

        EditorGUILayout.LabelField("폰에 PlayLink 앱이 없으면 폰의 기본 카메라로 이 QR 을 찍어 받으세요(인터넷 연결 필요).", wrapLabel);
        string url = PlayLink.AppDownloadUrl;
        DrawQr(window, downloadQr, url, DOWNLOAD_QR_POINTS);
        using (new EditorGUILayout.HorizontalScope())
        {
            string edited = EditorGUILayout.DelayedTextField(new GUIContent("주소", "앱을 받는 곳의 주소입니다. 이 PC 의 모든 프로젝트가 함께 씁니다."), url);
            if (edited != url)
            {
                PlayLink.AppDownloadUrl = edited;
            }

            if (GUILayout.Button("복사", GUILayout.Width(44)))
            {
                EditorGUIUtility.systemCopyBuffer = url;
            }

            using (new EditorGUI.DisabledScope(url == PlayLink.DEFAULT_APP_URL))
            {
                if (GUILayout.Button("기본값", GUILayout.Width(56)))
                {
                    PlayLink.AppDownloadUrl = PlayLink.DEFAULT_APP_URL;
                    GUI.FocusControl(null);
                }
            }
        }
    }

    // QR 을 그리고 그린 한 변의 크기(포인트)를 돌려줍니다. 만들지 못했으면 0.
    private static float DrawQr(EditorWindow window, QrView view, string payload, int wantedPoints)
    {
        float pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
        // 창이 좁으면 QR 을 창 너비에 맞춥니다(모듈당 4픽셀 아래로는 줄이지 않음).
        float available = window != null ? window.position.width - 28f : wantedPoints;
        view.Ensure(payload, Mathf.RoundToInt(Mathf.Min(wantedPoints, available) * pixelsPerPoint));
        if (view.texture == null)
        {
            EditorGUILayout.HelpBox("QR 을 만들지 못했습니다: " + view.error, MessageType.Error);
            return 0f;
        }

        // 텍스처 1픽셀 = 화면 1픽셀이 되게 그립니다(모듈 경계가 번지지 않음).
        float points = view.texture.width / pixelsPerPoint;
        Rect rect = GUILayoutUtility.GetRect(points, points, GUILayout.Width(points), GUILayout.Height(points));
        rect.x = Mathf.Round(rect.x * pixelsPerPoint) / pixelsPerPoint;
        rect.y = Mathf.Round(rect.y * pixelsPerPoint) / pixelsPerPoint;
        rect.width = points;
        rect.height = points;
        if (Event.current.type == EventType.Repaint)
        {
            GUI.DrawTexture(rect, view.texture, ScaleMode.StretchToFill, false);
        }

        if (available < wantedPoints)
        {
            EditorGUILayout.LabelField("창이 좁아 QR 을 작게 그렸습니다. 폰이 잘 못 읽으면 이 창을 넓혀 주세요.", EditorStyles.wordWrappedMiniLabel);
        }

        return points;
    }

    private static void DrawAutoArm()
    {
        bool auto = PlayLinkLink.AutoArm;
        bool picked = EditorGUILayout.ToggleLeft(new GUIContent("Unity 를 켤 때 자동으로 Start (이 프로젝트만)",
            "켜 두면 이 프로젝트를 열 때마다 Start 를 누른 상태로 시작합니다(폰 찾기만 켜고, 연결은 목록에서 골라야 합니다). 다른 프로젝트에는 영향을 주지 않습니다."), auto);
        if (picked != auto)
        {
            PlayLinkLink.AutoArm = picked;
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
        digitsLabel = new GUIStyle(EditorStyles.boldLabel) { fontSize = 34, alignment = TextAnchor.MiddleCenter };
    }
}
