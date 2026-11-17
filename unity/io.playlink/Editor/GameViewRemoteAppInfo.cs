// Game View Remote 안드로이드 앱에 묶인 상수를 한곳에 둡니다.
// 다른 이름으로 앱을 내려면 이 파일과 앱의 build.gradle.kts(applicationId, 스킴 자리 표시자)만 바꿉니다(설계 15.3).
public static class GameViewRemoteAppInfo
{
    // 앱 패키지 이름과 딥링크 스킴(착륙 페이지의 intent:// 와 [USB 로 연결]이 씀)
    public const string PACKAGE = "kr.co.seeplay.gvr";
    public const string SCHEME = "seeplay-gvr";
    // 인증 절차 번호(X-GVR-Auth 의 v)와 QR 형식 번호(QR 주소의 v)
    public const int AUTH_VERSION = 1;
    public const int QR_VERSION = 1;
    // 서버가 밝히는 기능 목록(X-GVR-Auth 의 caps). 버전을 올리지 않고 기능을 더하는 길입니다(설계 15.1 R1).
    public const string SERVER_CAPS = "h264,seat,take";
    public const string CAP_SEAT = "seat";
    // 도구 폴더 아래 APK 자리. '~' 로 끝나는 폴더는 Unity 가 가져오지 않습니다(.meta 없음, 빌드에 안 들어감).
    public const string APP_FOLDER = "App~";
    public const string APK_FILE = "GameViewRemote.apk";
    public const string APP_JSON_FILE = "app.json";
    public const string LANDING_FILE = "GameViewRemoteLanding.html";
}
