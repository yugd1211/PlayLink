using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.LowLevel;
using PlayerLoopPhase = UnityEngine.PlayerLoop;

// PlayLink 1단계 입력 PoC.
// 레거시 Input의 internal 함수 Input.SimulateTouch(Touch)를 리플렉션으로 불러 가짜 터치를 넣습니다.
// Editor 폴더의 MonoBehaviour는 런타임에 붙일 수 없어서, Play 중에만 PlayerLoop에 단계를 끼워 넣습니다.
//   - 주입: EarlyUpdate.UpdateInputManager 직전 (엔진이 이번 프레임 입력을 갱신하기 전)
//   - 입력 기록: Update.ScriptRunBehaviourUpdate 직전 (게임 Update()가 보는 값과 같음)
//   - 게임 상태 기록: PostLateUpdate 맨 앞 (게임 Update()/LateUpdate()가 끝난 뒤)
public static class PlayLinkTouchPoC
{
    public const string LOG_PREFIX = "[PlayLink-PoC]";

    private const string MENU_ROOT = "Tools/PlayLink PoC/";
    private const string MENU_SUPPRESS_MOUSE = MENU_ROOT + "Suppress Mouse Simulation While Injecting";
    private const string PREF_SUPPRESS_MOUSE = "PlayLink_PoC_SuppressMouse";

    // Began 다음에 Stationary를 몇 프레임 넣은 뒤 Ended를 넣을지
    private const int HOLD_FRAMES = 2;
    // 마지막 터치 뒤에 관찰만 하는 프레임 수
    private const int TAIL_FRAMES = 4;

    private struct InjectStep { }
    private struct ProbeStep { }
    private struct GameStateStep { }

    private static readonly MethodInfo simulateTouchMethod =
        typeof(Input).GetMethod("SimulateTouch", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly Queue<Touch[]> steps = new Queue<Touch[]>();
    private static readonly List<RaycastResult> raycastResults = new List<RaycastResult>();
    private static string testName = string.Empty;
    private static bool active = false;
    private static bool suppressingMouse = false;
    private static bool savedSimulateMouse = true;
    private static bool loopInstalled = false;
    private static PlayerLoopSystem savedLoop;

    private static bool SuppressMouse
    {
        get { return EditorPrefs.GetBool(PREF_SUPPRESS_MOUSE, false); }
        set { EditorPrefs.SetBool(PREF_SUPPRESS_MOUSE, value); }
    }

    #region Menu

    [MenuItem(MENU_ROOT + "Log SimulateTouch Signature", false, 0)]
    public static void LogSimulateTouchSignature()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Type inputType = typeof(Input);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"{LOG_PREFIX} Input type: {inputType.AssemblyQualifiedName}");
        foreach (MethodInfo method in inputType.GetMethods(flags))
        {
            if (method.Name.IndexOf("Simulat", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            string parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            string attributes = string.Join(", ", method.GetCustomAttributesData().Select(a => a.ToString()));
            bool isInternalCall = (method.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0;
            sb.AppendLine($"  method: {DescribeAccess(method)} {method.ReturnType.Name} {method.Name}({parameters}) internalCall={isInternalCall} attrs=[{attributes}]");
        }

        foreach (PropertyInfo property in inputType.GetProperties(flags))
        {
            if (property.Name.IndexOf("Touch", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            MethodInfo getter = property.GetGetMethod(true);
            MethodInfo setter = property.GetSetMethod(true);
            sb.AppendLine($"  property: {property.PropertyType.Name} {property.Name} get={(getter != null ? DescribeAccess(getter) : "-")} set={(setter != null ? DescribeAccess(setter) : "-")}");
        }

        sb.AppendLine($"  resolved for injection: {simulateTouchMethod}");
        Debug.Log(sb.ToString());
    }

    [MenuItem(MENU_ROOT + "Tap Center", false, 20)]
    public static void TapCenter()
    {
        Tap(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), "TapCenter");
    }

    [MenuItem(MENU_ROOT + "Tap Selected UI", false, 21)]
    public static void TapSelectedUi()
    {
        TapUi(Selection.activeGameObject);
    }

    [MenuItem(MENU_ROOT + "Two Finger Began", false, 22)]
    public static void TwoFingerBegan()
    {
        Vector2 a = new Vector2(Screen.width * 0.3f, Screen.height * 0.5f);
        Vector2 b = new Vector2(Screen.width * 0.7f, Screen.height * 0.5f);
        TwoFingerTap(a, b, "TwoFingerBegan");
    }

    [MenuItem(MENU_ROOT + "Tap Center With Inspector Focused", false, 23)]
    public static void TapCenterWithInspectorFocused()
    {
        Type inspectorType = typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");
        EditorWindow.FocusWindowIfItsOpen(inspectorType);
        Debug.Log($"{LOG_PREFIX} focusedWindow={DescribeFocusedWindow()}");
        Tap(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), "TapCenterInspectorFocused");
    }

    [MenuItem(MENU_SUPPRESS_MOUSE, false, 40)]
    public static void ToggleSuppressMouse()
    {
        SuppressMouse = !SuppressMouse;
        Debug.Log($"{LOG_PREFIX} suppressMouse={SuppressMouse}");
    }

    [MenuItem(MENU_SUPPRESS_MOUSE, true)]
    private static bool ToggleSuppressMouseValidate()
    {
        Menu.SetChecked(MENU_SUPPRESS_MOUSE, SuppressMouse);
        return true;
    }

    [MenuItem(MENU_ROOT + "Tap Center", true)]
    [MenuItem(MENU_ROOT + "Tap Selected UI", true)]
    [MenuItem(MENU_ROOT + "Two Finger Began", true)]
    [MenuItem(MENU_ROOT + "Tap Center With Inspector Focused", true)]
    private static bool IsPlaying()
    {
        return EditorApplication.isPlaying;
    }

    #endregion

    #region Test scenarios

    public static void Tap(Vector2 screenPos, string name)
    {
        List<Touch[]> list = new List<Touch[]>();
        list.Add(new[] { MakeTouch(0, TouchPhase.Began, screenPos) });
        for (int i = 0; i < HOLD_FRAMES; i++)
        {
            list.Add(new[] { MakeTouch(0, TouchPhase.Stationary, screenPos) });
        }

        list.Add(new[] { MakeTouch(0, TouchPhase.Ended, screenPos) });
        Run(name, list);
    }

    public static void TwoFingerTap(Vector2 a, Vector2 b, string name)
    {
        List<Touch[]> list = new List<Touch[]>();
        list.Add(new[] { MakeTouch(0, TouchPhase.Began, a), MakeTouch(1, TouchPhase.Began, b) });
        for (int i = 0; i < HOLD_FRAMES; i++)
        {
            list.Add(new[] { MakeTouch(0, TouchPhase.Stationary, a), MakeTouch(1, TouchPhase.Stationary, b) });
        }

        list.Add(new[] { MakeTouch(0, TouchPhase.Ended, a), MakeTouch(1, TouchPhase.Ended, b) });
        Run(name, list);
    }

    public static void TapUi(GameObject target)
    {
        RectTransform rt = target != null ? target.GetComponent<RectTransform>() : null;
        if (rt == null)
        {
            Debug.LogWarning($"{LOG_PREFIX} Tap Selected UI: select a UI GameObject (RectTransform) in the Hierarchy first.");
            return;
        }

        Canvas canvas = rt.GetComponentInParent<Canvas>();
        Camera cam = canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null;
        Vector2 screenPos = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
        Tap(screenPos, $"TapUi({target.name})");
    }

    private static void Run(string name, List<Touch[]> list)
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning($"{LOG_PREFIX} enter Play mode first.");
            return;
        }

        if (simulateTouchMethod == null)
        {
            Debug.LogError($"{LOG_PREFIX} Input.SimulateTouch not found by reflection.");
            return;
        }

        RestoreMouseSimulation();
        steps.Clear();
        foreach (Touch[] step in list)
        {
            steps.Enqueue(step);
        }

        for (int i = 0; i < TAIL_FRAMES; i++)
        {
            steps.Enqueue(new Touch[0]);
        }

        testName = name;
        active = true;
        if (SuppressMouse)
        {
            savedSimulateMouse = Input.simulateMouseWithTouches;
            Input.simulateMouseWithTouches = false;
            suppressingMouse = true;
        }

        InstallLoop();
        Debug.Log($"{LOG_PREFIX} ===== BEGIN {name} frame={Time.frameCount} steps={steps.Count} suppressMouse={suppressingMouse} screen={Screen.width}x{Screen.height} focusedWindow={DescribeFocusedWindow()} =====");
    }

    private static Touch MakeTouch(int fingerId, TouchPhase phase, Vector2 screenPos)
    {
        Touch touch = new Touch();
        touch.fingerId = fingerId;
        touch.phase = phase;
        touch.position = screenPos;
        touch.rawPosition = screenPos;
        touch.deltaPosition = Vector2.zero;
        touch.tapCount = 1;
        touch.type = TouchType.Direct;
        touch.pressure = 1f;
        touch.maximumPossiblePressure = 1f;
        return touch;
    }

    #endregion

    #region PlayerLoop steps

    private static void InjectUpdate()
    {
        if (!active || steps.Count == 0)
        {
            return;
        }

        foreach (Touch touch in steps.Peek())
        {
            simulateTouchMethod.Invoke(null, new object[] { touch });
            Debug.Log($"{LOG_PREFIX} inject frame={Time.frameCount} finger={touch.fingerId} phase={touch.phase} pos={touch.position}");
        }
    }

    private static void ProbeUpdate()
    {
        if (!active || steps.Count == 0)
        {
            return;
        }

        steps.Dequeue();
        StringBuilder sb = new StringBuilder();
        sb.Append($"{LOG_PREFIX} probe frame={Time.frameCount} touchCount={Input.touchCount}");
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch t = Input.GetTouch(i);
            sb.Append($" | #{i} finger={t.fingerId} phase={t.phase} pos={t.position} tap={t.tapCount}");
            sb.Append($" ui2d={DescribeUiColliderHit(t.position)} uiTop={DescribeEventSystemHit(t.position)}");
        }

        sb.Append($" | mouseDown0={Input.GetMouseButtonDown(0)} mouse0={Input.GetMouseButton(0)} mouseUp0={Input.GetMouseButtonUp(0)}");
        sb.Append($" fire1Down={Input.GetButtonDown("Fire1")} mousePos={Input.mousePosition}");
        sb.Append($" simulateMouseWithTouches={Input.simulateMouseWithTouches} appFocused={Application.isFocused} editorFocus={DescribeFocusedWindow()}");

        EventSystem es = EventSystem.current;
        if (es != null)
        {
            sb.Append($" es.isFocused={es.isFocused} module={(es.currentInputModule != null ? es.currentInputModule.GetType().Name : "null")}");
        }

        Debug.Log(sb.ToString());
    }

    private static void GameStateUpdate()
    {
        if (!active)
        {
            return;
        }

        LogGameState();
        if (steps.Count == 0)
        {
            active = false;
            RestoreMouseSimulation();
            Debug.Log($"{LOG_PREFIX} ===== END {testName} frame={Time.frameCount} =====");
            UninstallLoop();
        }
    }

    private static void InstallLoop()
    {
        if (loopInstalled)
        {
            return;
        }

        savedLoop = PlayerLoop.GetCurrentPlayerLoop();
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        InsertBefore(ref loop, typeof(PlayerLoopPhase.EarlyUpdate), typeof(PlayerLoopPhase.EarlyUpdate.UpdateInputManager), typeof(InjectStep), InjectUpdate);
        InsertBefore(ref loop, typeof(PlayerLoopPhase.Update), typeof(PlayerLoopPhase.Update.ScriptRunBehaviourUpdate), typeof(ProbeStep), ProbeUpdate);
        InsertBefore(ref loop, typeof(PlayerLoopPhase.PostLateUpdate), null, typeof(GameStateStep), GameStateUpdate);
        PlayerLoop.SetPlayerLoop(loop);
        loopInstalled = true;

        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void UninstallLoop()
    {
        if (!loopInstalled)
        {
            return;
        }

        PlayerLoop.SetPlayerLoop(savedLoop);
        loopInstalled = false;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingPlayMode)
        {
            return;
        }

        active = false;
        steps.Clear();
        RestoreMouseSimulation();
        UninstallLoop();
    }

    // anchor가 null이면 phase의 맨 앞에 넣습니다.
    private static void InsertBefore(ref PlayerLoopSystem loop, Type phase, Type anchor, Type marker, PlayerLoopSystem.UpdateFunction update)
    {
        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            if (loop.subSystemList[i].type != phase)
            {
                continue;
            }

            List<PlayerLoopSystem> children = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList);
            int index = anchor != null ? children.FindIndex(s => s.type == anchor) : 0;
            children.Insert(Mathf.Max(0, index), new PlayerLoopSystem { type = marker, updateDelegate = update });
            loop.subSystemList[i].subSystemList = children.ToArray();
            return;
        }
    }

    private static void RestoreMouseSimulation()
    {
        if (suppressingMouse)
        {
            Input.simulateMouseWithTouches = savedSimulateMouse;
            suppressingMouse = false;
        }
    }

    #endregion

    #region Observation

    // GameManager.Update()가 점프 전에 거르는 것과 같은 조건(UI 레이어 2D 콜라이더)입니다.
    private static string DescribeUiColliderHit(Vector2 screenPos)
    {
        RaycastHit2D hit = Physics2D.Raycast(screenPos, Vector2.zero, 0.1f, 1 << LayerMask.NameToLayer("UI"));
        return hit.collider != null ? hit.collider.name : "none";
    }

    private static string DescribeEventSystemHit(Vector2 screenPos)
    {
        EventSystem es = EventSystem.current;
        if (es == null)
        {
            return "noEventSystem";
        }

        PointerEventData data = new PointerEventData(es);
        data.position = screenPos;
        raycastResults.Clear();
        es.RaycastAll(data, raycastResults);
        return raycastResults.Count > 0 ? raycastResults[0].gameObject.name : "none";
    }

    private static string DescribeFocusedWindow()
    {
        return EditorWindow.focusedWindow != null ? EditorWindow.focusedWindow.GetType().Name : "null";
    }

    // 게임 코드를 참조하지 않고 리플렉션으로만 읽습니다.
    private static void LogGameState()
    {
        UnityEngine.Object gameManager = FindByTypeName("GameManager");
        if (gameManager == null)
        {
            return;
        }

        object playerCtrl = ReadMember(gameManager, "playerCtrl");
        object jumpStack = "null";
        Vector2 velocity = Vector2.zero;
        if (playerCtrl is UnityEngine.Object playerObj && playerObj != null)
        {
            jumpStack = ReadMember(playerCtrl, "jumpStack");
            if (ReadMember(playerCtrl, "rb") is Rigidbody2D rb && rb != null)
            {
                velocity = rb.velocity;
            }
        }

        UnityEngine.Object uiMgr = FindByTypeName("UIMgr");
        Debug.Log($"{LOG_PREFIX} game frame={Time.frameCount} mClickCount={ReadMember(gameManager, "mClickCount")} jumpStack={jumpStack} velocity={velocity}"
            + $" startGame={ReadMember(gameManager, "startGame")} gameSpeed={ReadMember(gameManager, "gameSpeed")} gameOver={ReadMember(gameManager, "gameOver")}"
            + $" isUIRays={(uiMgr != null ? ReadMember(uiMgr, "isUIRays") : "null")}");
    }

    // Singleton<T>.Instance는 없으면 새 오브젝트를 만들기 때문에 쓰지 않고, 씬에서 찾기만 합니다.
    public static UnityEngine.Object FindByTypeName(string typeName)
    {
        Type type = Type.GetType(typeName + ", Assembly-CSharp");
        if (type == null)
        {
            return null;
        }

        return UnityEngine.Object.FindObjectOfType(type);
    }

    public static object ReadMember(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();
        FieldInfo field = type.GetField(name, flags);
        if (field != null)
        {
            return field.GetValue(target);
        }

        PropertyInfo property = type.GetProperty(name, flags);
        if (property != null)
        {
            return property.GetValue(target);
        }

        return null;
    }

    private static string DescribeAccess(MethodInfo method)
    {
        string access = method.IsPublic ? "public" : method.IsAssembly ? "internal" : method.IsFamily ? "protected" : method.IsPrivate ? "private" : "other";
        return method.IsStatic ? access + " static" : access;
    }

    #endregion
}
