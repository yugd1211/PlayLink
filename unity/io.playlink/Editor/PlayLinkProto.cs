using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// 연결 프로토콜(docs/PROTOCOL.md)의 PC 쪽(거는 쪽): TCP 접속, who, 키 교환, 마무리 메시지.
// 전부 부르는 스레드에서 막히며 돕니다(메인 스레드에서 부르지 않음). 키와 비밀 값은 로그에 적지 않습니다.
public static class PlayLinkProto
{
    public const int VERSION = 1;
    public const int DEFAULT_PORT = 7300;
    // 영상/입력 메시지 형식 번호와 PC 가 밝히는 기능(fin)
    public const string CAPS = "h264,seat,take";
    public const string CHANNEL_VIDEO = "video";
    public const string CHANNEL_INPUT = "input";
    public const string CHANNEL_PAIR = "pair";
    public const string MODE_PAIRED = "paired";
    public const string MODE_QR = "qr";
    public const string MODE_SAS = "sas";
    private const int MAX_HANDSHAKE_MESSAGE = 2048;
    public const int CONNECT_TIMEOUT_MS = 3000;
    private const int WHO_TIMEOUT_MS = 3000;
    // 폰이 "바꿀까요?"를 묻는 동안 m2 를 30초까지 미룰 수 있습니다.
    private const int HELLO_TIMEOUT_MS = 40000;
    private const int SAS_TIMEOUT_MS = 70000;
    private const int FIN_TIMEOUT_MS = 5000;
    private const int SAS_POLL_MS = 50;
    // 소켓 쓰기가 이 시간 넘게 막히면 끊습니다(Wi-Fi 상대가 FIN 없이 사라졌을 때 전송 스레드가 오래 붙잡히지 않게).
    public const int SEND_TIMEOUT_MS = 3000;

    // 우리 쪽에서 붙이는 오류 코드(폰이 보내는 코드: version, bad_message, unknown_pc, busy, proto, rejected)
    public const string ERROR_NETWORK = "network";
    public const string ERROR_AUTH_FAILED = "auth_failed";
    public const string ERROR_CANCELLED = "cancelled";

    // 탐색 응답, m2, 마무리 메시지를 한 형식으로 읽습니다(없는 칸은 기본값).
    [Serializable]
    public class Message
    {
        public string t;
        public int v;
        public string ph;
        public string name;
        public int app;
        public string epk;
        public string nr;
        public string code;
        public string msg;
        public int port;
        public string busy;
        public bool want;
        public string n;
        public string mac;
        public bool qr;
        public int proto;
        public string caps;
    }

    public sealed class ProtoException : Exception
    {
        public readonly string code;

        public ProtoException(string code, string message) : base(string.IsNullOrEmpty(message) ? code : message)
        {
            this.code = code;
        }
    }

    // 응답의 증명을 확인한 결과
    public enum ReplyKind
    {
        // 형식이 틀리거나, 우리 질문의 답이 아니거나, 증명이 틀림 → 버림
        Bad,
        // 증명 없음 → "새 폰"으로만 보여 줌(want 무시)
        None,
        Paired,
        // 이 PC 의 QR 을 찍은 폰
        Qr,
    }

    // 키 교환이 끝난 연결 하나
    public sealed class Session
    {
        public TcpClient client;
        public PlayLinkSecureStream stream;
        public string phoneId;
        public string phoneName;
        public int appVersion;
        public string caps;
        // ch 가 pair 일 때 저장할 페어링 키
        public byte[] pairKey;
    }

    // 6자리 숫자 확인(sas). 숫자가 정해지면 onDigits 가 불리고, decision 이 0(아직) → 1(확인) / -1(취소)로 바뀌기를 기다립니다.
    public sealed class SasPrompt
    {
        public Action<string, string, string> onDigits;
        public Func<int> decision;
    }

    #region 평문 틀과 접속

    public static TcpClient Connect(string host, int port, int timeoutMs)
    {
        TcpClient client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            IAsyncResult pending = client.BeginConnect(host, port, null, null);
            if (!pending.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                throw new ProtoException(ERROR_NETWORK, "폰이 응답하지 않습니다.");
            }

            client.EndConnect(pending);
            client.NoDelay = true;
            client.SendTimeout = SEND_TIMEOUT_MS;
            EnableKeepAlive(client.Client);
            return client;
        }
        catch (Exception e)
        {
            client.Close();
            if (e is ProtoException)
            {
                throw;
            }

            throw new ProtoException(ERROR_NETWORK, "폰에 접속하지 못했습니다(" + e.GetType().Name + ").");
        }
    }

    // Play 밖에서는 보내는 것이 없어 폰이 사라져도(Wi-Fi 끊김, 잠듦) 알 수 없습니다. TCP keepalive 로 약 10초 안에 끊긴 것을 알게 합니다.
    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // onoff(4B) ‖ 유휴 시간 ms(4B) ‖ 다시 보내는 간격 ms(4B), 리틀엔디언
            byte[] values = new byte[12];
            BitConverter.GetBytes(1u).CopyTo(values, 0);
            BitConverter.GetBytes(5000u).CopyTo(values, 4);
            BitConverter.GetBytes(1000u).CopyTo(values, 8);
            socket.IOControl(IOControlCode.KeepAliveValues, values, null);
        }
        catch (Exception)
        {
            // 안 되는 환경이면 쓰기 제한 시간(SEND_TIMEOUT_MS)만으로 동작합니다.
        }
    }

    // 평문 틀: 길이 2바이트(빅엔디언) + UTF-8 JSON. 보낸 바이트를 돌려줍니다(키 계산의 m1, m3).
    private static byte[] WritePlain(Stream stream, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MAX_HANDSHAKE_MESSAGE)
        {
            throw new ProtoException("bad_message", "메시지가 너무 깁니다.");
        }

        byte[] frame = new byte[body.Length + 2];
        frame[0] = (byte)(body.Length >> 8);
        frame[1] = (byte)body.Length;
        Buffer.BlockCopy(body, 0, frame, 2, body.Length);
        stream.Write(frame, 0, frame.Length);
        return body;
    }

    private static byte[] ReadPlain(Stream stream)
    {
        byte[] head = new byte[2];
        ReadExactly(stream, head, 2);
        int length = (head[0] << 8) | head[1];
        if (length == 0 || length > MAX_HANDSHAKE_MESSAGE)
        {
            throw new IOException("bad handshake length");
        }

        byte[] body = new byte[length];
        ReadExactly(stream, body, length);
        return body;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                throw new IOException("connection closed");
            }

            offset += read;
        }
    }

    private static Message Parse(byte[] body)
    {
        Message message = JsonUtility.FromJson<Message>(Encoding.UTF8.GetString(body));
        if (message == null)
        {
            throw new IOException("bad json");
        }

        return message;
    }

    // 우리가 만드는 JSON 에 넣는 글자. 이름은 CleanName 을 거쳐 따옴표나 역슬래시가 없지만 한 번 더 막습니다.
    private static string Quote(string text)
    {
        return "\"" + (text ?? string.Empty).Replace("\\", string.Empty).Replace("\"", string.Empty) + "\"";
    }

    #endregion

    #region 탐색 응답

    // 탐색 질문(UDP)
    public static string BuildQuery(PlayLinkIdentity.Snapshot identity, string nonce)
    {
        return "{\"t\":\"q\",\"v\":" + VERSION + ",\"pc\":" + Quote(identity.pcId) + ",\"name\":" + Quote(identity.pcName) + ",\"n\":" + Quote(nonce) + "}";
    }

    public static string NewNonce()
    {
        return PlayLinkCrypto.Base64Url(PlayLinkCrypto.RandomBytes(8));
    }

    // UDP 응답 바이트를 읽습니다. JSON 이 아니면 null
    public static Message ParseReply(byte[] bytes, int count)
    {
        try
        {
            return JsonUtility.FromJson<Message>(Encoding.UTF8.GetString(bytes, 0, count));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 응답의 증명을 확인합니다(기준 구현의 checkReply). nonceMatches: 응답의 n 이 우리 질문의 것인가.
    public static ReplyKind CheckReply(Message reply, bool nonceMatches, PlayLinkIdentity.Snapshot identity)
    {
        if (reply == null || reply.t != "r" || reply.v != VERSION || !nonceMatches || !PlayLinkCrypto.IsId(reply.ph)
            || reply.port < 1 || reply.port > 65535)
        {
            return ReplyKind.Bad;
        }

        if (string.IsNullOrEmpty(reply.mac))
        {
            return ReplyKind.None;
        }

        if (reply.qr)
        {
            byte[] qrKey = PlayLinkCrypto.Sha256(identity.qrSecret);
            return MacMatches(reply, qrKey, identity) ? ReplyKind.Qr : ReplyKind.Bad;
        }

        // 이 PC 에 그 폰의 키가 없으면(이 PC 가 폰을 잊음) 증명을 확인할 길이 없으므로 "새 폰"으로 보여 줍니다.
        // 키가 있는데 증명이 틀리면 버립니다.
        PlayLinkIdentity.Phone phone = identity.Find(reply.ph);
        if (phone == null)
        {
            return ReplyKind.None;
        }

        return MacMatches(reply, phone.key, identity) ? ReplyKind.Paired : ReplyKind.Bad;
    }

    private static bool MacMatches(Message reply, byte[] key, PlayLinkIdentity.Snapshot identity)
    {
        string expected = PlayLinkCrypto.DiscoveryMac(key, identity.pcId, reply.ph, reply.name ?? string.Empty, reply.port, reply.want, reply.n);
        return PlayLinkCrypto.FixedTimeEquals(reply.mac, expected);
    }

    // TCP 로 묻기(who): 탐색이 안 되는 망이나 USB(adb forward). 실패하면 ProtoException.
    public static Message Who(string host, int port, PlayLinkIdentity.Snapshot identity, out ReplyKind kind)
    {
        TcpClient client = Connect(host, port, CONNECT_TIMEOUT_MS);
        try
        {
            client.ReceiveTimeout = WHO_TIMEOUT_MS;
            NetworkStream stream = client.GetStream();
            string nonce = NewNonce();
            WritePlain(stream, "{\"t\":\"who\",\"v\":" + VERSION + ",\"pc\":" + Quote(identity.pcId) + ",\"name\":" + Quote(identity.pcName) + ",\"n\":" + Quote(nonce) + "}");
            Message reply = Parse(ReadPlain(stream));
            if (reply.t == "err")
            {
                throw new ProtoException(string.IsNullOrEmpty(reply.code) ? "bad_message" : reply.code, reply.msg);
            }

            kind = CheckReply(reply, reply.n == nonce, identity);
            return reply;
        }
        catch (Exception e)
        {
            if (e is ProtoException)
            {
                throw;
            }

            throw new ProtoException(ERROR_NETWORK, "PlayLink 앱이 응답하지 않습니다(" + e.GetType().Name + ").");
        }
        finally
        {
            client.Close();
        }
    }

    #endregion

    #region 키 교환

    // 키 교환과 마무리(fin)까지 합니다. 성공하면 Session(소켓은 열려 있음), 실패하면 소켓을 닫고 ProtoException.
    //   mode paired: secret = 페어링 키 / qr: secret = QR 비밀 / sas: secret 없음, prompt 필요
    //   cancelled: true 를 돌려주면 숫자 확인을 기다리다 그만둡니다(소켓을 닫아 끊는 것은 부르는 쪽이 따로 함).
    public static Session Handshake(TcpClient client, PlayLinkIdentity.Snapshot identity, string channel, string mode, byte[] secret, bool take,
        SasPrompt prompt, Func<bool> cancelled)
    {
        PlayLinkSecureStream secure = null;
        bool keyed = false;
        try
        {
            NetworkStream stream = client.GetStream();
            byte[] privateKey = PlayLinkCrypto.NewPrivateKey();
            byte[] publicKey = PlayLinkCrypto.PublicKey(privateKey);
            byte[] nonce = PlayLinkCrypto.RandomBytes(16);
            byte[] m1 = WritePlain(stream, "{\"t\":\"hello\",\"v\":" + VERSION + ",\"pc\":" + Quote(identity.pcId) + ",\"name\":" + Quote(identity.pcName)
                + ",\"ch\":" + Quote(channel) + ",\"mode\":" + Quote(mode) + ",\"take\":" + (take ? "true" : "false")
                + ",\"ni\":" + Quote(PlayLinkCrypto.Base64Url(nonce)) + ",\"commit\":" + Quote(PlayLinkCrypto.Base64Url(PlayLinkCrypto.Sha256(publicKey, nonce))) + "}");

            client.ReceiveTimeout = HELLO_TIMEOUT_MS;
            byte[] m2 = ReadPlain(stream);
            Message hello = Parse(m2);
            if (hello.t == "err")
            {
                throw new ProtoException(string.IsNullOrEmpty(hello.code) ? "bad_message" : hello.code, hello.msg);
            }

            if (hello.t != "hello" || hello.v != VERSION)
            {
                throw new ProtoException("version", null);
            }

            byte[] peerKey = PlayLinkCrypto.FromBase64Url(hello.epk);
            if (!PlayLinkCrypto.IsId(hello.ph) || peerKey == null)
            {
                throw new ProtoException("bad_message", null);
            }

            // 상대 점이 곡선 위에 없으면 여기서 예외(키를 보내기 전에 끊음)
            byte[] shared = PlayLinkCrypto.SharedSecret(privateKey, peerKey);
            byte[] m3 = WritePlain(stream, "{\"t\":\"key\",\"epk\":" + Quote(PlayLinkCrypto.Base64Url(publicKey)) + "}");
            PlayLinkCrypto.Keys keys = PlayLinkCrypto.DeriveKeys(PlayLinkCrypto.TranscriptHash(m1, m2, m3), shared, PlayLinkCrypto.Psk(mode, secret));
            secure = new PlayLinkSecureStream(stream, keys.encI2R, keys.macI2R, keys.encR2I, keys.macR2I);
            keyed = true;

            Session session = new Session
            {
                client = client,
                stream = secure,
                phoneId = hello.ph,
                phoneName = PlayLinkCrypto.CleanName(hello.name),
                appVersion = hello.app,
                pairKey = keys.pair,
            };

            if (mode == MODE_SAS)
            {
                ConfirmDigits(client, secure, keys.sas, session, prompt, cancelled);
            }

            WriteJson(secure, "{\"t\":\"fin\",\"proto\":" + PlayLinkCapture.PROTOCOL_VERSION + ",\"caps\":" + Quote(CAPS) + "}");
            client.ReceiveTimeout = FIN_TIMEOUT_MS;
            Message fin = Parse(secure.ReadMessage());
            if (fin.t == "err")
            {
                throw new ProtoException(string.IsNullOrEmpty(fin.code) ? "bad_message" : fin.code, fin.msg);
            }

            if (fin.t != "fin")
            {
                throw new ProtoException("bad_message", null);
            }

            if (fin.app > 0)
            {
                session.appVersion = fin.app;
            }

            session.caps = fin.caps ?? string.Empty;
            client.ReceiveTimeout = 0;
            return session;
        }
        catch (Exception e)
        {
            if (secure != null)
            {
                secure.Dispose();
            }

            client.Close();
            if (e is ProtoException)
            {
                throw;
            }

            // 키를 정한 뒤에 폰이 그냥 끊으면 키가 서로 다른 것입니다(페어링 키나 QR 비밀이 다름).
            throw new ProtoException(keyed ? ERROR_AUTH_FAILED : ERROR_NETWORK, null);
        }
    }

    // sas: 양쪽에 같은 숫자를 보이고, 폰의 [허용](ok)과 PC 의 [확인]이 모두 있어야 넘어갑니다(순서 무관, 70초 기한).
    // PC 의 ok 는 사람이 누른 즉시 보냅니다.
    private static void ConfirmDigits(TcpClient client, PlayLinkSecureStream secure, string digits, Session session, SasPrompt prompt, Func<bool> cancelled)
    {
        prompt.onDigits(digits, session.phoneId, session.phoneName);
        long deadline = DateTime.UtcNow.Ticks + SAS_TIMEOUT_MS * TimeSpan.TicksPerMillisecond;
        bool mine = false;
        bool theirs = false;
        client.ReceiveTimeout = FIN_TIMEOUT_MS;
        while (!mine || !theirs)
        {
            if (DateTime.UtcNow.Ticks > deadline || (cancelled != null && cancelled()))
            {
                TryWriteJson(secure, "{\"t\":\"err\",\"code\":\"rejected\"}");
                throw new ProtoException(DateTime.UtcNow.Ticks > deadline ? "timeout" : ERROR_CANCELLED, null);
            }

            if (!mine)
            {
                int decision = prompt.decision();
                if (decision < 0)
                {
                    TryWriteJson(secure, "{\"t\":\"err\",\"code\":\"rejected\"}");
                    throw new ProtoException(ERROR_CANCELLED, null);
                }

                if (decision > 0)
                {
                    WriteJson(secure, "{\"t\":\"ok\"}");
                    mine = true;
                }
            }

            if (!theirs && client.Client.Poll(SAS_POLL_MS * 1000, SelectMode.SelectRead))
            {
                Message answer;
                try
                {
                    answer = Parse(secure.ReadMessage());
                }
                catch (IOException)
                {
                    // 숫자 확인 중에 폰이 끊음(폰에서 취소했거나 기한이 지남). sas 는 키가 어긋날 일이 없으므로 거절로 봅니다.
                    throw new ProtoException("rejected", null);
                }

                if (answer.t != "ok")
                {
                    throw new ProtoException(string.IsNullOrEmpty(answer.code) ? "rejected" : answer.code, answer.msg);
                }

                theirs = true;
            }
            else if (theirs)
            {
                Thread.Sleep(SAS_POLL_MS);
            }
        }
    }

    private static void WriteJson(PlayLinkSecureStream secure, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        secure.Write(bytes, 0, bytes.Length);
    }

    private static void TryWriteJson(PlayLinkSecureStream secure, string json)
    {
        try
        {
            WriteJson(secure, json);
        }
        catch (Exception)
        {
        }
    }

    #endregion

    // QR 내용: playlink://pair?v=1&pc=<pcId>&n=<URL 인코딩한 PC 이름>&s=<QR 비밀 16B base64url>. 비밀이 들어 있으므로 글자로 내보내지 않습니다.
    public static string BuildQrText(PlayLinkIdentity.Snapshot identity)
    {
        return PlayLinkAppInfo.SCHEME + "://pair?v=" + VERSION + "&pc=" + identity.pcId + "&n=" + Uri.EscapeDataString(identity.pcName)
            + "&s=" + PlayLinkCrypto.Base64Url(identity.qrSecret);
    }
}
