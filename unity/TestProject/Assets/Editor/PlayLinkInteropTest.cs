using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Unity 쪽 연결 코드를 Node 폰 흉내(tools/unity-peer-test.js, 기준 구현 playlink-proto.js 를 씀)와 실제 소켓으로 맞춰 보는 시험.
//   Unity.exe -batchmode -nographics -projectPath <TestProject> -executeMethod PlayLinkInteropTest.Run -logFile <로그>
//   (-quit 를 붙이지 않습니다. 끝나면 스스로 종료: 전부 통과 0, 실패 1, 시간 초과 3)
// 이 PC 의 실제 신원 파일은 건드리지 않습니다(임시 폴더를 씀). 키, QR 비밀은 로그에 적지 않습니다.
public static class PlayLinkInteropTest
{
    private const int PORT = 7411;
    private const int UNUSED_UDP_PORT = 7412;
    private const double TOTAL_TIMEOUT_SECONDS = 300.0;

    [Serializable]
    private class SimStatus
    {
        public string id;
        public bool ready;
        public int queries;
        public int whos;
        public int pairs;
        public string pairMode;
        public string digits;
        public string refused;
        public string failed;
        public bool videoOpen;
        public bool inputOpen;
        public int videoOpens;
        public int inputOpens;
        public int hello;
        public string seats;
        public int frames;
        public int badFrames;
        public int pongs;
        public int closeCode;
        public string finCaps;
        public int finProto;
        public string pcName;
        public bool take;
    }

    private static readonly StringBuilder log = new StringBuilder();
    private static readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
    private static int passed;
    private static int failed;
    private static double deadline;
    private static double waitUntil;
    private static string folder;
    private static string statePath;
    private static string statusPath;
    private static Process sim;
    private static string savedManualAddress;
    private static bool hadManualAddress;
    private const string PREF_MANUAL_ADDRESS = "PlayLink_ManualAddress";

    public static void Run()
    {
        folder = Path.Combine(Path.GetTempPath(), "playlink-interop-" + Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(folder);
        statePath = Path.Combine(folder, "phone.json");
        statusPath = Path.Combine(folder, "status.json");
        PlayLinkIdentity.UseFolderForTests(Path.Combine(folder, "pc"));
        PlayLinkDiscovery.UdpPort = PORT;
        hadManualAddress = EditorPrefs.HasKey(PREF_MANUAL_ADDRESS);
        savedManualAddress = PlayLinkLink.ManualAddress;
        deadline = EditorApplication.timeSinceStartup + TOTAL_TIMEOUT_SECONDS;
        stack.Push(Script());
        EditorApplication.update += Step;
    }

    private static void Step()
    {
        try
        {
            if (EditorApplication.timeSinceStartup > deadline)
            {
                Check("전체 시간 안에 끝남", false);
                Finish(3);
                return;
            }

            if (EditorApplication.timeSinceStartup < waitUntil)
            {
                return;
            }

            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                if (!top.MoveNext())
                {
                    stack.Pop();
                    continue;
                }

                if (top.Current is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }

                if (top.Current is double seconds)
                {
                    waitUntil = EditorApplication.timeSinceStartup + seconds;
                }

                return;
            }

            Finish(failed == 0 ? 0 : 1);
        }
        catch (Exception e)
        {
            log.Append("  FAIL 예외: ").Append(e.GetType().Name).Append(" ").Append(e.Message).Append('\n').Append(e.StackTrace).Append('\n');
            failed++;
            Finish(1);
        }
    }

    private static void Finish(int code)
    {
        EditorApplication.update -= Step;
        try
        {
            PlayLinkLink.Disarm();
            PlayLinkLink.SetManualAddress(savedManualAddress);
            if (!hadManualAddress)
            {
                EditorPrefs.DeleteKey(PREF_MANUAL_ADDRESS);
            }
        }
        catch (Exception)
        {
        }

        StopSim();
        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception)
        {
        }

        Debug.Log("[PlayLink] INTEROP " + (code == 0 ? "PASS" : "FAIL") + "\n" + log + "통과 " + passed + ", 실패 " + failed);
        EditorApplication.Exit(code);
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            passed++;
        }
        else
        {
            failed++;
        }

        log.Append(ok ? "  ok   " : "  FAIL ").Append(name).Append('\n');
    }

    // 조건이 될 때까지 기다립니다(한 번에 한 에디터 업데이트). 결과는 lastWait.
    private static bool lastWait;

    private static IEnumerator WaitFor(Func<bool> condition, double seconds)
    {
        double until = EditorApplication.timeSinceStartup + seconds;
        while (!condition())
        {
            if (EditorApplication.timeSinceStartup > until)
            {
                lastWait = false;
                yield break;
            }

            yield return null;
        }

        lastWait = true;
    }

    private static IEnumerator Expect(string name, Func<bool> condition, double seconds)
    {
        yield return WaitFor(condition, seconds);
        Check(name, lastWait);
    }

    private static SimStatus Status()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(statusPath))
                {
                    SimStatus status = JsonUtility.FromJson<SimStatus>(File.ReadAllText(statusPath));
                    if (status != null)
                    {
                        return status;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        return new SimStatus();
    }

    private static string RepoRoot()
    {
        // <repo>/unity/TestProject/Assets
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
    }

    // 폰 흉내를 띄웁니다. qrText 는 환경 변수로만 넘깁니다(명령줄과 로그에 남지 않게).
    private static IEnumerator StartSim(string options, string qrText)
    {
        StopSim();
        File.Delete(statusPath);
        ProcessStartInfo info = new ProcessStartInfo("node",
            "\"" + Path.Combine(RepoRoot(), "tools", "unity-peer-test.js") + "\" --port " + PORT + " --state \"" + statePath + "\" --status \"" + statusPath + "\" --name SimPhone " + options)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.EnvironmentVariables.Remove("PLAYLINK_QR");
        if (qrText != null)
        {
            info.EnvironmentVariables["PLAYLINK_QR"] = qrText;
        }

        sim = Process.Start(info);
        sim.OutputDataReceived += (s, e) => { };
        sim.ErrorDataReceived += (s, e) => { };
        sim.BeginOutputReadLine();
        sim.BeginErrorReadLine();
        yield return WaitFor(() => Status().ready, 10.0);
        if (!lastWait)
        {
            Check("폰 흉내가 뜸(" + options + ")", false);
        }
    }

    private static void StopSim()
    {
        if (sim == null)
        {
            return;
        }

        try
        {
            if (!sim.HasExited)
            {
                sim.Kill();
                sim.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
        }

        sim = null;
    }

    private static void Tell(string command)
    {
        sim.StandardInput.Write(command + "\n");
        sim.StandardInput.Flush();
    }

    private static PlayLinkDiscovery.Phone Seen(string id, bool verified)
    {
        return PlayLinkDiscovery.Snapshot().Find(p => p.id == id && p.verified == verified);
    }

    private static bool Connected { get { return PlayLinkLink.Current == PlayLinkLink.State.Connected; } }

    private static bool FailedWith(string code)
    {
        return PlayLinkLink.Current == PlayLinkLink.State.Failed && PlayLinkLink.ErrorCode == code;
    }

    private static int PhoneCount { get { return PlayLinkIdentity.Current.phones.Length; } }

    private static IEnumerator Script()
    {
        // ---- 1. 탐색
        yield return StartSim(string.Empty, null);
        string id = Status().id;
        Check("새 신원이 만들어짐(pcId 모양, 폰 없음)", PlayLinkCrypto.IsId(PlayLinkIdentity.Current.pcId) && PhoneCount == 0);
        PlayLinkLink.Arm();
        yield return Expect("탐색: UDP 브로드캐스트로 같은 PC 의 폰 흉내가 '새 폰'으로 보임", () => Seen(id, false) != null, 6.0);
        PlayLinkDiscovery.Phone found = Seen(id, false);
        Check("탐색: 이름과 포트가 맞음", found != null && found.name == "SimPhone" && found.port == PORT && !string.IsNullOrEmpty(found.host));
        yield return Expect("탐색: 폰 흉내가 질문을 받음", () => Status().queries > 0, 2.0);
        Check("탐색: 사람이 고르기 전에는 접속하지 않음", PlayLinkLink.Current == PlayLinkLink.State.Idle && Status().videoOpens == 0 && Status().pairs == 0);
        PlayLinkProto.ReplyKind kind = PlayLinkProto.ReplyKind.Bad;
        PlayLinkProto.Message who = PlayLinkProto.Who("127.0.0.1", PORT, PlayLinkIdentity.Current, out kind);
        Check("who: 페어링 전에는 증명 없는 응답(None)", kind == PlayLinkProto.ReplyKind.None && who.ph == id);

        // ---- 2. 숫자 확인: 폰에서 거절
        yield return StartSim("--reject", null);
        yield return Expect("숫자(폰 거절): 폰이 다시 보임", () => Seen(id, false) != null, 6.0);
        PlayLinkLink.BeginPair(Seen(id, false) ?? found);
        yield return Expect("숫자(폰 거절): 페어링 실패로 끝남", () => PlayLinkLink.Pairing != null && PlayLinkLink.Pairing.stage == PlayLinkLink.PairStage.Failed, 10.0);
        Check("숫자(폰 거절): 저장된 폰 없음", PhoneCount == 0 && Status().pairs == 0);
        PlayLinkLink.CancelPair();

        // ---- 3. 숫자 확인: PC 에서 취소
        yield return StartSim(string.Empty, null);
        yield return Expect("숫자(PC 취소): 폰이 다시 보임", () => Seen(id, false) != null, 6.0);
        PlayLinkLink.BeginPair(Seen(id, false) ?? found);
        yield return Expect("숫자(PC 취소): 양쪽에 숫자가 뜸", () => PlayLinkLink.Pairing != null && PlayLinkLink.Pairing.stage == PlayLinkLink.PairStage.Digits, 10.0);
        yield return Expect("숫자(PC 취소): PC 와 폰의 6자리 숫자가 같음", () => PlayLinkLink.Pairing != null && PlayLinkLink.Pairing.digits != null
            && PlayLinkLink.Pairing.digits.Length == 6 && PlayLinkLink.Pairing.digits == Status().digits, 2.0);
        yield return 0.6;
        Check("숫자(PC 취소): 폰이 [허용]해도 PC 가 확인하기 전에는 페어링되지 않음", PhoneCount == 0 && Status().pairs == 0
            && PlayLinkLink.Pairing != null && PlayLinkLink.Pairing.stage == PlayLinkLink.PairStage.Digits);
        PlayLinkLink.CancelPair();
        yield return Expect("숫자(PC 취소): 폰이 거절을 받음", () => (Status().refused ?? string.Empty).Contains("rejected"), 5.0);
        yield return Expect("숫자(PC 취소): 페어링이 끝남", () => PlayLinkLink.Pairing == null || PlayLinkLink.Pairing.stage == PlayLinkLink.PairStage.Failed, 5.0);
        Check("숫자(PC 취소): 저장된 폰 없음", PhoneCount == 0 && Status().pairs == 0);
        PlayLinkLink.CancelPair();

        // ---- 4. 숫자 확인: 성공 → 바로 연결
        PlayLinkLink.BeginPair(Seen(id, false) ?? found);
        yield return Expect("숫자: 양쪽에 숫자가 뜸", () => PlayLinkLink.Pairing != null && PlayLinkLink.Pairing.stage == PlayLinkLink.PairStage.Digits, 10.0);
        yield return Expect("숫자: PC 와 폰의 숫자가 같음", () => PlayLinkLink.Pairing != null && PlayLinkLink.Pairing.digits == Status().digits, 2.0);
        PlayLinkLink.ConfirmPair();
        yield return Expect("숫자: 페어링 뒤 영상 · 입력 연결이 붙음", () => Connected && Status().videoOpen && Status().inputOpen, 10.0);
        SimStatus s = Status();
        Check("숫자: 양쪽이 페어링을 저장함(방식 sas)", PhoneCount == 1 && PlayLinkIdentity.Current.Find(id) != null && s.pairs == 1 && s.pairMode == "sas");
        Check("연결: fin 의 proto 2, caps h264,seat,take", s.finProto == 2 && s.finCaps == "h264,seat,take");
        Check("연결: 사람이 시작한 연결은 take=true", s.take);
        Check("연결: 폰이 PC 이름을 받음", s.pcName == PlayLinkIdentity.Current.pcName);
        yield return Expect("연결: 영상 연결의 첫 메시지가 hello(v 2), 그다음 대기 문구(seat lobby)", () => Status().hello == 2 && Status().seats == "lobby*", 5.0);
        int pongs = Status().pongs;
        yield return Expect("연결: Play 밖에서도 입력 연결의 ping 에 답함(양방향 암호화 통신)", () => Status().pongs >= pongs + 3, 5.0);
        Check("연결: Play 밖에서는 프레임을 보내지 않음", Status().frames == 0 && Status().badFrames == 0);
        yield return Expect("탐색: 페어링 뒤에는 증명이 맞는 '페어링된 폰'으로 보임", () => Seen(id, true) != null, 6.0);
        yield return Expect("탐색: 같은 폰이 '새 폰' 목록에서는 사라짐", () => Seen(id, false) == null, 8.0);
        who = PlayLinkProto.Who("127.0.0.1", PORT, PlayLinkIdentity.Current, out kind);
        Check("who: 페어링 뒤에는 증명이 맞음(Paired)", kind == PlayLinkProto.ReplyKind.Paired);
        byte[] file = File.ReadAllBytes(PlayLinkIdentity.FilePath);
        Check("저장: 신원 파일에 pcId 와 폰 ID 가 평문으로 보이지 않음(DPAPI)", IndexOf(file, Encoding.UTF8.GetBytes(PlayLinkIdentity.Current.pcId)) < 0
            && IndexOf(file, Encoding.UTF8.GetBytes(id)) < 0);

        // ---- 5. 끊김 → 다시 걸기
        int opens = Status().videoOpens;
        Tell("drop");
        yield return Expect("끊김: 연결이 끊긴 것을 알아챔", () => !Connected, 5.0);
        yield return Expect("끊김: 스스로 다시 걸어 붙음", () => Connected && Status().videoOpens == opens + 1 && Status().inputOpen, 10.0);
        Check("끊김: 다시 거는 연결은 take=false", !Status().take);

        // ---- 6. 폰에서 끊음(닫기 1000) → 다시 걸지 않음
        opens = Status().videoOpens;
        Tell("close");
        yield return Expect("폰에서 끊음: phone_closed 로 멈춤", () => FailedWith("phone_closed"), 5.0);
        yield return 2.0;
        Check("폰에서 끊음: 다시 걸지 않음", FailedWith("phone_closed") && Status().videoOpens == opens);
        PlayLinkLink.Reconnect();
        yield return Expect("[다시 연결]: 붙음", () => Connected && Status().videoOpens == opens + 1, 10.0);

        // ---- 7. PC 에서 끊기 → 닫기 코드 1000
        PlayLinkLink.Disconnect();
        yield return Expect("[끊기]: 폰이 닫기 코드 1000 을 받고 두 연결이 닫힘", () => Status().closeCode == 1000 && !Status().videoOpen && !Status().inputOpen, 5.0);
        Check("[끊기]: 고른 폰이 없어짐", PlayLinkLink.Current == PlayLinkLink.State.Idle && PlayLinkLink.TargetId == null);
        opens = Status().videoOpens;
        yield return 2.0;
        Check("[끊기]: 다시 걸지 않음", Status().videoOpens == opens);

        // ---- 8. 라벨 바꾸기 + 디스크에서 다시 읽기 + 목록에서 골라 연결
        PlayLinkIdentity.SetPhoneLabel(id, "내 폰 (시험)");
        string pcId = PlayLinkIdentity.Current.pcId;
        PlayLinkIdentity.UseFolderForTests(Path.Combine(folder, "pc"));
        Check("저장: 다시 읽어도 pcId, 라벨, 마지막 주소가 그대로", PlayLinkIdentity.Current.pcId == pcId && PhoneCount == 1
            && PlayLinkIdentity.Current.Find(id).label == "내 폰 (시험)" && PlayLinkIdentity.Current.Find(id).port == PORT);
        yield return WaitFor(() => Seen(id, true) != null, 6.0);
        PlayLinkLink.Connect(Seen(id, true));
        yield return Expect("목록에서 고름: 다시 읽은 키로 붙음", () => Connected && Status().videoOpen && Status().inputOpen, 10.0);
        PlayLinkLink.Disconnect();
        yield return WaitFor(() => !Status().videoOpen, 5.0);

        // ---- 9. 폰이 이 PC 를 잊음 → unknown_pc → 다시 페어링
        string goodState = File.ReadAllText(statePath);
        StopSim();
        File.WriteAllText(statePath, "{\"id\":\"" + id + "\",\"name\":\"SimPhone\",\"peers\":{}}");
        yield return StartSim(string.Empty, null);
        PlayLinkLink.ConnectLastAddress(PlayLinkIdentity.Current.Find(id));
        yield return Expect("unknown_pc: 폰이 모른다고 하면 멈춤(다시 걸지 않음)", () => FailedWith("unknown_pc"), 10.0);
        yield return Expect("unknown_pc: 폰 쪽도 unknown_pc 로 거절", () => (Status().refused ?? string.Empty).Contains("unknown_pc"), 2.0);
        PlayLinkLink.RePairTarget();
        yield return Expect("다시 페어링: 숫자가 뜸", () => PlayLinkLink.Pairing != null && PlayLinkLink.Pairing.stage == PlayLinkLink.PairStage.Digits, 10.0);
        PlayLinkLink.ConfirmPair();
        yield return Expect("다시 페어링: 새 키로 붙음", () => Connected && Status().videoOpen && Status().inputOpen && Status().pairs == 1, 10.0);
        Check("다시 페어링: 폰은 여전히 하나, 라벨 유지", PhoneCount == 1 && PlayLinkIdentity.Current.Find(id).label == "내 폰 (시험)");
        PlayLinkLink.Disconnect();
        yield return WaitFor(() => !Status().videoOpen, 5.0);

        // ---- 10. 키가 서로 다름 → auth_failed
        goodState = File.ReadAllText(statePath);
        StopSim();
        File.WriteAllText(statePath, "{\"id\":\"" + id + "\",\"name\":\"SimPhone\",\"peers\":{\"" + pcId + "\":{\"key\":\""
            + PlayLinkCrypto.Base64Url(PlayLinkCrypto.RandomBytes(32)) + "\",\"name\":\"x\"}}}");
        yield return StartSim(string.Empty, null);
        PlayLinkLink.ConnectLastAddress(PlayLinkIdentity.Current.Find(id));
        yield return Expect("auth_failed: 키가 다르면 멈춤", () => FailedWith("auth_failed"), 10.0);
        Check("auth_failed: 연결이 열리지 않음", Status().videoOpens == 0 && Status().inputOpens == 0);
        yield return 5.0;
        Check("auth_failed: 증명이 틀린 탐색 응답은 목록에 넣지 않음(예전 응답은 4초 뒤 사라짐)", Seen(id, true) == null && Seen(id, false) == null && Status().queries > 0);
        who = PlayLinkProto.Who("127.0.0.1", PORT, PlayLinkIdentity.Current, out kind);
        Check("auth_failed: who 의 증명도 틀림(Bad)", kind == PlayLinkProto.ReplyKind.Bad);
        PlayLinkLink.Disconnect();

        // ---- 11. 폰이 다른 PC 를 보는 중 → busy
        StopSim();
        File.WriteAllText(statePath, goodState);
        yield return StartSim("--busy", null);
        yield return Expect("busy: 폰이 다시 '페어링된 폰'으로 보이고 busy 가 채워짐", () => Seen(id, true) != null && !string.IsNullOrEmpty(Seen(id, true).busy), 6.0);
        PlayLinkLink.Connect(Seen(id, true));
        yield return Expect("busy: 폰이 거절하면 멈춤", () => FailedWith("busy"), 10.0);
        PlayLinkLink.Disconnect();

        // ---- 12. 폰이 "연결해 달라"(want) → 스스로 연결
        yield return StartSim("--want", null);
        yield return Expect("want: 페어링된 폰이 원하면 스스로 붙음", () => Connected && Status().videoOpen && Status().inputOpen, 10.0);
        PlayLinkLink.Disconnect();
        yield return WaitFor(() => !Status().videoOpen, 5.0);
        opens = Status().videoOpens;
        yield return 2.5;
        Check("want: 사람이 끊은 뒤에는 같은 want 로 되풀이해 붙지 않음", Status().videoOpens == opens && PlayLinkLink.Current == PlayLinkLink.State.Idle);

        // ---- 13. 페어링 해제
        PlayLinkLink.Unpair(id);
        Check("페어링 해제: 폰이 지워짐", PhoneCount == 0);

        // ---- 14. QR: 틀린 비밀을 가진 폰은 무시
        string qr = PlayLinkLink.BuildQrText();
        string fakeQr = qr.Substring(0, qr.LastIndexOf("s=", StringComparison.Ordinal) + 2) + PlayLinkCrypto.Base64Url(PlayLinkCrypto.RandomBytes(16));
        StopSim();
        File.Delete(statePath);
        yield return StartSim(string.Empty, fakeQr);
        yield return 3.5;
        Check("QR(틀린 비밀): 페어링을 시작하지 않고 폰도 저장하지 않음", PhoneCount == 0 && PlayLinkLink.Pairing == null && Status().pairs == 0 && Status().queries > 0);
        Check("QR(틀린 비밀): QR 은 그대로", PlayLinkLink.BuildQrText() == qr);

        // ---- 15. QR: 맞는 QR 을 찍은 폰 → 숫자 확인 없이 페어링 + 연결
        StopSim();
        File.Delete(statePath);
        yield return StartSim(string.Empty, qr);
        string qrPhone = Status().id;
        yield return Expect("QR: 찍은 폰과 스스로 페어링하고 붙음", () => Connected && Status().videoOpen && Status().inputOpen, 12.0);
        s = Status();
        Check("QR: 양쪽이 페어링을 저장함(방식 qr, 숫자 확인 없음)", PhoneCount == 1 && PlayLinkIdentity.Current.Find(qrPhone) != null && s.pairs == 1 && s.pairMode == "qr"
            && string.IsNullOrEmpty(s.digits));
        Check("QR: 한 번 쓴 QR 은 새로 바뀜", PlayLinkLink.BuildQrText() != qr);
        yield return Expect("QR: 대기 문구가 감", () => Status().hello == 2 && Status().seats == "lobby*", 5.0);
        PlayLinkLink.Disconnect();
        yield return WaitFor(() => !Status().videoOpen, 5.0);

        // ---- 16. 직접 입력한 주소(TCP who): UDP 탐색이 닿지 않는 포트로 바꿔 놓고 확인
        PlayLinkLink.Disarm();
        PlayLinkDiscovery.UdpPort = UNUSED_UDP_PORT;
        yield return StartSim("--no-udp", null);
        PlayLinkLink.Arm();
        yield return 2.5;
        Check("주소 입력: UDP 로는 보이지 않음", PlayLinkDiscovery.Snapshot().Count == 0);
        PlayLinkLink.SetManualAddress("127.0.0.1:" + PORT);
        yield return Expect("주소 입력: TCP 로 물어 '페어링된 폰'으로 보임", () => Seen(qrPhone, true) != null && Seen(qrPhone, true).host == "127.0.0.1", 6.0);
        yield return Expect("주소 입력: 폰이 who 를 받음", () => Status().whos > 0, 2.0);
        PlayLinkLink.Connect(Seen(qrPhone, true));
        yield return Expect("주소 입력: 그 주소로 붙음", () => Connected && Status().videoOpen && Status().inputOpen, 10.0);
        PlayLinkLink.SetManualAddress("not an address!");
        Check("주소 입력: 형식이 틀리면 저장하지 않음", PlayLinkLink.ManualAddress == "127.0.0.1:" + PORT);

        // ---- 17. Stop
        PlayLinkLink.Disarm();
        yield return Expect("Stop: 연결이 닫히고(1000) 폰 찾기가 멈춤", () => Status().closeCode == 1000 && !Status().videoOpen && !PlayLinkDiscovery.IsRunning, 5.0);
        Check("Stop: 상태가 Idle", PlayLinkLink.Current == PlayLinkLink.State.Idle && !PlayLinkLink.Armed);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }
}
