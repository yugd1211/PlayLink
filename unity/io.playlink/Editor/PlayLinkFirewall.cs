using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;

// Windows 방화벽(설계 9.7). 같은 Wi-Fi 의 폰이 이 Unity.exe 로 들어오려면 인바운드가 허용돼 있어야 합니다.
//   읽기(관리자 불필요): PowerShell 로 COM HNetCfg.FwPolicy2 를 읽어 지금 프로필에서 이 실행 파일의 TCP·도구 포트가 막혔는지 판정합니다.
//   쓰기(관리자 필요, UAC 창 1번): RequestAllow / RequestRevert. **창의 버튼에서만 부릅니다.** 자동으로 부르는 곳은 없습니다.
//   미리 보기(관리자 불필요): DryRunAllow / DryRunRevert 는 같은 스크립트를 "출력만" 하는 모드로 돌려 실제로 실행될 변경을 줄마다 보여 줍니다.
// 허용하는 범위(가능한 한 좁게): 이 Unity.exe 하나, TCP, 도구 포트 하나, 원격 주소는 **지금 붙어 있는 네트워크의 서브넷 숫자**(예: 192.168.25.0/255.255.255.0),
// 프로필은 지금 네트워크의 프로필 하나. 프로필만으로 좁히면 "공용"으로 분류된 다른 모든 네트워크(카페 Wi-Fi 등)에도 규칙이 걸리므로 서브넷 숫자로 한 번 더 좁힙니다.
// 다른 보안 제품은 이 판정에 나오지 않습니다. 실제로 닿는지는 [연결 점검](PlayLinkAppAdb.CheckReachAsync)으로만 확인됩니다.
public static class PlayLinkFirewall
{
    public enum Verdict
    {
        // 아직 읽지 않음
        Unknown,
        Checking,
        // 차단 규칙이 있음 → [방화벽 허용…]
        Blocked,
        // 차단은 없고 허용 규칙이 있음
        Allowed,
        // 규칙이 없고 기본 동작이 차단: Windows 가 처음 한 번 물을 수 있음
        WillPrompt,
        // 방화벽 꺼짐
        Off,
        // Windows 가 아니거나 읽지 못함
        Unsupported,
    }

    private const string RULE_GROUP = "PlayLink";
    private const int READ_TIMEOUT_MS = 15000;
    // 승격된 스크립트가 끝나기를 기다리는 시간(규칙 수백 개를 훑어도 몇 초)
    private const int WRITE_TIMEOUT_MS = 60000;
    private const int PROTOCOL_TCP = 6;
    private const int PROTOCOL_ANY = 256;
    private const int PROFILE_DOMAIN = 1;
    private const int PROFILE_PRIVATE = 2;
    private const int PROFILE_PUBLIC = 4;
    private const string REMOTE_LOCAL_SUBNET = "LocalSubnet";
    // UAC 에서 [아니요]를 눌렀을 때 Process.Start 가 내는 Win32 오류(ERROR_CANCELLED)
    private const int ERROR_CANCELLED = 1223;
    private const string SESSION_STATE = "PlayLink_FwState";
    private const string SESSION_MESSAGE = "PlayLink_FwMessage";
    private const string SESSION_PROFILES = "PlayLink_FwProfiles";

    private static readonly object stateLock = new object();
    private static Verdict state = Verdict.Unknown;
    private static string message = "방화벽 상태를 아직 확인하지 않았습니다.";
    // 마지막 [허용]/[되돌리기]의 결과 한 줄(실패했을 때만 값이 있음). 상태 문구 뒤에 붙여 보입니다.
    private static string lastActionProblem;
    private static int currentProfiles;
    private static int reading;

    public static Verdict State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
    }

    public static string Message
    {
        get
        {
            lock (stateLock)
            {
                return lastActionProblem == null ? message : message + "\n" + lastActionProblem;
            }
        }
    }

    // 마지막으로 읽은 "지금 네트워크의 프로필"(예: "공용"). 아직 읽지 않았으면 "확인 전"
    public static string CurrentProfileText
    {
        get
        {
            lock (stateLock)
            {
                return ProfileText(currentProfiles);
            }
        }
    }

    // 지금 네트워크가 공용 프로필인가(마지막으로 읽은 값)
    public static bool IsPublicProfile
    {
        get
        {
            lock (stateLock)
            {
                return (currentProfiles & PROFILE_PUBLIC) != 0;
            }
        }
    }

    private static string DataFolder
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlayLink"); }
    }

    // 실행 파일마다 다른 꼬리표(경로의 해시 6자). 기록 파일과 규칙 이름에 붙여, Unity 버전이 여러 개여도 서로의 규칙과 기록을 건드리지 않습니다(코드 리뷰 M3-d).
    public static string ExeTag(string exe)
    {
        using (SHA1 sha1 = SHA1.Create())
        {
            byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(exe.ToLowerInvariant()));
            return BitConverter.ToString(hash, 0, 3).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    private static string EditorExe { get { return EditorApplication.applicationPath.Replace('/', '\\'); } }
    private static string BackupPath(string exe) { return Path.Combine(DataFolder, "firewall-backup-" + ExeTag(exe) + ".json"); }
    private static string ResultPath(string exe) { return Path.Combine(DataFolder, "firewall-result-" + ExeTag(exe) + ".txt"); }

    // [되돌리기]로 되돌릴 것이 있는가(이 Unity 실행 파일로 [방화벽 허용…]이 한 번이라도 실행됨). 메인 스레드
    public static bool HasBackup { get { return File.Exists(BackupPath(EditorExe)); } }

    // 메인 스레드에서 부릅니다(실행 파일 경로와 포트를 읽음). 읽기는 백그라운드에서 돌고 끝나면 State/Message 가 바뀝니다.
    public static void RefreshAsync()
    {
#if UNITY_EDITOR_WIN
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
        {
            return;
        }

        string exe = EditorExe;
        int port = PlayLink.Port;
        string subnet = SubnetOf(PlayLinkHost.PrimaryAddress);
        Set(Verdict.Checking, "방화벽 상태를 확인하는 중…");
        new Thread(() =>
        {
            try
            {
                Evaluate(RunPowerShell(BuildReadScript(exe)), port, subnet);
            }
            catch (Exception e)
            {
                Set(Verdict.Unsupported, "방화벽 상태를 읽지 못했습니다: " + e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref reading, 0);
                PlayLinkHost.Post(null);
            }
        }) { IsBackground = true, Name = "PlayLink Firewall" }.Start();
#else
        Set(Verdict.Unsupported, "이 운영체제에서는 방화벽 상태를 판정하지 않습니다.");
#endif
    }

    // 아무 스레드에서: 메인 스레드로 넘겨 다시 읽습니다.
    internal static void RefreshAsyncFromAnyThread()
    {
        PlayLinkHost.Post(RefreshAsync);
    }

    // 허용 규칙의 원격 주소: 지금 QR 주소가 속한 서브넷("192.168.25.0/255.255.255.0"). 모르면 LocalSubnet(같은 서브넷이면 어디든).
    private static string RemoteScope()
    {
        return SubnetOf(PlayLinkHost.PrimaryAddress) ?? REMOTE_LOCAL_SUBNET;
    }

    // [방화벽 허용…] 을 누르기 전 대화 상자에 보일 내용: 무엇이, 어느 범위로 열리는지 그대로 적습니다. 메인 스레드
    public static string DescribeAllow()
    {
        int port = PlayLink.Port;
        string remote = RemoteScope();
        int profiles;
        lock (stateLock)
        {
            profiles = currentProfiles;
        }

        StringBuilder text = new StringBuilder();
        text.Append("Windows 방화벽 규칙을 바꿉니다(관리자 권한 확인 창이 한 번 뜹니다).\n\n");
        text.Append("허용되는 것(이것 하나뿐):\n");
        text.Append($"  프로그램  {EditorApplication.applicationPath}\n");
        text.Append($"  들어오는 TCP {port} 번 포트\n");
        text.Append(remote == REMOTE_LOCAL_SUBNET
            ? "  보내는 쪽  이 PC 와 같은 서브넷의 기기\n"
            : $"  보내는 쪽  {remote} 의 기기(지금 붙어 있는 네트워크)\n");
        text.Append($"  네트워크 프로필  {ProfileText(profiles)}\n\n");
        text.Append("하는 일:\n");
        text.Append("1. 이 Unity 에디터의 인바운드 '차단' 규칙을 끕니다(지우지 않고 이름을 먼저 기록해 둡니다).\n");
        text.Append($"2. 끈 규칙이 있으면 대신 TCP {port} 이외의 포트와 UDP 전체를 막는 규칙을 넣습니다(다른 포트는 계속 막힘).\n");
        text.Append("3. 위의 허용 규칙 하나를 넣습니다.\n\n");
        if ((profiles & PROFILE_PUBLIC) != 0)
        {
            text.Append("주의: 지금 네트워크는 Windows 에 '공용'으로 등록돼 있습니다. 허용 규칙은 위의 주소 범위로만 걸리므로 "
                + "주소 대역이 다른 공용 네트워크(카페 등)에서는 열리지 않습니다. 그래도 믿는 네트워크라면 "
                + "Windows 설정 > 네트워크 및 인터넷 > (연결된 Wi-Fi 또는 이더넷) > 네트워크 프로필 유형을 '개인'으로 바꾼 뒤 누르는 편이 좋습니다.\n\n");
        }

        text.Append($"넣는 규칙은 전부 '{RULE_GROUP}' 그룹이고 [되돌리기]로 원래대로 돌아갑니다.\n");
        text.Append("포트, Unity 버전, 네트워크(주소 대역이나 프로필)가 바뀌면 다시 눌러야 합니다.");
        return text.ToString();
    }

    // UAC 창 1번. 창의 버튼에서만 부르세요. 끝나면 결과를 읽고 상태를 다시 읽습니다.
    public static void RequestAllow()
    {
#if UNITY_EDITOR_WIN
        string exe = EditorExe;
        RunElevated(BuildAllowScript(exe, PlayLink.Port, RemoteScope(), BackupPath(exe), ResultPath(exe), false), ResultPath(exe), "방화벽 허용");
#endif
    }

    // UAC 창 1번. 넣은 규칙을 지우고 꺼 둔 차단 규칙을 다시 켭니다.
    public static void RequestRevert()
    {
#if UNITY_EDITOR_WIN
        string exe = EditorExe;
        RunElevated(BuildRevertScript(exe, BackupPath(exe), ResultPath(exe), false), ResultPath(exe), "방화벽 되돌리기");
#endif
    }

    // 관리자 권한 없이 "무엇을 할지"만 출력합니다(규칙은 읽기만 함). 줄마다 WOULD: … 로 나옵니다. 메인 스레드에서 부르고 몇 초 걸립니다.
    public static string DryRunAllow()
    {
        string exe = EditorExe;
        return RunPowerShell(BuildAllowScript(exe, PlayLink.Port, RemoteScope(), BackupPath(exe), ResultPath(exe), true));
    }

    public static string DryRunRevert()
    {
        string exe = EditorExe;
        return RunPowerShell(BuildRevertScript(exe, BackupPath(exe), ResultPath(exe), true));
    }

    [MenuItem("Tools/PlayLink/Firewall Dry Run (출력만)")]
    private static void DryRunMenu()
    {
        UnityEngine.Debug.Log($"{PlayLink.LOG_PREFIX} 방화벽 [허용] 미리 보기(아무것도 바꾸지 않음):\n{DryRunAllow()}\n--- [되돌리기] 미리 보기 ---\n{DryRunRevert()}");
    }

    #region 스크립트 (PowerShell)

    private static string Quote(string text)
    {
        return "'" + text.Replace("'", "''") + "'";
    }

    // 출력: profile=<비트>, enabled=<True|False>, defaultBlock=<True|False>, rule=<동작>|<프로토콜>|<로컬 포트>|<원격 주소>|<이름> (줄마다 하나)
    public static string BuildReadScript(string exe)
    {
        return "$ErrorActionPreference='Stop'\n"
            + "$exe=" + Quote(exe) + "\n"
            + "$p=New-Object -ComObject HNetCfg.FwPolicy2\n"
            + "$cur=$p.CurrentProfileTypes\n"
            + "$on=$false;$def=$false\n"
            + "foreach($t in 1,2,4){if($cur -band $t){if($p.FirewallEnabled($t)){$on=$true};if($p.DefaultInboundAction($t) -eq 0){$def=$true}}}\n"
            + "\"profile=$cur\";\"enabled=$on\";\"defaultBlock=$def\"\n"
            + "foreach($r in $p.Rules){if($r.Enabled -and $r.Direction -eq 1 -and $r.ApplicationName -and ($r.ApplicationName -ieq $exe) -and ($r.Profiles -band $cur)){"
            + "'rule='+$r.Action+'|'+$r.Protocol+'|'+$r.LocalPorts+'|'+$r.RemoteAddresses+'|'+$r.Name}}\n";
    }

    // 두 스크립트가 함께 쓰는 앞부분. Act: 출력만 하는 모드면 "WOULD: …" 만 찍고, 아니면 실행한 뒤 "DID: …" 를 찍습니다.
    // ReadSaved: 기록 파일을 배열로 읽습니다(PowerShell 5.1 의 ConvertFrom-Json 은 배열을 한 덩어리로 내보내므로 foreach 로 풉니다).
    private static string ScriptHead(string exe, string backupPath, string resultPath, bool dryRun)
    {
        return "$ErrorActionPreference='Stop'\n"
            + "$exe=" + Quote(exe) + ";$group=" + Quote(RULE_GROUP) + ";$tag=" + Quote(ExeTag(exe)) + "\n"
            + "$backup=" + Quote(backupPath) + ";$result=" + Quote(resultPath) + ";$dry=$" + (dryRun ? "true" : "false") + "\n"
            + "function Act($text,[scriptblock]$do){if($dry){\"WOULD: $text\"}else{& $do;\"DID: $text\"}}\n"
            + "function ReadSaved{$list=@();if(Test-Path $backup){try{$j=Get-Content -Raw -Encoding UTF8 $backup|ConvertFrom-Json;foreach($s in $j){if($s -and $s.Name){$list+=$s}}}catch{}};return ,$list}\n"
            + "function Finish($text){if(-not $dry){New-Item -ItemType Directory -Force (Split-Path $result)|Out-Null;Set-Content -Encoding UTF8 $result $text};$text}\n";
    }

    // 관리자 권한으로 실행됩니다(dryRun 이면 권한 없이, 출력만). 워커는 승격 실행을 하지 않았습니다(UAC 는 소유자가 누름).
    // 순서: 기록을 읽음 → 끌 차단 규칙을 찾음 → **기록을 먼저 씀** → 예전에 넣은 우리 규칙(이 실행 파일 것만) 삭제 → 차단 규칙 끔 → 대체 차단 규칙 → 허용 규칙.
    public static string BuildAllowScript(string exe, int port, string remote, string backupPath, string resultPath, bool dryRun)
    {
        string others = (port > 1 ? "1-" + (port - 1) : string.Empty) + (port > 1 && port < 65535 ? "," : string.Empty) + (port < 65535 ? (port + 1) + "-65535" : string.Empty);
        return ScriptHead(exe, backupPath, resultPath, dryRun)
            + "$port='" + port + "';$remote=" + Quote(remote) + "\n"
            + "try{\n"
            + "$p=New-Object -ComObject HNetCfg.FwPolicy2\n"
            + "$cur=$p.CurrentProfileTypes\n"
            + "\"profile=$cur exe=$exe port=$port remote=$remote\"\n"
            + "$saved=ReadSaved\n"
            // 대체 차단 규칙을 넣을 프로필: 이번에 끄는 규칙 + 예전에 꺼서 기록해 둔 규칙(두 번째 실행에서도 빠지지 않게, 코드 리뷰 M3-c)
            + "$prof=0;foreach($s in $saved){$prof=$prof -bor [int]$s.Profiles}\n"
            + "$targets=@();foreach($r in @($p.Rules)){if($r.Enabled -and $r.Direction -eq 1 -and $r.Action -eq 0 -and $r.ApplicationName -and ($r.ApplicationName -ieq $exe) -and $r.Grouping -ne $group){$targets+=$r}}\n"
            + "foreach($r in $targets){$prof=$prof -bor $r.Profiles;$known=$false;foreach($s in $saved){if($s.Name -eq $r.Name){$known=$true}};"
            + "if(-not $known){$saved+=[pscustomobject]@{Name=$r.Name;Profiles=$r.Profiles;Protocol=$r.Protocol;LocalPorts=$r.LocalPorts;Exe=$exe}}}\n"
            // 기록을 먼저 씁니다. 여기서 실패하면 아직 아무 규칙도 바뀌지 않았습니다(코드 리뷰 M3-b).
            + "Act \"write backup $backup ($($saved.Count) rule name(s))\" {New-Item -ItemType Directory -Force (Split-Path $backup)|Out-Null;ConvertTo-Json -InputObject @($saved)|Set-Content -Encoding UTF8 $backup}\n"
            + "foreach($r in @($p.Rules)){if($r.Grouping -eq $group -and $r.ApplicationName -and ($r.ApplicationName -ieq $exe)){$n=$r.Name;Act \"remove our old rule '$n'\" {$p.Rules.Remove($n)}}}\n"
            + "foreach($r in $targets){$t=$r;Act \"disable block rule '$($r.Name)' (profiles $($r.Profiles), protocol $($r.Protocol), ports '$($r.LocalPorts)')\" {$t.Enabled=$false}}\n"
            + "function AddRule($name,$action,$proto,$ports,$rem,$profiles){"
            + "Act \"add rule '$name': action=$action(0 block,1 allow) program=$exe protocol=$proto localPorts='$ports' remote='$rem' profiles=$profiles direction=in\" {"
            + "$r=New-Object -ComObject HNetCfg.FWRule;$r.Name=$name;$r.Grouping=$group;$r.ApplicationName=$exe;$r.Protocol=$proto;"
            + "if($ports){$r.LocalPorts=$ports};if($rem){$r.RemoteAddresses=$rem};$r.Direction=1;$r.Profiles=$profiles;$r.Action=$action;$r.Enabled=$true;$p.Rules.Add($r)}}\n"
            // 끈 차단 규칙이 있으면, 도구 포트 밖은 계속 막히도록 대신할 차단 규칙을 넣습니다.
            + "if($prof -ne 0){AddRule \"PlayLink [$tag] - block other TCP ports\" 0 6 '" + others + "' $null $prof;"
            + "AddRule \"PlayLink [$tag] - block UDP\" 0 17 $null $null $prof}\n"
            + "AddRule \"PlayLink [$tag] - allow TCP $port\" 1 6 $port $remote $cur\n"
            + "Finish 'result=ok'\n"
            + "}catch{Finish \"result=error $($_.Exception.Message)\";exit 1}\n";
    }

    // 이 실행 파일로 넣은 규칙만 지우고, 이 실행 파일의 기록에 있는 차단 규칙만 다시 켭니다.
    public static string BuildRevertScript(string exe, string backupPath, string resultPath, bool dryRun)
    {
        return ScriptHead(exe, backupPath, resultPath, dryRun)
            + "try{\n"
            + "$p=New-Object -ComObject HNetCfg.FwPolicy2\n"
            + "\"exe=$exe\"\n"
            + "foreach($r in @($p.Rules)){if($r.Grouping -eq $group -and $r.ApplicationName -and ($r.ApplicationName -ieq $exe)){$n=$r.Name;Act \"remove our rule '$n'\" {$p.Rules.Remove($n)}}}\n"
            + "$saved=ReadSaved\n"
            + "foreach($s in $saved){foreach($r in @($p.Rules)){if($r.Name -eq $s.Name -and $r.Direction -eq 1 -and $r.Action -eq 0 -and (-not $r.Enabled) -and $r.ApplicationName -and ($r.ApplicationName -ieq $exe)){"
            + "$t=$r;Act \"enable block rule '$($r.Name)' again\" {$t.Enabled=$true}}}}\n"
            + "if(Test-Path $backup){Act \"delete backup $backup\" {Remove-Item $backup}}\n"
            + "Finish 'result=ok'\n"
            + "}catch{Finish \"result=error $($_.Exception.Message)\";exit 1}\n";
    }

    #endregion

    private static string ProfileText(int profiles)
    {
        if (profiles == 0)
        {
            return "확인 전";
        }

        List<string> names = new List<string>();
        if ((profiles & PROFILE_DOMAIN) != 0)
        {
            names.Add("도메인");
        }

        if ((profiles & PROFILE_PRIVATE) != 0)
        {
            names.Add("개인");
        }

        if ((profiles & PROFILE_PUBLIC) != 0)
        {
            names.Add("공용");
        }

        return string.Join("+", names);
    }

    // 이 PC 의 주소가 속한 서브넷을 방화벽 규칙의 표기("192.168.25.0/255.255.255.0")로. 모르면 null
    public static string SubnetOf(string address)
    {
        if (string.IsNullOrEmpty(address) || !IPAddress.TryParse(address, out IPAddress ip))
        {
            return null;
        }

        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation info in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (!info.Address.Equals(ip) || info.IPv4Mask == null)
                    {
                        continue;
                    }

                    byte[] a = ip.GetAddressBytes();
                    byte[] m = info.IPv4Mask.GetAddressBytes();
                    if (a.Length != 4 || m.Length != 4 || (m[0] == 0 && m[1] == 0 && m[2] == 0 && m[3] == 0))
                    {
                        return null;
                    }

                    return $"{a[0] & m[0]}.{a[1] & m[1]}.{a[2] & m[2]}.{a[3] & m[3]}/{m[0]}.{m[1]}.{m[2]}.{m[3]}";
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static void Set(Verdict verdict, string text)
    {
        int profiles;
        lock (stateLock)
        {
            state = verdict;
            message = text;
            profiles = currentProfiles;
        }

        // 읽은 결과를 세션에 적어 둡니다: 재컴파일 뒤에도 PowerShell 을 다시 띄우지 않고 마지막 판정을 보입니다(코드 리뷰 m16).
        if (verdict != Verdict.Checking)
        {
            PlayLinkHost.Post(() =>
            {
                SessionState.SetInt(SESSION_STATE, (int)verdict);
                SessionState.SetString(SESSION_MESSAGE, text ?? string.Empty);
                SessionState.SetInt(SESSION_PROFILES, profiles);
            });
        }
    }

    [InitializeOnLoadMethod]
    private static void RestoreFromSession()
    {
        int saved = SessionState.GetInt(SESSION_STATE, -1);
        if (saved < 0 || !Enum.IsDefined(typeof(Verdict), saved))
        {
            return;
        }

        lock (stateLock)
        {
            if (state == Verdict.Unknown)
            {
                state = (Verdict)saved;
                message = SessionState.GetString(SESSION_MESSAGE, message);
                currentProfiles = SessionState.GetInt(SESSION_PROFILES, 0);
            }
        }
    }

    // 읽기 스크립트의 출력으로 판정합니다(지금 프로필에 걸리고 TCP·도구 포트에 해당하는 규칙만 봄).
    // subnet: 지금 QR 주소의 서브넷(없으면 null). 허용 규칙이 다른 주소 대역으로 좁혀져 있으면 "열려 있음"으로 보지 않습니다.
    internal static void Evaluate(string output, int port, string subnet)
    {
        bool enabled = false;
        bool defaultBlock = false;
        bool sawProfile = false;
        int profiles = 0;
        string blockRule = null;
        bool allow = false;
        bool allowElsewhere = false;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("profile=", StringComparison.Ordinal))
            {
                sawProfile = int.TryParse(line.Substring(8), out profiles);
            }
            else if (line.StartsWith("enabled=", StringComparison.Ordinal))
            {
                enabled = line.EndsWith("True", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("defaultBlock=", StringComparison.Ordinal))
            {
                defaultBlock = line.EndsWith("True", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("rule=", StringComparison.Ordinal))
            {
                string[] parts = line.Substring(5).Split(new[] { '|' }, 5);
                if (parts.Length < 5 || !int.TryParse(parts[0], out int action) || !int.TryParse(parts[1], out int protocol))
                {
                    continue;
                }

                if ((protocol != PROTOCOL_TCP && protocol != PROTOCOL_ANY) || !PortsInclude(parts[2], port))
                {
                    continue;
                }

                if (action == 0)
                {
                    blockRule = blockRule ?? parts[4];
                }
                else if (RemoteIncludes(parts[3], subnet))
                {
                    allow = true;
                }
                else
                {
                    allowElsewhere = true;
                }
            }
        }

        lock (stateLock)
        {
            currentProfiles = sawProfile ? profiles : 0;
        }

        string where = sawProfile ? $" (지금 네트워크 프로필: {ProfileText(profiles)})" : string.Empty;
        if (!sawProfile)
        {
            Set(Verdict.Unsupported, "방화벽 상태를 읽지 못했습니다: " + (output.Length > 200 ? output.Substring(0, 200) : output).Trim());
        }
        else if (!enabled)
        {
            Set(Verdict.Off, "Windows 방화벽은 꺼져 있습니다." + where);
        }
        else if (blockRule != null)
        {
            Set(Verdict.Blocked, $"Windows 방화벽이 폰에서 들어오는 연결을 막고 있습니다(규칙: {blockRule}). [방화벽 허용…]을 누르거나 USB 로 연결하세요." + where);
        }
        else if (allow)
        {
            Set(Verdict.Allowed, "Windows 방화벽은 열려 있습니다." + where);
        }
        else if (defaultBlock)
        {
            Set(Verdict.WillPrompt, (allowElsewhere
                ? "허용 규칙이 다른 네트워크(주소 대역)용입니다. 이 네트워크에서 쓰려면 [방화벽 허용…]을 다시 누르세요."
                : "Windows 가 처음 한 번 허용 여부를 물을 수 있습니다. 물으면 [액세스 허용]을 누르세요. 창이 뜨지 않았으면 [방화벽 허용…]") + where);
        }
        else
        {
            Set(Verdict.Allowed, "Windows 방화벽이 인바운드를 막지 않는 설정입니다." + where);
        }
    }

    // 허용 규칙의 원격 주소가 지금 서브넷의 기기를 받는가. "*", 빈 값, LocalSubnet 은 받음. 주소 대역이면 지금 서브넷 표기가 들어 있어야 받음.
    // (정확한 포함 계산은 하지 않습니다: 이 도구가 넣는 규칙과 Windows 가 물어서 넣는 규칙을 가리는 용도)
    public static bool RemoteIncludes(string remote, string subnet)
    {
        if (string.IsNullOrEmpty(remote) || remote.Trim() == "*" || remote.IndexOf(REMOTE_LOCAL_SUBNET, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return subnet != null && remote.IndexOf(subnet, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // 규칙의 로컬 포트("*", 빈 값, "7300", "1-7299,7301-65535")가 이 포트를 포함하는가
    public static bool PortsInclude(string ports, int port)
    {
        if (string.IsNullOrEmpty(ports) || ports.Trim() == "*")
        {
            return true;
        }

        foreach (string part in ports.Split(','))
        {
            string item = part.Trim();
            int dash = item.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(item.Substring(0, dash), out int from) && int.TryParse(item.Substring(dash + 1), out int to) && port >= from && port <= to)
                {
                    return true;
                }
            }
            else if (int.TryParse(item, out int single) && single == port)
            {
                return true;
            }
        }

        return false;
    }

    private static string Encode(string script)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    // 관리자 권한 없이 실행합니다(읽기와 미리 보기). 스크립트를 임시 파일로 쓰지 않고 인자(-EncodedCommand)에 실어 넘깁니다(리뷰 m12).
    private static string RunPowerShell(string script)
    {
        ProcessStartInfo info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Encode(script))
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
            if (!process.WaitForExit(READ_TIMEOUT_MS))
            {
                process.Kill();
                throw new TimeoutException("PowerShell 이 제때 끝나지 않았습니다.");
            }

            return output.Result + error.Result;
        }
    }

    // 관리자 권한으로 실행(UAC). 끝나면 종료 코드와 결과 파일을 읽어 실패를 표시하고(코드 리뷰 M3-e) 상태를 다시 읽습니다.
    private static void RunElevated(string script, string resultPath, string what)
    {
        lock (stateLock)
        {
            lastActionProblem = null;
        }

        try
        {
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
        }
        catch (Exception)
        {
        }

        Process process;
        try
        {
            ProcessStartInfo info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Encode(script))
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            process = Process.Start(info);
        }
        catch (Exception e)
        {
            NoteElevationFailure(e, what);
            return;
        }

        if (process == null)
        {
            NoteActionProblem($"{what}: 실행하지 못했습니다.");
            return;
        }

        new Thread(() =>
        {
            try
            {
                bool exited = process.WaitForExit(WRITE_TIMEOUT_MS);
                int code = exited ? process.ExitCode : -1;
                process.Dispose();
                NoteActionResult(what, exited, code, ReadResult(resultPath));
            }
            catch (Exception e)
            {
                NoteActionProblem($"{what}: 결과를 확인하지 못했습니다({e.Message}). [다시 확인]을 누르세요.");
            }

            RefreshAsyncFromAnyThread();
        }) { IsBackground = true, Name = "PlayLink Firewall Wait" }.Start();
    }

    private static string ReadResult(string resultPath)
    {
        try
        {
            return File.Exists(resultPath) ? File.ReadAllText(resultPath).Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 승격된 스크립트가 끝난 뒤: 종료 코드와 결과 한 줄로 성공/실패를 정합니다.
    internal static void NoteActionResult(string what, bool exited, int exitCode, string result)
    {
        if (!exited)
        {
            NoteActionProblem($"{what}: {WRITE_TIMEOUT_MS / 1000}초 안에 끝나지 않았습니다. [다시 확인]을 누르세요.");
        }
        else if (exitCode != 0 || result == null || !result.StartsWith("result=ok", StringComparison.Ordinal))
        {
            NoteActionProblem($"{what}: 적용하지 못했습니다(종료 코드 {exitCode}{(string.IsNullOrEmpty(result) ? string.Empty : ", " + result)}).");
        }
    }

    // UAC 에서 [아니요]를 눌렀거나 실행 자체가 안 됐을 때. 규칙은 바뀌지 않았습니다.
    internal static void NoteElevationFailure(Exception e, string what)
    {
        System.ComponentModel.Win32Exception win32 = e as System.ComponentModel.Win32Exception;
        NoteActionProblem(win32 != null && win32.NativeErrorCode == ERROR_CANCELLED
            ? $"{what}: 관리자 확인을 취소했습니다. 방화벽은 바뀌지 않았습니다."
            : $"{what}: 실행하지 못했습니다({e.Message}). 방화벽은 바뀌지 않았습니다.");
    }

    private static void NoteActionProblem(string text)
    {
        lock (stateLock)
        {
            lastActionProblem = text;
        }

        PlayLinkHost.Post(null);
    }
}
