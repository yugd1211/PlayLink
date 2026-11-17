using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// 인증된 앱 연결 하나의 정보. 기존 서버의 Connection.tag 에 담깁니다(tag 가 null 이면 인증 헤더 없는 연결 = Chrome 페이지).
public sealed class PlayLinkSession
{
    // did: 설치마다 한 번 만든 값. 영상과 입력 소켓이 같은 값이라 자리 배정에서 "같은 기기"로 묶습니다.
    public string deviceId;
    // dev: 창 표시용(없으면 "")
    public string deviceName;
    // app: 앱의 versionCode
    public int appVersion;
    // take=1: 사람이 방금 연결을 시작함. 0 이면 자동 재접속(자리를 뺏지 않음)
    public bool take;
    // 루프백으로 들어옴(USB adb reverse)
    public bool viaLoopback;
    public string remote;
    // 인증에 쓴 채널 문자열("video", "input", 또는 /x/<이름> 의 이름)
    public string channel;
    // 양쪽이 모두 밝힌 기능(설계 15.1 R1)
    public string[] caps = new string[0];

    public bool Has(string cap)
    {
        return Array.IndexOf(caps, cap) >= 0;
    }
}

// 입장 심사(설계 9.2). 앞단(Host)의 LAN 리스너와 기존 서버의 루프백 리스너가 함께 씁니다.
//   - Origin 이 있으면 localhost/127.0.0.1 의 페이지여야 함(다른 웹 페이지가 폰/PC 브라우저에서 이 서버의 소켓을 여는 것을 막음)
//   - X-PlayLink-Auth 가 없으면: 루프백 연결만 지금처럼 받음(Chrome 페이지). 루프백이 아니면 어느 리스너로 들어왔든 401(리뷰 M1)
//   - X-PlayLink-Auth 가 있으면: 101 + 서버 증명을 쓰고, 첫 메시지의 클라이언트 증명을 기한 안에 확인
public static class PlayLinkGate
{
    // Legacy   = 인증 헤더 없는 루프백 연결(호출한 쪽이 지금처럼 101 을 씀)
    // Admitted = 인증 완료(101 과 서버 증명을 이미 썼고 클라이언트 증명까지 확인함)
    // Rejected = 오류 응답 또는 닫기 프레임을 이미 씀(호출한 쪽은 소켓만 닫음)
    public enum Verdict
    {
        Legacy,
        Admitted,
        Rejected,
    }

    // 101 을 받고 아직 클라이언트 증명을 보내지 않은 연결 수 상한. 넘으면 503 busy
    public const int MAX_PENDING_UPGRADES = 8;
    // 연결을 받은 때부터 클라이언트 증명까지의 절대 기한
    public const int AUTH_DEADLINE_MS = 3000;
    public const ushort CLOSE_BAD_PROOF = 4401;
    public const ushort CLOSE_TOKEN_REVOKED = 4403;
    public const ushort CLOSE_RECONNECT = 4410;
    private const int MAX_AUTH_MESSAGE_BYTES = 256;
    private const int DEVICE_NAME_MAX = 24;

    [Serializable]
    private class AuthMessage
    {
        public string t;
        public string mac;
    }

    // 인증 대기 수는 리스너별로 셉니다(코드 리뷰 m2: LAN 쪽이 가득 차도 USB 로 붙는 앱은 막히지 않게). [0] = 루프백, [1] = 앞단
    private static readonly int[] pendingUpgrades = new int[2];
    private static long rejectedCount;

    // 인증 전/중에 거절한 수(누적, 진단용)
    public static long RejectedCount { get { return Interlocked.Read(ref rejectedCount); } }

    public static bool IsLoopback(TcpClient client)
    {
        try
        {
            IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
            return remote != null && PlayLinkNet.IsLoopback(remote.Address);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // frontListener: 앞단(LAN 주소) 리스너로 들어온 연결이면 true. 그 경우 원격 주소와 무관하게 인증이 필요합니다.
    // deadlineTicks: 연결을 받은 때 정한 기한(PlayLinkWs.DeadlineAfter). 0 이면 여기서부터 AUTH_DEADLINE_MS.
    public static Verdict Admit(TcpClient client, NetworkStream stream, string channel, Dictionary<string, string> headers,
        string acceptKey, bool frontListener, long deadlineTicks, out PlayLinkSession session)
    {
        session = null;
        bool loopback = !frontListener && IsLoopback(client);

        if (headers.TryGetValue("Origin", out string origin) && !string.IsNullOrEmpty(origin) && !(loopback && IsLocalPageOrigin(origin, LocalPort(client))))
        {
            return Reject(stream, "403 Forbidden", "bad_origin", "이 페이지에서는 연결할 수 없습니다.");
        }

        if (!headers.TryGetValue(PlayLinkAuth.HEADER_NAME, out string authHeader) || string.IsNullOrEmpty(authHeader))
        {
            return loopback ? Verdict.Legacy : Reject(stream, "401 Unauthorized", "auth_required", "앱의 QR 연결로만 접속할 수 있습니다.");
        }

        Dictionary<string, string> auth = PlayLinkAuth.ParseHeader(authHeader);
        if (Get(auth, "v") != PlayLinkAppInfo.AUTH_VERSION.ToString())
        {
            return Reject(stream, "426 Upgrade Required", "auth_version", "앱과 Unity 도구의 인증 버전이 다릅니다. 둘 다 최신으로 맞춰 주세요.");
        }

        string proto = Get(auth, "proto");
        if (proto != PlayLinkCapture.PROTOCOL_VERSION.ToString())
        {
            int.TryParse(proto, out int clientProto);
            return Reject(stream, "426 Upgrade Required", "proto_version", clientProto < PlayLinkCapture.PROTOCOL_VERSION
                ? "앱이 Unity 도구보다 오래됐습니다. 앱을 업데이트하세요." : "Unity 도구가 앱보다 오래됐습니다. 도구를 업데이트하세요.");
        }

        PlayLinkSettings.SecretSnapshot secret = PlayLinkSettings.Secret;
        string sid = Get(auth, "sid");
        if (secret == null || sid != secret.serverId)
        {
            return Reject(stream, "403 Forbidden", "unknown_server", "이 QR 은 다른 PC(또는 다른 프로젝트)의 것입니다. Unity 창의 QR 을 다시 찍으세요.");
        }

        string cn = Get(auth, "cn");
        string did = Get(auth, "did");
        if (!PlayLinkAuth.IsNonce(cn) || !PlayLinkAuth.IsDeviceId(did))
        {
            return Reject(stream, "400 Bad Request", "bad_request", "인증 헤더 형식이 잘못됐습니다.");
        }

        int pendingSlot = frontListener ? 1 : 0;
        if (Interlocked.Increment(ref pendingUpgrades[pendingSlot]) > MAX_PENDING_UPGRADES)
        {
            Interlocked.Decrement(ref pendingUpgrades[pendingSlot]);
            return Reject(stream, "503 Service Unavailable", "busy", "접속이 몰렸습니다. 잠시 뒤 다시 시도합니다.");
        }

        try
        {
            string sn = PlayLinkAuth.NewNonce();
            string serverProof = PlayLinkAuth.Proof(secret.key, PlayLinkAuth.ROLE_SERVER, channel, secret.serverId, cn, sn);
            string expected = PlayLinkAuth.Proof(secret.key, PlayLinkAuth.ROLE_CLIENT, channel, secret.serverId, cn, sn);
            string[] caps = Intersect(Get(auth, "caps"), PlayLinkAppInfo.SERVER_CAPS);
            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + "Sec-WebSocket-Accept: " + acceptKey + "\r\n"
                + PlayLinkAuth.HEADER_NAME + ": v=" + PlayLinkAppInfo.AUTH_VERSION + "; sn=" + sn + "; mac=" + serverProof
                + "; proto=" + PlayLinkCapture.PROTOCOL_VERSION + "; app=" + PlayLinkFiles.ApkVersionCode
                + "; caps=" + PlayLinkAppInfo.SERVER_CAPS + "; name=" + PlayLinkSettings.ProductName + "\r\n\r\n");
            stream.Write(response, 0, response.Length);

            long deadline = deadlineTicks != 0 ? deadlineTicks : PlayLinkWs.DeadlineAfter(AUTH_DEADLINE_MS);
            string mac = null;
            try
            {
                int opcode = PlayLinkWs.ReadFrame(client, stream, MAX_AUTH_MESSAGE_BYTES, deadline, out byte[] payload);
                if (opcode == PlayLinkWs.OPCODE_TEXT)
                {
                    AuthMessage message = JsonUtility.FromJson<AuthMessage>(Encoding.UTF8.GetString(payload));
                    if (message != null && message.t == "auth")
                    {
                        mac = message.mac;
                    }
                }
            }
            catch (Exception)
            {
                // 기한 초과, 끊김, 너무 긴 메시지, JSON 아님: 전부 "증명 없음"으로 봅니다.
                mac = null;
            }

            if (!PlayLinkAuth.FixedTimeEquals(mac, expected))
            {
                Interlocked.Increment(ref rejectedCount);
                PlayLinkWs.WriteCloseAndDrain(client, stream, CLOSE_BAD_PROOF);
                return Verdict.Rejected;
            }

            // 확인하는 사이 [새 QR 발급]으로 토큰이 바뀌었으면 받지 않습니다.
            if (!ReferenceEquals(PlayLinkSettings.Secret, secret))
            {
                Interlocked.Increment(ref rejectedCount);
                PlayLinkWs.TryWriteClose(stream, CLOSE_TOKEN_REVOKED);
                return Verdict.Rejected;
            }

            client.ReceiveTimeout = 0;
            int.TryParse(Get(auth, "app"), out int appVersion);
            session = new PlayLinkSession
            {
                deviceId = did,
                deviceName = PlayLinkAuth.SanitizeName(Get(auth, "dev"), DEVICE_NAME_MAX),
                appVersion = appVersion,
                take = Get(auth, "take") == "1",
                viaLoopback = loopback,
                remote = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "?",
                channel = channel,
                caps = caps,
            };
            return Verdict.Admitted;
        }
        finally
        {
            Interlocked.Decrement(ref pendingUpgrades[pendingSlot]);
        }
    }

    private static int LocalPort(TcpClient client)
    {
        try
        {
            IPEndPoint local = client.Client.LocalEndPoint as IPEndPoint;
            return local != null ? local.Port : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // HTTP 요청의 Host 헤더가 이 PC 를 가리키는가(localhost 또는 127.0.0.1, 포트는 무엇이든). 헤더가 없으면 true(브라우저는 항상 보냄).
    // 루프백 리스너의 페이지와 /stats, /debug 를 다른 이름으로 부르는 요청(DNS 리바인딩)을 거르는 데 씁니다(코드 리뷰 m4).
    public static bool IsLocalHostHeader(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return true;
        }

        int colon = host.IndexOf(':');
        string name = colon >= 0 ? host.Substring(0, colon) : host;
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1";
    }

    // 페이지의 Origin 이 이 서버가 내준 페이지의 것인가: http://localhost:<이 연결이 들어온 포트> 또는 http://127.0.0.1:<같은 포트>.
    // 포트까지 봅니다(코드 리뷰 m3: 이 PC 나 adb reverse 로 이어진 폰의 다른 localhost 웹 앱이 인증 없이 소켓을 열 수 있었음).
    // 이 도구의 adb reverse 는 항상 같은 포트 번호로 걸므로(tcp:P tcp:P) 폰의 페이지도 같은 포트입니다.
    private static bool IsLocalPageOrigin(string origin, int serverPort)
    {
        const string scheme = "http://";
        if (!origin.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = origin.Substring(scheme.Length);
        int colon = host.IndexOf(':');
        int originPort = 80;
        if (colon >= 0)
        {
            string port = host.Substring(colon + 1);
            host = host.Substring(0, colon);
            if (port.Length == 0 || port.Length > 5 || !int.TryParse(port, out originPort))
            {
                return false;
            }
        }

        if (originPort != serverPort)
        {
            return false;
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";
    }

    private static Verdict Reject(NetworkStream stream, string status, string code, string message)
    {
        Interlocked.Increment(ref rejectedCount);
        try
        {
            PlayLinkFiles.WriteError(stream, status, code, message);
        }
        catch (Exception)
        {
        }

        return Verdict.Rejected;
    }

    private static string Get(Dictionary<string, string> map, string name)
    {
        return map.TryGetValue(name, out string value) ? value : null;
    }

    // 양쪽이 모두 적은 기능만 남깁니다. 모르는 이름은 무시합니다.
    private static string[] Intersect(string clientCaps, string serverCaps)
    {
        if (string.IsNullOrEmpty(clientCaps))
        {
            return new string[0];
        }

        List<string> result = new List<string>();
        string[] server = serverCaps.Split(',');
        foreach (string part in clientCaps.Split(','))
        {
            string cap = part.Trim();
            if (cap.Length > 0 && Array.IndexOf(server, cap) >= 0 && !result.Contains(cap))
            {
                result.Add(cap);
            }
        }

        return result.ToArray();
    }
}
