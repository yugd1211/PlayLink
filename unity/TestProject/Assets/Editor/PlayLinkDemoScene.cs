using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 시험용 장면(Assets/Scenes/PlayLinkDemo.unity)을 만들고 빌드 설정의 첫 장면으로 둡니다. 여러 번 돌려도 같은 결과입니다.
//   메뉴 Tools > PlayLink > Build Demo Scene
//   batchmode: -executeMethod PlayLinkDemoScene.BuildBatch
public static class PlayLinkDemoScene
{
    public const string SCENE_PATH = "Assets/Scenes/PlayLinkDemo.unity";

    [MenuItem("Tools/PlayLink/Build Demo Scene", false, 60)]
    public static void Build()
    {
        UnityEngine.SceneManagement.Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        Camera camera = Camera.main;
        camera.transform.position = new Vector3(0f, 1.5f, -7f);
        camera.transform.rotation = Quaternion.Euler(8f, 0f, 0f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.12f, 0.16f, 0.24f);

        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = "Box";
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.position = new Vector3(0f, -0.5f, 0f);

        GameObject canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(720f, 1280f);
        scaler.matchWidthOrHeight = 0.5f;
        new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        Text label = MakeText(canvasObject.transform, "Label", font, 30, new Vector2(0f, -80f), new Vector2(680f, 120f), new Vector2(0.5f, 1f));
        label.text = "PlayLink 시험 장면";

        PlayLinkDemo demo = canvasObject.AddComponent<PlayLinkDemo>();
        demo.box = box.transform;
        demo.label = label;
        for (int i = 0; i < 3; i++)
        {
            GameObject buttonObject = new GameObject("Button " + (i + 1), typeof(Image), typeof(Button));
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.SetParent(canvasObject.transform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(200f, 140f);
            rect.anchoredPosition = new Vector2((i - 1) * 220f, 200f);
            buttonObject.GetComponent<Image>().color = Color.HSVToRGB(0.1f + i * 0.25f, 0.5f, 0.95f);
            Text text = MakeText(rect, "Text", font, 44, Vector2.zero, rect.sizeDelta, new Vector2(0.5f, 0.5f));
            text.text = (i + 1).ToString();
            text.color = Color.black;
            UnityEventTools.AddIntPersistentListener(buttonObject.GetComponent<Button>().onClick, demo.OnButton, i + 1);
        }

        // 화면 한가운데의 큰 버튼(시험 코드가 가운데를 눌러 봅니다)
        GameObject center = new GameObject("Button Center", typeof(Image), typeof(Button));
        RectTransform centerRect = center.GetComponent<RectTransform>();
        centerRect.SetParent(canvasObject.transform, false);
        centerRect.anchorMin = centerRect.anchorMax = new Vector2(0.5f, 0.5f);
        centerRect.sizeDelta = new Vector2(240f, 160f);
        center.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.35f);
        Text centerText = MakeText(centerRect, "Text", font, 36, Vector2.zero, centerRect.sizeDelta, new Vector2(0.5f, 0.5f));
        centerText.text = "가운데";
        centerText.color = Color.black;
        UnityEventTools.AddIntPersistentListener(center.GetComponent<Button>().onClick, demo.OnButton, 9);

        System.IO.Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, SCENE_PATH);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(SCENE_PATH, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("[PlayLink] demo scene saved: " + SCENE_PATH);
    }

    public static void BuildBatch()
    {
        Build();
        EditorApplication.Exit(0);
    }

    private static Text MakeText(Transform parent, string name, Font font, int size, Vector2 position, Vector2 dimensions, Vector2 anchor)
    {
        GameObject textObject = new GameObject(name, typeof(Text));
        RectTransform rect = textObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = anchor;
        rect.sizeDelta = dimensions;
        rect.anchoredPosition = position;
        Text text = textObject.GetComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }
}
