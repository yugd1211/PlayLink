using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

// QR 에 어느 주소를 넣을지 고릅니다(설계 9.6, 조사 3.5). 어느 스레드에서 불러도 됩니다.
// PC에 어댑터가 여러 개일 때(VPN, WSL, 가상 스위치) 폰이 닿을 수 없는 주소를 QR 에 넣지 않기 위한 규칙입니다.
public static class PlayLinkNet
{
    public const int MAX_EXTRA_ADDRESSES = 3;

    // 이름이나 설명에 이 글자가 있으면 가상/터널 어댑터로 보고 뺍니다.
    private static readonly string[] EXCLUDED_WORDS =
    {
        "vEthernet", "Hyper-V", "WSL", "VirtualBox", "VMware", "Loopback", "TAP", "VPN", "WireGuard", "Tailscale", "ZeroTier",
    };

    public sealed class Choice
    {
        // 주 주소(QR 의 호스트). 쓸 수 있는 주소가 없으면 null
        public string primary;
        // QR 의 a= 에 넣는 나머지(최대 3개). Host 는 주 주소와 이 주소들에만 듣습니다.
        public string[] extras = new string[0];
        // 창의 [다른 주소] 목록: 제외되지 않은 주소 전부(점수 높은 순)
        public string[] selectable = new string[0];

        public bool SameAs(Choice other)
        {
            return other != null && primary == other.primary && string.Join(",", extras) == string.Join(",", other.extras)
                && string.Join(",", selectable) == string.Join(",", other.selectable);
        }
    }

    private struct Candidate
    {
        public string address;
        public int score;
    }

    // selected: 창에서 고른 주소(빈 값이면 자동). phoneWifiAddress: adb 로 읽은 폰 wlan0 주소(모르면 null).
    public static Choice Choose(string selected, string phoneWifiAddress)
    {
        List<Candidate> candidates = new List<Candidate>();
        IPAddress phone = null;
        if (!string.IsNullOrEmpty(phoneWifiAddress))
        {
            IPAddress.TryParse(phoneWifiAddress, out phone);
        }

        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || IsExcluded(adapter))
                {
                    continue;
                }

                IPInterfaceProperties properties = adapter.GetIPProperties();
                bool hasGateway = false;
                foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                {
                    if (gateway.Address != null && gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))
                    {
                        hasGateway = true;
                    }
                }

                bool physical = adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet;
                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    IPAddress address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || !IsPrivate(address))
                    {
                        continue;
                    }

                    int score = 0;
                    // 1순위: 폰의 Wi-Fi 와 같은 서브넷(USB 가 붙어 있을 때만 알 수 있음)
                    if (phone != null && SameSubnet(address, phone, unicast.IPv4Mask))
                    {
                        score += 100;
                    }

                    // 2순위: 기본 게이트웨이가 있는 물리 어댑터
                    if (hasGateway)
                    {
                        score += 10;
                    }

                    if (physical)
                    {
                        score += 5;
                    }

                    candidates.Add(new Candidate { address = address.ToString(), score = score });
                }
            }
        }
        catch (Exception)
        {
            // 어댑터 나열 실패(드묾): 주소 없음으로 돌려주고 창이 이유를 보입니다.
        }

        // 점수 높은 순, 같으면 주소 글자 순(결과가 부를 때마다 달라지지 않게)
        candidates.Sort((a, b) => a.score != b.score ? b.score.CompareTo(a.score) : string.CompareOrdinal(a.address, b.address));
        List<string> ordered = new List<string>();
        foreach (Candidate candidate in candidates)
        {
            if (!ordered.Contains(candidate.address))
            {
                ordered.Add(candidate.address);
            }
        }

        Choice choice = new Choice { selectable = ordered.ToArray() };
        if (ordered.Count == 0)
        {
            return choice;
        }

        choice.primary = !string.IsNullOrEmpty(selected) && ordered.Contains(selected) ? selected : ordered[0];
        List<string> extras = new List<string>();
        foreach (string address in ordered)
        {
            if (address != choice.primary && extras.Count < MAX_EXTRA_ADDRESSES)
            {
                extras.Add(address);
            }
        }

        choice.extras = extras.ToArray();
        return choice;
    }

    public static bool IsLoopback(IPAddress address)
    {
        if (address == null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }

    private static bool IsExcluded(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel
            || adapter.NetworkInterfaceType == NetworkInterfaceType.Ppp)
        {
            return true;
        }

        string text = (adapter.Name ?? string.Empty) + " " + (adapter.Description ?? string.Empty);
        foreach (string word in EXCLUDED_WORDS)
        {
            if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // 10/8, 172.16/12, 192.168/16 만(169.254 링크 로컬과 공인 주소는 QR 에 넣지 않음)
    private static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        if (mask == null || b.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] x = a.GetAddressBytes();
        byte[] y = b.GetAddressBytes();
        byte[] m = mask.GetAddressBytes();
        if (m.Length != 4 || (m[0] | m[1] | m[2] | m[3]) == 0)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if ((x[i] & m[i]) != (y[i] & m[i]))
            {
                return false;
            }
        }

        return true;
    }
}
