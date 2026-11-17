using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// 연결된 폰 하나: 키 교환을 마친 영상 연결과 입력 연결(PlayLinkLink 가 걸어서 넘겨줌).
// 암호화된 바이트 흐름 안에는 예전과 같은 WebSocket 프레임(RFC 6455)이 HTTP 업그레이드 없이 흐릅니다(PROTOCOL.md 4장).
// PC 가 예전의 서버 역할입니다: 마스크 없이 보내고, 마스크된 프레임을 받습니다.
//   영상 연결  PC → 폰: 텍스트 hello / seat / 상태(s), 바이너리 프레임(JPEG 또는 H.264)
//              폰 → PC: "a" 수신 확인, {"t":"caps","h264":1}, {"t":"key"} 키프레임 요청, {"t":"take"}
//   입력 연결  폰 → PC: 포인터 JSON, ping. PC → 폰: ping 의 답
// 폰은 한 번에 하나만 연결됩니다. Play 밖에서는 연결을 유지한 채 "Unity 에서 Play 를 누르면 화면이 나옵니다"(seat lobby)를 보이고,
// Play 중(SetLive(true))에만 프레임과 상태가 나가고 입력을 받습니다.
// 네트워크 I/O 는 전부 백그라운드 스레드에서 합니다(연결마다 수신 스레드, 영상은 전송 스레드 하나 더).
public sealed class PlayLinkPeer
{
    private const int MAX_CLIENT_MESSAGE_BYTES = 64 * 1024;
    // 수신 확인이 이 시간 안에 안 오면 다음 프레임을 그냥 보냅니다(확인 메시지 유실 대비).
    private const int ACK_TIMEOUT_MS = 1000;
    // 수신 확인 전에 보내 둘 수 있는 프레임 수(MJPEG). 2면 한 장이 가는 동안 다음 장을 보내 대역폭만큼 보냅니다.
    private const int MAX_FRAMES_IN_FLIGHT = 2;
    // H.264 역압은 장 수가 아니라 시간으로 봅니다: 가장 오래된 "보냈지만 확인을 못 받은(또는 아직 못 보낸)" 프레임의 나이.
    // 프레임이 작아서 왕복이 프레임 간격보다 긴 링크에서도 여러 장이 가는 중인 것이 정상이고, 장 수로 막으면 왕복이 잠깐 튈 때마다
    // 프레임이 불규칙하게 빠집니다. 이 시간만큼 밀리면 더 보내지 않고, 캡처 쪽이 인코딩 전에 건너뜁니다.
    public const int MAX_H264_BACKLOG_MS = 150;
    // 확인이 전혀 오지 않을 때 소켓 버퍼에 한없이 쌓지 않게 하는 상한(장)
    private const int MAX_H264_FRAMES_IN_FLIGHT = 32;
    private const int SEND_BUFFER_BYTES = 64 * 1024;
    private const int MAX_FRAME_HEADER_BYTES = 10;
    // 사람이 끝냄(끊기, Stop, 다른 폰 고름). 앱은 바로 대기 화면으로 돌아갑니다.
    public const ushort CLOSE_NORMAL = 1000;
    public const ushort CLOSE_GOING_AWAY = 1001;
    // 곧 다시 겁니다(도메인 리로드, 연결 오류). 앱은 이 코드나 그냥 끊김이면 대기 화면으로 가지 않고 몇 초 "다시 연결 중"으로 기다립니다.
    public const ushort CLOSE_RECONNECT = 4410;
    // 닫기 프레임을 보낸 뒤 소켓을 닫기까지 두는 틈. 받지 않은 데이터가 남은 채 바로 닫으면 RST 가 나가 닫기 프레임이 버려질 수 있습니다.
    private const int CLOSE_LINGER_MS = 250;
    // 메인 스레드가 닫기 프레임을 쓰려고 쓰기 잠금을 기다리는 최대 시간. 못 잡으면 프레임 없이 소켓을 닫습니다.
    private const int CLOSE_LOCK_WAIT_MS = 50;
    private const string LOBBY_MESSAGE = "Unity 에서 Play 를 누르면 화면이 나옵니다.";

    public enum ChannelKind
    {
        Video,
        Input,
    }

    // 연결 하나. 영상은 보낼 프레임을 "최신 1장" 슬롯(MJPEG)과 순서 큐(H.264)에 두고 전송 스레드가 꺼내 보냅니다.
    public sealed class Connection
    {
        public readonly int id;
        public readonly ChannelKind kind;
        public readonly string remote;

        internal readonly TcpClient client;
        internal readonly Stream stream;
        internal readonly object writeLock = new object();
        internal readonly AutoResetEvent wake = new AutoResetEvent(false);
        internal VideoMessage pending;
        // 보낼 상태 텍스트(최신 1개). 수신 확인과 무관하게 바로 보냅니다.
        internal string pendingText;
        // H.264 프레임은 앞 프레임에 기대므로 덮어쓰지 않고 순서대로 전부 보냅니다(최신 1장 슬롯과 별도).
        internal readonly Queue<VideoMessage> ordered = new Queue<VideoMessage>();
        // 앱이 H.264 디코딩이 된다고 알렸는지, 키프레임(디코더 재설정)을 요청했는지
        internal volatile bool supportsH264;
        internal volatile bool keyframeRequested;
        // 수신 확인을 기다리는 프레임의 보내기 시작 시각(Stopwatch, 보낸 순서). 확인까지 = 전송 + 왕복 + 폰 수신 처리
        internal readonly Queue<long> inFlight = new Queue<long>();
        internal long ackDeadlineTicks;
        internal long lastAckTicks;
        internal volatile bool closed;
        // WebSocket 메시지 1개(프레임 헤더 + 내용)를 모아 쓰기 1번으로 보내는 버퍼(lock(stream) 안에서만 씀)
        internal byte[] sendBuffer = new byte[SEND_BUFFER_BYTES];

        internal Connection(int id, ChannelKind kind, PlayLinkProto.Session session)
        {
            this.id = id;
            this.kind = kind;
            client = session.client;
            stream = session.stream;
            remote = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "?";
        }
    }

    // 인코딩이 끝난 프레임 1장. 헤더(28B)와 JPEG를 따로 들고 WebSocket 메시지 하나로 이어 보냅니다(큰 배열 복사 없음).
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

    // 프레임 1장을 다 보냈을 때(전송 스레드에서 호출). 인자: 보낸 바이트 수, 캡처부터 전송 완료까지 걸린 시간(ms)
    public Action<int, double> onFrameSent;
    // 수신 확인이 왔을 때(수신 스레드). 인자: 보내기 시작부터 확인까지(ms), 링크가 바빴을 때 직전 확인과의 간격(ms, 아니면 -1)
    public Action<double, double> onAck;
    // 아직 못 보낸 프레임이 새 프레임에 덮어써진 수(누적, 전송 쪽에서 버려진 프레임)
    public long SlotOverwrites { get { return Interlocked.Read(ref slotOverwrites); } }
    // 수신 확인 1초 대기가 지나 그냥 보낸 수(누적)
    public long AckTimeouts { get { return Interlocked.Read(ref ackTimeouts); } }

    public readonly string phoneId;
    public readonly string phoneName;
    public readonly int appVersion;
    public readonly string remote;
    public bool IsOpen { get { return !closing; } }
    public bool IsLive { get { return live; } }
    // 영상 연결(닫혔으면 null)
    public Connection Video { get { return closing ? null : video; } }

    private static int nextId;
    private readonly Connection video;
    private readonly Connection input;
    private readonly ManualResetEvent closedEvent = new ManualResetEvent(false);
    private readonly object seatLock = new object();
    private volatile bool closing;
    private volatile bool live;
    private bool inputCounted;
    private long slotOverwrites;
    private long ackTimeouts;
    private volatile string lastStatus;
    private volatile int remoteCloseCode;

    // 폰이 닫기 프레임으로 끝냈으면 그 코드(아니면 0: 우리가 닫았거나 그냥 끊김)
    public int RemoteCloseCode { get { return remoteCloseCode; } }

    public PlayLinkPeer(PlayLinkProto.Session videoSession, PlayLinkProto.Session inputSession)
    {
        phoneId = videoSession.phoneId;
        phoneName = videoSession.phoneName;
        appVersion = videoSession.appVersion;
        video = new Connection(Interlocked.Increment(ref nextId), ChannelKind.Video, videoSession);
        input = new Connection(Interlocked.Increment(ref nextId), ChannelKind.Input, inputSession);
        remote = video.remote;
    }

    // 연결 스레드에서 한 번 부릅니다. hello 를 먼저 보내고(항상 첫 메시지), Play 밖이면 대기 문구를 보냅니다.
    // lobbyNotice 가 false 면(Play 중이라 곧 SetLive(true) 가 불림) 대기 문구를 건너뜁니다.
    public void Start(bool lobbyNotice)
    {
        try
        {
            SendTextNow(video, "{\"t\":\"hello\",\"v\":" + PlayLinkCapture.PROTOCOL_VERSION + "}");
            if (lobbyNotice)
            {
                SendSeat();
            }
        }
        catch (Exception)
        {
            Close(CLOSE_RECONNECT);
            return;
        }

        StartThread(() => VideoSendLoop(video), "PlayLink Video Send");
        StartThread(() => ReadLoop(video), "PlayLink Video Read");
        StartThread(() => ReadLoop(input), "PlayLink Input Read");
    }

    // 닫힐 때까지 기다립니다(연결 스레드).
    public void WaitClosed()
    {
        closedEvent.WaitOne();
    }

    // Play 중인가. true 가 되면 폰에 seat active 를 알리고 프레임과 입력이 흐르기 시작합니다. false 면 대기 문구로 돌아갑니다.
    public void SetLive(bool value)
    {
        lock (seatLock)
        {
            if (closing)
            {
                live = false;
                value = false;
            }
            else
            {
                live = value;
            }

            if (value)
            {
                // 설정 + 키프레임부터 받게 합니다(화면이 멈춰 있어도 한 장 보냄).
                video.keyframeRequested = true;
            }
            else
            {
                lock (video.writeLock)
                {
                    video.ordered.Clear();
                    video.pending = null;
                    video.pendingText = null;
                    video.inFlight.Clear();
                }

                lastStatus = null;
            }

            // 입력 연결 수는 여기 한 곳에서만 셉니다(Play 밖의 입력은 세지 않음).
            if (value != inputCounted)
            {
                inputCounted = value;
                if (value)
                {
                    PlayLinkInput.OnInputConnected(input.id);
                }
                else
                {
                    PlayLinkInput.OnInputDisconnected(input.id);
                }
            }

            if (!closing)
            {
                SendSeat();
            }
        }
    }

    // 지금 자리 상태를 영상 연결로 알립니다.
    private void SendSeat()
    {
        SendText(video, live ? "{\"t\":\"seat\",\"s\":\"active\"}" : "{\"t\":\"seat\",\"s\":\"lobby\",\"msg\":\"" + LOBBY_MESSAGE + "\"}");
    }

    public int CountConnections(ChannelKind kind)
    {
        return closing ? 0 : 1;
    }

    // 영상 연결의 슬롯을 최신 프레임으로 바꿉니다. 아직 못 보낸 이전 프레임은 버려집니다.
    public void PublishFrame(VideoMessage message)
    {
        if (!live || closing)
        {
            return;
        }

        lock (video.writeLock)
        {
            if (video.pending != null)
            {
                Interlocked.Increment(ref slotOverwrites);
            }

            video.pending = message;
        }

        video.wake.Set();
    }

    // 상태 텍스트(JSON)를 보냅니다(화면 멈춤 등).
    public void PublishStatus(string json)
    {
        lastStatus = json;
        if (!live || closing)
        {
            return;
        }

        lock (video.writeLock)
        {
            video.pendingText = json;
        }

        video.wake.Set();
    }

    #region WebSocket

    private static void StartThread(ThreadStart body, string name)
    {
        new Thread(body) { IsBackground = true, Name = name }.Start();
    }

    private void ReadLoop(Connection connection)
    {
        try
        {
            byte[] header = new byte[14];
            byte[] mask = new byte[4];
            MemoryStream message = new MemoryStream();
            int messageOpcode = 0;
            while (true)
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

                if (length < 0 || length > MAX_CLIENT_MESSAGE_BYTES || message.Length + length > MAX_CLIENT_MESSAGE_BYTES)
                {
                    throw new IOException("message too large");
                }

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

                if (connection.closed)
                {
                    // 닫는 중: 상대가 닫을 때까지 읽어서 버리기만 합니다.
                    continue;
                }

                if (opcode == 0x8)
                {
                    // 폰이 닫기 프레임을 보냄(사람이 폰에서 연결을 끊음). 그냥 끊긴 것과 구별해 다시 걸지 않게 합니다.
                    remoteCloseCode = payload.Length >= 2 ? (payload[0] << 8) | payload[1] : CLOSE_NORMAL;
                    break;
                }

                if (opcode == 0x9)
                {
                    WriteMessage(connection, 0xA, payload, 0, payload.Length, null, 0);
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
                    HandleText(connection, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }

                message.SetLength(0);
            }
        }
        catch (Exception)
        {
            // 끊김, 태그가 틀린 레코드, 너무 긴 메시지: 전부 연결을 닫습니다.
        }

        // 한쪽 연결이 끝나면 폰과의 연결 전체를 닫습니다(다시 거는 것은 PlayLinkLink 가 함).
        // 폰이 닫기 프레임으로 끝냈으면 정상 종료로 답하고, 그냥 끊겼으면 "곧 다시 겁니다"로 닫습니다.
        Close(remoteCloseCode != 0 ? CLOSE_NORMAL : CLOSE_RECONNECT);
    }

    private void HandleText(Connection connection, string text)
    {
        if (connection.kind == ChannelKind.Video && text.StartsWith("{", StringComparison.Ordinal))
        {
            // 앱 능력 / 키프레임 요청 / 여기서 보기(고정 형식이라 문자열로 봅니다).
            // 값이 아니라 "t":"이름" 으로 봅니다: 나중에 더하는 메시지의 값에 key 같은 단어가 들어가도 오판하지 않게.
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
                // 폰이 하나뿐이라 자리를 다툴 상대가 없습니다. 앱이 답을 기다리므로 지금 상태를 다시 알립니다.
                lock (seatLock)
                {
                    if (!closing)
                    {
                        SendSeat();
                    }
                }
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
                    ackMs = PlayLink.ElapsedMs(sent);
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

        // Play 밖의 입력은 버립니다(ping 만 답함).
        if (!live && !text.StartsWith("{\"t\":\"p\"", StringComparison.Ordinal))
        {
            return;
        }

        string reply = PlayLinkInput.HandleMessage(connection, text);
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
            while (!connection.closed)
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
                            && PlayLink.ElapsedMs(connection.inFlight.Peek()) < MAX_H264_BACKLOG_MS)
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
                    SendTextNow(connection, text);
                }

                if (message != null)
                {
                    WriteMessage(connection, 0x2, message.header, 0, message.header.Length, message.jpeg, message.jpeg.Length);
                    onFrameSent?.Invoke(message.Length, PlayLink.ElapsedMs(message.captureTicks));
                }

                sentLast = message != null;
            }
        }
        catch (Exception)
        {
            Close(CLOSE_RECONNECT);
        }
    }

    // H.264 프레임 1장을 순서 큐에 넣습니다(버리지 않음).
    public void PublishOrdered(Connection connection, VideoMessage message)
    {
        lock (connection.writeLock)
        {
            if (!live || connection.closed)
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
                return PlayLink.ElapsedMs(connection.inFlight.Peek());
            }

            return connection.ordered.Count > 0 ? PlayLink.ElapsedMs(connection.ordered.Peek().queuedTicks) : 0.0;
        }
    }

    // 인코딩 전에 건너뛴 프레임을 전송 쪽 버림으로 셉니다(자동 프레임 간격이 봄).
    public void NoteDroppedFrame()
    {
        Interlocked.Increment(ref slotOverwrites);
    }

    // H.264 를 받을 수 있으면 영상 연결, 아니면 null(MJPEG로 보냄)
    public Connection H264Client()
    {
        return live && !closing && video.supportsH264 ? video : null;
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

    private void SendText(Connection connection, string text)
    {
        try
        {
            SendTextNow(connection, text);
        }
        catch (Exception)
        {
            Close(CLOSE_RECONNECT);
        }
    }

    private static void SendTextNow(Connection connection, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        WriteMessage(connection, 0x1, bytes, 0, bytes.Length, null, 0);
    }

    // 닫기 프레임을 "기다리지 않고" 보냅니다. 전송 스레드가 사라진 상대에게 쓰다가 막혀 쓰기 잠금을 쥐고 있으면
    // 잠금을 기다리지 않고 포기합니다(곧 소켓을 닫고, 그러면 막힌 쓰기도 풀립니다). 소켓 버퍼가 차 있어도 쓰지 않습니다.
    private static void TrySendCloseNoWait(Connection connection, ushort code)
    {
        bool taken = false;
        try
        {
            Monitor.TryEnter(connection.stream, CLOSE_LOCK_WAIT_MS, ref taken);
            if (!taken || !connection.client.Client.Poll(0, SelectMode.SelectWrite))
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

    // WebSocket 메시지 1개를 버퍼에 모아 쓰기 1번으로 보냅니다(암호화 계층도 쓰기 1번 = 소켓 쓰기 1번).
    // 프레임 헤더 · 영상 헤더 · 내용을 따로 쓰면 NoDelay라 각각 따로 나가고, adb 터널에서 뒤쪽이 약 40ms 묶입니다(개발 문서 12.7).
    // 전송 스레드(영상)와 수신 스레드(pong, ping 답장), 메인 스레드(seat)가 같은 연결에 쓰므로 메시지 단위로 잠급니다.
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

    // PC → 폰 프레임 헤더(마스크 없음)를 버퍼 맨 앞에 쓰고 그 길이를 돌려줍니다.
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

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
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

    // 닫기 코드를 보내고 두 연결을 모두 닫습니다. 아무 스레드에서 불러도 막히지 않고, 여러 번 불려도 됩니다.
    public void Close(ushort code)
    {
        lock (closedEvent)
        {
            if (closing)
            {
                return;
            }

            closing = true;
        }

        foreach (Connection connection in new[] { video, input })
        {
            TrySendCloseNoWait(connection, code);
            connection.closed = true;
            connection.wake.Set();
            try
            {
                // 보내는 쪽만 먼저 닫습니다(FIN). 수신 스레드는 상대가 닫을 때까지 읽어서 버립니다.
                connection.client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception)
            {
            }
        }

        StartThread(() =>
        {
            Thread.Sleep(CLOSE_LINGER_MS);
            foreach (Connection connection in new[] { video, input })
            {
                try
                {
                    connection.client.Close();
                }
                catch (Exception)
                {
                }
            }
        }, "PlayLink Close");

        // 눌려 있던 손가락을 취소하고 입력 연결 수에서 뺍니다.
        SetLive(false);
        closedEvent.Set();
    }

    #endregion
}
