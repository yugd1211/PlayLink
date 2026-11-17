using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEditor;

// USB 연결용 adb 도우미(Unity 에 들어 있는 adb 를 씁니다).
// USB 에서도 Wi-Fi 와 같은 프로토콜을 씁니다: adb forward tcp:<PC 포트> tcp:7300 으로 폰의 포트를 PC 로 끌어온 뒤 127.0.0.1:<PC 포트> 로 겁니다.
// adb 명령은 **항상 일련번호를 붙여(-s)** 한 기기에만 보냅니다. adb 는 수 초가 걸릴 수 있어 메인 스레드에서 돌리지 않습니다.
public static class PlayLinkAdb
{
    private const int ADB_TIMEOUT_MS = 10000;

    public struct Device
    {
        public string serial;
        // device(준비됨), unauthorized, offline …
        public string state;

        public bool IsReady { get { return state == "device"; } }
        // IP:포트 또는 무선 디버깅 mDNS 이름이면 무선 adb(케이블이 아님)
        public bool IsWireless { get { return serial.Contains(":") || serial.Contains("._adb-tls-connect."); } }
    }

    public static readonly string AdbPath = Path.Combine(EditorApplication.applicationContentsPath,
        "PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe");

    // adb 명령은 한 번에 하나씩 돌립니다.
    private static readonly object runLock = new object();
    private static int busy;
    private static volatile Device[] devices = new Device[0];
    private static volatile string message = "아직 확인하지 않았습니다.";

    public static bool Busy { get { return Volatile.Read(ref busy) > 0; } }
    // 마지막으로 읽은 기기 목록과 한 줄 설명
    public static Device[] Devices { get { return devices; } }
    public static string Message { get { return message; } }

    // 기기 목록을 다시 읽습니다(이미 돌고 있으면 건너뜀).
    public static void RefreshAsync()
    {
        if (Busy)
        {
            return;
        }

        Interlocked.Increment(ref busy);
        new Thread(() =>
        {
            try
            {
                Refresh();
            }
            catch (Exception e)
            {
                message = "adb 를 실행하지 못했습니다(" + e.GetType().Name + ").";
            }
            finally
            {
                Interlocked.Decrement(ref busy);
                PlayLinkLink.NoteChanged();
            }
        }) { IsBackground = true, Name = "PlayLink adb" }.Start();
    }

    private static void Refresh()
    {
        if (!File.Exists(AdbPath))
        {
            devices = new Device[0];
            message = "adb 가 없습니다(Unity 의 Android Build Support 가 필요합니다).";
            return;
        }

        List<Device> found = new List<Device>();
        foreach (string line in Run("devices").Split('\n'))
        {
            string[] parts = line.Trim().Split('\t');
            if (parts.Length == 2)
            {
                found.Add(new Device { serial = parts[0], state = parts[1] });
            }
        }

        devices = found.ToArray();
        message = found.Count == 0
            ? "USB 로 연결된 기기가 없습니다. 폰의 USB 디버깅이 켜져 있는지 확인하세요."
            : found.Exists(d => d.state == "unauthorized") ? "폰 화면의 'USB 디버깅 허용' 창에서 허용을 누르세요." : string.Empty;
    }

    // 폰의 TCP 포트를 PC 의 빈 포트로 끌어옵니다(이미 걸려 있으면 그 포트를 그대로 씀). PC 쪽 포트를 돌려주고, 실패하면 0 과 이유.
    // 백그라운드 스레드에서 부릅니다.
    public static int Forward(string serial, int phonePort, out string error)
    {
        error = null;
        if (!File.Exists(AdbPath))
        {
            error = "adb 가 없습니다(Unity 의 Android Build Support 가 필요합니다).";
            return 0;
        }

        try
        {
            string remote = "tcp:" + phonePort;
            foreach (string line in Run("-s " + serial + " forward --list").Split('\n'))
            {
                // <일련번호> tcp:<PC 포트> tcp:<폰 포트>
                string[] parts = line.Trim().Split(' ');
                if (parts.Length == 3 && parts[0] == serial && parts[2] == remote && parts[1].StartsWith("tcp:", StringComparison.Ordinal)
                    && int.TryParse(parts[1].Substring(4), out int existing))
                {
                    return existing;
                }
            }

            string output = Run("-s " + serial + " forward tcp:0 " + remote).Trim();
            if (int.TryParse(output, out int port) && port > 0)
            {
                return port;
            }

            error = "adb forward 실패: " + output;
            return 0;
        }
        catch (Exception e)
        {
            error = "adb 를 실행하지 못했습니다(" + e.GetType().Name + ").";
            return 0;
        }
    }

    // 표준 출력 + 표준 오류를 합쳐 돌려줍니다. 오래 걸리면 ADB_TIMEOUT_MS 뒤 끝냅니다.
    private static string Run(string args)
    {
        lock (runLock)
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
                if (!process.WaitForExit(ADB_TIMEOUT_MS))
                {
                    process.Kill();
                    return "adb " + args + ": " + (ADB_TIMEOUT_MS / 1000) + "초 안에 끝나지 않았습니다.";
                }

                return output.Result + error.Result;
            }
        }
    }
}
