using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

// 앞단(설계 9.1). 창의 [Start] 를 누른 동안 QR 에 넣은 주소(같은 Wi-Fi 의 LAN 주소)에서 듣고,
// 인증을 마친 앱 연결만 기존 서버(GameViewRemoteServer, 127.0.0.1, Play 중에만)에 넘깁니다.
//   GET /c, /apk, /app.json            착륙 페이지와 APK(GameViewRemoteFiles)
//   /video, /input + Upgrade            GameViewRemoteGate 로 인증 → Play 중이면 서버에 넘김, 아니면 대기실
//   /x/<이름> + Upgrade                 등록된 채널이 있으면 인증 뒤 넘김(시제품에는 없음 → 404)
//   그 밖(/, /stats, /debug 포함)        404. 인증 헤더 없는 업그레이드는 401
// Play 밖에서도 열려 있어서 앱이 "Unity 에서 Play 를 누르면 화면이 나옵니다" 상태로 기다릴 수 있고(대기실),
// Play 가 시작되면 대기실 연결을 4410 으로 닫아 앱이 곧바로 다시 붙게 합니다.
// "Start 켜짐"은 SessionState 에 있어 도메인 리로드를 넘어 유지되고, 리로드 뒤 같은 포트와 토큰으로 다시 엽니다.
// 이 클래스는 Windows 방화벽을 건드리지 않습니다(이 PC에서는 Unity.exe 인바운드가 막혀 있어, 허용 전에는 폰에서 닿지 않음: GameViewRemoteFirewall).
[InitializeOnLoad]
public static class GameViewRemoteHost
{
    private const string SESSION_ARMED = "GVR_HostArmed";
    private const string SESSION_BOOTED = "GVR_HostBooted";
    private const string SESSION_PREFERRED = "GVR_SeatPreferred";
    private const string WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MAX_HEADER_BYTES = 16 * 1024;
    private const int MAX_CLIENT_MESSAGE_BYTES = 64 * 1024;
    // 인증을 마치지 않은 연결(HTTP GET 포함)의 동시 상한. 넘으면 받자마자 닫습니다(리뷰 M4).
    private const int MAX_PRE_AUTH_CONNECTIONS = 16;
    // 대기실(인증은 됐지만 Play 밖)에 둘 수 있는 연결 수
    private const int MAX_LOBBY_CONNECTIONS = 16;
    // 원격 주소 하나가 차지할 수 있는 인증 전 연결 수. 한 기기가 전체 한도를 혼자 채워 다른 폰의 QR 연결·착륙 페이지·APK 를 막지 못하게 합니다(코드 리뷰 m2).
    // 브라우저가 착륙 페이지를 열 때 동시에 여는 연결(페이지, app.json, 아이콘, APK)과 앱의 소켓 2개를 넘는 값입니다.
    private const int MAX_PRE_AUTH_PER_ADDRESS = 6;
    // 포트 설정이 바뀌었거나 리스너를 열지 못한 상태면 이 간격으로 다시 엽니다(코드 리뷰 m17).
    private const double RETRY_SECONDS = 3.0;
    private const string LOBBY_MESSAGE_STOPPED = "Unity 에서 화면 전송이 멈춰 있습니다. Game View Remote 창에서 다시 시작하세요.";
    private const int ADDRESS_WATCH_MS = 5000;
    private const ushort CLOSE_NORMAL = 1000;
    private const string LOBBY_MESSAGE = "Unity 에서 Play 를 누르면 화면이 나옵니다.";

    private sealed class LobbyConnection
    {
        public TcpClient client;
        public NetworkStream stream;
        public GameViewRemoteSession session;
        public GameViewRemoteServer.ChannelKind kind;
    }

    [Serializable]
    private class PingMessage
    {
        public string t;
        public double ts;
    }

    private static readonly object stateLock = new object();
    private static readonly List<TcpListener> listeners = new List<TcpListener>();
    private static readonly List<LobbyConnection> lobby = new List<LobbyConnection>();
    private static readonly ConcurrentQueue<Action> mainQueue = new ConcurrentQueue<Action>();
    private static volatile bool listening;
    private static volatile int generation;
    private static volatile int port;
    private static volatile GameViewRemoteNet.Choice choice = new GameViewRemoteNet.Choice();
    private static volatile string lastError;
    private static volatile string phoneWifiAddress;
    private static volatile bool changedPending;
    private static int preAuthConnections;
    private static readonly Dictionary<string, int> preAuthByAddress = new Dictionary<string, int>();
    // 메인 스레드가 적어 두는 값(네트워크 스레드는 EditorApplication 을 읽지 못함)
    private static volatile bool editorPlaying;
    private static double nextRetryTime;
    private static int revision;
    private static ManualResetEvent watchStop;

    #region 창이 쓰는 API (addendum 1장)

    public static event Action Changed;

    // "Start 켜짐". 메인 스레드 전용
    public static bool Armed { get { return SessionState.GetBool(SESSION_ARMED, false); } }
    public static bool IsListening { get { return listening; } }
    public static string LastError { get { return lastError; } }
    public static int Port { get { return listening ? port : GameViewRemote.Port; } }
    public static string PrimaryAddress { get { return listening ? choice.primary : null; } }
    public static string[] ExtraAddresses { get { return listening ? (string[])choice.extras.Clone() : new string[0]; } }
    public static string[] SelectableAddresses { get { return (string[])choice.selectable.Clone(); } }
    public static int Revision { get { return Volatile.Read(ref revision); } }
    public static bool ApkExists { get { return GameViewRemoteFiles.ApkExists; } }
    public static string ApkPath { get { return GameViewRemoteFiles.ApkPath; } }
    public static int ApkVersionCode { get { return GameViewRemoteFiles.ApkVersionCode; } }
    public static string ApkVersionName { get { return GameViewRemoteFiles.ApkVersionName; } }

    public static string ServerId
    {
        get
        {
            GameViewRemoteSettings.SecretSnapshot secret = GameViewRemoteSettings.Secret;
            return secret != null ? secret.serverId : null;
        }
    }

    public static string SelectedAddress
    {
        get { return GameViewRemoteSettings.HostAddress; }
        set
        {
            if (GameViewRemoteSettings.HostAddress == (value ?? string.Empty))
            {
                return;
            }

            GameViewRemoteSettings.HostAddress = value;
            if (Armed)
            {
                Reopen();
            }
        }
    }

    public static bool AutoArm
    {
        get { return GameViewRemoteSettings.AutoArm; }
        set { GameViewRemoteSettings.AutoArm = value; }
    }

    // adb 로 읽은 폰 wlan0 주소(GameViewRemoteAppAdb 가 넣음). 같은 서브넷의 어댑터를 주 주소로 고르는 데 씁니다.
    public static string PhoneWifiAddress
    {
        get { return phoneWifiAddress; }
        set { phoneWifiAddress = value; }
    }

    // 창의 [Start]
    public static void Arm()
    {
        SessionState.SetBool(SESSION_ARMED, true);
        GameViewRemoteSettings.EnsureSecret();
        Open();
        if (EditorApplication.isPlaying)
        {
            GameViewRemote.Start();
        }

        GameViewRemoteFirewall.RefreshAsync();
    }

    // 창의 [Stop]
    public static void Disarm()
    {
        SessionState.SetBool(SESSION_ARMED, false);
        CloseListeners(CLOSE_NORMAL);
        lastError = null;
        GameViewRemoteSeats.ClearPreferred();
        SessionState.EraseString(SESSION_PREFERRED);
        GameViewRemote.Stop();
        Bump();
    }

    // 창의 [새 QR 발급]: 예전 QR 과 앱이 기억한 토큰이 더 이상 통하지 않습니다.
    public static void RenewToken()
    {
        GameViewRemoteSettings.RenewToken();
        KickLobby(GameViewRemoteGate.CLOSE_TOKEN_REVOKED);
        GameViewRemote.CloseAppClients(GameViewRemoteGate.CLOSE_TOKEN_REVOKED);
        GameViewRemoteSeats.ClearPreferred();
        Bump();
    }

    // 설계 4.1: http://<주 주소>:<포트>/c#v=1&s=<서버 표시>&t=<토큰>[&a=<다른 주소>,…]. 듣고 있지 않으면 null.
    // 토큰이 들어 있으므로 로그나 창의 글자로 내보내지 않습니다.
    public static string BuildQrPayload()
    {
        GameViewRemoteSettings.SecretSnapshot secret = GameViewRemoteSettings.Secret;
        GameViewRemoteNet.Choice current = choice;
        if (!listening || secret == null || current.primary == null)
        {
            return null;
        }

        // QR 내용은 한 곳(GameViewRemoteQr.BuildPayload)에서만 만듭니다: 형식 검사와 자체 점검이 실제 경로에 그대로 걸립니다(코드 리뷰 m20).
        string payload = GameViewRemoteQr.BuildPayload(current.primary, port, secret.serverId, secret.token, current.extras, out string error);
        if (payload == null)
        {
            lastError = "QR 을 만들지 못했습니다: " + error;
        }

        return payload;
    }

    // 설계 4.2: seeplay-gvr://<호스트>:<포트>/c?v=1&s=…&t=…[&a=…][&via=usb][&take=1]. 토큰이 없으면 null.
    public static string BuildDeepLink(string host, bool usb, bool take)
    {
        GameViewRemoteSettings.SecretSnapshot secret = GameViewRemoteSettings.Secret;
        if (secret == null || string.IsNullOrEmpty(host))
        {
            return null;
        }

        List<string> extras = new List<string>();
        if (!usb)
        {
            GameViewRemoteNet.Choice current = choice;
            if (current.primary != null && current.primary != host)
            {
                extras.Add(current.primary);
            }

            foreach (string address in current.extras)
            {
                if (address != host && extras.Count < GameViewRemoteNet.MAX_EXTRA_ADDRESSES)
                {
                    extras.Add(address);
                }
            }
        }

        return GameViewRemoteAppInfo.SCHEME + "://" + host + ":" + Port + "/c?" + LinkQuery(secret, extras.ToArray())
            + (usb ? "&via=usb" : string.Empty) + (take ? "&take=1" : string.Empty);
    }

    public static string StatusText
    {
        get
        {
            if (!Armed)
            {
                return "꺼짐";
            }

            if (!listening)
            {
                return lastError ?? "여는 중";
            }

            List<GameViewRemoteSeats.Viewer> viewers = Viewers;
            List<string> apps = new List<string>();
            foreach (GameViewRemoteSeats.Viewer viewer in viewers)
            {
                if (viewer.group == GameViewRemoteSeats.PAGE_GROUP)
                {
                    continue;
                }

                string state = viewer.state == "active" ? "보는 중" : viewer.state == "standby" ? "대기" : "Play 대기";
                apps.Add((string.IsNullOrEmpty(viewer.deviceName) ? "앱" : viewer.deviceName) + " " + (viewer.usb ? "USB" : "무선") + ", " + state);
            }

            if (apps.Count > 0)
            {
                return "연결됨: " + string.Join(" / ", apps);
            }

            return EditorApplication.isPlaying ? "대기 중(QR 을 찍으면 연결됩니다)" : "대기 중(Play 를 누르면 화면이 나갑니다)";
        }
    }

    // 접속 기기 목록(복사본): 기존 서버에 붙은 무리 + 대기실
    public static List<GameViewRemoteSeats.Viewer> Viewers
    {
        get
        {
            List<GameViewRemoteSeats.Viewer> result = GameViewRemoteSeats.Snapshot();
            lock (stateLock)
            {
                foreach (LobbyConnection waiting in lobby)
                {
                    string group = GameViewRemoteSeats.GroupOf(waiting.session);
                    int index = result.FindIndex(viewer => viewer.group == group);
                    GameViewRemoteSeats.Viewer viewer = index >= 0 ? result[index] : new GameViewRemoteSeats.Viewer
                    {
                        group = group,
                        deviceName = waiting.session.deviceName,
                        remote = waiting.session.remote,
                        usb = waiting.session.viaLoopback,
                        appVersion = waiting.session.appVersion,
                        state = "lobby",
                    };
                    if (waiting.kind == GameViewRemoteServer.ChannelKind.Video)
                    {
                        viewer.video = true;
                    }
                    else
                    {
                        viewer.input = true;
                    }

                    if (index >= 0)
                    {
                        result[index] = viewer;
                    }
                    else
                    {
                        result.Add(viewer);
                    }
                }
            }

            return result;
        }
    }

    #endregion

    #region 수명

    static GameViewRemoteHost()
    {
        GameViewRemoteSettings.Load();
        editorPlaying = EditorApplication.isPlaying;
        ConfigureFiles();
        // 누가 보고 있었는지는 리로드와 Play 재시작을 넘어 유지합니다(리뷰 M3).
        GameViewRemoteSeats.Preferred = SessionState.GetString(SESSION_PREFERRED, string.Empty);
        GameViewRemoteSeats.onChanged = NoteChangedFromAnyThread;
        EditorApplication.update += Pump;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.quitting += OnQuitting;

        // 에디터를 켠 뒤 첫 로드에서만 "자동으로 열기" 설정을 봅니다.
        if (!SessionState.GetBool(SESSION_BOOTED, false))
        {
            SessionState.SetBool(SESSION_BOOTED, true);
            if (GameViewRemoteSettings.AutoArm)
            {
                SessionState.SetBool(SESSION_ARMED, true);
            }
        }

        if (Armed)
        {
            EditorApplication.delayCall += () =>
            {
                if (!Armed || listening)
                {
                    return;
                }

                GameViewRemoteSettings.EnsureSecret();
                Open();
                if (EditorApplication.isPlaying)
                {
                    GameViewRemote.Start();
                }
            };
        }
    }

    // 도구 폴더를 이 스크립트의 위치에서 찾습니다(폴더 경로를 글자로 적지 않음: 다른 프로젝트로 폴더째 옮겨도 됨).
    private static void ConfigureFiles()
    {
        // 컴파일할 때의 이 파일 위치를 먼저 씁니다(AssetDatabase 가 준비되지 않은 때에도 됨, 코드 리뷰 m14).
        string compiled = CompiledFolder();
        if (compiled != null && File.Exists(Path.Combine(compiled, GameViewRemoteAppInfo.LANDING_FILE)))
        {
            GameViewRemoteFiles.Configure(compiled, GameViewRemoteSettings.ApkPath);
            return;
        }

        string folder = null;
        try
        {
            foreach (string guid in AssetDatabase.FindAssets(nameof(GameViewRemoteHost) + " t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == nameof(GameViewRemoteHost) + ".cs")
                {
                    folder = Path.GetDirectoryName(Path.GetFullPath(path));
                    break;
                }
            }
        }
        catch (Exception)
        {
            folder = null;
        }

        if (folder == null)
        {
            // 에디터를 막 켠 직후에는 AssetDatabase 가 아직 준비되지 않았을 수 있어 한 번 더 시도합니다.
            if (GameViewRemoteFiles.ToolFolder == null)
            {
                GameViewRemoteFiles.Configure(null, GameViewRemoteSettings.ApkPath);
                EditorApplication.delayCall += RetryConfigureFiles;
            }

            return;
        }

        GameViewRemoteFiles.Configure(folder, GameViewRemoteSettings.ApkPath);
    }

    private static string CompiledFolder([System.Runtime.CompilerServices.CallerFilePath] string file = null)
    {
        try
        {
            return string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(Path.GetFullPath(file));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void RetryConfigureFiles()
    {
        foreach (string guid in AssetDatabase.FindAssets(nameof(GameViewRemoteHost) + " t:MonoScript"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileName(path) == nameof(GameViewRemoteHost) + ".cs")
            {
                GameViewRemoteFiles.Configure(Path.GetDirectoryName(Path.GetFullPath(path)), GameViewRemoteSettings.ApkPath);
                return;
            }
        }
    }

    private static void OnBeforeAssemblyReload()
    {
        SessionState.SetString(SESSION_PREFERRED, GameViewRemoteSeats.Preferred ?? string.Empty);
        CloseListeners(CLOSE_NORMAL);
    }

    private static void OnQuitting()
    {
        CloseListeners(CLOSE_NORMAL);
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        editorPlaying = state == PlayModeStateChange.EnteredPlayMode;
        if (state == PlayModeStateChange.EnteredPlayMode && Armed)
        {
            // 기존 서버를 열고(이미 돌고 있으면 그대로), 대기실의 앱을 다시 붙게 합니다.
            GameViewRemote.Start();
            OnServerStarted();
        }

        NoteChangedFromAnyThread();
    }

    // 기존 서버가 열린 직후(GameViewRemote.Start 끝, 메인 스레드). 대기실의 앱에 "다시 붙으세요"(4410)를 보냅니다.
    public static void OnServerStarted()
    {
        if (GameViewRemote.IsRunning)
        {
            KickLobby(GameViewRemoteGate.CLOSE_RECONNECT);
        }
    }

    // 메인 스레드. QR 에 넣는 주소마다 리스너를 엽니다(QR 에 넣은 주소 = 듣는 주소).
    private static void Open()
    {
        CloseListeners(GameViewRemoteGate.CLOSE_RECONNECT);
        lastError = null;
        int targetPort = GameViewRemote.Port;
        GameViewRemoteNet.Choice next = GameViewRemoteNet.Choose(GameViewRemoteSettings.HostAddress, phoneWifiAddress);
        if (next.primary == null)
        {
            choice = next;
            lastError = "같은 Wi-Fi 로 쓸 수 있는 PC 주소가 없습니다(Wi-Fi 나 랜 연결을 확인하세요).";
            StartWatch();
            Bump();
            return;
        }

        List<TcpListener> opened = new List<TcpListener>();
        List<string> extras = new List<string>();
        try
        {
            opened.Add(Listen(next.primary, targetPort));
        }
        catch (Exception e)
        {
            choice = next;
            lastError = $"{next.primary}:{targetPort} 을 열지 못했습니다: {e.Message}";
            Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} host: {lastError}");
            StartWatch();
            Bump();
            return;
        }

        foreach (string address in next.extras)
        {
            try
            {
                opened.Add(Listen(address, targetPort));
                extras.Add(address);
            }
            catch (Exception e)
            {
                // 열지 못한 후보는 QR 에서 뺍니다.
                Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} host: {address}:{targetPort} 을 열지 못해 QR 에서 뺍니다: {e.Message}");
            }
        }

        next.extras = extras.ToArray();
        int current;
        lock (stateLock)
        {
            listeners.AddRange(opened);
            current = ++generation;
        }

        choice = next;
        port = targetPort;
        listening = true;
        foreach (TcpListener listener in opened)
        {
            TcpListener target = listener;
            new Thread(() => AcceptLoop(target, current)) { IsBackground = true, Name = "GVR Host Accept" }.Start();
        }

        StartWatch();
        Debug.Log($"{GameViewRemote.LOG_PREFIX} host listening on {next.primary}:{targetPort}"
            + (extras.Count > 0 ? " (+" + string.Join(", ", extras) + ")" : string.Empty) + " (인증된 앱 연결만 받음)");
        Bump();
    }

    private static TcpListener Listen(string address, int targetPort)
    {
        // ReuseAddress 를 켜지 않습니다(Windows 에서는 다른 프로세스가 같은 주소를 겹쳐 열 수 있게 됨).
        // 기존 서버는 127.0.0.1, 여기는 LAN 주소라 같은 포트여도 겹치지 않습니다.
        TcpListener listener = new TcpListener(IPAddress.Parse(address), targetPort);
        listener.Start();
        return listener;
    }

    private static void Reopen()
    {
        if (Armed)
        {
            Open();
        }
    }

    // 리스너와 대기실을 닫습니다. 기존 서버에 넘긴 연결은 그 서버가 닫습니다.
    private static void CloseListeners(ushort lobbyCloseCode)
    {
        List<TcpListener> closing;
        lock (stateLock)
        {
            closing = new List<TcpListener>(listeners);
            listeners.Clear();
            generation++;
        }

        listening = false;
        StopWatch();
        foreach (TcpListener listener in closing)
        {
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
            }
        }

        KickLobby(lobbyCloseCode);
    }

    private static void KickLobby(ushort code)
    {
        List<LobbyConnection> kicked;
        lock (stateLock)
        {
            kicked = new List<LobbyConnection>(lobby);
            lobby.Clear();
        }

        foreach (LobbyConnection waiting in kicked)
        {
            // 메인 스레드에서 불립니다: 사라진 상대의 소켓에 쓰다가 막히지 않게 기다리지 않는 닫기를 씁니다(코드 리뷰 M1).
            GameViewRemoteWs.TryWriteCloseNoWait(waiting.client, waiting.stream, code);
            try
            {
                waiting.client.Close();
            }
            catch (Exception)
            {
            }
        }

        if (kicked.Count > 0)
        {
            NoteChangedFromAnyThread();
        }
    }

    // 어댑터가 바뀌었는지(Wi-Fi 를 옮김, 랜선을 뽑음) 5초마다 봅니다. 바뀌면 리스너를 다시 열고 QR 을 다시 그리게 합니다.
    private static void StartWatch()
    {
        StopWatch();
        ManualResetEvent stop = new ManualResetEvent(false);
        watchStop = stop;
        new Thread(() =>
        {
            while (!stop.WaitOne(ADDRESS_WATCH_MS))
            {
                GameViewRemoteNet.Choice now = GameViewRemoteNet.Choose(GameViewRemoteSettings.HostAddress, phoneWifiAddress);
                GameViewRemoteNet.Choice current = choice;
                bool same = now.primary == current.primary && string.Join(",", now.selectable) == string.Join(",", current.selectable);
                if (!same)
                {
                    mainQueue.Enqueue(Reopen);
                    return;
                }
            }
        }) { IsBackground = true, Name = "GVR Host Watch" }.Start();
    }

    private static void StopWatch()
    {
        ManualResetEvent stop = watchStop;
        watchStop = null;
        if (stop != null)
        {
            stop.Set();
        }
    }

    // 아무 스레드에서: 메인 스레드에서 할 일을 넘깁니다(null 이면 창 다시 그리기만).
    internal static void Post(Action action)
    {
        if (action != null)
        {
            mainQueue.Enqueue(action);
        }

        changedPending = true;
    }

    private static void Bump()
    {
        Interlocked.Increment(ref revision);
        changedPending = true;
    }

    private static void NoteChangedFromAnyThread()
    {
        changedPending = true;
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
                Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} host: {e}");
            }
        }

        // 포트 설정이 바뀌었거나(기존 서버는 새 포트로 열림) 리스너를 열지 못한 채면 다시 엽니다(코드 리뷰 m17).
        if (EditorApplication.timeSinceStartup >= nextRetryTime)
        {
            nextRetryTime = EditorApplication.timeSinceStartup + RETRY_SECONDS;
            if (Armed && ((listening && port != GameViewRemote.Port) || (!listening && lastError != null && choice.primary != null)))
            {
                Open();
            }
        }

        if (changedPending)
        {
            changedPending = false;
            Changed?.Invoke();
        }
    }

    private static string LinkQuery(GameViewRemoteSettings.SecretSnapshot secret, string[] extras)
    {
        return "v=" + GameViewRemoteAppInfo.QR_VERSION + "&s=" + secret.serverId + "&t=" + secret.token
            + (extras != null && extras.Length > 0 ? "&a=" + string.Join(",", extras) : string.Empty);
    }

    #endregion

    #region 연결 처리 (백그라운드 스레드)

    private static void AcceptLoop(TcpListener listener, int myGeneration)
    {
        while (generation == myGeneration)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception e)
            {
                // 리스너를 닫아서 난 예외면(세대가 바뀜) 조용히 끝냅니다. 아니면 리스너가 죽은 것이므로 다시 엽니다:
                // 그대로 두면 QR 은 보이는데 아무도 붙지 못합니다(코드 리뷰 m1).
                if (generation == myGeneration)
                {
                    Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} host: 리스너가 멈춰 다시 엽니다: {e.Message}");
                    Post(Reopen);
                }

                break;
            }

            // 인증 전 연결이 상한(전체 또는 그 원격 주소)이면 스레드를 만들지 않고 바로 닫습니다.
            string address = RemoteAddress(client);
            if (!EnterPreAuth(address))
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }

                continue;
            }

            long deadline = GameViewRemoteWs.DeadlineAfter(GameViewRemoteGate.AUTH_DEADLINE_MS);
            try
            {
                client.SendTimeout = GameViewRemoteServer.SEND_TIMEOUT_MS;
                new Thread(() => HandleClient(client, deadline, address)) { IsBackground = true, Name = "GVR Host Client" }.Start();
            }
            catch (Exception e)
            {
                // 스레드를 만들지 못했으면(자원 부족) 세어 둔 것을 되돌리고 이 연결만 버립니다(코드 리뷰 m1).
                LeavePreAuth(address);
                Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} host: 연결을 처리할 스레드를 만들지 못했습니다: {e.Message}");
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static string RemoteAddress(TcpClient client)
    {
        try
        {
            IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
            return remote != null ? remote.Address.ToString() : "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static bool EnterPreAuth(string address)
    {
        lock (preAuthByAddress)
        {
            preAuthByAddress.TryGetValue(address, out int mine);
            if (preAuthConnections >= MAX_PRE_AUTH_CONNECTIONS || mine >= MAX_PRE_AUTH_PER_ADDRESS)
            {
                return false;
            }

            preAuthConnections++;
            preAuthByAddress[address] = mine + 1;
            return true;
        }
    }

    private static void LeavePreAuth(string address)
    {
        lock (preAuthByAddress)
        {
            preAuthConnections--;
            if (preAuthByAddress.TryGetValue(address, out int mine))
            {
                if (mine <= 1)
                {
                    preAuthByAddress.Remove(address);
                }
                else
                {
                    preAuthByAddress[address] = mine - 1;
                }
            }
        }
    }

    private static void HandleClient(TcpClient client, long deadline, string address)
    {
        // 이 연결이 아직 "인증 전"으로 세어져 있는가(인증을 마치거나 끝나면 한 번만 뺌)
        bool counted = true;
        try
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            // 헤더 읽기와 인증 메시지 읽기를 합쳐 기한 하나(연결을 받은 때부터 3초)를 씁니다.
            string header = GameViewRemoteWs.ReadHttpHeader(client, stream, MAX_HEADER_BYTES, deadline);
            if (header == null)
            {
                return;
            }

            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            string method = requestLine[0];
            string path = requestLine.Length > 1 ? requestLine[1] : "/";
            int query = path.IndexOf('?');
            if (query >= 0)
            {
                path = path.Substring(0, query);
            }

            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
            }

            bool isUpgrade = headers.TryGetValue("Upgrade", out string upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase);
            if (isUpgrade && headers.TryGetValue("Sec-WebSocket-Key", out string key))
            {
                string channel = path == "/video" ? "video" : path == "/input" ? "input" : null;
                GameViewRemoteChannels.Handler handler = null;
                if (channel == null && path.StartsWith(GameViewRemoteChannels.PATH_PREFIX, StringComparison.Ordinal))
                {
                    string name = path.Substring(GameViewRemoteChannels.PATH_PREFIX.Length);
                    handler = GameViewRemoteChannels.Find(name);
                    channel = handler != null ? name : null;
                }

                if (channel == null)
                {
                    GameViewRemoteFiles.WriteError(stream, "404 Not Found", "not_found", "not found");
                    return;
                }

                GameViewRemoteGate.Verdict verdict = GameViewRemoteGate.Admit(client, stream, channel, headers, ComputeAccept(key), true, deadline,
                    out GameViewRemoteSession session);
                if (verdict != GameViewRemoteGate.Verdict.Admitted)
                {
                    return;
                }

                counted = false;
                LeavePreAuth(address);
                if (handler != null)
                {
                    handler(client, stream, session);
                    return;
                }

                RunAuthenticated(client, stream, path == "/video" ? GameViewRemoteServer.ChannelKind.Video : GameViewRemoteServer.ChannelKind.Input, session);
                return;
            }

            if (method != "GET" && method != "HEAD")
            {
                GameViewRemoteFiles.WriteError(stream, "405 Method Not Allowed", "method_not_allowed", "GET 만 됩니다.");
            }
            else if (!GameViewRemoteFiles.TryServe(method, path, client, stream, headers.TryGetValue("User-Agent", out string userAgent) ? userAgent : null))
            {
                // 페이지(/), /stats, /debug 는 LAN 에 열지 않습니다.
                GameViewRemoteFiles.WriteError(stream, "404 Not Found", "not_found", "not found");
            }
        }
        catch (Exception e)
        {
            if (!(e is IOException) && !(e is SocketException) && !(e is ObjectDisposedException))
            {
                Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} host client error: {e}");
            }
        }
        finally
        {
            if (counted)
            {
                LeavePreAuth(address);
            }

            try
            {
                client.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    // 인증을 마친 앱 연결: Play 중이면 기존 서버에 넘기고(연결이 끝날 때까지 돌아오지 않음), 아니면 대기실에 둡니다.
    private static void RunAuthenticated(TcpClient client, NetworkStream stream, GameViewRemoteServer.ChannelKind kind, GameViewRemoteSession session)
    {
        if (GameViewRemote.AdoptClient(client, kind, session))
        {
            return;
        }

        LobbyConnection waiting = new LobbyConnection { client = client, stream = stream, session = session, kind = kind };
        bool full;
        lock (stateLock)
        {
            full = lobby.Count >= MAX_LOBBY_CONNECTIONS || !listening;
            if (!full)
            {
                lobby.Add(waiting);
            }
        }

        if (full)
        {
            GameViewRemoteWs.TryWriteClose(stream, CLOSE_NORMAL);
            return;
        }

        NoteChangedFromAnyThread();
        try
        {
            // 넣는 사이 Play 가 시작됐으면(대기실 비우기가 이미 지나감) 곧바로 다시 붙게 합니다.
            if (GameViewRemote.IsRunning)
            {
                GameViewRemoteWs.TryWriteClose(stream, GameViewRemoteGate.CLOSE_RECONNECT);
                return;
            }

            if (kind == GameViewRemoteServer.ChannelKind.Video)
            {
                GameViewRemoteWs.WriteText(stream, "{\"t\":\"hello\",\"v\":" + GameViewRemoteCapture.PROTOCOL_VERSION + "}");
                if (session.Has(GameViewRemoteAppInfo.CAP_SEAT))
                {
                    // Play 중인데 서버가 꺼져 있으면(예전 "화면 전송 중지" 버튼) "Play 를 누르면"은 틀린 안내입니다(코드 리뷰 m9).
                    GameViewRemoteWs.WriteText(stream, "{\"t\":\"seat\",\"s\":\"lobby\",\"msg\":\"" + (editorPlaying ? LOBBY_MESSAGE_STOPPED : LOBBY_MESSAGE) + "\"}");
                }
            }

            LobbyLoop(waiting);
        }
        finally
        {
            bool removed;
            lock (stateLock)
            {
                removed = lobby.Remove(waiting);
            }

            if (removed)
            {
                NoteChangedFromAnyThread();
            }
        }
    }

    // 대기실: 연결을 유지하며 입력 채널의 ping 에만 답합니다. 닫기는 KickLobby(다른 스레드)가 소켓을 닫아 끝냅니다.
    private static void LobbyLoop(LobbyConnection waiting)
    {
        while (true)
        {
            int opcode = GameViewRemoteWs.ReadFrame(waiting.client, waiting.stream, MAX_CLIENT_MESSAGE_BYTES, 0, out byte[] payload);
            if (opcode == GameViewRemoteWs.OPCODE_CLOSE)
            {
                GameViewRemoteWs.TryWriteClose(waiting.stream, CLOSE_NORMAL);
                return;
            }

            if (opcode == GameViewRemoteWs.OPCODE_PING)
            {
                GameViewRemoteWs.WriteFrame(waiting.stream, GameViewRemoteWs.OPCODE_PONG, payload);
                continue;
            }

            if (opcode != GameViewRemoteWs.OPCODE_TEXT || waiting.kind != GameViewRemoteServer.ChannelKind.Input)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(payload);
            if (!text.StartsWith("{\"t\":\"p\"", StringComparison.Ordinal))
            {
                continue;
            }

            PingMessage ping;
            try
            {
                ping = JsonUtility.FromJson<PingMessage>(text);
            }
            catch (Exception)
            {
                continue;
            }

            if (ping != null && ping.t == "p")
            {
                GameViewRemoteWs.WriteText(waiting.stream, "{\"t\":\"P\",\"ts\":" + ping.ts.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}");
            }
        }
    }

    private static string ComputeAccept(string key)
    {
        using (SHA1 sha1 = SHA1.Create())
        {
            return Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WS_GUID)));
        }
    }

    #endregion
}
