using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// PlayLink 창의 그리기 코드를 상태별로 실제로 돌려 보는 시험(예외 없이 Layout + Repaint 가 도는지). 창의 글자와 배치는 사람이 눈으로 봐야 합니다.
//   Unity.exe -projectPath <TestProject> -executeMethod PlayLinkWindowTest.Run -logFile <로그>
//   (-batchmode 없이 돌립니다: 창이 실제로 그려져야 함. 끝나면 스스로 종료: 통과 0, 실패 1)
// USB 구역은 열지 않습니다(adb 를 부르지 않게). 이 PC 의 실제 신원 파일은 건드리지 않습니다.
public class PlayLinkWindowTest : EditorWindow
{
    private const int PORT = 7431;
    private const double TOTAL_TIMEOUT_SECONDS = 150.0;

    [Serializable]
    private class SimStatus
    {
        public string id;
        public bool ready;
        public bool videoOpen;
    }

    private static readonly StringBuilder log = new StringBuilder();
    private static readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
    private static int passed;
    private static int failed;
    private static double deadline;
    private static double waitUntil;
    private static bool lastWait;
    private static string folder;
    private static Process sim;
    private static PlayLinkWindowTest host;
    private static PlayLinkWindow inner;
    private static MethodInfo innerOnGui;
    private static int layouts;
    private static int repaints;
    private static string guiError;

    public static void Run()
    {
        folder = Path.Combine(Path.GetTempPath(), "playlink-window-" + Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(folder);
        PlayLinkIdentity.UseFolderForTests(Path.Combine(folder, "pc"));
        PlayLinkDiscovery.UdpPort = PORT;
        deadline = EditorApplication.timeSinceStartup + TOTAL_TIMEOUT_SECONDS;
        inner = CreateInstance<PlayLinkWindow>();
        inner.hideFlags = HideFlags.HideAndDontSave;
        innerOnGui = typeof(PlayLinkWindow).GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.NonPublic);
        host = CreateInstance<PlayLinkWindowTest>();
        host.titleContent = new GUIContent("PlayLink (시험)");
        host.position = new Rect(60, 60, 520, 980);
        host.ShowUtility();
        host.Focus();
        stack.Push(Script());
        EditorApplication.update += Step;
    }

    // 실제 창과 같은 그리기 코드를 이 창 안에서 돌립니다.
    private void OnGUI()
    {
        if (inner == null || innerOnGui == null)
        {
            return;
        }

        try
        {
            inner.position = position;
            innerOnGui.Invoke(inner, null);
            if (Event.current.type == EventType.Layout)
            {
                layouts++;
            }
            else if (Event.current.type == EventType.Repaint)
            {
                repaints++;
            }
        }
        catch (TargetInvocationException e)
        {
            if (e.InnerException is ExitGUIException)
            {
                throw e.InnerException;
            }

            guiError = e.InnerException.GetType().Name + ": " + e.InnerException.Message + "\n" + e.InnerException.StackTrace;
        }
    }

    private static void Step()
    {
        try
        {
            if (host != null)
            {
                host.Repaint();
            }

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
        }
        catch (Exception)
        {
        }

        try
        {
            if (sim != null && !sim.HasExited)
            {
                sim.Kill();
                sim.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception)
        {
        }

        Debug.Log("[PlayLink] WINDOW " + (code == 0 ? "PASS" : "FAIL") + "\n" + log + "통과 " + passed + ", 실패 " + failed);
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

    // 지금 상태로 창이 예외 없이 몇 번 그려지는지 봅니다.
    private static IEnumerator Draws(string name)
    {
        yield return 0.3;
        layouts = 0;
        repaints = 0;
        guiError = null;
        yield return WaitFor(() => (layouts >= 3 && repaints >= 3) || guiError != null, 6.0);
        Check("창 그리기: " + name + (guiError != null ? "\n" + guiError : lastWait ? string.Empty : " (Layout " + layouts + ", Repaint " + repaints + ")"), lastWait && guiError == null);
    }

    private static SimStatus Status()
    {
        try
        {
            string path = Path.Combine(folder, "status.json");
            if (File.Exists(path))
            {
                return JsonUtility.FromJson<SimStatus>(File.ReadAllText(path)) ?? new SimStatus();
            }
        }
        catch (Exception)
        {
        }

        return new SimStatus();
    }

    private static void SetFoldouts(bool qr, bool manual, bool download, bool settings, bool status)
    {
        SessionState.SetBool("PlayLink_PairQrOpen", qr);
        SessionState.SetBool("PlayLink_ManualOpen", manual);
        SessionState.SetBool("PlayLink_UsbOpen", false);
        SessionState.SetBool("PlayLink_DownloadOpen", download);
        SessionState.SetBool("PlayLink_WindowSettingsOpen", settings);
        SessionState.SetBool("PlayLink_WindowStatusOpen", status);
    }

    private static IEnumerator Script()
    {
        SetFoldouts(false, false, false, false, false);
        yield return Draws("Start 전(전부 접음)");
        SetFoldouts(false, false, true, true, true);
        yield return Draws("Start 전(앱 다운로드 QR, 설정, 통계 펼침)");
        Check("앱 다운로드 주소의 기본값", PlayLink.AppDownloadUrl == PlayLink.DEFAULT_APP_URL);

        PlayLinkLink.Arm();
        SetFoldouts(false, false, false, false, false);
        yield return Draws("Start 뒤, 폰 없음");
        SetFoldouts(true, true, false, false, false);
        yield return Draws("페어링 QR 과 주소 입력 펼침");
        Check("페어링 QR 텍스처가 만들어짐", PlayLinkLinkGUI.CurrentQrTexture != null && PlayLinkLinkGUI.CurrentQrTexture.width >= 100);
        SetFoldouts(false, false, false, false, false);

        string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
        ProcessStartInfo info = new ProcessStartInfo("node", "\"" + Path.Combine(repo, "tools", "unity-peer-test.js") + "\" --port " + PORT
            + " --state \"" + Path.Combine(folder, "phone.json") + "\" --status \"" + Path.Combine(folder, "status.json") + "\" --name WindowPhone")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        info.EnvironmentVariables.Remove("PLAYLINK_QR");
        sim = Process.Start(info);
        yield return WaitFor(() => Status().ready, 10.0);
        string id = Status().id;
        yield return WaitFor(() => PlayLinkDiscovery.Snapshot().Exists(p => p.id == id), 6.0);
        Check("폰 흉내가 목록에 보임", lastWait);
        yield return Draws("새 폰 1대");

        PlayLinkLink.BeginPair(PlayLinkDiscovery.Snapshot().Find(p => p.id == id));
        yield return WaitFor(() => PlayLinkLink.Pairing != null && PlayLinkLink.Pairing.stage == PlayLinkLink.PairStage.Digits, 10.0);
        yield return Draws("페어링: 6자리 숫자와 [확인]/[취소]");
        PlayLinkLink.ConfirmPair();
        yield return WaitFor(() => PlayLinkLink.Current == PlayLinkLink.State.Connected && Status().videoOpen, 12.0);
        Check("페어링 뒤 연결됨", lastWait);
        yield return Draws("연결됨(Play 밖)");
        SetFoldouts(false, false, false, true, true);
        yield return Draws("연결됨 + 설정, 통계 펼침");
        SetFoldouts(false, false, false, false, false);

        sim.StandardInput.Write("close\n");
        sim.StandardInput.Flush();
        yield return WaitFor(() => PlayLinkLink.Current == PlayLinkLink.State.Failed, 6.0);
        yield return Draws("폰에서 끊음(다시 연결 / 닫기)");
        PlayLinkLink.Disconnect();
        yield return 1.5;
        yield return Draws("페어링된 폰이 켜져 있음(연결 버튼)");
        sim.Kill();
        sim.WaitForExit(3000);
        yield return 5.0;
        yield return Draws("페어링된 폰이 보이지 않음");
        PlayLinkLink.ConnectLastAddress(PlayLinkIdentity.Current.phones[0]);
        yield return 2.0;
        yield return Draws("꺼진 폰에 다시 거는 중");
        PlayLinkLink.Disarm();
        yield return Draws("Stop 뒤");
    }
}
