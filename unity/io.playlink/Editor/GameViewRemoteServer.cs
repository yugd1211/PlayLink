using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

// Game View Remote 에디터 내장 서버.
// HttpListener는 Mono WebSocket 지원이 부족하고 0.0.0.0 바인드에 관리자 권한(urlacl)이 필요해서
// TcpListener 위에 최소한의 HTTP/1.1 + RFC6455 WebSocket을 직접 구현합니다.
//   GET /        → 폰 페이지(HTML)
//   GET /video   → 영상 WebSocket (서버 → 폰: 바이너리 프레임(JPEG 또는 H.264) + 텍스트 상태 JSON,
//                  폰 → 서버: "a" 수신 확인, {"t":"caps","h264":1} H.264 디코딩 가능, {"t":"key"} 키프레임 요청)
//   GET /input   → 입력 WebSocket (폰 → 서버: 포인터 JSON, ping)
//   GET /stats   → 진단 JSON(1초 통계, 누적 카운터, 최근 Unity 프레임 · 인코딩 기록)
//   GET /debug?pattern=1|0 → 테스트 패턴 켜기/끄기(진단용)
// 네트워크 I/O는 전부 백그라운드 스레드에서 하고, 메인 스레드는 막지 않습니다.
// 이 서버는 항상 127.0.0.1 에만 엽니다(USB adb reverse 의 Chrome 페이지, USB 로 붙는 앱). 같은 Wi-Fi 의 앱은 앞단(GameViewRemoteHost)이
// QR 주소에서 받아 인증한 뒤 Adopt() 로 넘깁니다. 인증 없는 LAN 리슨("Wi-Fi 접속 허용")은 없앴습니다.
public sealed class GameViewRemoteServer
{
    private const string WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MAX_HEADER_BYTES = 16 * 1024;
    private const int MAX_CLIENT_MESSAGE_BYTES = 64 * 1024;
    // 수신 확인이 이 시간 안에 안 오면 다음 프레임을 그냥 보냅니다(확인 메시지 유실 대비).
    private const int ACK_TIMEOUT_MS = 1000;
    // 수신 확인 전에 보내 둘 수 있는 프레임 수. 1이면 매 프레임 왕복(RTT)을 기다려 무선(RTT 약 9ms)에서 처리량이 왕복 시간에 묶입니다.
    // 2면 한 장이 가는 동안 다음 장을 보내 대역폭만큼 보냅니다(지연은 최대 전송 1장만큼 늘 수 있음).
    private const int MAX_FRAMES_IN_FLIGHT = 2;
    // H.264 역압은 장 수가 아니라 시간으로 봅니다: 가장 오래된 "보냈지만 확인을 못 받은(또는 아직 못 보낸)" 프레임의 나이.
    // 프레임이 작아서(720x1280 6Mbps 약 12KB) 왕복이 프레임 간격보다 긴 링크(무선 adb, 평소 20ms · 순간 80ms)에서도 여러 장이 가는 중인 것이 정상이고,
    // 장 수로 막으면(예전 4장) 왕복이 잠깐 튈 때마다 프레임이 불규칙하게 빠집니다. 이 시간만큼 밀리면 더 보내지 않고, 캡처 쪽이 인코딩 전에 건너뜁니다.
    public const int MAX_H264_BACKLOG_MS = 150;
    // 확인이 전혀 오지 않을 때 소켓 버퍼에 한없이 쌓지 않게 하는 상한(장)
    private const int MAX_H264_FRAMES_IN_FLIGHT = 32;
    // 클라이언트당 전송 버퍼 처음 크기(H.264 프레임 약 12KB는 그대로 들어가고, MJPEG 프레임은 처음 몇 장에서 한 번 키웁니다).
    private const int SEND_BUFFER_BYTES = 64 * 1024;
    private const int MAX_FRAME_HEADER_BYTES = 10;
    private const ushort CLOSE_NORMAL = 1000;
    // 소켓 쓰기가 이 시간 넘게 막히면 그 연결을 끊습니다. Wi-Fi 상대가 FIN 없이 사라졌을 때(폰이 범위를 벗어남, 잠듦)
    // 전송 스레드가 Windows 의 재전송 포기(10초 안팎)까지 붙잡혀 있지 않게 합니다(코드 리뷰 M1).
    // 정상 연결에서는 걸리지 않습니다: 확인(ack) 역압 때문에 받는 쪽이 멈추면 서버가 먼저 보내기를 멈춥니다.
    public const int SEND_TIMEOUT_MS = 3000;
    // 메인 스레드가 닫기 프레임을 쓰려고 쓰기 잠금을 기다리는 최대 시간. 못 잡으면 프레임 없이 소켓을 닫습니다.
    private const int CLOSE_LOCK_WAIT_MS = 50;

    public enum ChannelKind
    {
        Video,
        Input,
    }

    // 영상 클라이언트 1개. 보낼 프레임은 "최신 1장" 슬롯만 두고, 수신 확인을 기다리는 프레임이 MAX_FRAMES_IN_FLIGHT장이면 더 보내지 않습니다.
    public sealed class Connection
    {
        public readonly int id;
        public readonly ChannelKind kind;
        public readonly string remote;

        internal readonly TcpClient client;
        internal readonly NetworkStream stream;
        internal readonly object writeLock = new object();
        internal readonly AutoResetEvent wake = new AutoResetEvent(false);
        internal VideoMessage pending;
        // 보낼 상태 텍스트(최신 1개). 수신 확인과 무관하게 바로 보냅니다.
        internal string pendingText;
        // H.264 프레임은 앞 프레임에 기대므로 덮어쓰지 않고 순서대로 전부 보냅니다(최신 1장 슬롯과 별도).
        internal readonly Queue<VideoMessage> ordered = new Queue<VideoMessage>();
        // 페이지가 H.264(WebCodecs) 디코딩이 된다고 알렸는지, 키프레임(디코더 재설정)을 요청했는지
        internal volatile bool supportsH264;
        internal volatile bool keyframeRequested;
        // 수신 확인을 기다리는 프레임의 보내기 시작 시각(Stopwatch, 보낸 순서). 확인까지 = 전송 + 왕복 + 폰 수신 처리
        internal readonly Queue<long> inFlight = new Queue<long>();
        internal long ackDeadlineTicks;
        internal long lastAckTicks;
        internal volatile bool closed;
        // WebSocket 메시지 1개(프레임 헤더 + 내용)를 모아 소켓 쓰기 1번으로 보내는 버퍼(lock(stream) 안에서만 씀). 모자라면 키우고 계속 다시 씁니다.
        internal byte[] sendBuffer = new byte[SEND_BUFFER_BYTES];
        // 자리 배정(GameViewRemoteSeats)이 정합니다. true면 대기: 영상 프레임과 상태 텍스트를 받지 않고, 입력은 버려집니다.
        public volatile bool standby;
        // 인증된 앱 연결이면 GameViewRemoteSession, 인증 헤더 없는 연결(Chrome 페이지)이면 null
        public object tag;

        internal Connection(int id, ChannelKind kind, TcpClient client)
        {
            this.id = id;
            this.kind = kind;
            this.client = client;
            stream = client.GetStream();
            remote = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "?";
        }
    }

    // 인코딩이 끝난 프레임 1장. 헤더(28B)와 JPEG를 따로 들고 WebSocket 메시지 하나로 이어 보냅니다(큰 배열 복사 없음).
    // 여러 클라이언트와 같은 화면 재전송이 같은 JPEG 배열을 공유하므로 보낸 뒤에도 바꾸지 않습니다.
    public sealed class VideoMessage
    {
        public byte[] header;
        public byte[] jpeg;
        public int Length { get { return header.Length + jpeg.Length; } }
        public long captureTicks;
        public int frameId;
        // H.264 순서 큐에 넣은 시각(Stopwatch). 아직 못 보낸 프레임의 나이를 재는 데 씁니다.
        public long queuedTicks;
    }

    public Action<Connection> onConnected;
    public Action<Connection> onDisconnected;
    // 입력 소켓 텍스트 메시지(수신 스레드에서 호출). 반환값이 null이 아니면 그 텍스트로 답장합니다.
    public Func<Connection, string, string> onInputText;
    // 프레임 1장을 다 보냈을 때(전송 스레드에서 호출). 인자: 보낸 바이트 수, 캡처부터 전송 완료까지 걸린 시간(ms)
    public Action<int, double> onFrameSent;
    // 수신 확인이 왔을 때(수신 스레드). 인자: 보내기 시작부터 확인까지(ms), 링크가 바빴을 때(다른 프레임도 가는 중) 직전 확인과의 간격(ms, 아니면 -1).
    // 두 번째 값이 실제 한 장당 전송 처리 시간입니다.
    public Action<double, double> onAck;
    // GET /stats 응답 JSON을 만듭니다(클라이언트 스레드에서 호출). 없으면 404
    public Func<string> onStatsRequest;
    // GET / 때마다 페이지 내용을 다시 읽습니다(페이지 파일만 고쳤을 때 서버를 다시 시작하지 않아도 폰 새로 고침으로 반영). null을 돌려주면 시작할 때 읽어 둔 것을 씁니다.
    public Func<byte[]> onPageRequest;
    // GET /debug?… 진단용 스위치(인자: ? 뒤 문자열, 반환: 응답 JSON)
    public Func<string, string> onDebugRequest;
    // 아직 못 보낸 프레임이 새 프레임에 덮어써진 수(누적, 전송 쪽에서 버려진 프레임)
    public long SlotOverwrites { get { return Interlocked.Read(ref slotOverwrites); } }
    // 수신 확인 1초 대기가 지나 그냥 보낸 수(누적)
    public long AckTimeouts { get { return Interlocked.Read(ref ackTimeouts); } }

    public int Port { get; private set; }
    public bool IsRunning { get { return running; } }
    // 마지막으로 알린 상태 텍스트(없으면 null). 자리가 바뀐 클라이언트에 다시 보낼 때 씁니다.
    public string LastStatus { get { return lastStatus; } }

    private readonly byte[] pageBytes;
    private readonly List<Connection> connections = new List<Connection>();
    private readonly List<Thread> threads = new List<Thread>();
    private TcpListener listener;
    private Thread acceptThread;
    private volatile bool running;
    private int nextId;
    private long slotOverwrites;
    private long ackTimeouts;
    private volatile string lastStatus;

    public GameViewRemoteServer(byte[] pageBytes)
    {
        this.pageBytes = pageBytes;
    }

    public void Start(int port)
    {
        if (running)
        {
            return;
        }

        // 이 PC 안(127.0.0.1)에서만 받습니다. adb reverse는 PC 쪽에서 127.0.0.1로 접속하므로 USB는 그대로 됩니다.
        listener = new TcpListener(IPAddress.Loopback, port);
        // 이전 세션 소켓이 TIME_WAIT여도 바로 다시 열 수 있게 합니다.
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        running = true;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "GVR Accept" };
        acceptThread.Start();
    }

    public void Stop()
    {
        if (!running)
        {
            return;
        }

        running = false;
        try
        {
            listener.Stop();
        }
        catch (Exception)
        {
        }

        List<Connection> snapshot;
        lock (connections)
        {
            snapshot = new List<Connection>(connections);
        }

        foreach (Connection connection in snapshot)
        {
            // 메인 스레드: 막힌 쓰기를 기다리지 않습니다(M1).
            TrySendCloseNoWait(connection, CLOSE_NORMAL);
            Close(connection);
        }

        List<Thread> threadSnapshot;
        lock (threads)
        {
            threadSnapshot = new List<Thread>(threads);
            threads.Clear();
        }

        acceptThread.Join(500);
        foreach (Thread thread in threadSnapshot)
        {
            thread.Join(500);
        }

        // 연결 표만 비웁니다. 누가 보고 있었는지(선호)는 남아서 Play 를 다시 시작해도 보던 쪽이 계속 봅니다.
        GameViewRemoteSeats.Reset();
    }

    public List<Connection> GetConnections()
    {
        lock (connections)
        {
            return new List<Connection>(connections);
        }
    }

    public int CountConnections(ChannelKind kind)
    {
        int count = 0;
        lock (connections)
        {
            foreach (Connection connection in connections)
            {
                if (connection.kind == kind)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // 모든 영상 클라이언트의 슬롯을 최신 프레임으로 바꿉니다. 아직 못 보낸 이전 프레임은 버려집니다.
    public void PublishFrame(VideoMessage message)
    {
        lock (connections)
        {
            foreach (Connection connection in connections)
            {
                if (connection.kind != ChannelKind.Video || connection.standby)
                {
                    continue;
                }

                lock (connection.writeLock)
                {
                    if (connection.pending != null)
                    {
                        Interlocked.Increment(ref slotOverwrites);
                    }

                    connection.pending = message;
                }

                connection.wake.Set();
            }
        }
    }

    // 모든 영상 클라이언트에 상태 텍스트(JSON)를 보냅니다. 나중에 접속하는 클라이언트도 접속 직후 마지막 상태를 받습니다.
    public void PublishStatus(string json)
    {
        lastStatus = json;
        lock (connections)
        {
            foreach (Connection connection in connections)
            {
                if (connection.kind != ChannelKind.Video || connection.standby)
                {
                    continue;
                }

                lock (connection.writeLock)
                {
                    connection.pendingText = json;
                }

                connection.wake.Set();
            }
        }
    }

    // 막 접속한 영상 클라이언트에게 첫 화면(seed)을 바로 보냅니다.
    public void Seed(Connection connection, VideoMessage message)
    {
        if (message == null || connection.standby)
        {
            return;
        }

        lock (connection.writeLock)
        {
            if (connection.pending == null)
            {
                connection.pending = message;
            }
        }

        connection.wake.Set();
    }

    #region Accept / HTTP

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                break;
            }

            client.NoDelay = true;
            client.SendTimeout = SEND_TIMEOUT_MS;
            StartThread(() => HandleClient(client), "GVR Client");
        }
    }

    private void StartThread(ThreadStart body, string name)
    {
        Thread thread = new Thread(body) { IsBackground = true, Name = name };
        lock (threads)
        {
            threads.RemoveAll(t => !t.IsAlive);
            threads.Add(thread);
        }

        thread.Start();
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            string header = ReadHttpHeader(stream);
            if (header == null)
            {
                client.Close();
                return;
            }

            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            string path = requestLine.Length > 1 ? requestLine[1] : "/";
            int query = path.IndexOf('?');
            string queryText = string.Empty;
            if (query >= 0)
            {
                queryText = path.Substring(query + 1);
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
            if (isUpgrade && (path == "/video" || path == "/input") && headers.TryGetValue("Sec-WebSocket-Key", out string key))
            {
                // 입장 심사: 인증 헤더가 없는 루프백 연결(Chrome 페이지)은 지금처럼 받고, 앱(X-GVR-Auth)은 인증을 거칩니다.
                ChannelKind kind = path == "/video" ? ChannelKind.Video : ChannelKind.Input;
                GameViewRemoteGate.Verdict verdict = GameViewRemoteGate.Admit(client, stream, kind == ChannelKind.Video ? "video" : "input",
                    headers, ComputeAccept(key), false, 0, out GameViewRemoteSession session);
                if (verdict == GameViewRemoteGate.Verdict.Rejected)
                {
                    client.Close();
                    return;
                }

                if (verdict == GameViewRemoteGate.Verdict.Legacy)
                {
                    WriteAscii(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                        + $"Sec-WebSocket-Accept: {ComputeAccept(key)}\r\n\r\n");
                }

                RunWebSocket(client, kind, session);
                return;
            }

            // 루프백이 아닌 곳에서는 페이지와 진단 경로를 내주지 않습니다(이 리스너는 127.0.0.1 에만 열지만 한 번 더 막음).
            // Host 헤더가 이 PC 를 가리키지 않으면 내주지 않습니다(코드 리뷰 m4: PC 브라우저의 다른 웹 페이지가 이름을 127.0.0.1 로 돌려
            // /stats 를 읽거나 /debug 를 부르는 것을 막음). Host 가 아예 없는 요청(측정 스크립트)은 지금처럼 받습니다.
            if (!GameViewRemoteGate.IsLoopback(client) || !GameViewRemoteGate.IsLocalHostHeader(headers.TryGetValue("Host", out string hostHeader) ? hostHeader : null))
            {
                GameViewRemoteFiles.WriteError(stream, "404 Not Found", "not_found", "not found");
            }
            else if (path == "/" || path == "/index.html")
            {
                WriteHttp(stream, "200 OK", "text/html; charset=utf-8", (onPageRequest != null ? onPageRequest() : null) ?? pageBytes);
            }
            else if (path == "/debug" && onDebugRequest != null)
            {
                WriteHttp(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(onDebugRequest(queryText)));
            }
            else if (path == "/stats" && onStatsRequest != null)
            {
                WriteHttp(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(onStatsRequest()));
            }
            else if (GameViewRemoteFiles.TryServe(requestLine[0], path, client, stream, headers.TryGetValue("User-Agent", out string userAgent) ? userAgent : null))
            {
                // 착륙 페이지(/c), APK(/apk), app.json: Play 중 USB 로도 QR 주소와 같은 경로를 확인할 수 있게 루프백에서도 내줍니다.
            }
            else
            {
                WriteHttp(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"));
            }

            client.Close();
        }
        catch (Exception e)
        {
            if (running && !(e is IOException) && !(e is SocketException) && !(e is ObjectDisposedException))
            {
                Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} client error: {e}");
            }
        }
        finally
        {
            client.Close();
        }
    }

    // 앞단(GameViewRemoteHost)이 QR 주소에서 받아 인증까지 마친 소켓을 넘깁니다. 앞단의 연결 스레드에서 불리고, 연결이 끝날 때까지 돌아오지 않습니다.
    // 서버가 돌고 있지 않으면 소켓을 건드리지 않고 false(앞단이 대기실로 보냄).
    public bool Adopt(TcpClient client, ChannelKind kind, object tag)
    {
        if (!running)
        {
            return false;
        }

        try
        {
            RunWebSocket(client, kind, tag);
        }
        catch (Exception e)
        {
            if (running && !(e is IOException) && !(e is SocketException) && !(e is ObjectDisposedException))
            {
                Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} client error: {e}");
            }
        }

        return true;
    }

    // WebSocket 연결 하나의 일생: 자리 배정 → 목록에 넣기 → (영상이면) hello 와 전송 스레드 → 수신 루프 → 닫기.
    // 101 응답은 부르는 쪽이 이미 썼습니다(페이지는 HandleClient, 앱은 Gate).
    private void RunWebSocket(TcpClient client, ChannelKind kind, object tag)
    {
        // 앞단이 넘긴 소켓에도 같은 쓰기 제한을 겁니다(앞단이 이미 걸었어도 한 번 더: 넘겨받는 경로가 늘어도 빠지지 않게).
        client.SendTimeout = SEND_TIMEOUT_MS;
        Connection connection = new Connection(Interlocked.Increment(ref nextId), kind, client);
        connection.tag = tag;
        try
        {
            // 목록에 넣기 전에 보는 중/대기를 정합니다(대기 연결에는 프레임과 상태 텍스트가 가지 않음).
            GameViewRemoteSeats.OnJoin(this, connection);
            connection.pendingText = kind == ChannelKind.Video && !connection.standby ? lastStatus : null;
            lock (connections)
            {
                connections.Add(connection);
            }

            // Stop 이 연결 목록을 복사한 뒤에 들어왔으면 아무도 닫아 주지 않으므로 여기서 닫습니다.
            if (!running)
            {
                SendClose(connection, CLOSE_NORMAL);
                return;
            }

            if (kind == ChannelKind.Video)
            {
                // 프로토콜 번호를 먼저 알립니다. 폰에 예전 페이지가 떠 있으면(서버만 새로 컴파일됨) 페이지가 스스로 새로 고칩니다.
                byte[] hello = Encoding.UTF8.GetBytes("{\"t\":\"hello\",\"v\":" + GameViewRemoteCapture.PROTOCOL_VERSION + "}");
                WriteFrame(connection, 0x1, hello, 0, hello.Length);
                StartThread(() => VideoSendLoop(connection), "GVR Video Send");
            }

            // hello 다음에 자리 상태를 알립니다(앱: seat, 대기 중인 페이지: 대기 문구).
            GameViewRemoteSeats.Announce(this, connection);
            onConnected?.Invoke(connection);
            ReadLoop(connection);
        }
        finally
        {
            Close(connection);
        }
    }

    private static string ReadHttpHeader(NetworkStream stream)
    {
        byte[] buffer = new byte[MAX_HEADER_BYTES];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read <= 0)
            {
                return null;
            }

            length += read;
            for (int i = 3; i < length; i++)
            {
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                {
                    return Encoding.ASCII.GetString(buffer, 0, i - 3);
                }
            }
        }

        return null;
    }

    private static string ComputeAccept(string key)
    {
        using (SHA1 sha1 = SHA1.Create())
        {
            return Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WS_GUID)));
        }
    }

    // 헤더와 본문을 한 번에 씁니다(나눠 쓰면 adb 터널에서 뒤쪽이 약 40ms 늦게 갈 수 있음, WriteMessage 설명 참고).
    private static void WriteHttp(NetworkStream stream, string status, string contentType, byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        byte[] response = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, response, 0, head.Length);
        Buffer.BlockCopy(body, 0, response, head.Length, body.Length);
        stream.Write(response, 0, response.Length);
        stream.Flush();
    }

    private static void WriteAscii(NetworkStream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    #endregion

    #region WebSocket

    private void ReadLoop(Connection connection)
    {
        byte[] header = new byte[14];
        MemoryStream message = new MemoryStream();
        int messageOpcode = 0;
        while (running && !connection.closed)
        {
            ReadExactly(connection.stream, header, 0, 2);
            bool fin = (header[0] & 0x80) != 0;
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;
            if (length == 126)
            {
                ReadExactly(connection.stream, header, 0, 2);
                length = (header[0] << 8) | header[1];
            }
            else if (length == 127)
            {
                ReadExactly(connection.stream, header, 0, 8);
                length = 0;
                for (int i = 0; i < 8; i++)
                {
                    length = (length << 8) | header[i];
                }
            }

            if (length > MAX_CLIENT_MESSAGE_BYTES)
            {
                throw new IOException("message too large");
            }

            byte[] mask = new byte[4];
            if (masked)
            {
                ReadExactly(connection.stream, mask, 0, 4);
            }

            byte[] payload = new byte[length];
            ReadExactly(connection.stream, payload, 0, (int)length);
            if (masked)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i & 3];
                }
            }

            if (opcode == 0x8)
            {
                SendClose(connection, CLOSE_NORMAL);
                return;
            }

            if (opcode == 0x9)
            {
                WriteFrame(connection, 0xA, payload, 0, payload.Length);
                continue;
            }

            if (opcode == 0xA)
            {
                continue;
            }

            if (opcode != 0x0)
            {
                messageOpcode = opcode;
                message.SetLength(0);
            }

            message.Write(payload, 0, payload.Length);
            if (!fin)
            {
                continue;
            }

            if (messageOpcode == 0x1)
            {
                string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                HandleText(connection, text);
            }

            message.SetLength(0);
        }
    }

    private void HandleText(Connection connection, string text)
    {
        if (connection.kind == ChannelKind.Video && text.StartsWith("{", StringComparison.Ordinal))
        {
            // 페이지 능력 / 키프레임 요청 / 여기서 보기(우리 페이지와 앱이 보내는 고정 형식이라 문자열로 봅니다).
            // 값이 아니라 "t":"이름" 으로 봅니다: 나중에 더하는 메시지의 값에 key 같은 단어가 들어가도 오판하지 않게(설계 15.1 R3).
            if (text.Contains("\"t\":\"caps\""))
            {
                connection.supportsH264 = text.Contains("\"h264\":1");
            }
            else if (text.Contains("\"t\":\"key\""))
            {
                connection.keyframeRequested = true;
            }
            else if (text.Contains("\"t\":\"take\""))
            {
                GameViewRemoteSeats.OnTake(this, connection);
            }

            return;
        }

        if (connection.kind == ChannelKind.Video)
        {
            // "a" = 프레임 수신 확인(보낸 순서대로 옴). 다음 프레임을 보내도 됩니다.
            double ackMs = -1;
            double busyIntervalMs = -1;
            lock (connection.writeLock)
            {
                if (connection.inFlight.Count > 0)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    long sent = connection.inFlight.Dequeue();
                    ackMs = GameViewRemote.ElapsedMs(sent);
                    // 이 프레임을 보내기 전에 직전 확인이 이미 왔다면 링크가 놀던 구간이 섞이므로 간격으로 쓰지 않습니다.
                    if (connection.lastAckTicks != 0 && connection.lastAckTicks > sent)
                    {
                        busyIntervalMs = (now - connection.lastAckTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    }

                    connection.lastAckTicks = now;
                    // 확인이 오는 동안에는 기한을 미룹니다. 보낼 때만 정하면 왕복이 프레임 간격보다 긴 링크(무선)에서 대기 중인 프레임이
                    // 1초 넘게 한 번도 비지 않아, 확인이 잘 오는데도 시간 초과로 보고 비우게 됩니다.
                    connection.ackDeadlineTicks = DateTime.UtcNow.Ticks + ACK_TIMEOUT_MS * TimeSpan.TicksPerMillisecond;
                }
            }

            if (ackMs >= 0)
            {
                onAck?.Invoke(ackMs, busyIntervalMs);
            }

            connection.wake.Set();
            return;
        }

        // 대기 중인 클라이언트의 입력은 버립니다(ping 만 답함).
        if (connection.standby && !text.StartsWith("{\"t\":\"p\"", StringComparison.Ordinal))
        {
            return;
        }

        string reply = onInputText?.Invoke(connection, text);
        if (reply != null)
        {
            SendText(connection, reply);
        }
    }

    private void VideoSendLoop(Connection connection)
    {
        try
        {
            // 깨우는 신호(AutoResetEvent)는 겹치면 1번으로 합쳐집니다. 한 장 보낸 뒤 바로 기다리면 큐에 남은 프레임이 다음 신호(다음 프레임이나
            // 수신 확인)까지 밀리므로, 방금 보냈으면 기다리지 않고 한 번 더 확인합니다.
            bool sentLast = false;
            while (running && !connection.closed)
            {
                if (!sentLast)
                {
                    connection.wake.WaitOne(100);
                }

                string text;
                VideoMessage message = null;
                lock (connection.writeLock)
                {
                    text = connection.pendingText;
                    connection.pendingText = null;
                    // 확인이 1초 동안 하나도 오지 않으면 확인이 유실된 것으로 보고 비웁니다(기한은 보낼 때와 확인이 올 때마다 미룸).
                    if (connection.inFlight.Count > 0 && DateTime.UtcNow.Ticks >= connection.ackDeadlineTicks)
                    {
                        Interlocked.Increment(ref ackTimeouts);
                        connection.inFlight.Clear();
                    }

                    // MJPEG는 장 수(2장), H.264는 가장 오래된 미확인 프레임의 나이로 막습니다.
                    bool room = connection.ordered.Count > 0
                        ? connection.inFlight.Count == 0 || (connection.inFlight.Count < MAX_H264_FRAMES_IN_FLIGHT
                            && GameViewRemote.ElapsedMs(connection.inFlight.Peek()) < MAX_H264_BACKLOG_MS)
                        : connection.inFlight.Count < MAX_FRAMES_IN_FLIGHT;
                    if (room && (connection.ordered.Count > 0 || connection.pending != null))
                    {
                        // H.264 순서 큐가 먼저입니다. MJPEG 최신 1장 슬롯은 H.264를 안 쓸 때만 찹니다.
                        if (connection.ordered.Count > 0)
                        {
                            message = connection.ordered.Dequeue();
                        }
                        else
                        {
                            message = connection.pending;
                            connection.pending = null;
                        }

                        if (connection.inFlight.Count == 0)
                        {
                            connection.ackDeadlineTicks = DateTime.UtcNow.Ticks + ACK_TIMEOUT_MS * TimeSpan.TicksPerMillisecond;
                        }

                        connection.inFlight.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
                    }
                }

                if (text != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    WriteFrame(connection, 0x1, bytes, 0, bytes.Length);
                }

                if (message != null)
                {
                    WriteFrame(connection, 0x2, message.header, message.jpeg);
                    onFrameSent?.Invoke(message.Length, GameViewRemote.ElapsedMs(message.captureTicks));
                }

                sentLast = message != null;
            }
        }
        catch (Exception)
        {
            Close(connection);
        }
    }

    // H.264 프레임 1장을 이 클라이언트의 순서 큐에 넣습니다(버리지 않음).
    public void PublishOrdered(Connection connection, VideoMessage message)
    {
        lock (connection.writeLock)
        {
            // 캡처 스레드가 이 연결을 고른 뒤 대기로 바뀌었으면 넣지 않습니다(코드 리뷰 m12: 대기 연결로 프레임이 한두 장 나가던 것).
            if (connection.standby)
            {
                return;
            }

            // H.264로 바뀐 뒤 남은 MJPEG 한 장이 H.264 프레임 뒤에 그려져 화면이 잠깐 되돌아가지 않게 버립니다.
            connection.pending = null;
            message.queuedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            connection.ordered.Enqueue(message);
        }

        connection.wake.Set();
    }

    // 가장 오래된 "수신 확인을 기다리는(없으면 아직 보내지 않은)" 프레임의 나이(ms, H.264 역압용). 밀린 것이 없으면 0.
    // MAX_H264_BACKLOG_MS를 넘으면 캡처 쪽에서 프레임을 인코딩 전에 건너뜁니다.
    public double BacklogMs(Connection connection)
    {
        lock (connection.writeLock)
        {
            if (connection.inFlight.Count > 0)
            {
                return GameViewRemote.ElapsedMs(connection.inFlight.Peek());
            }

            return connection.ordered.Count > 0 ? GameViewRemote.ElapsedMs(connection.ordered.Peek().queuedTicks) : 0.0;
        }
    }

    // 인코딩 전에 건너뛴 프레임을 전송 쪽 버림으로 셉니다(자동 프레임 간격이 봄).
    public void NoteDroppedFrame()
    {
        Interlocked.Increment(ref slotOverwrites);
    }

    // H.264를 쓸 수 있는 유일한 영상 클라이언트. 영상 클라이언트가 1개가 아니거나 그 페이지가 H.264를 못 하면 null(MJPEG로 보냄)
    public Connection SingleH264Client()
    {
        Connection found = null;
        lock (connections)
        {
            foreach (Connection connection in connections)
            {
                if (connection.kind != ChannelKind.Video || connection.standby)
                {
                    continue;
                }

                if (found != null)
                {
                    return null;
                }

                found = connection;
            }
        }

        return found != null && found.supportsH264 && !found.closed ? found : null;
    }

    // 키프레임 요청을 꺼냅니다(한 번만 true).
    public bool TakeKeyframeRequest(Connection connection)
    {
        if (!connection.keyframeRequested)
        {
            return false;
        }

        connection.keyframeRequested = false;
        return true;
    }

    public void SendText(Connection connection, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            WriteFrame(connection, 0x1, bytes, 0, bytes.Length);
        }
        catch (Exception)
        {
            Close(connection);
        }
    }

    private static void SendClose(Connection connection, ushort code)
    {
        try
        {
            WriteFrame(connection, 0x8, new byte[] { (byte)(code >> 8), (byte)code }, 0, 2);
        }
        catch (Exception)
        {
        }
    }

    // 닫기 프레임을 "기다리지 않고" 보냅니다. 전송 스레드가 사라진 상대에게 쓰다가 막혀 쓰기 잠금을 쥐고 있으면
    // 잠금을 기다리지 않고 포기합니다(부르는 쪽이 곧 소켓을 닫고, 그러면 막힌 쓰기도 풀립니다). 소켓 버퍼가 차 있어도 쓰지 않습니다.
    // 메인 스레드(Stop, [새 QR 발급])와 자리 배정이 부릅니다(코드 리뷰 M1).
    private static void TrySendCloseNoWait(Connection connection, ushort code)
    {
        bool taken = false;
        try
        {
            Monitor.TryEnter(connection.stream, CLOSE_LOCK_WAIT_MS, ref taken);
            if (!taken || !connection.client.Client.Poll(0, System.Net.Sockets.SelectMode.SelectWrite))
            {
                return;
            }

            byte[] frame = { 0x88, 2, (byte)(code >> 8), (byte)code };
            connection.stream.Write(frame, 0, frame.Length);
        }
        catch (Exception)
        {
        }
        finally
        {
            if (taken)
            {
                Monitor.Exit(connection.stream);
            }
        }
    }

    // 닫기 코드를 보내고 연결을 닫습니다(설계 3.5: 4403 토큰 폐기 등). 아무 스레드에서 불러도 막히지 않습니다.
    public void CloseWith(Connection connection, ushort code)
    {
        TrySendCloseNoWait(connection, code);
        Close(connection);
    }

    // 인증된 앱 연결(tag 가 있는 연결)을 전부 이 코드로 닫습니다. [새 QR 발급] 때 씁니다.
    public void CloseTagged(ushort code)
    {
        foreach (Connection connection in GetConnections())
        {
            if (connection.tag != null)
            {
                CloseWith(connection, code);
            }
        }
    }

    // 영상 프레임: 헤더와 JPEG(또는 H.264)를 WebSocket 메시지 하나(바이너리)로 보냅니다.
    private static void WriteFrame(Connection connection, int opcode, byte[] first, byte[] second)
    {
        WriteMessage(connection, opcode, first, 0, first.Length, second, second.Length);
    }

    private static void WriteFrame(Connection connection, int opcode, byte[] data, int offset, int count)
    {
        WriteMessage(connection, opcode, data, offset, count, null, 0);
    }

    // WebSocket 메시지 1개를 클라이언트 버퍼에 모아 소켓 쓰기 1번으로 보냅니다.
    // 프레임 헤더(2~10B) · 영상 헤더 · 내용을 따로 쓰면 NoDelay라 각각 따로 나가고, adb 서버가 앞의 몇 바이트만 먼저 넘긴 경우
    // 폰 쪽(adbd → Chrome)에서 나머지가 약 40ms 묶입니다. 실측(USB adb reverse, 12.5KB 메시지): 3번 쓰기는 62%가 약 45ms, 1번 쓰기는 최대 16ms.
    // 그 정체 뒤에 2~3장이 몰려 도착해 폰 화면이 끊겨 보였습니다(문서 12.7).
    // 전송 스레드(영상)와 수신 스레드(pong/close/ping 답장)가 같은 소켓에 쓰므로 메시지 단위로 잠급니다. 버퍼도 이 잠금 안에서만 씁니다.
    private static void WriteMessage(Connection connection, int opcode, byte[] first, int firstOffset, int firstCount, byte[] second, int secondCount)
    {
        int count = firstCount + secondCount;
        lock (connection.stream)
        {
            byte[] buffer = connection.sendBuffer;
            if (buffer.Length < MAX_FRAME_HEADER_BYTES + count)
            {
                buffer = new byte[Math.Max(MAX_FRAME_HEADER_BYTES + count, buffer.Length * 2)];
                connection.sendBuffer = buffer;
            }

            int length = WriteFrameHeader(buffer, opcode, count);
            Buffer.BlockCopy(first, firstOffset, buffer, length, firstCount);
            length += firstCount;
            if (secondCount > 0)
            {
                Buffer.BlockCopy(second, 0, buffer, length, secondCount);
                length += secondCount;
            }

            connection.stream.Write(buffer, 0, length);
        }
    }

    // 서버 → 클라이언트 프레임 헤더(마스크 없음)를 버퍼 맨 앞에 쓰고 그 길이를 돌려줍니다.
    private static int WriteFrameHeader(byte[] buffer, int opcode, int count)
    {
        buffer[0] = (byte)(0x80 | opcode);
        if (count < 126)
        {
            buffer[1] = (byte)count;
            return 2;
        }

        if (count <= ushort.MaxValue)
        {
            buffer[1] = 126;
            buffer[2] = (byte)(count >> 8);
            buffer[3] = (byte)count;
            return 4;
        }

        buffer[1] = 127;
        long length = count;
        for (int i = 0; i < 8; i++)
        {
            buffer[9 - i] = (byte)(length >> (8 * i));
        }

        return MAX_FRAME_HEADER_BYTES;
    }

    private static void ReadExactly(NetworkStream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = stream.Read(buffer, offset, count);
            if (read <= 0)
            {
                throw new IOException("connection closed");
            }

            offset += read;
            count -= read;
        }
    }

    private void Close(Connection connection)
    {
        bool removed;
        lock (connections)
        {
            removed = connections.Remove(connection);
        }

        connection.closed = true;
        connection.wake.Set();
        try
        {
            connection.client.Close();
        }
        catch (Exception)
        {
        }

        // 자리 표에서 빼고(여러 번 불려도 됨), 이 연결이 보고 있었으면 남은 클라이언트가 이어받습니다.
        GameViewRemoteSeats.OnLeave(this, connection);
        if (removed)
        {
            onDisconnected?.Invoke(connection);
        }
    }

    #endregion
}
