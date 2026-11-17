using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

// 끊김 진단용 기록. 최근 Unity 프레임(약 34초)과 인코딩 결정을 고정 크기 고리 버퍼에 두고 GET /stats 로 JSON으로 내보냅니다.
// 프레임 기록은 메인 스레드(PlayerLoop)가 쓰고, 인코딩 기록은 인코더 스레드가 쓰고, 서버 스레드가 읽습니다(짧은 잠금).
// 폰 페이지의 표시 간격과 나란히 놓고 "Unity 프레임이 튄 것인지, 캡처/전송 쪽인지"를 가르는 데 씁니다(개발 문서 12.8).
public static class GameViewRemoteTrace
{
    private const int FRAME_CAPACITY = 2048;
    private const int ENCODE_CAPACITY = 2048;

    // 캡처 결과(프레임당 1개)
    public const int CAPTURE_NONE = 0;
    public const int CAPTURE_REQUESTED = 1;
    public const int CAPTURE_STRIDE_SKIP = 2;
    public const int CAPTURE_INFLIGHT_SKIP = 3;
    public const int CAPTURE_NO_TEXTURE = 4;

    // 인코딩 결정(캡처한 프레임당 1개)
    public const int ENCODE_H264 = 0;
    public const int ENCODE_IDENTICAL_SKIP = 1;
    public const int ENCODE_BACKLOG_SKIP = 2;
    public const int ENCODE_JPEG = 3;
    public const int ENCODE_GUARD_SKIP = 4;

    private struct Frame
    {
        public int frame;
        public double timeMs;
        public float deltaMs;
        public byte capture;
        public byte renders;
        public byte screenRenders;
        public byte fingers;
        public ushort pointerEvents;
        public float captureMs;
        public float injectMs;
        public float readbackWaitMs;
    }

    private struct Encode
    {
        public int captureFrame;
        public double timeMs;
        public byte kind;
    }

    private static readonly object gate = new object();
    private static readonly Frame[] frames = new Frame[FRAME_CAPACITY];
    private static readonly Encode[] encodes = new Encode[ENCODE_CAPACITY];
    private static long frameCount;
    private static long encodeCount;
    private static Frame current;
    private static bool hasCurrent;
    private static long pointerMessagesReceived;
    private static long pointerEventsApplied;

    private static long originTicks = Stopwatch.GetTimestamp();

    // 서버 시작 뒤 흐른 시간(ms). 프레임 헤더의 캡처 시각과 같은 시계입니다.
    public static double NowMs { get { return SessionMs(Stopwatch.GetTimestamp()); } }

    // Stopwatch 시각 → 서버 시작 뒤 흐른 시간(ms)
    public static double SessionMs(long stopwatchTicks)
    {
        return (stopwatchTicks - Volatile.Read(ref originTicks)) * 1000.0 / Stopwatch.Frequency;
    }

    public static void Reset()
    {
        Volatile.Write(ref originTicks, Stopwatch.GetTimestamp());
        lock (gate)
        {
            frameCount = 0;
            encodeCount = 0;
            hasCurrent = false;
        }

        Interlocked.Exchange(ref pointerMessagesReceived, 0);
        Interlocked.Exchange(ref pointerEventsApplied, 0);
    }

    // 메인 스레드, 프레임 맨 앞. 직전 프레임 기록을 버퍼에 넣고 새 기록을 시작합니다.
    public static void BeginFrame(int frame, float deltaMs)
    {
        if (hasCurrent)
        {
            lock (gate)
            {
                frames[frameCount % FRAME_CAPACITY] = current;
                frameCount++;
            }
        }

        current = new Frame { frame = frame, timeMs = NowMs, deltaMs = deltaMs };
        hasCurrent = true;
    }

    public static void SetCapture(int result, int renders, int screenRenders, double elapsedMs)
    {
        current.capture = (byte)result;
        current.renders = (byte)System.Math.Min(255, renders);
        current.screenRenders = (byte)System.Math.Min(255, screenRenders);
        current.captureMs = (float)elapsedMs;
    }

    public static void SetInject(int pointerEvents, int fingers, double elapsedMs)
    {
        current.pointerEvents = (ushort)System.Math.Min(ushort.MaxValue, pointerEvents);
        current.fingers = (byte)System.Math.Min(255, fingers);
        current.injectMs = (float)elapsedMs;
        Interlocked.Add(ref pointerEventsApplied, pointerEvents);
    }

    public static void SetReadbackWait(double elapsedMs)
    {
        current.readbackWaitMs = (float)elapsedMs;
    }

    // 입력 소켓 수신 스레드: 포인터 메시지 1개를 받음
    public static void NotePointerMessage()
    {
        Interlocked.Increment(ref pointerMessagesReceived);
    }

    // 인코더 스레드: 캡처한 프레임 1장을 어떻게 처리했는지
    public static void NoteEncode(int captureFrame, int kind)
    {
        lock (gate)
        {
            encodes[encodeCount % ENCODE_CAPACITY] = new Encode { captureFrame = captureFrame, timeMs = NowMs, kind = (byte)kind };
            encodeCount++;
        }
    }

    // GET /stats 응답(서버 스레드). snapshotJson은 창에 보이는 1초 통계입니다.
    public static string BuildJson(string snapshotJson, string extraJson)
    {
        Frame[] frameCopy;
        Encode[] encodeCopy;
        lock (gate)
        {
            int frameLength = (int)System.Math.Min(frameCount, FRAME_CAPACITY);
            frameCopy = new Frame[frameLength];
            for (int i = 0; i < frameLength; i++)
            {
                frameCopy[i] = frames[(frameCount - frameLength + i) % FRAME_CAPACITY];
            }

            int encodeLength = (int)System.Math.Min(encodeCount, ENCODE_CAPACITY);
            encodeCopy = new Encode[encodeLength];
            for (int i = 0; i < encodeLength; i++)
            {
                encodeCopy[i] = encodes[(encodeCount - encodeLength + i) % ENCODE_CAPACITY];
            }
        }

        CultureInfo inv = CultureInfo.InvariantCulture;
        StringBuilder json = new StringBuilder(frameCopy.Length * 48 + encodeCopy.Length * 24 + 1024);
        json.Append("{\"nowMs\":").Append(NowMs.ToString("0.0", inv));
        json.Append(",\"pointerMessagesReceived\":").Append(Interlocked.Read(ref pointerMessagesReceived));
        json.Append(",\"pointerEventsApplied\":").Append(Interlocked.Read(ref pointerEventsApplied));
        json.Append(",\"snapshot\":").Append(string.IsNullOrEmpty(snapshotJson) ? "null" : snapshotJson);
        json.Append(",\"extra\":").Append(string.IsNullOrEmpty(extraJson) ? "null" : extraJson);
        // frames: [frame, timeMs, deltaMs, capture, renders, screenRenders, fingers, pointerEvents, captureMs, injectMs, readbackWaitMs]
        json.Append(",\"frames\":[");
        for (int i = 0; i < frameCopy.Length; i++)
        {
            Frame f = frameCopy[i];
            if (i > 0)
            {
                json.Append(',');
            }

            json.Append('[').Append(f.frame).Append(',').Append(f.timeMs.ToString("0.0", inv)).Append(',').Append(f.deltaMs.ToString("0.00", inv)).Append(',')
                .Append(f.capture).Append(',').Append(f.renders).Append(',').Append(f.screenRenders).Append(',').Append(f.fingers).Append(',').Append(f.pointerEvents).Append(',')
                .Append(f.captureMs.ToString("0.00", inv)).Append(',').Append(f.injectMs.ToString("0.00", inv)).Append(',').Append(f.readbackWaitMs.ToString("0.00", inv)).Append(']');
        }

        // encodes: [captureFrame, timeMs, kind]
        json.Append("],\"encodes\":[");
        for (int i = 0; i < encodeCopy.Length; i++)
        {
            Encode e = encodeCopy[i];
            if (i > 0)
            {
                json.Append(',');
            }

            json.Append('[').Append(e.captureFrame).Append(',').Append(e.timeMs.ToString("0.0", inv)).Append(',').Append(e.kind).Append(']');
        }

        json.Append("]}");
        return json.ToString();
    }
}
