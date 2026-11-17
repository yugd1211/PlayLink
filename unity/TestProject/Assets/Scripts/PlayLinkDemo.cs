using UnityEngine;
using UnityEngine.UI;

// PlayLink 시험용 장면의 동작: 상자가 돌고 좌우로 움직이며, 버튼을 누른 횟수와 터치를 화면에 적습니다.
// 폰에서 화면이 움직이는지, 터치가 버튼과 레거시 Input 터치로 들어오는지 눈으로 확인하는 용도입니다.
public class PlayLinkDemo : MonoBehaviour
{
    public Transform box;
    public Text label;

    // 시험 코드가 읽습니다.
    public static int ButtonPresses { get; private set; }
    public static int TouchesBegan { get; private set; }

    private int lastButton;
    private Vector2 lastTouch;

    private void OnEnable()
    {
        ButtonPresses = 0;
        TouchesBegan = 0;
    }

    public void OnButton(int number)
    {
        ButtonPresses++;
        lastButton = number;
    }

    private void Update()
    {
        if (box != null)
        {
            box.Rotate(40f * Time.deltaTime, 90f * Time.deltaTime, 0f);
            box.position = new Vector3(Mathf.Sin(Time.time * 1.5f) * 2f, 0.5f, 0f);
        }

        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);
            lastTouch = touch.position;
            if (touch.phase == TouchPhase.Began)
            {
                TouchesBegan++;
            }
        }

        if (label != null)
        {
            label.text = "버튼 " + ButtonPresses + "번 (마지막 " + lastButton + ") · 터치 " + TouchesBegan + "번 · 지금 " + Input.touchCount + "개 "
                + "(" + Mathf.RoundToInt(lastTouch.x) + ", " + Mathf.RoundToInt(lastTouch.y) + ") · " + Time.frameCount;
        }
    }
}
