using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// 실제 앱(가상 기기 또는 USB 폰)과 끝에서 끝까지 붙여 보는 시험. 창의 [USB 로 연결]과 같은 길(adb forward)을 씁니다.
//   Unity.exe -batchmode -projectPath <TestProject> -executeMethod PlayLinkDeviceTest.Run -playlinkSerial emulator-5554
//             -playlinkStatus <상태 파일> [-playlinkPlaySeconds 40] [-playlinkRealIdentity] -logFile <로그>     (-quit 없이, 스스로 종료)
// 순서: Start → USB 로 묻기 → (페어링 안 돼 있으면 숫자 확인: PC 쪽은 자동 [확인], 폰의 [허용]은 밖에서 눌러 줌) → 연결 → Play →
//       정한 시간 동안 화면 전송(밖에서 폰 화면을 찍고 터치를 넣음) → Play 종료 → 대기 문구 → Stop.
// 상태 파일에 한 줄씩 진행 상황을 적어 밖의 스크립트가 맞춰 움직이게 합니다.
[InitializeOnLoad]
public static class PlayLinkDeviceTest
{
    private const string KEY_PHASE = "PlayLinkDeviceTest_Phase";
    private const string KEY_T0 = "PlayLinkDeviceTest_T0";
    private const string FFMPEG = @"C:\Users\dbrud\Tools\ffmpeg-gvr\ffmpeg-n8.1-latest-win64-gpl-8.1\bin\ffmpeg.exe";
    private static double nextLog;
    private static bool confirmed;
    private static bool playRequested;
    private static string lastLine;

    static PlayLinkDeviceTest()
    {
        if (SessionState.GetInt(KEY_PHASE, 0) != 0)
        {
            Prepare();
            EditorApplication.update += Step;
        }
    }

    private static string Arg(string name, string fallback)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    private static bool Flag(string name)
    {
        return Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;
    }

    private static void Prepare()
    {
        if (!Flag("-playlinkRealIdentity"))
        {
            PlayLinkIdentity.UseFolderForTests(Path.Combine(Path.GetTempPath(), "playlink-device-test-pc"));
        }
    }

    public static void Run()
    {
        Prepare();
        if (string.IsNullOrEmpty(PlayLink.FfmpegPath) && File.Exists(FFMPEG))
        {
            PlayLink.FfmpegPath = FFMPEG;
        }

        PlayLink.UseH264 = true;
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(PlayLinkDemoScene.SCENE_PATH);
        EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")).Show();
        SessionState.SetInt(KEY_PHASE, 1);
        SessionState.SetFloat(KEY_T0, (float)EditorApplication.timeSinceStartup);
        PlayLinkLink.Arm();
        PlayLinkLink.ConnectUsb(Arg("-playlinkSerial", "emulator-5554"));
        EditorApplication.update += Step;
    }

    private static void Status(string line)
    {
        if (line == lastLine)
        {
            return;
        }

        lastLine = line;
        Debug.Log("[PlayLink] DEVICE " + line);
        try
        {
            File.AppendAllText(Arg("-playlinkStatus", Path.Combine(Path.GetTempPath(), "playlink-device-test.txt")), line + "\n");
        }
        catch (Exception)
        {
        }
    }

    private static void Step()
    {
        double now = EditorApplication.timeSinceStartup;
        double elapsed = now - SessionState.GetFloat(KEY_T0, 0f);
        int phase = SessionState.GetInt(KEY_PHASE, 0);
        PlayLinkLink.PairingInfo pairing = PlayLinkLink.Pairing;
        PlayLinkPeer peer = PlayLinkLink.Peer;
        bool connected = PlayLinkLink.Current == PlayLinkLink.State.Connected && peer != null && peer.IsOpen;

        if (now >= nextLog)
        {
            nextLog = now + 1.0;
            Debug.Log("[PlayLink] DEVICE t=" + elapsed.ToString("F0") + " phase=" + phase + " state=" + PlayLinkLink.Current + " err=" + PlayLinkLink.ErrorCode
                + " notice=" + PlayLinkLink.Notice + " pair=" + (pairing != null ? pairing.stage.ToString() : "-") + " playing=" + EditorApplication.isPlaying
                + " streaming=" + PlayLink.IsRunning + " encoder=" + PlayLinkCapture.CodecStatus
                + " buttons=" + PlayLinkDemo.ButtonPresses + " touches=" + PlayLinkDemo.TouchesBegan);
        }

        if (elapsed > 240.0)
        {
            Status("timeout phase=" + phase);
            Finish(3);
            return;
        }

        if (phase == 1)
        {
            if (pairing != null && pairing.stage == PlayLinkLink.PairStage.Digits && !confirmed)
            {
                confirmed = true;
                Status("digits " + pairing.digits);
                PlayLinkLink.ConfirmPair();
            }
            else if (pairing != null && pairing.stage == PlayLinkLink.PairStage.Failed)
            {
                Status("pair-failed " + pairing.error);
                Finish(1);
            }
            else if (PlayLinkLink.Current == PlayLinkLink.State.Failed)
            {
                Status("link-failed " + PlayLinkLink.ErrorCode);
                Finish(1);
            }
            else if (connected && !playRequested)
            {
                playRequested = true;
                Status("connected-lobby");
                SessionState.SetInt(KEY_PHASE, 2);
                SessionState.SetFloat(KEY_T0 + "_play", (float)now);
                EditorApplication.EnterPlaymode();
            }
        }
        else if (phase == 2)
        {
            double playFor = double.Parse(Arg("-playlinkPlaySeconds", "40"));
            double inPlay = now - SessionState.GetFloat(KEY_T0 + "_play", 0f);
            if (EditorApplication.isPlaying && connected && PlayLink.IsRunning)
            {
                Status("streaming encoder=" + PlayLinkCapture.CodecStatus);
            }

            if (inPlay > playFor && EditorApplication.isPlaying)
            {
                Status("play-end connected=" + connected + " buttons=" + PlayLinkDemo.ButtonPresses + " touches=" + PlayLinkDemo.TouchesBegan);
                SessionState.SetInt(KEY_PHASE, 3);
                SessionState.SetFloat(KEY_T0 + "_play", (float)now);
                EditorApplication.ExitPlaymode();
            }
        }
        else if (phase == 3)
        {
            double after = now - SessionState.GetFloat(KEY_T0 + "_play", 0f);
            if (!EditorApplication.isPlaying && after > 6.0)
            {
                Status("after-play connected=" + connected + " state=" + PlayLinkLink.Current);
                SessionState.SetInt(KEY_PHASE, 4);
                SessionState.SetFloat(KEY_T0 + "_play", (float)now);
            }
        }
        else if (phase == 4)
        {
            double after = now - SessionState.GetFloat(KEY_T0 + "_play", 0f);
            if (after > 5.0)
            {
                PlayLinkLink.Disarm();
                Status("stopped");
                Finish(0);
            }
        }
    }

    private static void Finish(int code)
    {
        SessionState.SetInt(KEY_PHASE, 0);
        EditorApplication.update -= Step;
        EditorApplication.Exit(code);
    }
}
