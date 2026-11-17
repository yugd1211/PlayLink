using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

// 실제 화면 전송 경로 시험: Node 폰 흉내와 페어링 · 연결한 뒤 Play 에 들어가(도메인 리로드) 다시 붙고, 프레임이 가고, 폰의 터치가 들어오고,
// Play 를 끝내면 대기 문구로 돌아가는지 봅니다. 그래픽 장치가 필요해서 -nographics 없이 돌립니다.
//   Unity.exe -batchmode -projectPath <TestProject> -executeMethod PlayLinkStreamTest.Run -playlinkCodec mjpeg|h264|libx264|h264_nvenc -logFile <로그>
//   (-quit 없이. 끝나면 스스로 종료: 통과 0, 실패 1, 시간 초과 3)
// Play 진입 때 도메인이 다시 로드되므로 진행 상황은 SessionState 에 두고, 폰 흉내는 명령 파일로 조종합니다.
// 이 PC 의 실제 신원 파일은 건드리지 않고(임시 폴더), 바꾼 EditorPrefs 는 끝날 때 되돌립니다.
[InitializeOnLoad]
public static class PlayLinkStreamTest
{
    private const int PORT = 7421;
    private const double TOTAL_TIMEOUT_SECONDS = 240.0;
    private const string KEY_PHASE = "PlayLinkStreamTest_Phase";
    private const string KEY_FOLDER = "PlayLinkStreamTest_Folder";
    private const string KEY_LOG = "PlayLinkStreamTest_Log";
    private const string KEY_PASSED = "PlayLinkStreamTest_Passed";
    private const string KEY_FAILED = "PlayLinkStreamTest_Failed";
    private const string KEY_PID = "PlayLinkStreamTest_Pid";
    private const string KEY_SAVED = "PlayLinkStreamTest_Saved";
    private const string KEY_STARTED = "PlayLinkStreamTest_Started";
    private static readonly string[] PREF_KEYS = { "PlayLink_VideoCodec", "PlayLink_H264Encoder", "PlayLink_FfmpegPath" };
    private const string FFMPEG = @"C:\Users\dbrud\Tools\ffmpeg-gvr\ffmpeg-n8.1-latest-win64-gpl-8.1\bin\ffmpeg.exe";

    [Serializable]
    private class SimStatus
    {
        public string id;
        public bool ready;
        public string digits;
        public string failed;
        public bool videoOpen;
        public bool inputOpen;
        public int videoOpens;
        public int hello;
        public string seats;
        public int statusTexts;
        public int frames;
        public int frameBytes;
        public int jpegFrames;
        public int h264Frames;
        public int h264Configs;
        public int h264Keys;
        public int badFrames;
        public int width;
        public int height;
        public int lastInputSeq;
        public int pongs;
        public int closeCode;
        public string closeCodes;
    }

    private static readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
    private static double waitUntil;
    private static bool lastWait;
    private static string folder;

    static PlayLinkStreamTest()
    {
        int phase = SessionState.GetInt(KEY_PHASE, 0);
        if (phase == 0)
        {
            return;
        }

        // 도메인 리로드 뒤: 이어서 합니다.
        folder = SessionState.GetString(KEY_FOLDER, string.Empty);
        PlayLinkDiscovery.UdpPort = PORT;
        stack.Push(phase == 2 ? InPlay() : AfterPlay());
        EditorApplication.update += Step;
    }

    public static void Run()
    {
        folder = Path.Combine(Path.GetTempPath(), "playlink-stream-" + Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(folder);
        SessionState.SetString(KEY_FOLDER, folder);
        SessionState.SetFloat(KEY_STARTED, (float)EditorApplication.timeSinceStartup);
        // 이 시험이 바꾸는 EditorPrefs: 원래 있던 값은 되돌리고, 없던 키는 끝날 때 지웁니다.
        string saved = string.Empty;
        foreach (string key in PREF_KEYS)
        {
            saved += EditorPrefs.HasKey(key) ? "1" : "0";
        }

        SessionState.SetString(KEY_SAVED, saved + "\n" + (PlayLink.UseH264 ? "1" : "0") + "\n" + PlayLink.EncoderChoice + "\n" + PlayLink.FfmpegPath);
        SessionState.SetInt(KEY_PHASE, 1);
        PlayLinkIdentity.UseFolderForTests(Path.Combine(folder, "pc"));
        PlayLinkDiscovery.UdpPort = PORT;
        stack.Push(BeforePlay());
        EditorApplication.update += Step;
    }

    // 인코더마다 작은 시험 인코딩이 되는지와 자동 선택 결과를 적습니다: -executeMethod PlayLinkStreamTest.Encoders (-nographics 가능)
    public static void Encoders()
    {
        string report = string.Empty;
        foreach (string encoder in PlayLinkH264.ENCODERS)
        {
            bool ok = PlayLinkH264.Probe(FFMPEG, encoder, out string reason);
            report += "  " + encoder + ": " + (ok ? "ok" : "안 됨 (" + reason + ")") + "\n";
        }

        string picked = PlayLinkH264.PickEncoder(FFMPEG, string.Empty, out string error);
        Debug.Log("[PlayLink] ENCODERS\n" + report + "  자동 선택: " + (picked ?? error));
        EditorApplication.Exit(picked != null ? 0 : 1);
    }

    private static string Codec
    {
        get
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-playlinkCodec");
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : "mjpeg";
        }
    }

    private static void Step()
    {
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(KEY_STARTED, 0f) + TOTAL_TIMEOUT_SECONDS)
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

            // 다음 단계는 도메인 리로드 뒤 정적 생성자가 잇습니다.
            EditorApplication.update -= Step;
        }
        catch (Exception e)
        {
            Note("  FAIL 예외: " + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace);
            SessionState.SetInt(KEY_FAILED, SessionState.GetInt(KEY_FAILED, 0) + 1);
            Finish(1);
        }
    }

    private static void Note(string line)
    {
        SessionState.SetString(KEY_LOG, SessionState.GetString(KEY_LOG, string.Empty) + line + "\n");
    }

    private static void Check(string name, bool ok)
    {
        string key = ok ? KEY_PASSED : KEY_FAILED;
        SessionState.SetInt(key, SessionState.GetInt(key, 0) + 1);
        Note((ok ? "  ok   " : "  FAIL ") + name);
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

    private static IEnumerator Expect(string name, Func<bool> condition, double seconds)
    {
        yield return WaitFor(condition, seconds);
        Check(name, lastWait);
    }

    private static SimStatus Status()
    {
        string path = Path.Combine(folder, "status.json");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    SimStatus status = JsonUtility.FromJson<SimStatus>(File.ReadAllText(path));
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

    private static void Tell(string command)
    {
        File.WriteAllText(Path.Combine(folder, "cmd.txt"), command + "\n");
    }

    private static bool Connected { get { return PlayLinkLink.Current == PlayLinkLink.State.Connected; } }

    private static bool SeatsEndWith(string suffix)
    {
        string seats = Status().seats ?? string.Empty;
        return seats.EndsWith(suffix, StringComparison.Ordinal);
    }

    // ---- 1단계: 폰 흉내를 띄우고 페어링 · 연결한 뒤 Play 에 들어갑니다.
    private static IEnumerator BeforePlay()
    {
        string codec = Codec;
        Note("codec " + codec + ", graphics " + SystemInfo.graphicsDeviceType);
        string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
        ProcessStartInfo info = new ProcessStartInfo("node", "\"" + Path.Combine(repo, "tools", "unity-peer-test.js") + "\" --port " + PORT
            + " --state \"" + Path.Combine(folder, "phone.json") + "\" --status \"" + Path.Combine(folder, "status.json") + "\" --cmd \"" + Path.Combine(folder, "cmd.txt")
            + "\" --name StreamPhone" + (codec == "mjpeg" ? string.Empty : " --h264"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.EnvironmentVariables.Remove("PLAYLINK_QR");
        Process sim = Process.Start(info);
        SessionState.SetInt(KEY_PID, sim.Id);
        yield return Expect("폰 흉내가 뜸", () => Status().ready, 10.0);

        PlayLink.UseH264 = codec != "mjpeg";
        PlayLink.EncoderChoice = codec == "mjpeg" || codec == "h264" ? string.Empty : codec;
        PlayLink.FfmpegPath = FFMPEG;
        if (!File.Exists(PlayLinkDemoScene.SCENE_PATH))
        {
            PlayLinkDemoScene.Build();
        }

        EditorSceneManager.OpenScene(PlayLinkDemoScene.SCENE_PATH);
        // batchmode 에는 창이 없으므로 Game 뷰를 하나 만듭니다(캡처가 Game 뷰의 렌더 타깃을 읽음).
        Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        EditorWindow.GetWindow(gameView).Show();

        string id = Status().id;
        PlayLinkLink.Arm();
        yield return Expect("탐색으로 폰이 보임", () => PlayLinkDiscovery.Snapshot().Exists(p => p.id == id), 6.0);
        PlayLinkLink.BeginPair(PlayLinkDiscovery.Snapshot().Find(p => p.id == id));
        yield return WaitFor(() => PlayLinkLink.Pairing != null && PlayLinkLink.Pairing.stage == PlayLinkLink.PairStage.Digits, 10.0);
        PlayLinkLink.ConfirmPair();
        yield return Expect("Play 전에 페어링 · 연결됨(대기 문구)", () => Connected && Status().videoOpen && Status().inputOpen && SeatsEndWith("lobby*"), 12.0);
        yield return 1.0;
        Check("Play 밖에서는 프레임이 가지 않음", Status().frames == 0 && !PlayLink.IsRunning);

        SessionState.SetInt(KEY_PHASE, 2);
        EditorApplication.EnterPlaymode();
    }

    // ---- 2단계(Play 진입의 도메인 리로드 뒤)
    private static IEnumerator InPlay()
    {
        string codec = Codec;
        yield return Expect("Play 에 들어감", () => EditorApplication.isPlaying, 30.0);
        yield return Expect("리로드 때 폰에 '곧 다시 겁니다'(4410)로 닫음", () => ("," + Status().closeCodes + ",").Contains(",4410,"), 5.0);
        yield return Expect("리로드 뒤 스스로 다시 붙음(사람 조작 없음)", () => Connected && Status().videoOpens == 2 && Status().inputOpen, 15.0);
        yield return Expect("Play 중이면 seat active 를 알림", () => SeatsEndWith("active"), 10.0);
        yield return Expect("화면 전송이 켜짐", () => PlayLink.IsRunning, 5.0);
        yield return WaitFor(() => Status().frames >= 20, 20.0);
        SimStatus s = Status();
        Check("프레임이 폰에 도착함(20장 이상)", s.frames >= 20);
        Note("       frames " + s.frames + ", bytes " + s.frameBytes + ", size " + s.width + "x" + s.height + ", jpeg " + s.jpegFrames + ", h264 " + s.h264Frames
            + " (config " + s.h264Configs + ", key " + s.h264Keys + "), codec status: " + PlayLinkCapture.CodecStatus + ", captures " + PlayLinkCapture.Captures
            + ", no-render " + PlayLinkCapture.NoRenderCaptures + ", screen " + Screen.width + "x" + Screen.height);
        Check("프레임 형식이 올바름(헤더 28바이트 + 내용, 깨진 것 없음)", s.frames > 0 && s.badFrames == 0 && s.width > 0 && s.height > 0);
        if (codec == "mjpeg")
        {
            Check("MJPEG: JPEG 프레임만 감", s.jpegFrames > 0 && s.h264Frames == 0 && s.h264Configs == 0);
        }
        else
        {
            yield return Expect("H.264: 설정(avcC) → 키프레임 → 프레임 순으로 감", () => Status().h264Configs >= 1 && Status().h264Keys >= 1 && Status().h264Frames >= 10, 20.0);
            string status = PlayLinkCapture.CodecStatus ?? string.Empty;
            Check("H.264: 고른 인코더로 인코딩 중(" + status + ")", status.StartsWith("H.264", StringComparison.Ordinal) && (codec == "h264" || status.Contains(codec)));
            int keys = Status().h264Keys;
            Tell("key");
            yield return Expect("H.264: 폰의 키프레임 요청에 새 키프레임을 보냄", () => Status().h264Keys > keys, 10.0);
        }

        int before = Status().frames;
        yield return 2.0;
        Check("프레임이 계속 감", Status().frames > before);
        Tell("touch");
        yield return Expect("터치: 폰의 터치가 레거시 Input 터치(Began)로 들어옴", () => PlayLinkDemo.TouchesBegan >= 1, 5.0);
        yield return Expect("터치: 프레임 헤더의 입력 번호가 따라옴(102)", () => Status().lastInputSeq >= 102, 5.0);
        yield return WaitFor(() => PlayLinkDemo.ButtonPresses >= 1, 2.0);
        Note("       (참고) 가운데 uGUI 버튼 눌림 횟수: " + PlayLinkDemo.ButtonPresses + ", 마지막 입력: " + PlayLinkInput.LastInput);
        PlayLink.Snapshot stats = PlayLink.Stats;
        Note("       stats: unity " + stats.unityFps.ToString("0.0") + " fps, sent " + stats.sentFps.ToString("0.0") + " fps, " + stats.kbps.ToString("0") + " kbps, ack "
            + stats.ackMs.ToString("0.0") + " ms");

        // Play 를 끝낼 때 도메인이 다시 로드되면 정적 생성자가 3단계를 잇고, 아니면 여기서 바로 이어집니다.
        SessionState.SetInt(KEY_PHASE, 3);
        EditorApplication.ExitPlaymode();
        yield return AfterPlay();
    }

    // ---- 3단계: Play 를 끝낸 뒤
    private static IEnumerator AfterPlay()
    {
        yield return Expect("Play 가 끝남", () => !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode, 30.0);
        yield return Expect("Play 를 끝내면 대기 문구로 돌아감(연결은 그대로)", () => SeatsEndWith("lobby*") && Connected && Status().videoOpen, 10.0);
        Check("Play 를 끝내도 다시 걸지 않고 같은 연결을 씀", Status().videoOpens == 2);
        yield return 0.5;
        int frames = Status().frames;
        int pongs = Status().pongs;
        yield return 1.5;
        Check("Play 밖에서는 프레임이 멈춤", Status().frames == frames && !PlayLink.IsRunning);
        Check("Play 밖에서도 연결이 살아 있음(ping 답)", Status().pongs > pongs);
        PlayLinkLink.Disarm();
        yield return Expect("Stop: 닫기 코드 1000", () => Status().closeCode == 1000 && !Status().videoOpen, 5.0);
        Check("폰 흉내 쪽 오류 없음 " + Status().failed, string.IsNullOrEmpty(Status().failed));
        Finish(SessionState.GetInt(KEY_FAILED, 0) == 0 ? 0 : 1);
    }

    private static void Finish(int code)
    {
        EditorApplication.update -= Step;
        SessionState.SetInt(KEY_PHASE, 0);
        try
        {
            PlayLinkLink.Disarm();
            string[] saved = SessionState.GetString(KEY_SAVED, string.Empty).Split('\n');
            if (saved.Length == 4)
            {
                PlayLink.UseH264 = saved[1] == "1";
                PlayLink.EncoderChoice = saved[2];
                PlayLink.FfmpegPath = saved[3];
                for (int i = 0; i < PREF_KEYS.Length; i++)
                {
                    if (saved[0][i] == '0')
                    {
                        EditorPrefs.DeleteKey(PREF_KEYS[i]);
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        try
        {
            Process.GetProcessById(SessionState.GetInt(KEY_PID, 0)).Kill();
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

        Debug.Log("[PlayLink] STREAM " + (code == 0 ? "PASS" : "FAIL") + "\n" + SessionState.GetString(KEY_LOG, string.Empty)
            + "통과 " + SessionState.GetInt(KEY_PASSED, 0) + ", 실패 " + SessionState.GetInt(KEY_FAILED, 0));
        EditorApplication.Exit(code);
    }
}
