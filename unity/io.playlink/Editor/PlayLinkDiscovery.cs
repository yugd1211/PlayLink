using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// 폰 찾기(PROTOCOL.md 1장). Start 중에 1초마다 UDP 브로드캐스트(255.255.255.255 와 각 네트워크의 브로드캐스트 주소)로 묻고,
// 답한 폰을 목록으로 유지합니다. 4초 동안 답이 없는 폰은 목록에서 뺍니다.
// PC 가 먼저 보낸 것에 대한 답이라 Windows 방화벽 설정 없이 받습니다.
// 브로드캐스트가 안 되는 망과 USB(adb forward)는 "따로 묻는 주소"에 TCP 로 묻습니다(who).
//   - 증명(mac)이 맞는 응답만 "페어링된 폰"으로 믿습니다. 증명이 없으면 "새 폰"으로만 보여 주고, 증명이 틀리면 버립니다.
// 스레드 둘(UDP, TCP who)이 돌고, 목록은 잠금 안에서만 바꿉니다. 결과는 PlayLinkLink 로 알립니다.
public static class PlayLinkDiscovery
{
    private const int QUERY_INTERVAL_MS = 1000;
    private const int RECEIVE_SLICE_MS = 200;
    private const double EXPIRE_MS = 4000.0;
    // TCP 로 묻는 주소는 3초마다 묻고, 세 번 놓치면 뺍니다.
    private const int PROBE_INTERVAL_MS = 3000;
    private const double PROBE_EXPIRE_MS = 10000.0;
    // 질문의 n 은 최근 몇 개까지 인정합니다(답이 다음 질문 뒤에 올 수 있음).
    private const int NONCE_HISTORY = 4;
    private const int MAX_REPLY_BYTES = 2048;
    private const int MAX_PHONES = 64;

    // 목록의 폰 하나(복사본으로 내줍니다)
    public sealed class Phone
    {
        public string id;
        // 폰이 알려 준 이름
        public string name;
        public string host;
        public int port;
        // 폰이 지금 보고 있는 PC 의 pcId(없으면 빈 값)
        public string busy;
        // 증명이 맞음(페어링된 폰)
        public bool verified;
        // USB(adb forward)로 찾았으면 기기 일련번호, 아니면 null
        public string usbSerial;
        internal long lastSeenTicks;
        internal bool want;
        internal bool probe;

        public bool IsUsb { get { return usbSerial != null; } }
    }

    // TCP 로 따로 묻는 주소 하나(직접 입력한 주소, USB 의 adb forward 포트)
    private sealed class ProbeTarget
    {
        public string host;
        public int port;
        public string usbSerial;
    }

    private static readonly object gate = new object();
    private static readonly Dictionary<string, Phone> phones = new Dictionary<string, Phone>();
    private static readonly List<ProbeTarget> probes = new List<ProbeTarget>();
    private static readonly List<string> nonces = new List<string>();
    private static volatile int generation;
    private static volatile bool running;
    private static volatile int udpPort = PlayLinkProto.DEFAULT_PORT;
    private static volatile string lastError;

    public static bool IsRunning { get { return running; } }
    public static string LastError { get { return lastError; } }

    // 폰의 UDP 포트(기본 7300). 시험에서만 바꿉니다.
    public static int UdpPort
    {
        get { return udpPort; }
        set { udpPort = value; }
    }

    public static void Start()
    {
        if (running)
        {
            return;
        }

        running = true;
        lastError = null;
        int mine = ++generation;
        new Thread(() => UdpLoop(mine)) { IsBackground = true, Name = "PlayLink Discovery" }.Start();
        new Thread(() => ProbeLoop(mine)) { IsBackground = true, Name = "PlayLink Discovery Who" }.Start();
    }

    public static void Stop()
    {
        running = false;
        generation++;
        lock (gate)
        {
            phones.Clear();
            nonces.Clear();
        }
    }

    // 지금 보이는 폰(복사본)
    public static List<Phone> Snapshot()
    {
        List<Phone> result = new List<Phone>();
        lock (gate)
        {
            foreach (Phone phone in phones.Values)
            {
                result.Add(Copy(phone));
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.name + a.id, b.name + b.id));
        return result;
    }

    // 이 폰의 지금 주소(증명이 맞는 것만). usb 가 null 이면 Wi-Fi 쪽, 아니면 그 USB 기기 쪽. 안 보이면 null
    public static Phone FindVerified(string phoneId, string usbSerial)
    {
        lock (gate)
        {
            return phones.TryGetValue(Key(phoneId, true, usbSerial), out Phone phone) ? Copy(phone) : null;
        }
    }

    // TCP 로 따로 물을 주소를 더합니다(직접 입력한 주소는 usbSerial = null). 같은 종류는 하나만 둡니다: 새로 넣으면 예전 것을 바꿉니다.
    public static void SetProbe(string host, int port, string usbSerial)
    {
        lock (gate)
        {
            probes.RemoveAll(p => p.usbSerial == usbSerial);
            if (!string.IsNullOrEmpty(host))
            {
                probes.Add(new ProbeTarget { host = host, port = port, usbSerial = usbSerial });
            }
        }
    }

    // who 의 답을 목록에 넣습니다(PlayLinkLink 가 USB 나 직접 입력한 주소에 처음 물었을 때도 부름).
    public static void Report(PlayLinkProto.Message reply, PlayLinkProto.ReplyKind kind, string host, int port, string usbSerial, bool probe)
    {
        if (kind == PlayLinkProto.ReplyKind.Bad)
        {
            return;
        }

        bool verified = kind == PlayLinkProto.ReplyKind.Paired;
        bool wantRose = false;
        Phone copy;
        lock (gate)
        {
            string key = Key(reply.ph, verified, usbSerial);
            if (!phones.TryGetValue(key, out Phone phone))
            {
                if (phones.Count >= MAX_PHONES)
                {
                    return;
                }

                phone = new Phone { id = reply.ph, verified = verified, usbSerial = usbSerial };
                phones[key] = phone;
            }

            // 페어링된 폰이 "연결해 달라"고 한 순간에만 알립니다(계속 true 인 동안 되풀이하지 않음). 증명이 없는 응답의 want 는 무시합니다.
            wantRose = verified && reply.want && !phone.want;
            phone.want = verified && reply.want;
            phone.name = PlayLinkCrypto.CleanName(reply.name);
            phone.host = host;
            phone.port = port;
            phone.busy = reply.busy ?? string.Empty;
            phone.probe = probe;
            phone.lastSeenTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            copy = Copy(phone);
        }

        PlayLinkLink.OnPhoneSeen(copy, kind, wantRose);
    }

    private static string Key(string phoneId, bool verified, string usbSerial)
    {
        return (verified ? "p|" : "n|") + phoneId + "|" + (usbSerial ?? string.Empty);
    }

    private static Phone Copy(Phone phone)
    {
        return new Phone
        {
            id = phone.id,
            name = phone.name,
            host = phone.host,
            port = phone.port,
            busy = phone.busy,
            verified = phone.verified,
            usbSerial = phone.usbSerial,
            lastSeenTicks = phone.lastSeenTicks,
            want = phone.want,
            probe = phone.probe,
        };
    }

    private static void UdpLoop(int mine)
    {
        UdpClient udp = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            udp.EnableBroadcast = true;
            // 닿을 수 없는 주소로 보낸 뒤 오는 ICMP 오류가 다음 수신을 예외로 끝내지 않게 합니다(Windows 의 SIO_UDP_CONNRESET).
            try
            {
                udp.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception)
            {
            }

            long nextQuery = 0;
            byte[] buffer = new byte[MAX_REPLY_BYTES];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (generation == mine)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now >= nextQuery)
                {
                    nextQuery = now + QUERY_INTERVAL_MS * System.Diagnostics.Stopwatch.Frequency / 1000;
                    SendQuery(udp);
                    Expire();
                }

                if (!udp.Client.Poll(RECEIVE_SLICE_MS * 1000, SelectMode.SelectRead))
                {
                    continue;
                }

                int count;
                try
                {
                    count = udp.Client.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException)
                {
                    // 너무 긴 응답이나 ICMP 오류: 이 응답만 버립니다.
                    continue;
                }

                IPEndPoint sender = from as IPEndPoint;
                PlayLinkProto.Message reply = PlayLinkProto.ParseReply(buffer, count);
                if (reply == null || sender == null)
                {
                    continue;
                }

                bool known;
                lock (gate)
                {
                    known = reply.n != null && nonces.Contains(reply.n);
                }

                PlayLinkProto.ReplyKind kind = PlayLinkProto.CheckReply(reply, known, PlayLinkIdentity.Current);
                // 접속할 곳은 응답을 보낸 주소와 응답의 port(폰의 TCP 포트)
                Report(reply, kind, sender.Address.ToString(), reply.port, null, false);
            }
        }
        catch (Exception e)
        {
            if (generation == mine)
            {
                lastError = "폰 찾기를 시작하지 못했습니다(" + e.GetType().Name + ").";
                PlayLinkLink.NoteChanged();
            }
        }
        finally
        {
            if (udp != null)
            {
                udp.Close();
            }
        }
    }

    private static void SendQuery(UdpClient udp)
    {
        string nonce = PlayLinkProto.NewNonce();
        List<ProbeTarget> direct;
        lock (gate)
        {
            nonces.Add(nonce);
            if (nonces.Count > NONCE_HISTORY)
            {
                nonces.RemoveAt(0);
            }

            direct = new List<ProbeTarget>(probes);
        }

        byte[] query = Encoding.UTF8.GetBytes(PlayLinkProto.BuildQuery(PlayLinkIdentity.Current, nonce));
        int port = udpPort;
        List<IPAddress> targets = BroadcastAddresses();
        // 직접 입력한 주소에는 같은 질문을 그 주소로도 보냅니다(브로드캐스트만 막힌 망).
        foreach (ProbeTarget probe in direct)
        {
            if (probe.usbSerial == null && IPAddress.TryParse(probe.host, out IPAddress address) && !targets.Contains(address))
            {
                targets.Add(address);
            }
        }

        foreach (IPAddress target in targets)
        {
            try
            {
                udp.Send(query, query.Length, new IPEndPoint(target, port));
            }
            catch (Exception)
            {
                // 그 네트워크로는 보낼 수 없음(어댑터가 내려감 등): 다른 주소는 계속 보냅니다.
            }
        }
    }

    // 255.255.255.255 와, 켜져 있는 IPv4 어댑터마다의 브로드캐스트 주소(주소 | ~마스크)
    private static List<IPAddress> BroadcastAddresses()
    {
        List<IPAddress> result = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                    {
                        continue;
                    }

                    byte[] address = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4 || (mask[0] | mask[1] | mask[2] | mask[3]) == 0 || mask[3] == 255)
                    {
                        continue;
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        address[i] |= (byte)~mask[i];
                    }

                    IPAddress broadcast = new IPAddress(address);
                    if (!result.Contains(broadcast))
                    {
                        result.Add(broadcast);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 어댑터 나열 실패(드묾): 255.255.255.255 로만 보냅니다.
        }

        return result;
    }

    private static void Expire()
    {
        bool removed = false;
        lock (gate)
        {
            List<string> expired = null;
            foreach (KeyValuePair<string, Phone> pair in phones)
            {
                double limit = pair.Value.probe ? PROBE_EXPIRE_MS : EXPIRE_MS;
                if (PlayLink.ElapsedMs(pair.Value.lastSeenTicks) > limit)
                {
                    (expired ?? (expired = new List<string>())).Add(pair.Key);
                }
            }

            if (expired != null)
            {
                removed = true;
                foreach (string key in expired)
                {
                    phones.Remove(key);
                }
            }
        }

        if (removed)
        {
            PlayLinkLink.NoteChanged();
        }
    }

    // 따로 묻는 주소에 3초마다 TCP 로 묻습니다. 지금 연결돼 있는 주소는 묻지 않습니다(연결이 살아 있는 것이 곧 답).
    private static void ProbeLoop(int mine)
    {
        while (generation == mine)
        {
            List<ProbeTarget> targets;
            lock (gate)
            {
                targets = new List<ProbeTarget>(probes);
            }

            foreach (ProbeTarget target in targets)
            {
                if (generation != mine)
                {
                    return;
                }

                if (PlayLinkLink.IsLinkedTo(target.host, target.port))
                {
                    Touch(target);
                    continue;
                }

                try
                {
                    PlayLinkProto.Message reply = PlayLinkProto.Who(target.host, target.port, PlayLinkIdentity.Current, out PlayLinkProto.ReplyKind kind);
                    Report(reply, kind, target.host, target.port, target.usbSerial, true);
                }
                catch (Exception)
                {
                    // 답이 없으면 목록에서 저절로 빠집니다.
                }
            }

            Thread.Sleep(PROBE_INTERVAL_MS);
        }
    }

    // 연결 중인 주소의 폰은 묻지 않아도 목록에 남깁니다.
    private static void Touch(ProbeTarget target)
    {
        lock (gate)
        {
            foreach (Phone phone in phones.Values)
            {
                if (phone.probe && phone.host == target.host && phone.port == target.port)
                {
                    phone.lastSeenTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
        }
    }
}
