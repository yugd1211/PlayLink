using System;
using System.IO;
using UnityEngine;

// 프로젝트별 저장소 두 가지(같은 PC에서 여러 프로젝트가 이 도구를 써도 섞이지 않게 EditorPrefs 를 쓰지 않습니다).
//   <프로젝트>/UserSettings/GameViewRemote.json        비밀이 아닌 설정(자동 시작, 고른 주소, APK 경로)
//   <프로젝트>/Library/GameViewRemote/secret.json      서버 표시와 토큰(평문). Library 는 버전 관리 밖입니다(브리프 3.3 의 2번, 리뷰 M2).
//                                                      Library 를 지우면 새로 만들어지고, 폰은 QR 을 다시 찍어야 합니다.
// 파일 읽기/쓰기는 메인 스레드에서만 하고, 네트워크 스레드는 Secret 속성(바뀌지 않는 객체를 통째로 바꿔 끼움)만 읽습니다.
public static class GameViewRemoteSettings
{
    private const string SETTINGS_PATH = "UserSettings/GameViewRemote.json";
    private const string SECRET_FOLDER = "Library/GameViewRemote";
    private const string SECRET_PATH = SECRET_FOLDER + "/secret.json";
    private const int NAME_MAX = 24;

    [Serializable]
    private class SettingsData
    {
        public bool autoArm;
        public string hostAddress = string.Empty;
        public string apkPath = string.Empty;
    }

    [Serializable]
    private class SecretData
    {
        public string serverId;
        public string token;
    }

    // 서버 표시와 토큰. 한 번 만든 객체는 바꾸지 않습니다(다른 스레드가 읽는 중에 반만 바뀐 값을 보지 않게).
    public sealed class SecretSnapshot
    {
        public readonly string serverId;
        public readonly string token;
        public readonly byte[] key;

        public SecretSnapshot(string serverId, string token, byte[] key)
        {
            this.serverId = serverId;
            this.token = token;
            this.key = key;
        }
    }

    private static SettingsData settings = new SettingsData();
    private static volatile SecretSnapshot secret;
    private static volatile string projectRoot;
    private static volatile string productName = string.Empty;
    private static bool loaded;

    public static string ProjectRoot { get { return projectRoot; } }
    // 인증 응답의 name: 앱 화면에 "어느 프로젝트"인지 보입니다. Application.productName 은 메인 스레드 전용이라 Load 때 복사해 둡니다(리뷰 m15).
    public static string ProductName { get { return productName; } }
    // 없으면 null(이 프로젝트에서 한 번도 Start 를 누른 적이 없음)
    public static SecretSnapshot Secret { get { return secret; } }

    public static bool AutoArm
    {
        get { return settings.autoArm; }
        set
        {
            if (settings.autoArm != value)
            {
                settings.autoArm = value;
                SaveSettings();
            }
        }
    }

    // 창의 목록에서 고른 주 주소. 빈 값이면 자동
    public static string HostAddress
    {
        get { return settings.hostAddress ?? string.Empty; }
        set
        {
            string next = value ?? string.Empty;
            if (HostAddress != next)
            {
                settings.hostAddress = next;
                SaveSettings();
            }
        }
    }

    // 빈 값이면 도구 폴더의 App~/GameViewRemote.apk
    public static string ApkPath
    {
        get { return settings.apkPath ?? string.Empty; }
        set
        {
            string next = value ?? string.Empty;
            if (ApkPath != next)
            {
                settings.apkPath = next;
                SaveSettings();
            }
        }
    }

    // 메인 스레드. 여러 번 불러도 한 번만 읽습니다(도메인 리로드마다 정적 상태가 비므로 리로드 뒤 다시 읽힘).
    public static void Load()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        projectRoot = Directory.GetParent(Application.dataPath).FullName;
        productName = GameViewRemoteAuth.SanitizeName(Application.productName, NAME_MAX);
        try
        {
            string path = Path.Combine(projectRoot, SETTINGS_PATH);
            if (File.Exists(path))
            {
                settings = JsonUtility.FromJson<SettingsData>(File.ReadAllText(path)) ?? new SettingsData();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} {SETTINGS_PATH} 을 읽지 못해 기본값을 씁니다: {e.Message}");
            settings = new SettingsData();
        }

        // 토큰을 기억한 앱이 예전 Start 버튼만 쓰는 날에 USB 로 붙어도 인증되도록, 파일이 있으면 Start 여부와 무관하게 읽어 둡니다(리뷰 m7).
        try
        {
            string path = Path.Combine(projectRoot, SECRET_PATH);
            if (File.Exists(path))
            {
                SecretData data = JsonUtility.FromJson<SecretData>(File.ReadAllText(path));
                secret = ToSnapshot(data);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} {SECRET_PATH} 을 읽지 못했습니다(Start 를 누르면 새로 만듭니다): {e.Message}");
            secret = null;
        }
    }

    // 메인 스레드. 서버 표시와 토큰이 없으면 만들어 저장합니다.
    public static SecretSnapshot EnsureSecret()
    {
        Load();
        SecretSnapshot current = secret;
        if (current != null)
        {
            return current;
        }

        return Store(GameViewRemoteAuth.NewServerId());
    }

    // 메인 스레드. [새 QR 발급]: 서버 표시는 그대로 두고 토큰만 바꿉니다. 예전 QR 과 앱이 기억한 토큰은 더 이상 통하지 않습니다.
    public static SecretSnapshot RenewToken()
    {
        Load();
        SecretSnapshot current = secret;
        return Store(current != null ? current.serverId : GameViewRemoteAuth.NewServerId());
    }

    private static SecretSnapshot Store(string serverId)
    {
        byte[] key = GameViewRemoteAuth.RandomBytes(GameViewRemoteAuth.TOKEN_BYTES);
        SecretSnapshot next = new SecretSnapshot(serverId, GameViewRemoteAuth.Base64Url(key), key);
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, SECRET_FOLDER));
            File.WriteAllText(Path.Combine(projectRoot, SECRET_PATH),
                JsonUtility.ToJson(new SecretData { serverId = next.serverId, token = next.token }, true));
        }
        catch (Exception e)
        {
            // 저장하지 못해도 이번 에디터 세션 동안은 메모리의 값으로 동작합니다(리로드 뒤에는 새로 만들어짐).
            Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} {SECRET_PATH} 을 쓰지 못했습니다: {e.Message}");
        }

        secret = next;
        return next;
    }

    private static SecretSnapshot ToSnapshot(SecretData data)
    {
        if (data == null || !GameViewRemoteAuth.IsServerId(data.serverId))
        {
            return null;
        }

        byte[] key = GameViewRemoteAuth.FromBase64Url(data.token);
        if (key == null || key.Length != GameViewRemoteAuth.TOKEN_BYTES)
        {
            return null;
        }

        return new SecretSnapshot(data.serverId, data.token, key);
    }

    private static void SaveSettings()
    {
        if (projectRoot == null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(projectRoot, SETTINGS_PATH);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(settings, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{GameViewRemote.LOG_PREFIX} {SETTINGS_PATH} 을 쓰지 못했습니다: {e.Message}");
        }
    }
}
