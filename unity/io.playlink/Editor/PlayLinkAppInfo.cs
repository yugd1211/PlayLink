// PlayLink 안드로이드 앱에 묶인 상수를 한곳에 둡니다.
// 다른 이름으로 앱을 내려면 이 파일과 앱의 build.gradle.kts(applicationId, 스킴 자리 표시자)만 바꿉니다.
public static class PlayLinkAppInfo
{
    // 앱 패키지 이름과 딥링크 스킴(페어링 QR 의 playlink://pair?… 가 씀)
    public const string PACKAGE = "io.playlink.app";
    public const string SCHEME = "playlink";
}
