using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;

// USB 버튼(설계 9.8): [USB 로 앱 설치], [USB 로 연결], [연결 점검].
// 전부 백그라운드 스레드에서 돌고 결과는 LastOutput 에 남습니다(토큰은 가려서 적음).
// adb 명령은 **항상 일련번호를 붙여(-s)** 한 기기에만 보냅니다. 대상은 무선이 아닌(케이블 또는 에뮬레이터) 준비된 기기 하나입니다.
public static class GameViewRemoteAppAdb
{
    // 프로젝트 시험 기간의 안전장치(소유자 지시: 실제 폰을 건드리지 않음). true 인 동안은 일련번호가 emulator-NNNN 인 기기만 대상으로 삼습니다.
    // **끄는 방법(소유자가 실제 폰에서 쓰기로 정한 뒤)**: 아래 값을 false 로 바꾸고 저장하면 끝입니다(Unity 가 다시 컴파일).
    // 그 뒤에는 케이블로 꽂힌 실제 폰이 [USB 로 앱 설치] / [USB 로 연결] / [연결 점검]의 대상이 됩니다. 무선 adb 기기는 여전히 대상이 아닙니다.
    private const bool EMULATOR_ONLY = true;
    private const string EMULATOR_PREFIX = "emulator-";
    private const int REACH_TIMEOUT_SECONDS = 3;
    // 앱의 adb 전용 입구(AndroidManifest 의 activity-alias). 앱 쪽 MainActivity.DIAG_ALIAS 와 같아야 합니다.
    private const string DIAG_ALIAS = ".Diag";
    // 설치는 APK 전송 + 기기의 검사(Play 프로텍트)가 붙어 오래 걸릴 수 있습니다. adb 의 기본 10초 제한으로는 중간에 죽습니다(코드 리뷰 M4).
    private const int INSTALL_TIMEOUT_MS = 120000;

    private static int busy;
    private static volatile string lastOutput = string.Empty;

    public static bool Busy { get { return Volatile.Read(ref busy) > 0; } }
    // 창이 "지금은 가상 기기에만 동작합니다"를 보이는 데 씁니다.
    public static bool EmulatorOnly { get { return EMULATOR_ONLY; } }
    public static string LastOutput { get { return lastOutput; } }

    // [USB 로 앱 설치]
    public static void InstallAsync()
    {
        string apk = GameViewRemoteFiles.ApkPath;
        if (!GameViewRemoteFiles.ApkExists)
        {
            lastOutput = "APK 가 없습니다: " + apk;
            return;
        }

        RunInBackground(() =>
        {
            string serial = PickDevice(out string problem);
            if (serial == null)
            {
                lastOutput = problem;
                return;
            }

            string output = GameViewRemoteAdb.Run($"-s {serial} install -r \"{apk}\"", INSTALL_TIMEOUT_MS).Trim();
            if (output.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE"))
            {
                lastOutput = "설치된 앱과 서명이 달라 덮어쓸 수 없습니다. 지우고 다시 설치할지 묻습니다.";
                // 지우면 앱이 기억한 연결 정보도 사라지므로 물어본 뒤에만 지웁니다(대화 상자는 메인 스레드).
                GameViewRemoteHost.Post(() =>
                {
                    if (EditorUtility.DisplayDialog("Game View Remote", $"기기({serial})에 서명이 다른 앱이 이미 있습니다.\n기존 앱을 지우고 다시 설치할까요?", "지우고 설치", "취소"))
                    {
                        RunInBackground(() =>
                        {
                            string removed = GameViewRemoteAdb.Run($"-s {serial} uninstall {GameViewRemoteAppInfo.PACKAGE}").Trim();
                            string installed = GameViewRemoteAdb.Run($"-s {serial} install -r \"{apk}\"", INSTALL_TIMEOUT_MS).Trim();
                            lastOutput = $"uninstall ({serial}): {removed}\ninstall: {installed}\n{InstalledVersion(serial)}";
                        });
                    }
                    else
                    {
                        lastOutput = "설치를 취소했습니다.";
                    }
                });
                return;
            }

            // 결과 글자만 믿지 않고 기기에 실제로 깔린 버전을 다시 읽어 보입니다(시간 초과로 끝났어도 설치는 됐을 수 있음).
            lastOutput = $"adb install ({serial}): {output}"
                + (output.Contains("INSTALL_FAILED_VERSION_DOWNGRADE") ? "\n기기에 더 새 버전이 이미 있습니다. 그대로 두었습니다." : string.Empty)
                + "\n" + InstalledVersion(serial);
        });
    }

    // 기기에 깔린 앱의 버전 한 줄(없으면 "설치돼 있지 않습니다").
    private static string InstalledVersion(string serial)
    {
        string dump = GameViewRemoteAdb.Run($"-s {serial} shell dumpsys package {GameViewRemoteAppInfo.PACKAGE}");
        string code = null;
        string name = null;
        foreach (string raw in dump.Split('\n'))
        {
            string line = raw.Trim();
            if (code == null && line.StartsWith("versionCode=", StringComparison.Ordinal))
            {
                int end = line.IndexOf(' ');
                code = end > 0 ? line.Substring(12, end - 12) : line.Substring(12);
            }
            else if (name == null && line.StartsWith("versionName=", StringComparison.Ordinal))
            {
                name = line.Substring(12);
            }
        }

        return code == null
            ? $"기기({serial})에 앱이 설치돼 있지 않습니다."
            : $"기기({serial})에 설치된 앱: {name} (versionCode {code}), 도구에 든 APK: {GameViewRemoteFiles.ApkVersionName} (versionCode {GameViewRemoteFiles.ApkVersionCode})";
    }

    // [USB 로 연결]: adb reverse 를 걸고 앱을 연결 링크로 엽니다. 메인 스레드에서 부릅니다.
    public static void ConnectUsbAsync()
    {
        if (!EditorApplication.isPlaying || !GameViewRemote.IsRunning)
        {
            lastOutput = "Play 를 먼저 누르세요(USB 연결은 Play 중에만 됩니다).";
            return;
        }

        GameViewRemoteSettings.EnsureSecret();
        GameViewRemoteSettings.SecretSnapshot secret = GameViewRemoteSettings.Secret;
        int port = GameViewRemote.RunningPort;
        RunInBackground(() =>
        {
            string serial = PickDevice(out string problem);
            if (serial == null)
            {
                lastOutput = problem;
                return;
            }

            string reverse = GameViewRemoteAdb.Run($"-s {serial} reverse tcp:{port} tcp:{port}").Trim();
            // 토큰은 주소가 아니라 --es t 로 넘깁니다(리뷰 m3: 주소는 logcat 에 남음). 앱의 진단 빌드가 받습니다.
            string link = $"{GameViewRemoteAppInfo.SCHEME}://127.0.0.1:{port}/c?v={GameViewRemoteAppInfo.QR_VERSION}&s={secret.serverId}&via=usb&take=1";
            // 앱 0.1.2 부터 --es t 는 adb 전용 입구(.Diag 별칭, 진단 빌드에만 있음)로 온 인텐트에서만 받습니다(안드로이드 쪽 코드 리뷰 M5).
            // 그래서 패키지가 아니라 그 별칭을 -n 으로 지정합니다. 진단 값이 꺼진 빌드(release)에서는 이 버튼으로 연결되지 않습니다.
            string command = $"-s {serial} shell \"am start -W -n {GameViewRemoteAppInfo.PACKAGE}/{DIAG_ALIAS} -a android.intent.action.VIEW -d '{link}' --es t {{0}}\"";
            string start = GameViewRemoteAdb.Run(string.Format(command, secret.token)).Trim().Replace(secret.token, "…");
            lastOutput = $"adb reverse tcp:{port} ({serial}) {(reverse.Length > 0 ? reverse : "ok")}\nadb {string.Format(command, "…")}\n{start}";
        });
    }

    // [연결 점검]: 기기에서 PC 의 QR 주소로 TCP 가 닿는지 봅니다(방화벽 판정에 나오지 않는 보안 제품까지 포함한 실제 도달 여부).
    // 덤으로 기기의 wlan0 주소를 읽어 주 주소 선택에 넘깁니다. 메인 스레드에서 부릅니다.
    public static void CheckReachAsync()
    {
        string address = GameViewRemoteHost.PrimaryAddress;
        int port = GameViewRemoteHost.Port;
        if (!GameViewRemoteHost.IsListening || address == null)
        {
            lastOutput = "Start 를 먼저 누르세요(QR 주소가 열려 있어야 점검할 수 있습니다).";
            return;
        }

        RunInBackground(() =>
        {
            string serial = PickDevice(out string problem);
            if (serial == null)
            {
                lastOutput = problem;
                return;
            }

            string wifi = FindWifiAddress(serial);
            // 가상 기기의 wlan0 은 PC 안의 가짜 주소(10.0.2.x)라 주 주소 선택에 쓰지 않습니다.
            if (wifi != null && !serial.StartsWith(EMULATOR_PREFIX, StringComparison.Ordinal))
            {
                GameViewRemoteHost.PhoneWifiAddress = wifi;
            }

            // 연결되면 nc 가 입력 끝(/dev/null)에서 0 으로 끝나고, 막히면 시간 초과로 0 이 아닌 값으로 끝납니다.
            string output = GameViewRemoteAdb.Run($"-s {serial} shell \"toybox nc -w {REACH_TIMEOUT_SECONDS} {address} {port} </dev/null >/dev/null 2>&1; echo gvr-exit=$?\"").Trim();
            bool reached = output.Contains("gvr-exit=0");
            lastOutput = (reached
                    ? $"닿습니다: 기기({serial}) → {address}:{port}"
                    : $"닿지 않습니다: 기기({serial}) → {address}:{port}. 같은 Wi-Fi 인지, Windows 방화벽이나 보안 제품이 막는지 확인하세요. ({output})")
                + (wifi != null ? $"\n기기 Wi-Fi 주소: {wifi}" : "\n기기가 Wi-Fi 에 붙어 있지 않습니다.");
            GameViewRemoteFirewall.RefreshAsyncFromAnyThread();
        });
    }

    // 준비된(device 상태) 기기 중 무선이 아닌 것의 일련번호. 없으면 null 과 이유.
    // **대상이 둘 이상이면 고르지 않고 멈춥니다**(어느 기기에 깔고 여는지 모르는 채로 명령을 보내지 않음, 코드 리뷰 M4).
    private static string PickDevice(out string problem)
    {
        problem = null;
        if (!File.Exists(GameViewRemoteAdb.AdbPath))
        {
            problem = "adb 없음: " + GameViewRemoteAdb.AdbPath + " (Unity Android Build Support 필요)";
            return null;
        }

        List<string> skipped = new List<string>();
        List<string> candidates = new List<string>();
        foreach (string line in GameViewRemoteAdb.Run("devices").Split('\n'))
        {
            string[] parts = line.Trim().Split('\t');
            if (parts.Length != 2)
            {
                continue;
            }

            string serial = parts[0];
            bool wireless = serial.Contains(":") || serial.Contains("._adb-tls-connect.");
            if (parts[1] != "device")
            {
                skipped.Add($"{serial} {parts[1]}");
            }
            else if (wireless)
            {
                skipped.Add($"{serial} 무선");
            }
            else if (EMULATOR_ONLY && !serial.StartsWith(EMULATOR_PREFIX, StringComparison.Ordinal))
            {
                skipped.Add($"{serial} 실제 기기(시험 기간에는 에뮬레이터만 씁니다)");
            }
            else
            {
                candidates.Add(serial);
            }
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (candidates.Count > 1)
        {
            problem = "USB 기기가 둘 이상 연결돼 있어 어느 기기인지 정할 수 없습니다: " + string.Join(", ", candidates) + ". 하나만 남기고 다시 누르세요.";
            return null;
        }

        problem = skipped.Count == 0
            ? "USB 로 연결된 기기가 없습니다(USB 디버깅을 확인하세요)."
            : "쓸 수 있는 USB 기기가 없습니다: " + string.Join(", ", skipped);
        return null;
    }

    // 기기 wlan0 의 IPv4 주소. 없으면 null
    private static string FindWifiAddress(string serial)
    {
        foreach (string line in GameViewRemoteAdb.Run($"-s {serial} shell ip -f inet addr show wlan0").Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("inet ", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = trimmed.Split(' ');
            if (parts.Length < 2)
            {
                continue;
            }

            int slash = parts[1].IndexOf('/');
            return slash > 0 ? parts[1].Substring(0, slash) : parts[1];
        }

        return null;
    }

    private static void RunInBackground(Action body)
    {
        Interlocked.Increment(ref busy);
        lastOutput = "실행 중...";
        new Thread(() =>
        {
            try
            {
                // 창의 주기적인 adb 상태 확인과 같은 잠금: adb 명령이 겹쳐 돌지 않습니다.
                lock (GameViewRemoteAdb.runLock)
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
                GameViewRemoteHost.Post(null);
            }
        }) { IsBackground = true, Name = "GVR app adb" }.Start();
    }
}
