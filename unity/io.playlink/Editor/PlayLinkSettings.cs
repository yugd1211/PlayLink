using System;
using System.IO;
using UnityEngine;

// 프로젝트별 설정(같은 PC에서 여러 프로젝트가 이 도구를 써도 섞이지 않게 EditorPrefs 를 쓰지 않습니다).
//   <프로젝트>/UserSettings/PlayLink.json   Unity 를 켤 때 자동으로 Start
// 이 PC 의 신원과 페어링한 폰은 프로젝트가 아니라 Windows 사용자마다 하나입니다(PlayLinkIdentity).
// 화질 같은 값은 이 PC 의 모든 프로젝트가 함께 씁니다(PlayLink 의 EditorPrefs).
// 메인 스레드에서만 다룹니다.
public static class PlayLinkSettings
{
    private const string SETTINGS_PATH = "UserSettings/PlayLink.json";

    [Serializable]
    private class SettingsData
    {
        public bool autoArm;
    }

    private static SettingsData settings = new SettingsData();
    private static string projectRoot;
    private static bool loaded;

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

    // 여러 번 불러도 한 번만 읽습니다(도메인 리로드마다 정적 상태가 비므로 리로드 뒤 다시 읽힘).
    public static void Load()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        projectRoot = Directory.GetParent(Application.dataPath).FullName;
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
            Debug.LogWarning($"{PlayLink.LOG_PREFIX} {SETTINGS_PATH} 을 읽지 못해 기본값을 씁니다: {e.Message}");
            settings = new SettingsData();
        }
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
            Debug.LogWarning($"{PlayLink.LOG_PREFIX} {SETTINGS_PATH} 을 쓰지 못했습니다: {e.Message}");
        }
    }
}
