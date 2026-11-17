using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Unity에 들어 있는 adb로 폰 상태 확인, adb reverse, 폰 Chrome에서 페이지 열기, 무선 adb 연결을 합니다.
// adb는 수 초가 걸릴 수 있어서 전부 백그라운드 스레드에서 돌리고, 결과(문자열/상태)만 정적 필드에 남깁니다.
// 무선: PC가 폰에 adb로 접속(PC → 폰)하고 그 위에 adb reverse를 겁니다. 폰은 USB와 똑같이 http://localhost:포트/ 로 열고,
// PC 쪽으로 들어오는 연결이 없어서 Windows 방화벽을 바꾸지 않아도 됩니다(Wi-Fi 직접 접속과 다른 점).
//   - USB 폰을 무선으로 전환: adb tcpip 5555 → 폰 Wi-Fi IP 확인 → adb connect IP:5555 (폰을 재부팅하기 전까지 유지)
//   - Android 11+ 무선 디버깅: adb pair IP:페어링포트 코드 → adb connect IP:무선디버깅포트
public static class GameViewRemoteAdb
{
    private const int ADB_TIMEOUT_MS = 10000;
    // adb tcpip로 여는 폰 쪽 포트
    private const int TCPIP_PORT = 5555;
    // adb tcpip 뒤 폰 adbd가 다시 뜨기를 기다리는 시간
    private const int TCPIP_RESTART_MS = 2500;

    public enum Level
    {
        Ok,
        Info,
        Warning,
    }

    // 창에 보여 줄 USB(adb) 상태 한 줄
    public struct Status
    {
        public Level level;
        public string message;
        // 기기가 정확히 1대이고 adb reverse만 빠졌을 때 true(창에 "adb reverse" 버튼 강조)
        public bool reverseMissing;
    }

    private struct Device
    {
        public string serial;
        public string state;

        // IP:포트 또는 무선 디버깅 mDNS 이름(adb-…._adb-tls-connect._tcp)이면 무선
        public bool IsWireless { get { return serial.Contains(":") || serial.Contains("._adb-tls-connect."); } }
    }

    public static readonly string AdbPath = Path.Combine(EditorApplication.applicationContentsPath,
        "PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe");

    // adb 명령은 한 번에 하나씩 돌립니다(상태 확인 중에 누른 버튼은 끝나기를 기다렸다가 실행).
    // internal: GameViewRemoteAppAdb 의 USB 버튼도 같은 잠금을 잡아, 창의 주기적인 상태 확인과 adb 명령이 겹쳐 돌지 않게 합니다(코드 리뷰 M4).
    internal static readonly object runLock = new object();
    private static int busy;
    private static volatile string lastOutput = string.Empty;
    private static volatile string connectedAddress;
    private static Status lastStatus = new Status { level = Level.Info, message = "확인 전" };
    private static readonly object statusLock = new object();

    public static bool Busy { get { return Volatile.Read(ref busy) > 0; } }
    public static string LastOutput { get { return lastOutput; } }
    // 마지막으로 무선 연결에 성공한 주소(IP:포트). 창이 EditorPrefs에 저장해 다음 Start 때 자동 재연결에 씁니다.
    public static string ConnectedAddress { get { return connectedAddress; } }

    public static Status LastStatus
    {
        get
        {
            lock (statusLock)
            {
                return lastStatus;
            }
        }
    }

    // 기기 목록과 adb reverse 상태를 다시 확인합니다(이미 돌고 있으면 건너뜀).
    // repair가 true면(서버가 돌고 "Start 때 adb reverse 자동"이 켜져 있을 때) 터널이 없거나 USB가 있는데 무선으로 가고 있으면 다시 겁니다.
    // USB 케이블을 뽑아 USB 쪽 터널이 사라졌을 때 무선으로 넘기고, 케이블을 다시 꽂았을 때 USB로 되돌리는 경로입니다.
    public static void RefreshStatusAsync(int port, bool repair = false)
    {
        if (Busy)
        {
            return;
        }

        RunInBackground(() =>
        {
            UpdateStatus(port);
            if (repair && LastStatus.reverseMissing)
            {
                Debug.Log($"{GameViewRemote.LOG_PREFIX} adb reverse repaired: {ReversePreferUsb(port).Replace("\n", " / ")}");
                UpdateStatus(port);
            }
        }, false);
    }

    // 서버 Start 때 자동으로 부릅니다. 연결된 기기가 없고 기억해 둔 무선 주소가 있으면 먼저 다시 연결하고,
    // 연결된 기기(USB/무선) 모두에 adb reverse tcp:port tcp:port를 겁니다.
    public static void AutoReverseAsync(int port, string savedWirelessAddress)
    {
        RunInBackground(() =>
        {
            List<Device> devices = ListReadyDevices(out string problem);
            if (devices.Count == 0 && !string.IsNullOrEmpty(savedWirelessAddress))
            {
                string connect = Connect(savedWirelessAddress);
                Debug.Log($"{GameViewRemote.LOG_PREFIX} auto adb connect {savedWirelessAddress}: {connect}");
                devices = ListReadyDevices(out problem);
            }

            if (devices.Count == 0)
            {
                Debug.Log($"{GameViewRemote.LOG_PREFIX} auto adb reverse skipped: {problem}");
            }

            if (devices.Count > 0)
            {
                Debug.Log($"{GameViewRemote.LOG_PREFIX} auto {ReversePreferUsb(port).Replace("\n", " / ")}");
            }

            UpdateStatus(port);
        }, false);
    }

    // adb reverse tcp:port tcp:port 를 겁니다. 같은 폰이 USB와 무선으로 함께 잡혀 있으면 한쪽에만 겁니다:
    // 기본은 USB, 창의 "영상 경로: 무선 우선"(GameViewRemote.PreferWireless)을 켜면 무선.
    // 폰의 localhost:port 는 하나뿐이라 나중에 건 쪽이 가져가는데, 예전에는 USB → 무선 순서로 모두 걸어서 케이블이 꽂혀 있으면
    // 고르지도 않았는데 영상이 Wi-Fi로 갔습니다(이 Wi-Fi는 전송이 100~470ms씩 밀리는 순간이 있음, 개발 문서 12.9).
    // 고른 쪽 연결이 사라지면(케이블을 뽑음, 무선 끊김) 상태 확인(RefreshStatusAsync)이 남은 쪽에 다시 겁니다.
    private static string ReversePreferUsb(int port)
    {
        List<Device> devices = ListReadyDevices(out string problem);
        if (devices.Count == 0)
        {
            return problem;
        }

        // 하드웨어 일련번호(USB 기기는 serial이 곧 일련번호)가 같으면 같은 폰입니다.
        bool wirelessFirst = GameViewRemote.PreferWireless;
        Dictionary<string, string> hardware = new Dictionary<string, string>();
        HashSet<string> preferredPhones = new HashSet<string>();
        foreach (Device device in devices)
        {
            string id = device.IsWireless ? Run($"-s {device.serial} shell getprop ro.serialno").Trim() : device.serial;
            hardware[device.serial] = id;
            if (device.IsWireless == wirelessFirst)
            {
                preferredPhones.Add(id);
            }
        }

        List<string> lines = new List<string>();
        List<Device> targets = new List<Device>();
        foreach (Device device in devices)
        {
            if (device.IsWireless != wirelessFirst && preferredPhones.Contains(hardware[device.serial]))
            {
                Run($"-s {device.serial} reverse --remove tcp:{port}");
                lines.Add($"adb reverse tcp:{port} ({device.serial}) 건너뜀: 같은 폰이 {(wirelessFirst ? "무선" : "USB")}으로도 연결돼 있어 그쪽으로 보냅니다");
                continue;
            }

            targets.Add(device);
        }

        foreach (Device device in targets)
        {
            lines.Add(ReverseOne(device.serial, port));
        }

        return string.Join("\n", lines);
    }

    // 창의 "USB 폰을 무선으로 전환": USB 기기가 정확히 1대여야 합니다. 끝나면 케이블을 뽑아도 됩니다.
    public static void SwitchToWirelessAsync(int port)
    {
        RunInBackground(() =>
        {
            List<string> lines = new List<string> { "> USB 폰을 무선으로 전환" };
            List<Device> usb = ListReadyDevices(out string problem).FindAll(device => !device.IsWireless);
            if (usb.Count != 1)
            {
                lines.Add(usb.Count == 0 ? $"USB로 연결된 폰이 없습니다. {problem}" : $"USB 기기가 {usb.Count}대입니다. 1대만 연결하세요.");
                lastOutput = string.Join("\n", lines);
                return;
            }

            string serial = usb[0].serial;
            string ip = FindWifiAddress(serial);
            if (ip == null)
            {
                lines.Add("폰의 Wi-Fi IP를 찾지 못했습니다. 폰이 PC와 같은 Wi-Fi에 연결돼 있는지 확인하세요.");
                lastOutput = string.Join("\n", lines);
                return;
            }

            lines.Add($"폰 Wi-Fi IP: {ip}");
            lines.Add(Run($"-s {serial} tcpip {TCPIP_PORT}").Trim());
            Thread.Sleep(TCPIP_RESTART_MS);
            string address = $"{ip}:{TCPIP_PORT}";
            lines.Add(Connect(address));
            if (connectedAddress == address)
            {
                lines.Add(ReversePreferUsb(port));
                lines.Add("무선 연결됨. 케이블이 꽂혀 있는 동안은 USB로 보내고, 뽑으면 몇 초 안에 무선으로 넘어갑니다(폰을 재부팅하면 다시 USB로 전환 필요).");
            }

            lastOutput = string.Join("\n", lines);
            UpdateStatus(port);
        }, true);
    }

    // 창의 "연결": IP:포트로 adb connect 후 adb reverse
    public static void ConnectAsync(string address, int port)
    {
        RunInBackground(() =>
        {
            string result = Connect(address.Trim());
            lastOutput = $"> adb connect {address}\n{result}" + (connectedAddress == address.Trim() ? "\n" + ReversePreferUsb(port) : string.Empty);
            UpdateStatus(port);
        }, true);
    }

    // 창의 "페어링": Android 11+ 무선 디버깅의 "페어링 코드로 기기 페어링" 화면에 나온 IP:포트와 6자리 코드
    public static void PairAsync(string address, string code, int port)
    {
        RunInBackground(() =>
        {
            lastOutput = $"> adb pair {address}\n{Run($"pair {address.Trim()} {code.Trim()}").Trim()}\n페어링이 되면 무선 디버깅 화면 위쪽 'IP 주소 및 포트'(페어링 포트와 다름)로 '연결'을 누르세요.";
            UpdateStatus(port);
        }, true);
    }

    // 창의 "무선 끊기": 무선 adb 연결을 모두 끊습니다(폰의 tcpip 모드는 재부팅하면 풀림).
    public static void DisconnectWirelessAsync(int port)
    {
        RunInBackground(() =>
        {
            lastOutput = $"> adb disconnect\n{Run("disconnect").Trim()}";
            connectedAddress = null;
            UpdateStatus(port);
        }, true);
    }

    // adb connect 결과 한 줄. 성공하면 connectedAddress를 바꿉니다.
    private static string Connect(string address)
    {
        string output = Run($"connect {address}").Trim();
        if (output.Contains("connected to"))
        {
            connectedAddress = address;
        }

        return output;
    }

    private static string ReverseOne(string serial, int port)
    {
        string output = Run($"-s {serial} reverse tcp:{port} tcp:{port}").Trim();
        return $"adb reverse tcp:{port} ({serial}) {(output.Length > 0 ? output : "ok")}";
    }

    // 폰 wlan0의 IPv4 주소. 없으면 null
    private static string FindWifiAddress(string serial)
    {
        foreach (string line in Run($"-s {serial} shell ip -f inet addr show wlan0").Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("inet "))
            {
                continue;
            }

            string cidr = trimmed.Split(' ')[1];
            int slash = cidr.IndexOf('/');
            return slash > 0 ? cidr.Substring(0, slash) : cidr;
        }

        return null;
    }

    // 창의 "adb reverse" 버튼
    public static void ReverseAsync(int port)
    {
        RunInBackground(() =>
        {
            lastOutput = $"> reverse tcp:{port} tcp:{port}\n{ReversePreferUsb(port)}";
            UpdateStatus(port);
        }, true);
    }

    // 창의 "폰에서 열기" 버튼: 연결된 기기 모두에 adb reverse 후 폰 Chrome에서 http://localhost:port/ 를 엽니다.
    // 같은 폰이 USB와 무선으로 함께 잡혀 있을 수 있어서 페이지는 첫 기기(USB 우선)에서 한 번만 엽니다.
    public static void OpenOnPhoneAsync(int port)
    {
        RunInBackground(() =>
        {
            string url = $"http://localhost:{port}/";
            string reverse = ReversePreferUsb(port);
            List<Device> devices = ListReadyDevices(out string problem);
            lastOutput = devices.Count == 0
                ? $"> open {url}\n{problem}"
                : $"> open {url}\n{reverse}\n{Run($"-s {devices[0].serial} shell am start -a android.intent.action.VIEW -d {url} -p com.android.chrome").Trim()}";
            UpdateStatus(port);
        }, true);
    }

    public static void DevicesAsync(int port)
    {
        RunInBackground(() =>
        {
            lastOutput = "> adb devices -l\n" + Run("devices -l").Trim();
            UpdateStatus(port);
        }, true);
    }

    private static string ForEachReadyDevice(Func<Device, string> action, string title)
    {
        List<Device> devices = ListReadyDevices(out string problem);
        if (devices.Count == 0)
        {
            return $"> {title}\n{problem}";
        }

        List<string> lines = new List<string>();
        foreach (Device device in devices)
        {
            lines.Add($"> {title} ({device.serial})");
            string output = action(device).Trim();
            lines.Add(output.Length > 0 ? output : "ok");
        }

        return string.Join("\n", lines);
    }

    private static void UpdateStatus(int port)
    {
        Status status;
        List<Device> devices = ListReadyDevices(out string problem);
        if (devices.Count == 0)
        {
            status = new Status { level = problem != null && problem.Contains("unauthorized") ? Level.Warning : Level.Info, message = problem };
        }
        else
        {
            List<string> missing = new List<string>();
            // 터널을 가진 쪽: reverse --list 줄 맨 앞이 UsbFfs면 USB, host-N이면 무선(TCP)입니다.
            string route = null;
            foreach (Device device in devices)
            {
                string list = Run($"-s {device.serial} reverse --list");
                string owner = null;
                foreach (string line in list.Split('\n'))
                {
                    if (line.Contains($"tcp:{port} tcp:{port}"))
                    {
                        owner = line.Trim().StartsWith("UsbFfs") ? "USB" : "무선";
                    }
                }

                if (owner == null)
                {
                    missing.Add(device.serial);
                }
                else
                {
                    route = owner;
                }
            }

            int wireless = devices.FindAll(device => device.IsWireless).Count;
            string kind = wireless == 0 ? "USB" : wireless == devices.Count ? "무선" : "USB + 무선";
            status = missing.Count == 0
                ? BuildRouteStatus(kind, devices, port, route, wireless)
                : new Status { level = Level.Warning, message = $"adb reverse tcp:{port}가 없습니다({string.Join(", ", missing)}). 'adb reverse' 또는 '폰에서 열기'를 누르세요.", reverseMissing = true };
        }

        lock (statusLock)
        {
            lastStatus = status;
        }
    }

    // 터널이 있는 상태의 창 표시. USB와 무선이 함께 있는데 고른 쪽("무선 우선" 여부)과 다른 쪽으로 가고 있으면 경고하고 다시 걸게 합니다.
    private static Status BuildRouteStatus(string kind, List<Device> devices, int port, string route, int wireless)
    {
        string wanted = GameViewRemote.PreferWireless ? "무선" : "USB";
        bool wrong = wireless != 0 && wireless != devices.Count && route != wanted;
        return new Status
        {
            level = wrong ? Level.Warning : Level.Ok,
            message = $"{kind} 준비됨: 기기 {devices.Count}대({string.Join(", ", devices.ConvertAll(device => device.serial))}), adb reverse tcp:{port} 있음, 영상 경로: {route}"
                + (wrong ? $" (설정은 {wanted}입니다. 'adb reverse'를 누르면 옮깁니다)" : string.Empty),
            reverseMissing = wrong,
        };
    }

    // 'device' 상태 기기만 돌려줍니다. 없으면 problem에 이유(adb 없음, 기기 없음, unauthorized, offline)를 넣습니다.
    private static List<Device> ListReadyDevices(out string problem)
    {
        List<Device> ready = new List<Device>();
        problem = null;
        // USB를 앞에 둡니다("폰에서 열기"가 USB를 먼저 씀).
        List<Device> wirelessReady = new List<Device>();
        if (!File.Exists(AdbPath))
        {
            problem = $"adb 없음: {AdbPath} (Unity Android Build Support 필요)";
            return ready;
        }

        List<string> notReady = new List<string>();
        foreach (string line in Run("devices").Split('\n'))
        {
            string[] parts = line.Trim().Split('\t');
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[1] == "device")
            {
                Device device = new Device { serial = parts[0], state = parts[1] };
                (device.IsWireless ? wirelessReady : ready).Add(device);
            }
            else
            {
                notReady.Add($"{parts[0]} {parts[1]}");
            }
        }

        ready.AddRange(wirelessReady);
        if (ready.Count == 0)
        {
            problem = notReady.Count == 0
                ? "연결된 기기가 없습니다(USB 디버깅 확인, 무선이면 '연결')."
                : $"기기가 준비되지 않았습니다: {string.Join(", ", notReady)} — unauthorized면 폰의 USB 디버깅 허용 창에서 허용하세요.";
        }

        return ready;
    }

    private static void RunInBackground(Action body, bool showBusy)
    {
        Interlocked.Increment(ref busy);
        if (showBusy)
        {
            lastOutput = "실행 중...";
        }

        new Thread(() =>
        {
            try
            {
                lock (runLock)
                {
                    body();
                }
            }
            catch (Exception e)
            {
                lastOutput = e.Message;
            }
            finally
            {
                Interlocked.Decrement(ref busy);
            }
        }) { IsBackground = true, Name = "GVR adb" }.Start();
    }

    // 백그라운드 스레드 전용. 표준 출력 + 표준 오류를 합쳐 돌려줍니다. 무선 기기가 꺼져 adb가 오래 걸리면 ADB_TIMEOUT_MS 뒤 끝냅니다.
    // internal: GameViewRemoteAppAdb 가 같이 씁니다(리뷰 M8).
    internal static string Run(string args)
    {
        return Run(args, ADB_TIMEOUT_MS);
    }

    // timeoutMs: 설치처럼 오래 걸리는 명령은 더 길게 줍니다(코드 리뷰 M4: 설치가 10초 제한에 걸려 중간에 죽던 것).
    internal static string Run(string args, int timeoutMs)
    {
        ProcessStartInfo info = new ProcessStartInfo(AdbPath, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using (Process process = Process.Start(info))
        {
            System.Threading.Tasks.Task<string> output = process.StandardOutput.ReadToEndAsync();
            System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill();
                return $"adb {args}: {timeoutMs / 1000}초 안에 끝나지 않았습니다.";
            }

            return output.Result + error.Result;
        }
    }
}
