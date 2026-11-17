using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

// 앱 인증(인증 1, 설계 3.2)의 계산 부분. 전부 순수 함수라 어느 스레드에서 불러도 됩니다.
//   문자열 = "PLAYLINK-AUTH-1|" + 역할(srv|cli) + "|" + 채널(video|input|…) + "|" + sid + "|" + cn + "|" + sn
//   증명   = base64url( HMAC-SHA256(토큰 16바이트, 문자열) )  (패딩 없음, 43자)
// 역할이 들어가 서버 증명을 클라이언트 증명으로 되쓸 수 없고(반사), 양쪽 임의 값이 들어가 다시 보내기가 안 됩니다.
public static class PlayLinkAuth
{
    public const string HEADER_NAME = "X-PlayLink-Auth";
    public const string ROLE_SERVER = "srv";
    public const string ROLE_CLIENT = "cli";
    public const int TOKEN_BYTES = 16;
    public const int NONCE_BYTES = 16;
    private const int NONCE_CHARS = 22;
    private const int SERVER_ID_CHARS = 8;
    private const int DEVICE_ID_CHARS = 12;
    private const string BASE32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Proof(byte[] key, string role, string channel, string sid, string cn, string sn)
    {
        byte[] text = Encoding.ASCII.GetBytes("PLAYLINK-AUTH-1|" + role + "|" + channel + "|" + sid + "|" + cn + "|" + sn);
        using (HMACSHA256 hmac = new HMACSHA256(key))
        {
            return Base64Url(hmac.ComputeHash(text));
        }
    }

    public static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // 형식이 틀리면 null
    public static byte[] FromBase64Url(string text)
    {
        if (string.IsNullOrEmpty(text) || !IsBase64UrlText(text))
        {
            return null;
        }

        string padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
            case 1:
                return null;
        }

        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // 길이가 같으면 내용과 무관하게 같은 시간이 걸리는 비교(증명 값 검증용)
    public static bool FixedTimeEquals(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            return false;
        }

        int diff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }

    // "이름=값; 이름=값" 을 해석합니다. 모르는 이름은 그대로 담아 두고(쓰는 쪽이 무시), 순서는 상관없습니다.
    public static Dictionary<string, string> ParseHeader(string value)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value))
        {
            return result;
        }

        foreach (string part in value.Split(';'))
        {
            string item = part.Trim();
            int equals = item.IndexOf('=');
            if (equals > 0)
            {
                result[item.Substring(0, equals).Trim()] = item.Substring(equals + 1).Trim();
            }
        }

        return result;
    }

    // 임의 값(cn, sn): 16바이트의 base64url 22자
    public static bool IsNonce(string text)
    {
        return text != null && text.Length == NONCE_CHARS && IsBase64UrlText(text);
    }

    // 서버 표시: [A-Z2-7]{8}
    public static bool IsServerId(string text)
    {
        if (text == null || text.Length != SERVER_ID_CHARS)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (BASE32.IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }

    // 기기 표시: [a-z0-9]{12}
    public static bool IsDeviceId(string text)
    {
        if (text == null || text.Length != DEVICE_ID_CHARS)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    // [A-Za-z0-9_-] 만 남기고 최대 길이로 자릅니다(dev, name 처럼 화면에 보이는 값).
    public static string SanitizeName(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        foreach (char c in text)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
            {
                builder.Append(c);
                if (builder.Length >= maxLength)
                {
                    break;
                }
            }
        }

        return builder.ToString();
    }

    public static byte[] RandomBytes(int count)
    {
        byte[] bytes = new byte[count];
        using (RNGCryptoServiceProvider random = new RNGCryptoServiceProvider())
        {
            random.GetBytes(bytes);
        }

        return bytes;
    }

    public static string NewNonce()
    {
        return Base64Url(RandomBytes(NONCE_BYTES));
    }

    // 무작위 5바이트(40비트)의 base32 8자
    public static string NewServerId()
    {
        byte[] bytes = RandomBytes(5);
        ulong value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        char[] chars = new char[SERVER_ID_CHARS];
        for (int i = SERVER_ID_CHARS - 1; i >= 0; i--)
        {
            chars[i] = BASE32[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }

    private static bool IsBase64UrlText(string text)
    {
        foreach (char c in text)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    #region Self Test

    // 설계 3.2 의 시험 벡터. 앱(net.Auth 단위 시험), 시험 서버(replay.js)와 같은 값을 내야 합니다.
    [MenuItem("Tools/PlayLink/Self Test", false, 200)]
    private static void SelfTestMenu()
    {
        bool passed = RunSelfTest(out string report);
        if (passed)
        {
            Debug.Log($"{PlayLink.LOG_PREFIX} Self Test 통과\n{report}");
        }
        else
        {
            Debug.LogError($"{PlayLink.LOG_PREFIX} Self Test 실패\n{report}");
        }
    }

    public static bool RunSelfTest(out string report)
    {
        StringBuilder log = new StringBuilder();
        bool ok = true;
        byte[] key = new byte[TOKEN_BYTES];
        byte[] cnBytes = new byte[NONCE_BYTES];
        byte[] snBytes = new byte[NONCE_BYTES];
        for (int i = 0; i < 16; i++)
        {
            key[i] = (byte)i;
            cnBytes[i] = (byte)(0x10 + i);
            snBytes[i] = (byte)(0x20 + i);
        }

        const string sid = "PLKTEST1";
        string cn = Base64Url(cnBytes);
        string sn = Base64Url(snBytes);
        ok &= Check(log, "토큰 t", Base64Url(key), "AAECAwQFBgcICQoLDA0ODw");
        ok &= Check(log, "cn", cn, "EBESExQVFhcYGRobHB0eHw");
        ok &= Check(log, "sn", sn, "ICEiIyQlJicoKSorLC0uLw");
        ok &= Check(log, "video/srv", Proof(key, ROLE_SERVER, "video", sid, cn, sn), "rASdAm0R4E9eOg8WrTdhWEl_tYFZPYQaTVDJQFcq7MI");
        ok &= Check(log, "video/cli", Proof(key, ROLE_CLIENT, "video", sid, cn, sn), "Kum7Uc2NTxLHeflx39gkJOZfTH0T9JUHS--WvYy_kR8");
        ok &= Check(log, "input/srv", Proof(key, ROLE_SERVER, "input", sid, cn, sn), "L04dFts1PdX9hTbKhFnitvOCwRrHVJlvmdKtLpSdFms");
        ok &= Check(log, "input/cli", Proof(key, ROLE_CLIENT, "input", sid, cn, sn), "5GsiBVEovxdUf6qehtU9FF2vtYkyHPwfOdV_X5P_Ijg");

        byte[] decoded = FromBase64Url("AAECAwQFBgcICQoLDA0ODw");
        ok &= Check(log, "base64url 풀기", decoded != null ? BitConverter.ToString(decoded) : "null", BitConverter.ToString(key));
        ok &= Check(log, "base64url 잘못된 글자", FromBase64Url("AAEC+wQF") == null ? "null" : "값", "null");

        Dictionary<string, string> header = ParseHeader("v=1; sid=PLKTEST1;cn=" + cn + " ;  take=1; 모르는=값; caps=h264,seat");
        ok &= Check(log, "헤더 v", Get(header, "v"), "1");
        ok &= Check(log, "헤더 sid", Get(header, "sid"), "PLKTEST1");
        ok &= Check(log, "헤더 cn", Get(header, "cn"), cn);
        ok &= Check(log, "헤더 take", Get(header, "take"), "1");
        ok &= Check(log, "헤더 caps", Get(header, "caps"), "h264,seat");
        ok &= Check(log, "IsNonce(22자)", IsNonce(cn).ToString(), "True");
        ok &= Check(log, "IsNonce(21자)", IsNonce(cn.Substring(1)).ToString(), "False");
        // 시험 벡터의 sid "PLKTEST1" 은 '1' 이 들어 있어 실제 형식([A-Z2-7]{8})에는 맞지 않습니다(계산 확인용 값일 뿐).
        ok &= Check(log, "IsServerId", (IsServerId("PLKTEST2") && !IsServerId(sid) && !IsServerId("plktest2") && !IsServerId("PLKTEST22")).ToString(), "True");
        ok &= Check(log, "IsDeviceId", (IsDeviceId("abc123def456") && !IsDeviceId("ABC123def456") && !IsDeviceId("abc")).ToString(), "True");
        ok &= Check(log, "FixedTimeEquals", (FixedTimeEquals("abc", "abc") && !FixedTimeEquals("abc", "abd") && !FixedTimeEquals("abc", "ab")).ToString(), "True");
        ok &= Check(log, "NewServerId 형식", IsServerId(NewServerId()).ToString(), "True");
        ok &= Check(log, "NewNonce 형식", IsNonce(NewNonce()).ToString(), "True");
        report = log.ToString();
        return ok;
    }

    private static string Get(Dictionary<string, string> map, string name)
    {
        return map.TryGetValue(name, out string value) ? value : null;
    }

    private static bool Check(StringBuilder log, string name, string actual, string expected)
    {
        bool same = actual == expected;
        log.Append(same ? "  ok   " : "  FAIL ").Append(name).Append(": ").Append(actual);
        if (!same)
        {
            log.Append("  (기대: ").Append(expected).Append(')');
        }

        log.Append('\n');
        return same;
    }

    #endregion
}
