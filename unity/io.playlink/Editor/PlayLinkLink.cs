using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using UnityEditor;
using UnityEngine;

// 폰과의 연결을 맡습니다. 앱(폰)이 기다리고 Unity 가 폰으로 접속합니다(PC 에서 나가는 연결이라 Windows 방화벽 설정이 필요 없음).
//   - 창의 [Start] 를 누른 동안 폰을 찾고(PlayLinkDiscovery), 사람이 목록에서 고른 폰 하나에 영상 · 입력 연결을 겁니다.
//     Unity 가 스스로 거는 경우는 둘뿐입니다: 페어링된 폰이 "연결해 달라"(want)고 했을 때, 폰이 이 PC 의 QR 을 찍었을 때.
//   - 폰은 한 번에 하나입니다. 다른 폰을 고르면 지금 연결을 끊습니다.
//   - 연결은 Play 시작/종료와 도메인 리로드를 넘어 유지됩니다("Start 켜짐"과 고른 폰을 SessionState 에 두고 리로드 뒤 다시 겁니다).
//     Play 밖에서는 폰이 "Unity 에서 Play 를 누르면 화면이 나옵니다"로 기다립니다(PlayLinkPeer).
//   - 사람이 끊지 않았는데 끊기면 간격을 늘려 가며 다시 겁니다.
// 스레드: 접속과 키 교환은 연결 스레드(연결 시도마다 하나), 페어링은 페어링 스레드에서 합니다. 창이 부르는 함수와 SessionState,
// PlayLink.Start/Stop 은 메인 스레드에서만 다루고, 다른 스레드는 Post 로 넘깁니다.
[InitializeOnLoad]
public static class PlayLinkLink
{
    private const string SESSION_ARMED = "PlayLink_Armed";
    private const string SESSION_BOOTED = "PlayLink_Booted";
    private const string SESSION_TARGET_ID = "PlayLink_TargetId";
    private const string SESSION_TARGET_HOST = "PlayLink_TargetHost";
    private const string SESSION_TARGET_PORT = "PlayLink_TargetPort";
    private const string SESSION_TARGET_USB = "PlayLink_TargetUsb";
    private const string PREF_MANUAL_ADDRESS = "PlayLink_ManualAddress";
    private const string LOOPBACK = "127.0.0.1";
    private const int RETRY_FIRST_MS = 500;
    private const int RETRY_MAX_MS = 8000;
    // 이보다 빨리 끊긴 연결은 실패로 셉니다(붙자마자 끊기는 것을 쉬지 않고 되풀이하지 않게).
    private const double SHORT_LINK_MS = 5000.0;
    // 페어링 직후 폰이 키를 저장할 틈
    private const int AFTER_PAIR_DELAY_MS = 200;

    public enum State
    {
        // 고른 폰이 없음
        Idle,
        Connecting,
        Connected,
        // 끊겨서 다시 걸기를 기다리는 중
        Retrying,
        // 다시 걸어도 소용없는 이유로 멈춤(ErrorCode)
        Failed,
    }

    public enum PairStage
    {
        Dialing,
        // 양쪽에 같은 숫자가 떠 있고 사람의 확인을 기다림
        Digits,
        // PC 에서는 확인했고 폰의 [허용]을 기다림
        WaitingPhone,
        Done,
        Failed,
    }

    // 진행 중이거나 방금 끝난 페어링 하나(창 표시용)
    public sealed class PairingInfo
    {
        public string host;
        public string phoneName;
        public bool qr;
        public volatile string digits;
        public volatile PairStage stage;
        public volatile string error;
        internal volatile int decision;
        internal volatile bool cancelled;
        internal volatile TcpClient client;
    }

    private sealed class Target
    {
        public string phoneId;
        public string host;
        public int port;
        public string usbSerial;
        public bool justPaired;
    }

    private static readonly ConcurrentQueue<Action> mainQueue = new ConcurrentQueue<Action>();
    private static readonly List<TcpClient> dialing = new List<TcpClient>();
    private static volatile bool changedPending;
    private static volatile bool armed;
    private static volatile bool playing;
    private static volatile int generation;
    private static volatile Target target;
    private static volatile State state = State.Idle;
    private static volatile string errorCode;
    private static volatile string errorMessage;
    private static volatile PlayLinkPeer peer;
    private static volatile PairingInfo pairing;
    private static volatile string notice;
    // PlayLink 가 화면을 보내고 있는 연결(메인 스레드)
    private static PlayLinkPeer streamingPeer;

    #region 창이 쓰는 API (메인 스레드)

    public static event Action Changed;

    // "Start 켜짐"
    public static bool Armed { get { return armed; } }
    public static State Current { get { return state; } }
    // Failed 의 이유: unknown_pc, busy, auth_failed, version, proto, bad_message, phone_closed, unknown_phone
    public static string ErrorCode { get { return errorCode; } }
    public static string ErrorMessage { get { return errorMessage; } }
    // 고른 폰의 phoneId(없으면 null)
    public static string TargetId { get { Target t = target; return t != null ? t.phoneId : null; } }
    public static bool TargetIsUsb { get { Target t = target; return t != null && t.usbSerial != null; } }
    public static string TargetHost { get { Target t = target; return t != null ? t.host : null; } }
    public static PlayLinkPeer Peer { get { return peer; } }
    public static PairingInfo Pairing { get { return pairing; } }
    // 직접 입력한 주소나 USB 로 찾은 결과 한 줄(없으면 null)
    public static string Notice { get { return notice; } }

    public static bool AutoArm
    {
        get { return PlayLinkSettings.AutoArm; }
        set { PlayLinkSettings.AutoArm = value; }
    }

    // 직접 입력한 폰 주소(ip[:port]). 이 PC 의 모든 프로젝트가 함께 씁니다.
    public static string ManualAddress
    {
        get { return EditorPrefs.GetString(PREF_MANUAL_ADDRESS, string.Empty); }
    }

    // 창의 [Start]
    public static void Arm()
    {
        armed = true;
        SessionState.SetBool(SESSION_ARMED, true);
        notice = null;
        ApplyManualProbe();
        PlayLinkDiscovery.Start();
        NoteChanged();
    }

    // 창의 [Stop]: 연결을 끊고 폰 찾기를 멈춥니다.
    public static void Disarm()
    {
        armed = false;
        SessionState.SetBool(SESSION_ARMED, false);
        CancelPair();
        pairing = null;
        Disconnect();
        PlayLinkDiscovery.Stop();
        NoteChanged();
    }

    // 목록에서 폰을 고름(페어링된 폰). 다른 폰과 연결돼 있으면 끊고 이 폰에 겁니다.
    public static void Connect(PlayLinkDiscovery.Phone phone)
    {
        Connect(phone.id, phone.host, phone.port, phone.usbSerial, true, false);
    }

    // 지금 꺼져 있는(목록에 안 보이는) 페어링된 폰에 마지막 주소로 걸어 봅니다.
    public static void ConnectLastAddress(PlayLinkIdentity.Phone phone)
    {
        if (!string.IsNullOrEmpty(phone.host))
        {
            Connect(phone.id, phone.host, phone.port, null, true, false);
        }
    }

    // 실패한 뒤 [다시 연결]
    public static void Reconnect()
    {
        Target t = target;
        if (t != null)
        {
            Connect(t.phoneId, t.host, t.port, t.usbSerial, true, false);
        }
    }

    // 창의 [끊기]. 고른 폰도 지웁니다(다시 걸지 않음).
    public static void Disconnect()
    {
        generation++;
        AbortDialing();
        PlayLinkPeer closing = peer;
        peer = null;
        if (closing != null)
        {
            closing.Close(PlayLinkPeer.CLOSE_NORMAL);
        }

        StopStreaming(closing);
        SetTarget(null);
        SetState(State.Idle, null, null);
    }

    // 페어링을 지웁니다. 그 폰과 연결돼 있으면 끊습니다. 폰 쪽 기억은 폰에서 지워야 합니다.
    public static void Unpair(string phoneId)
    {
        if (TargetId == phoneId)
        {
            Disconnect();
        }

        PlayLinkIdentity.RemovePhone(phoneId);
        NoteChanged();
    }

    // "새 폰"을 고름: 6자리 숫자 확인으로 페어링을 시작합니다. 끝나면 그 폰에 연결합니다.
    public static void BeginPair(PlayLinkDiscovery.Phone phone)
    {
        StartPair(phone.host, phone.port, phone.usbSerial, phone.name, null);
    }

    // 연결이 unknown_pc / auth_failed 로 멈췄을 때 [다시 페어링]: 같은 주소에 숫자 확인으로 다시 페어링합니다.
    public static void RePairTarget()
    {
        Target t = target;
        if (t == null)
        {
            return;
        }

        PlayLinkIdentity.Phone known = PlayLinkIdentity.Current.Find(t.phoneId);
        Disconnect();
        StartPair(t.host, t.port, t.usbSerial, known != null ? known.label : null, null);
    }

    // 창의 [확인]: PC 에 보이는 숫자가 폰과 같다고 사람이 확인함
    public static void ConfirmPair()
    {
        PairingInfo info = pairing;
        if (info != null && info.stage == PairStage.Digits)
        {
            info.decision = 1;
            info.stage = PairStage.WaitingPhone;
            NoteChanged();
        }
    }

    public static void CancelPair()
    {
        PairingInfo info = pairing;
        if (info == null || info.stage == PairStage.Done || info.stage == PairStage.Failed)
        {
            pairing = null;
            NoteChanged();
            return;
        }

        info.decision = -1;
        info.cancelled = true;
        // 접속 중이거나 폰의 답을 기다리는 중이면 소켓을 닫아 바로 끝냅니다(숫자 확인 중이면 거절을 보낸 뒤 스레드가 닫음).
        if (info.stage == PairStage.Dialing)
        {
            TcpClient client = info.client;
            if (client != null)
            {
                client.Close();
            }
        }

        NoteChanged();
    }

    // 직접 입력한 폰 주소(ip[:port])를 정하고 바로 물어봅니다. 빈 값이면 지웁니다.
    public static void SetManualAddress(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length > 0 && !ParseAddress(text, out string _, out int _))
        {
            notice = "주소 형식이 올바르지 않습니다. 예: 192.168.0.12 또는 192.168.0.12:7300";
            NoteChanged();
            return;
        }

        EditorPrefs.SetString(PREF_MANUAL_ADDRESS, text);
        ApplyManualProbe();
        if (text.Length == 0)
        {
            notice = null;
            NoteChanged();
            return;
        }

        ParseAddress(text, out string host, out int port);
        notice = host + " 에 묻는 중…";
        NoteChanged();
        new Thread(() => AskAddress(host, port, null, false)) { IsBackground = true, Name = "PlayLink Who" }.Start();
    }

    // USB 기기에 연결: adb forward 로 폰의 포트를 끌어온 뒤 묻고, 페어링돼 있으면 연결하고 아니면 숫자 확인으로 페어링합니다.
    public static void ConnectUsb(string serial)
    {
        notice = "USB 기기(" + serial + ")에 묻는 중…";
        NoteChanged();
        new Thread(() =>
        {
            int port = PlayLinkAdb.Forward(serial, PlayLinkProto.DEFAULT_PORT, out string error);
            if (port <= 0)
            {
                notice = error;
                NoteChanged();
                return;
            }

            PlayLinkDiscovery.SetProbe(LOOPBACK, port, serial);
            AskAddress(LOOPBACK, port, serial, true);
        }) { IsBackground = true, Name = "PlayLink USB" }.Start();
    }

    // 창에 그리는 QR 의 내용(비밀이 들어 있으니 글자로 내보내지 않음)
    public static string BuildQrText()
    {
        return PlayLinkProto.BuildQrText(PlayLinkIdentity.Current);
    }

    #endregion

    #region 다른 스레드가 부르는 것

    // 메인 스레드에서 할 일을 넘깁니다.
    internal static void Post(Action action)
    {
        mainQueue.Enqueue(action);
        changedPending = true;
    }

    // 창을 다시 그리게 합니다(아무 스레드).
    public static void NoteChanged()
    {
        changedPending = true;
    }

    // 이 주소에 걸고 있거나 연결돼 있는가(PlayLinkDiscovery 가 그 주소에는 따로 묻지 않음)
    internal static bool IsLinkedTo(string host, int port)
    {
        Target t = target;
        State now = state;
        return t != null && t.host == host && t.port == port && (now == State.Connected || now == State.Connecting);
    }

    // 폰이 탐색(또는 who)에 답함(탐색 스레드). wantRose: 페어링된 폰이 방금 "연결해 달라"로 바뀜.
    internal static void OnPhoneSeen(PlayLinkDiscovery.Phone phone, PlayLinkProto.ReplyKind kind, bool wantRose)
    {
        changedPending = true;
        if (kind == PlayLinkProto.ReplyKind.Qr)
        {
            // 폰이 이 PC 의 QR 을 찍음 → QR 비밀로 페어링하고 연결합니다. 비밀은 한 번 쓰면 바뀌므로 같은 응답으로 되풀이되지 않습니다.
            Post(() =>
            {
                if (armed && !IsPairing)
                {
                    StartPair(phone.host, phone.port, phone.usbSerial, phone.name, PlayLinkIdentity.Current.qrSecret);
                }
            });
        }
        else if (kind == PlayLinkProto.ReplyKind.Paired && wantRose)
        {
            Post(() =>
            {
                Target t = target;
                bool already = t != null && t.phoneId == phone.id && state != State.Failed && state != State.Idle;
                if (armed && !already && !IsPairing)
                {
                    Connect(phone);
                }
            });
        }
    }

    #endregion

    #region 수명

    static PlayLinkLink()
    {
        PlayLinkSettings.Load();
        playing = EditorApplication.isPlayingOrWillChangePlaymode;
        PlayLinkIdentity.onChanged = NoteChanged;
        EditorApplication.update += Pump;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.quitting += OnQuitting;

        // 에디터를 켠 뒤 첫 로드에서만 "자동으로 Start" 설정을 봅니다.
        if (!SessionState.GetBool(SESSION_BOOTED, false))
        {
            SessionState.SetBool(SESSION_BOOTED, true);
            if (PlayLinkSettings.AutoArm)
            {
                SessionState.SetBool(SESSION_ARMED, true);
            }
        }

        armed = SessionState.GetBool(SESSION_ARMED, false);
        if (!armed)
        {
            return;
        }

        string id = SessionState.GetString(SESSION_TARGET_ID, string.Empty);
        if (id.Length > 0)
        {
            string usb = SessionState.GetString(SESSION_TARGET_USB, string.Empty);
            target = new Target
            {
                phoneId = id,
                host = SessionState.GetString(SESSION_TARGET_HOST, string.Empty),
                port = SessionState.GetInt(SESSION_TARGET_PORT, PlayLinkProto.DEFAULT_PORT),
                usbSerial = usb.Length > 0 ? usb : null,
            };
        }

        // 리로드 뒤: 폰 찾기를 다시 켜고, 고른 폰이 있으면 다시 겁니다(사람이 방금 시작한 것이 아니므로 take 없이).
        EditorApplication.delayCall += () =>
        {
            if (!armed)
            {
                return;
            }

            ApplyManualProbe();
            PlayLinkDiscovery.Start();
            Target t = target;
            if (t != null)
            {
                if (t.usbSerial != null)
                {
                    PlayLinkDiscovery.SetProbe(LOOPBACK, t.port, t.usbSerial);
                }

                Connect(t.phoneId, t.host, t.port, t.usbSerial, false, false);
            }
        };
    }

    private static void OnBeforeAssemblyReload()
    {
        // 고른 폰은 SessionState 에 남아 있어 리로드 뒤 다시 겁니다. 폰에는 "곧 다시 겁니다"(4410)로 닫습니다.
        Shutdown(PlayLinkPeer.CLOSE_RECONNECT);
    }

    private static void OnQuitting()
    {
        Shutdown(PlayLinkPeer.CLOSE_NORMAL);
    }

    private static void Shutdown(ushort closeCode)
    {
        generation++;
        AbortDialing();
        CancelPair();
        PlayLinkPeer closing = peer;
        peer = null;
        StopStreaming(closing);
        if (closing != null)
        {
            closing.Close(closeCode);
        }

        PlayLinkDiscovery.Stop();
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.EnteredPlayMode)
        {
            playing = true;
            StartStreaming(peer);
        }
        else if (change == PlayModeStateChange.ExitingPlayMode)
        {
            playing = false;
            StopStreaming(streamingPeer);
        }
        else if (change == PlayModeStateChange.EnteredEditMode)
        {
            playing = false;
        }

        NoteChanged();
    }

    // 메인 스레드: Play 중이고 연결이 살아 있으면 화면을 보내기 시작합니다.
    private static void StartStreaming(PlayLinkPeer made)
    {
        if (made == null || !made.IsOpen || !EditorApplication.isPlaying || streamingPeer == made)
        {
            return;
        }

        if (streamingPeer != null)
        {
            PlayLink.Stop();
        }

        streamingPeer = made;
        PlayLink.Start(made);
    }

    private static void StopStreaming(PlayLinkPeer made)
    {
        if (made != null && streamingPeer == made)
        {
            streamingPeer = null;
            PlayLink.Stop();
        }
    }

    // 메인 스레드(EditorApplication.update): 다른 스레드가 넘긴 일을 하고, 바뀐 것이 있으면 창에 알립니다.
    private static void Pump()
    {
        while (mainQueue.TryDequeue(out Action action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{PlayLink.LOG_PREFIX} link: {e}");
            }
        }

        if (changedPending)
        {
            changedPending = false;
            Changed?.Invoke();
        }
    }

    private static void ApplyManualProbe()
    {
        if (ParseAddress(ManualAddress, out string host, out int port))
        {
            PlayLinkDiscovery.SetProbe(host, port, null);
        }
        else
        {
            PlayLinkDiscovery.SetProbe(null, 0, null);
        }
    }

    // "ip" 또는 "ip:port"(이름도 됨). 포트를 생략하면 7300.
    private static bool ParseAddress(string text, out string host, out int port)
    {
        host = (text ?? string.Empty).Trim();
        port = PlayLinkProto.DEFAULT_PORT;
        int colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(host.Substring(colon + 1), out port) || port < 1 || port > 65535)
            {
                return false;
            }

            host = host.Substring(0, colon);
        }

        if (host.Length == 0 || host.Length > 253)
        {
            return false;
        }

        foreach (char c in host)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region 연결 (연결 스레드)

    private static bool IsPairing
    {
        get
        {
            PairingInfo info = pairing;
            return info != null && info.stage != PairStage.Done && info.stage != PairStage.Failed;
        }
    }

    // 메인 스레드. take: 사람이 방금 시작한 연결인가(자동 재접속이면 false)
    private static void Connect(string phoneId, string host, int port, string usbSerial, bool take, bool justPaired)
    {
        if (!armed)
        {
            return;
        }

        int mine = ++generation;
        AbortDialing();
        PlayLinkPeer closing = peer;
        peer = null;
        if (closing != null)
        {
            closing.Close(PlayLinkPeer.CLOSE_NORMAL);
        }

        StopStreaming(closing);
        Target next = new Target { phoneId = phoneId, host = host, port = port, usbSerial = usbSerial, justPaired = justPaired };
        SetTarget(next);
        SetState(State.Connecting, null, null);
        new Thread(() => Run(next, mine, take)) { IsBackground = true, Name = "PlayLink Link" }.Start();
    }

    private static void SetTarget(Target next)
    {
        target = next;
        SessionState.SetString(SESSION_TARGET_ID, next != null ? next.phoneId : string.Empty);
        SessionState.SetString(SESSION_TARGET_HOST, next != null ? next.host : string.Empty);
        SessionState.SetInt(SESSION_TARGET_PORT, next != null ? next.port : 0);
        SessionState.SetString(SESSION_TARGET_USB, next != null && next.usbSerial != null ? next.usbSerial : string.Empty);
    }

    private static void SetState(State next, string code, string message)
    {
        errorCode = code;
        errorMessage = message;
        state = next;
        changedPending = true;
    }

    // 고른 폰 하나에 대한 연결 시도와 다시 걸기. generation 이 바뀌면(끊기, 다른 폰 고름, 리로드) 끝납니다.
    private static void Run(Target t, int mine, bool take)
    {
        int failures = 0;
        while (generation == mine)
        {
            SetState(State.Connecting, null, null);
            PlayLinkPeer made = null;
            try
            {
                made = Dial(t, take);
            }
            catch (PlayLinkProto.ProtoException e)
            {
                if (generation != mine)
                {
                    return;
                }

                if (e.code == "unknown_pc" && t.justPaired)
                {
                    // 페어링 직후 폰이 아직 키를 저장하지 못했을 수 있어 한 번은 다시 겁니다.
                    t.justPaired = false;
                }
                else if (e.code != PlayLinkProto.ERROR_NETWORK)
                {
                    SetState(State.Failed, e.code, Describe(e.code, e.Message));
                    return;
                }
            }
            catch (Exception)
            {
                // 그 밖의 실패는 네트워크 실패처럼 다시 겁니다.
            }

            if (generation != mine)
            {
                if (made != null)
                {
                    made.Close(PlayLinkPeer.CLOSE_NORMAL);
                }

                return;
            }

            if (made != null)
            {
                long since = System.Diagnostics.Stopwatch.GetTimestamp();
                peer = made;
                SetState(State.Connected, null, null);
                PlayLinkPeer ready = made;
                Post(() =>
                {
                    if (generation == mine)
                    {
                        StartStreaming(ready);
                    }
                });
                made.WaitClosed();
                Post(() => StopStreaming(ready));
                if (generation != mine)
                {
                    return;
                }

                peer = null;
                if (made.RemoteCloseCode == PlayLinkPeer.CLOSE_NORMAL || made.RemoteCloseCode == PlayLinkPeer.CLOSE_GOING_AWAY)
                {
                    // 폰에서 사람이 끊었습니다(또는 앱을 닫음). 다시 걸지 않습니다: 그 뒤 폰은 사람이 시작하지 않은 연결(take:false)을 busy 로 거절합니다.
                    // 목록에서 다시 고르거나 폰에서 이 PC 를 고르면(want) 연결됩니다.
                    SetState(State.Failed, "phone_closed", Describe("phone_closed", null));
                    return;
                }

                failures = PlayLink.ElapsedMs(since) < SHORT_LINK_MS ? failures + 1 : 0;
            }
            else
            {
                failures++;
            }

            take = false;
            SetState(State.Retrying, null, null);
            int wait = Math.Min(RETRY_MAX_MS, RETRY_FIRST_MS << Math.Min(failures, 5));
            for (int waited = 0; waited < wait && generation == mine; waited += 100)
            {
                Thread.Sleep(100);
            }

            // 폰의 주소가 바뀌었을 수 있습니다(Wi-Fi 를 옮김): 탐색에 보이는 지금 주소로 겁니다.
            PlayLinkDiscovery.Phone seen = PlayLinkDiscovery.FindVerified(t.phoneId, t.usbSerial);
            if (seen != null && t.usbSerial == null)
            {
                t.host = seen.host;
                t.port = seen.port;
            }
        }
    }

    // 영상 연결과 입력 연결을 차례로 겁니다. 실패하면 ProtoException.
    private static PlayLinkPeer Dial(Target t, bool take)
    {
        PlayLinkIdentity.Snapshot identity = PlayLinkIdentity.Current;
        PlayLinkIdentity.Phone phone = identity.Find(t.phoneId);
        if (phone == null)
        {
            throw new PlayLinkProto.ProtoException("unknown_phone", null);
        }

        if (t.usbSerial != null)
        {
            int local = PlayLinkAdb.Forward(t.usbSerial, PlayLinkProto.DEFAULT_PORT, out string error);
            if (local <= 0)
            {
                throw new PlayLinkProto.ProtoException(PlayLinkProto.ERROR_NETWORK, error);
            }

            t.host = LOOPBACK;
            t.port = local;
        }

        PlayLinkProto.Session video = Open(t.host, t.port, identity, PlayLinkProto.CHANNEL_VIDEO, phone.key, take);
        PlayLinkProto.Session input;
        try
        {
            input = Open(t.host, t.port, identity, PlayLinkProto.CHANNEL_INPUT, phone.key, take);
        }
        catch (Exception)
        {
            video.stream.Dispose();
            video.client.Close();
            throw;
        }

        if (t.usbSerial == null)
        {
            PlayLinkIdentity.SetPhoneAddress(t.phoneId, t.host, t.port);
        }

        PlayLinkPeer made = new PlayLinkPeer(video, input);
        made.Start(!playing);
        return made;
    }

    private static PlayLinkProto.Session Open(string host, int port, PlayLinkIdentity.Snapshot identity, string channel, byte[] key, bool take)
    {
        TcpClient client = PlayLinkProto.Connect(host, port, PlayLinkProto.CONNECT_TIMEOUT_MS);
        lock (dialing)
        {
            dialing.Add(client);
        }

        try
        {
            return PlayLinkProto.Handshake(client, identity, channel, PlayLinkProto.MODE_PAIRED, key, take, null, null);
        }
        finally
        {
            lock (dialing)
            {
                dialing.Remove(client);
            }
        }
    }

    // 걸고 있는 소켓을 닫아 연결 스레드가 바로 끝나게 합니다(폰이 "바꿀까요?"를 묻는 동안 40초까지 기다릴 수 있음).
    private static void AbortDialing()
    {
        lock (dialing)
        {
            foreach (TcpClient client in dialing)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
            }

            dialing.Clear();
        }
    }

    private static string Describe(string code, string fallback)
    {
        switch (code)
        {
            case "unknown_pc":
                return "폰이 이 PC 를 기억하지 못합니다(앱을 다시 설치했거나 폰에서 이 PC 를 지움). 다시 페어링하세요.";
            case "unknown_phone":
                return "이 폰은 페어링되어 있지 않습니다. 다시 페어링하세요.";
            case "auth_failed":
                return "PC 와 폰의 페어링 정보가 서로 다릅니다. 다시 페어링하세요.";
            case "busy":
                return "폰이 다른 PC 화면을 보는 중이거나, 폰에서 연결을 끊었거나 바꾸기를 거절했습니다. [다시 연결]을 누르면 폰에 다시 묻습니다.";
            case "version":
            case "proto":
                return "앱과 Unity 도구의 버전이 다릅니다. 둘 다 최신으로 맞춰 주세요.";
            case "rejected":
                return "폰에서 거절했습니다.";
            case "timeout":
                return "확인을 기다리다 시간이 지났습니다. 다시 시도하세요.";
            case "cancelled":
                return "취소했습니다.";
            case "phone_closed":
                return "폰에서 연결을 끊었습니다.";
            case "network":
                return string.IsNullOrEmpty(fallback) || fallback == code ? "폰에 접속하지 못했습니다. 폰에서 앱이 켜져 있고 같은 Wi-Fi 인지 확인하세요." : fallback;
            default:
                return "연결하지 못했습니다(" + code + ").";
        }
    }

    #endregion

    #region 페어링 (페어링 스레드)

    // 메인 스레드. qrSecret 이 있으면 QR 방식(숫자 확인 없음), 없으면 숫자 확인(sas)
    private static void StartPair(string host, int port, string usbSerial, string phoneName, byte[] qrSecret)
    {
        if (!armed || IsPairing)
        {
            return;
        }

        PairingInfo info = new PairingInfo { host = host, phoneName = phoneName, qr = qrSecret != null, stage = PairStage.Dialing };
        pairing = info;
        NoteChanged();
        new Thread(() => RunPair(info, host, port, usbSerial, qrSecret)) { IsBackground = true, Name = "PlayLink Pair" }.Start();
    }

    private static void RunPair(PairingInfo info, string host, int port, string usbSerial, byte[] qrSecret)
    {
        try
        {
            PlayLinkIdentity.Snapshot identity = PlayLinkIdentity.Current;
            TcpClient client = PlayLinkProto.Connect(host, port, PlayLinkProto.CONNECT_TIMEOUT_MS);
            info.client = client;
            if (info.cancelled)
            {
                client.Close();
                throw new PlayLinkProto.ProtoException(PlayLinkProto.ERROR_CANCELLED, null);
            }

            PlayLinkProto.SasPrompt prompt = new PlayLinkProto.SasPrompt
            {
                onDigits = (digits, phoneId, name) =>
                {
                    info.digits = digits;
                    info.phoneName = name;
                    info.stage = PairStage.Digits;
                    NoteChanged();
                },
                decision = () => info.decision,
            };
            PlayLinkProto.Session session = PlayLinkProto.Handshake(client, identity, PlayLinkProto.CHANNEL_PAIR,
                qrSecret != null ? PlayLinkProto.MODE_QR : PlayLinkProto.MODE_SAS, qrSecret, true, prompt, () => info.cancelled);
            // fin 을 주고받았습니다: 양쪽이 같은 키를 저장하고 이 연결은 닫습니다.
            session.stream.Dispose();
            session.client.Close();
            PlayLinkIdentity.SavePhone(session.phoneId, session.phoneName, session.pairKey, usbSerial == null ? host : string.Empty, usbSerial == null ? port : 0);
            info.phoneName = session.phoneName;
            info.stage = PairStage.Done;
            string phoneId = session.phoneId;
            Thread.Sleep(AFTER_PAIR_DELAY_MS);
            Post(() =>
            {
                if (pairing == info)
                {
                    pairing = null;
                }

                Connect(phoneId, host, port, usbSerial, true, true);
            });
        }
        catch (PlayLinkProto.ProtoException e)
        {
            // 숫자 확인 페어링은 앱이 대기 화면에 있고 다른 창이 떠 있지 않을 때만 받습니다(아니면 busy).
            info.error = info.cancelled ? Describe("cancelled", null)
                : e.code == "busy" && qrSecret == null ? "폰이 지금 페어링을 받을 수 없습니다. 폰에서 PlayLink 앱의 대기 화면을 열어 두고(다른 창이 떠 있으면 닫고) 다시 시도하세요."
                : Describe(e.code, e.Message);
            info.stage = PairStage.Failed;
        }
        catch (Exception e)
        {
            info.error = "페어링하지 못했습니다(" + e.GetType().Name + ").";
            info.stage = PairStage.Failed;
        }
        finally
        {
            // QR 비밀은 한 번 쓰면(성공이든 실패든) 새로 만듭니다. 창의 QR 도 새로 그려집니다.
            if (qrSecret != null)
            {
                PlayLinkIdentity.RenewQrSecret();
            }

            NoteChanged();
        }
    }

    // 직접 입력한 주소나 USB 의 포트에 묻습니다(who). connect 가 true 면(USB 버튼) 답에 따라 바로 연결하거나 페어링을 시작합니다.
    private static void AskAddress(string host, int port, string usbSerial, bool connect)
    {
        try
        {
            PlayLinkProto.Message reply = PlayLinkProto.Who(host, port, PlayLinkIdentity.Current, out PlayLinkProto.ReplyKind kind);
            if (kind == PlayLinkProto.ReplyKind.Bad)
            {
                notice = "응답을 확인하지 못했습니다(PlayLink 앱이 아니거나 페어링 정보가 다름). 폰에서 이 PC 를 지운 뒤 다시 페어링하세요.";
                return;
            }

            PlayLinkDiscovery.Report(reply, kind, host, port, usbSerial, true);
            string name = PlayLinkCrypto.CleanName(reply.name);
            notice = "찾았습니다: " + name + (kind == PlayLinkProto.ReplyKind.Paired ? " (페어링된 폰)" : " (새 폰)");
            if (connect && kind != PlayLinkProto.ReplyKind.Qr)
            {
                PlayLinkDiscovery.Phone phone = new PlayLinkDiscovery.Phone
                {
                    id = reply.ph,
                    name = name,
                    host = host,
                    port = port,
                    usbSerial = usbSerial,
                    verified = kind == PlayLinkProto.ReplyKind.Paired,
                };
                Post(() =>
                {
                    if (phone.verified)
                    {
                        Connect(phone);
                    }
                    else
                    {
                        BeginPair(phone);
                    }
                });
            }
        }
        catch (PlayLinkProto.ProtoException e)
        {
            notice = usbSerial != null
                ? "USB 기기(" + usbSerial + ")에서 PlayLink 앱이 응답하지 않습니다. 폰에서 앱을 켜 두세요."
                : Describe(e.code, e.Message);
        }
        finally
        {
            NoteChanged();
        }
    }

    #endregion
}
