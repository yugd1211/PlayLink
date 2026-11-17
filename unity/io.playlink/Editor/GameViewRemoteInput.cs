using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.EventSystems;

// 폰 포인터 이벤트 → 레거시 Input 터치.
// 수신 스레드: JSON 파싱 → ConcurrentQueue. 메인 스레드(PlayerLoop, EarlyUpdate.UpdateInputManager 직전): Input.SimulateTouch.
// 규칙
//   - 손가락 하나는 프레임당 페이즈 전환 1번만 합니다. down+up이 같은 프레임에 와도 Began → 다음 프레임 Ended로 나눠 탭을 잃지 않습니다.
//   - 누르고 있는 손가락은 움직임이 없어도 매 프레임 Stationary를 넣습니다. 넣지 않아도 터치는 유지되지만 엔진이 마지막 페이즈를
//     그대로 반복해서, Moved 뒤에는 멈춰 있어도 Moved + 이전 deltaPosition이 계속 보입니다(2단계 결과 보고서 참고).
//   - 원격 입력 연결이 있는 동안 Input.simulateMouseWithTouches = false(1단계 3.5), EventSystem 포커스를 강제로 켭니다(1단계 6장 1-a).
public static class GameViewRemoteInput
{
    // 같은 자리 연속 탭으로 볼 시간/거리(tapCount 계산)
    private const float MULTI_TAP_SECONDS = 0.3f;
    private const float MULTI_TAP_PIXELS = 40f;

    [Serializable]
    private class Message
    {
        public string t;
        public int id;
        public float x;
        public float y;
        public int s;
        public double ts;
        // t == "q": 페이지 화면 검사 누적값(그린 프레임, 검은 디코딩, JPEG 손상, 크기 불일치, 디코딩 실패, 화면 캔버스 검정, 컨텍스트 손실, 마지막 이상 프레임 id)
        public int fr;
        public int bk;
        public int jb;
        public int sz;
        public int de;
        public int cb;
        public int cl;
        public int lb;
        // 한 장당 디코딩 비용(ms, 평균 디코딩 시간 / 병렬 디코더 수)과 지난 보고(2초) 뒤 디코더가 바빠 버린 프레임 수. 서버의 자동 프레임 간격이 씁니다.
        public float dm;
        public int dp;
    }

    private struct PointerEvent
    {
        public char type;
        public int connectionId;
        public int pointerId;
        public Vector2 normalized;
        public int seq;
        public long receivedTicks;
        public int receivedFrame;
    }

    private sealed class Finger
    {
        public int fingerId;
        public bool active;
        public Vector2 position;
        public readonly Queue<PointerEvent> pending = new Queue<PointerEvent>();
    }

    private static readonly MethodInfo simulateTouchMethod =
        typeof(Input).GetMethod("SimulateTouch", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo hasFocusField =
        typeof(EventSystem).GetField("m_HasFocus", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly ConcurrentQueue<PointerEvent> queue = new ConcurrentQueue<PointerEvent>();
    private static readonly Dictionary<long, Finger> fingers = new Dictionary<long, Finger>();
    private static readonly List<long> finishedKeys = new List<long>();
    private static readonly object[] invokeArgs = new object[1];
    private static int inputConnections;
    private static volatile int currentFrame;
    private static volatile int lastInjectedSeq;
    private static bool suppressingMouse;
    private static bool savedSimulateMouse = true;
    private static float lastTapTime = -1f;
    private static Vector2 lastTapPosition;
    private static int lastTapCount;
    private static float avgInjectLatencyMs;
    private static float avgInjectLatencyFrames;

    public static int LastInjectedSeq { get { return lastInjectedSeq; } }
    public static float AvgInjectLatencyMs { get { return avgInjectLatencyMs; } }
    public static float AvgInjectLatencyFrames { get { return avgInjectLatencyFrames; } }
    public static string LastInput { get; private set; } = "-";
    // 페이지가 입력 소켓으로 보낸 마지막 화면 검사 결과
    public static string ClientReport { get; private set; } = "-";
    private static int lastClientBad;
    private static volatile float clientDecodeMs;
    private static volatile float clientDropsPerSecond;
    private const float CLIENT_REPORT_SECONDS = 2f;

    // 페이지가 보고한 최근 디코딩 시간(ms, 여러 클라이언트면 마지막 보고). 보고 전이면 0
    public static float ClientDecodeMs { get { return clientDecodeMs; } }
    // 페이지가 디코더가 바빠 버린 프레임(초당, 마지막 보고 기준)
    public static float ClientDropsPerSecond { get { return clientDropsPerSecond; } }
    public static int ActiveTouches { get { return fingers.Count; } }

    public static void OnInputConnected(int connectionId)
    {
        // 페이지를 새로 열면 입력 번호가 1부터 다시 시작합니다. 이전 페이지의 큰 번호가 남아 있으면 프레임 헤더의 inputSeq가 처음부터 새 번호보다 커서
        // 페이지의 터치 → 표시 시간 측정이 실제보다 훨씬 짧게 나옵니다(다른 입력 연결이 없을 때만 지움).
        if (Interlocked.Increment(ref inputConnections) == 1)
        {
            lastInjectedSeq = 0;
        }
    }

    // 연결이 끊기면 그 연결의 손가락을 전부 Canceled로 끝냅니다(메인 스레드에서 처리).
    public static void OnInputDisconnected(int connectionId)
    {
        // 0 아래로 내려가지 않게 합니다(코드 리뷰 m11: Stop 의 Reset 과 늦게 닫힌 연결이 엇갈리면 −1 이 되어 그 뒤 판정이 틀어짐).
        int current;
        do
        {
            current = Volatile.Read(ref inputConnections);
        }
        while (current > 0 && Interlocked.CompareExchange(ref inputConnections, current - 1, current) != current);

        queue.Enqueue(new PointerEvent { type = 'x', connectionId = connectionId, receivedTicks = Stopwatch.GetTimestamp(), receivedFrame = currentFrame });
    }

    // 입력 소켓 수신 스레드에서 호출됩니다. JsonUtility는 백그라운드 스레드에서 써도 됩니다.
    public static string HandleMessage(GameViewRemoteServer.Connection connection, string text)
    {
        Message message;
        try
        {
            message = JsonUtility.FromJson<Message>(text);
        }
        catch (Exception)
        {
            return null;
        }

        if (message == null || string.IsNullOrEmpty(message.t))
        {
            return null;
        }

        // 메시지 종류는 한 글자입니다. 두 글자 이상은 모르는 메시지로 버립니다(첫 글자만 보면 "mouse" 가 move 로 처리됨, 설계 15.1 R2).
        if (message.t.Length != 1)
        {
            return null;
        }

        char type = message.t[0];
        if (type == 'p')
        {
            return "{\"t\":\"P\",\"ts\":" + message.ts.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        if (type == 'q')
        {
            int bad = message.bk + message.jb + message.sz + message.de + message.cb + message.cl;
            string report = $"drawn {message.fr}, black {message.bk}, jpeg {message.jb}, size {message.sz}, decodeErr {message.de}, canvasBlack {message.cb}, ctxLost {message.cl}";
            if (bad != lastClientBad)
            {
                UnityEngine.Debug.Log($"{GameViewRemote.LOG_PREFIX} client #{connection.id} check: {report}, last bad frameId {message.lb}");
                lastClientBad = bad;
            }

            ClientReport = $"#{connection.id} {report}, decode {message.dm:0.0} ms";
            clientDecodeMs = message.dm;
            clientDropsPerSecond = message.dp / CLIENT_REPORT_SECONDS;
            return null;
        }

        if (type != 'd' && type != 'm' && type != 'u' && type != 'c')
        {
            return null;
        }

        GameViewRemoteTrace.NotePointerMessage();
        queue.Enqueue(new PointerEvent
        {
            type = type,
            connectionId = connection.id,
            pointerId = message.id,
            normalized = new Vector2(Mathf.Clamp01(message.x), Mathf.Clamp01(message.y)),
            seq = message.s,
            receivedTicks = Stopwatch.GetTimestamp(),
            receivedFrame = currentFrame,
        });
        return null;
    }

    // Play 종료/서버 중지 때 호출. 마우스 시뮬레이션과 EventSystem 포커스를 원래대로 돌립니다.
    public static void Reset()
    {
        while (queue.TryDequeue(out PointerEvent _))
        {
        }

        fingers.Clear();
        Interlocked.Exchange(ref inputConnections, 0);
        RestoreMouseSimulation();
        EventSystem es = EventSystem.current;
        if (es != null && hasFocusField != null)
        {
            hasFocusField.SetValue(es, Application.isFocused);
        }

        lastInjectedSeq = 0;
        LastInput = "-";
        ClientReport = "-";
        lastClientBad = 0;
        clientDecodeMs = 0f;
        clientDropsPerSecond = 0f;
    }

    public static void InjectUpdate()
    {
        long start = Stopwatch.GetTimestamp();
        int pointerEvents = InjectPending();
        GameViewRemoteTrace.SetInject(pointerEvents, fingers.Count, GameViewRemote.ElapsedMs(start));
    }

    // 받은 포인터 이벤트를 손가락별로 나눠 이번 프레임 터치를 넣습니다. 반환값은 이번 프레임에 꺼낸 이벤트 수
    private static int InjectPending()
    {
        int pointerEvents = 0;
        currentFrame = Time.frameCount;
        bool connected = Volatile.Read(ref inputConnections) > 0;
        UpdateConnectionState(connected);

        while (queue.TryDequeue(out PointerEvent e))
        {
            pointerEvents++;
            long key = ((long)e.connectionId << 32) | (uint)e.pointerId;
            if (e.type == 'x')
            {
                CancelConnection(e.connectionId);
                continue;
            }

            if (!fingers.TryGetValue(key, out Finger finger))
            {
                if (e.type != 'd')
                {
                    continue;
                }

                finger = new Finger { fingerId = AllocateFingerId() };
                fingers.Add(key, finger);
            }

            finger.pending.Enqueue(e);
        }

        if (simulateTouchMethod == null || fingers.Count == 0)
        {
            return pointerEvents;
        }

        finishedKeys.Clear();
        foreach (KeyValuePair<long, Finger> pair in fingers)
        {
            if (!InjectFinger(pair.Value))
            {
                finishedKeys.Add(pair.Key);
            }
        }

        foreach (long key in finishedKeys)
        {
            fingers.Remove(key);
        }

        return pointerEvents;
    }

    // 이번 프레임 손가락 하나의 터치를 넣습니다. 손가락이 끝났으면 false.
    private static bool InjectFinger(Finger finger)
    {
        Vector2 previous = finger.position;
        if (!finger.active)
        {
            // Began을 넣기 전에 연결이 끊긴 손가락은 아무것도 넣지 않고 지웁니다.
            if (finger.pending.Count == 0)
            {
                return false;
            }

            // 첫 이벤트는 항상 down(큐에 넣을 때 보장). Began만 넣고 나머지는 다음 프레임으로 미룹니다.
            PointerEvent down = finger.pending.Dequeue();
            finger.active = true;
            finger.position = ToScreen(down.normalized);
            Inject(finger, TouchPhase.Began, Vector2.zero, ComputeTapCount(finger.position));
            RecordInjected(down, finger);
            return true;
        }

        bool moved = false;
        PointerEvent last = default;
        while (finger.pending.Count > 0 && finger.pending.Peek().type == 'm')
        {
            last = finger.pending.Dequeue();
            finger.position = ToScreen(last.normalized);
            moved = true;
        }

        if (finger.pending.Count > 0)
        {
            PointerEvent end = finger.pending.Dequeue();
            if (end.type == 'd')
            {
                // up을 잃어버린 채 같은 포인터가 다시 down: 이전 터치를 끝내고 새 down은 다음 프레임 Began으로 넣습니다.
                Inject(finger, TouchPhase.Canceled, finger.position - previous, lastTapCount);
                finger.active = false;
                Queue<PointerEvent> rest = new Queue<PointerEvent>(finger.pending);
                finger.pending.Clear();
                finger.pending.Enqueue(end);
                foreach (PointerEvent e in rest)
                {
                    finger.pending.Enqueue(e);
                }

                return true;
            }

            finger.position = ToScreen(end.normalized);
            TouchPhase phase = end.type == 'c' ? TouchPhase.Canceled : TouchPhase.Ended;
            Inject(finger, phase, finger.position - previous, lastTapCount);
            if (phase == TouchPhase.Ended)
            {
                lastTapTime = Time.unscaledTime;
                lastTapPosition = finger.position;
            }

            RecordInjected(end, finger);
            return false;
        }

        if (moved)
        {
            Inject(finger, TouchPhase.Moved, finger.position - previous, lastTapCount);
            RecordInjected(last, finger);
        }
        else
        {
            Inject(finger, TouchPhase.Stationary, Vector2.zero, lastTapCount);
        }

        return true;
    }

    private static void Inject(Finger finger, TouchPhase phase, Vector2 delta, int tapCount)
    {
        Touch touch = new Touch();
        touch.fingerId = finger.fingerId;
        touch.phase = phase;
        touch.position = finger.position;
        touch.rawPosition = finger.position;
        touch.deltaPosition = delta;
        touch.deltaTime = Time.unscaledDeltaTime;
        touch.tapCount = tapCount;
        touch.type = TouchType.Direct;
        touch.pressure = 1f;
        touch.maximumPossiblePressure = 1f;
        invokeArgs[0] = touch;
        simulateTouchMethod.Invoke(null, invokeArgs);
    }

    private static int ComputeTapCount(Vector2 position)
    {
        bool repeat = lastTapTime >= 0f && Time.unscaledTime - lastTapTime <= MULTI_TAP_SECONDS
            && Vector2.Distance(position, lastTapPosition) <= MULTI_TAP_PIXELS;
        lastTapCount = repeat ? lastTapCount + 1 : 1;
        return lastTapCount;
    }

    private static void RecordInjected(PointerEvent e, Finger finger)
    {
        float ms = (float)GameViewRemote.ElapsedMs(e.receivedTicks);
        float frames = Time.frameCount - e.receivedFrame;
        avgInjectLatencyMs = avgInjectLatencyMs <= 0f ? ms : Mathf.Lerp(avgInjectLatencyMs, ms, 0.2f);
        avgInjectLatencyFrames = avgInjectLatencyFrames <= 0f ? frames : Mathf.Lerp(avgInjectLatencyFrames, frames, 0.2f);
        if (e.seq > lastInjectedSeq)
        {
            lastInjectedSeq = e.seq;
        }

        if (e.type != 'm')
        {
            LastInput = $"{e.type} finger={finger.fingerId} pos=({finger.position.x:0},{finger.position.y:0}) frame={Time.frameCount} recv→inject {ms:0.0}ms/{frames:0}f";
        }
    }

    private static void CancelConnection(int connectionId)
    {
        foreach (KeyValuePair<long, Finger> pair in fingers)
        {
            if ((int)(pair.Key >> 32) != connectionId)
            {
                continue;
            }

            Finger finger = pair.Value;
            finger.pending.Clear();
            if (finger.active)
            {
                finger.pending.Enqueue(new PointerEvent { type = 'c', normalized = ToNormalized(finger.position) });
            }
        }
    }

    // 폰 좌표(0~1, 위가 0) → Unity 화면 좌표(픽셀, 아래가 0)
    private static Vector2 ToScreen(Vector2 normalized)
    {
        return new Vector2(normalized.x * Screen.width, (1f - normalized.y) * Screen.height);
    }

    private static Vector2 ToNormalized(Vector2 screen)
    {
        return new Vector2(screen.x / Mathf.Max(1, Screen.width), 1f - screen.y / Mathf.Max(1, Screen.height));
    }

    private static int AllocateFingerId()
    {
        int id = 0;
        bool used = true;
        while (used)
        {
            used = false;
            foreach (Finger finger in fingers.Values)
            {
                if (finger.fingerId == id)
                {
                    used = true;
                    id++;
                    break;
                }
            }
        }

        return id;
    }

    private static void UpdateConnectionState(bool connected)
    {
        if (connected && !suppressingMouse)
        {
            // 가짜 터치가 에디터 마우스 경로(GameManager.cs #if UNITY_EDITOR)로 한 번 더 들어가지 않게 합니다.
            savedSimulateMouse = Input.simulateMouseWithTouches;
            Input.simulateMouseWithTouches = false;
            suppressingMouse = true;
        }
        else if (!connected && suppressingMouse && fingers.Count == 0)
        {
            RestoreMouseSimulation();
        }

        if (!connected || hasFocusField == null)
        {
            return;
        }

        // Unity가 OS 포커스를 잃으면 EventSystem.OnApplicationFocus(false)가 m_HasFocus를 끄고,
        // StandaloneInputModule.Process()가 모든 UI 이벤트를 버립니다. 원격 조작 중에는 매 프레임 다시 켭니다.
        EventSystem es = EventSystem.current;
        if (es != null && !es.isFocused)
        {
            hasFocusField.SetValue(es, true);
        }
    }

    private static void RestoreMouseSimulation()
    {
        if (!suppressingMouse)
        {
            return;
        }

        Input.simulateMouseWithTouches = savedSimulateMouse;
        suppressingMouse = false;
    }
}
