using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PlayLinkThirdParty.QrCodeGenerator;
using UnityEditor;
using UnityEngine;

// PlayLink: QR 만들기와 그리기.
// - 인터넷과 외부 서비스를 쓰지 않습니다. 인코더는 ThirdParty/QrCodeGenerator(MIT) 소스입니다.
// - 내용은 설계 4.1절의 QR 형식 1: http://<주 주소>:<포트>/c#v=1&s=<서버 표시>&t=<토큰>[&a=<다른 주소>,…]
// - 바이트 모드, 오류 정정 M 이상(같은 크기에 들어가면 인코더가 더 높은 수준으로 올림), 둘레 여백 4모듈,
//   모듈 하나는 정수 픽셀(4 이상), 필터 Point.
// 이 파일은 서버 쪽(PlayLinkHost 등)에 기대지 않습니다. 문자열만 받아 그림을 만듭니다.
public static class PlayLinkQr
{
    public const int QR_FORMAT_VERSION = 1;

    // QR 규격의 둘레 여백(quiet zone). 4모듈보다 좁으면 못 읽는 카메라가 있습니다.
    public const int QUIET_ZONE_MODULES = 4;

    // 모듈 하나의 최소 픽셀 수
    public const int MIN_PIXELS_PER_MODULE = 4;

    // QR 형식 1의 `a=` 에 넣는 다른 주소의 최대 개수
    public const int MAX_OTHER_ADDRESSES = 3;

    // 인코딩 결과 하나. modules[y, x] 가 true 면 검은 모듈이고 y = 0 이 맨 윗줄입니다.
    public sealed class Code
    {
        public string text;
        public int byteCount;
        public int version;
        public int size;
        public int mask;
        public string errorCorrection;
        public bool[,] modules;
    }

    private static readonly Color32 DARK = new Color32(0, 0, 0, 255);
    private static readonly Color32 LIGHT = new Color32(255, 255, 255, 255);

    // 설계 4.1절의 QR 내용을 만듭니다. 형식이 맞지 않으면 null 을 돌려주고 error 에 이유를 넣습니다.
    public static string BuildPayload(string host, int port, string serverId, string token, IList<string> otherAddresses, out string error)
    {
        error = null;
        if (!IsIpv4(host))
        {
            error = "PC 주소가 올바르지 않습니다.";
            return null;
        }

        if (port < 1 || port > 65535)
        {
            error = "포트가 올바르지 않습니다.";
            return null;
        }

        if (!IsServerId(serverId))
        {
            error = "서버 표시가 올바르지 않습니다.";
            return null;
        }

        if (!IsToken(token))
        {
            error = "토큰이 올바르지 않습니다.";
            return null;
        }

        StringBuilder builder = new StringBuilder(128);
        builder.Append("http://").Append(host).Append(':').Append(port).Append("/c#v=").Append(QR_FORMAT_VERSION)
            .Append("&s=").Append(serverId).Append("&t=").Append(token);
        int added = 0;
        if (otherAddresses != null)
        {
            foreach (string other in otherAddresses)
            {
                if (added >= MAX_OTHER_ADDRESSES)
                {
                    break;
                }

                if (!IsIpv4(other) || other == host)
                {
                    continue;
                }

                builder.Append(added == 0 ? "&a=" : ",").Append(other);
                added++;
            }
        }

        return builder.ToString();
    }

    // 토큰을 가린 표시용 문자열(창, 콘솔, 문서에 토큰을 남기지 않기 위해).
    public static string MaskToken(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return string.Empty;
        }

        int start = payload.IndexOf("t=", StringComparison.Ordinal);
        while (start > 0 && payload[start - 1] != '&' && payload[start - 1] != '#' && payload[start - 1] != '?')
        {
            start = payload.IndexOf("t=", start + 2, StringComparison.Ordinal);
        }

        if (start < 0)
        {
            return payload;
        }

        int end = payload.IndexOf('&', start);
        return payload.Substring(0, start + 2) + "…" + (end < 0 ? string.Empty : payload.Substring(end));
    }

    // 글자를 UTF-8 바이트로 바꿔 바이트 모드로 인코딩합니다.
    public static Code Encode(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("QR 내용이 비어 있습니다.", "text");
        }

        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        QrCode qr = QrCode.EncodeBinary(bytes, QrCode.Ecc.Medium);
        Code code = new Code();
        code.text = text;
        code.byteCount = bytes.Length;
        code.version = qr.Version;
        code.size = qr.Size;
        code.mask = qr.Mask;
        code.errorCorrection = EccName(qr.ErrorCorrectionLevel);
        code.modules = new bool[qr.Size, qr.Size];
        for (int y = 0; y < qr.Size; y++)
        {
            for (int x = 0; x < qr.Size; x++)
            {
                code.modules[y, x] = qr.GetModule(x, y);
            }
        }

        return code;
    }

    // 표시하려는 한 변의 길이(화면 픽셀)에 맞는 모듈당 픽셀 수. 항상 정수이고 4 이상입니다.
    public static int PixelsPerModule(int moduleCount, int targetPixels)
    {
        int total = moduleCount + QUIET_ZONE_MODULES * 2;
        return Mathf.Max(MIN_PIXELS_PER_MODULE, targetPixels / total);
    }

    // 여백을 포함한 그림 한 변의 픽셀 수
    public static int ImagePixels(int moduleCount, int pixelsPerModule)
    {
        return (moduleCount + QUIET_ZONE_MODULES * 2) * pixelsPerModule;
    }

    // 흰 바탕에 검은 모듈로 그린 픽셀(위에서 아래로, 한 줄씩). 창의 텍스처와 PNG 가 모두 이 배열에서 나옵니다.
    public static Color32[] Rasterize(Code code, int pixelsPerModule, out int imagePixels)
    {
        if (code == null)
        {
            throw new ArgumentNullException("code");
        }

        if (pixelsPerModule < 1)
        {
            throw new ArgumentOutOfRangeException("pixelsPerModule");
        }

        imagePixels = ImagePixels(code.size, pixelsPerModule);
        Color32[] pixels = new Color32[imagePixels * imagePixels];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = LIGHT;
        }

        for (int y = 0; y < code.size; y++)
        {
            for (int x = 0; x < code.size; x++)
            {
                if (!code.modules[y, x])
                {
                    continue;
                }

                int left = (x + QUIET_ZONE_MODULES) * pixelsPerModule;
                int top = (y + QUIET_ZONE_MODULES) * pixelsPerModule;
                for (int py = 0; py < pixelsPerModule; py++)
                {
                    int row = (top + py) * imagePixels + left;
                    for (int px = 0; px < pixelsPerModule; px++)
                    {
                        pixels[row + px] = DARK;
                    }
                }
            }
        }

        return pixels;
    }

    // 창에 그릴 텍스처. Texture2D 는 아래 줄이 먼저라 줄 순서를 뒤집어 넣습니다(뒤집지 않으면 거울상이 되어 읽히지 않음).
    public static Texture2D CreateTexture(Code code, int pixelsPerModule)
    {
        int imagePixels;
        Color32[] topDown = Rasterize(code, pixelsPerModule, out imagePixels);
        Color32[] bottomUp = new Color32[topDown.Length];
        for (int y = 0; y < imagePixels; y++)
        {
            Array.Copy(topDown, y * imagePixels, bottomUp, (imagePixels - 1 - y) * imagePixels, imagePixels);
        }

        Texture2D texture = new Texture2D(imagePixels, imagePixels, TextureFormat.RGB24, false, true);
        texture.name = "PlayLinkQr";
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(bottomUp);
        texture.Apply(false, false);
        return texture;
    }

    // 창이 그리는 것과 같은 텍스처를 PNG 로 저장합니다(검증과 공유용).
    public static void SavePng(Texture2D texture, string path)
    {
        if (texture == null)
        {
            throw new ArgumentNullException("texture");
        }

        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, texture.EncodeToPNG());
    }

    public static bool IsIpv4(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string[] parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length < 1 || part.Length > 3)
            {
                return false;
            }

            int number = 0;
            foreach (char c in part)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }

                number = number * 10 + (c - '0');
            }

            if (number > 255 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }
        }

        return true;
    }

    // 서버 표시: 8자. 실제 값은 base32([A-Z2-7])이지만 설계의 시험 벡터(PLKTEST1)에 맞춰 대문자와 숫자를 모두 받습니다.
    public static bool IsServerId(string value)
    {
        if (value == null || value.Length != 8)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    // 토큰: base64url 22자(16바이트, 패딩 없음)
    public static bool IsToken(string value)
    {
        if (value == null || value.Length != 22)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string EccName(QrCode.Ecc ecc)
    {
        if (ecc == QrCode.Ecc.Low)
        {
            return "L";
        }

        if (ecc == QrCode.Ecc.Medium)
        {
            return "M";
        }

        return ecc == QrCode.Ecc.Quartile ? "Q" : "H";
    }

    // ---------------------------------------------------------------------------------------------
    // 자체 점검: 알려진 값과 맞는지 확인합니다. 메뉴 Tools > PlayLink > QR Self Test
    // ---------------------------------------------------------------------------------------------

    // ISO/IEC 18004 부속서의 예: "01234567", 버전 1-M 의 데이터 16바이트와 오류 정정 10바이트
    private static readonly byte[] ISO_DATA = { 0x10, 0x20, 0x0C, 0x56, 0x61, 0x80, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11 };
    private static readonly byte[] ISO_ECC = { 0xA5, 0x24, 0xD4, 0xC1, 0xED, 0x36, 0xC7, 0x87, 0x2C, 0x55 };

    // 버전 1 ~ 10 의 바이트 모드 용량(오류 정정 M). 규격 표의 값.
    private static readonly int[] BYTE_CAPACITY_M = { 14, 26, 42, 62, 84, 106, 122, 152, 180, 213 };

    // 설계 4.1절의 예(81자). 버전 5-M, 독립 구현(Python segno)으로 만든 행렬과 모듈 단위로 같은지 본 값입니다.
    public const string DESIGN_EXAMPLE = "http://192.168.25.5:7300/c#v=1&s=PLKTEST1&t=AAECAwQFBgcICQoLDA0ODw&a=172.30.224.1";

    [MenuItem("Tools/PlayLink/QR Self Test", false, 41)]
    public static void SelfTestMenu()
    {
        string report;
        bool passed = SelfTest(out report);
        if (passed)
        {
            Debug.Log("[PlayLink] QR 자체 점검 통과\n" + report);
        }
        else
        {
            Debug.LogError("[PlayLink] QR 자체 점검 실패\n" + report);
        }
    }

    public static bool SelfTest(out string report)
    {
        StringBuilder log = new StringBuilder();
        bool ok = true;

        // 1. 리드-솔로몬: 규격 예제의 오류 정정 바이트
        byte[] ecc = new ReedSolomonGenerator(ISO_ECC.Length).GetRemainder(ISO_DATA);
        ok &= Check(log, "리드-솔로몬(규격 예제 01234567, 1-M)", BytesEqual(ecc, ISO_ECC));

        // 2. 규격 예제 전체: 숫자 모드 1-M 이 되고, 행렬에서 다시 읽은 형식 정보가 맞는지
        QrCode iso = QrCode.EncodeText("01234567", QrCode.Ecc.Medium);
        ok &= Check(log, "규격 예제의 버전 1(21x21)", iso.Version == 1 && iso.Size == 21);

        // 3. 바이트 모드 용량 경계: 길이 n 은 버전 v, n + 1 은 버전 v + 1
        for (int version = 1; version <= BYTE_CAPACITY_M.Length; version++)
        {
            int capacity = BYTE_CAPACITY_M[version - 1];
            Code atLimit = Encode(new string('a', capacity));
            bool limitOk = atLimit.version == version && atLimit.size == 17 + 4 * version;
            if (version < BYTE_CAPACITY_M.Length)
            {
                limitOk &= Encode(new string('a', capacity + 1)).version == version + 1;
            }

            ok &= Check(log, $"바이트 모드 용량 경계 버전 {version}-M = {capacity}바이트", limitOk);
        }

        // 4. 설계의 예: 81바이트, 버전 5, 37x37
        Code example = Encode(DESIGN_EXAMPLE);
        ok &= Check(log, "설계 예제 81바이트 → 버전 5(37x37)", example.byteCount == 81 && example.version == 5 && example.size == 37);

        // 5. 구조: 위치 찾기 무늬 3개, 타이밍 무늬, 항상 검은 모듈, 형식 정보(오류 정정 수준과 마스크)
        foreach (string text in new[] { "a", DESIGN_EXAMPLE, new string('z', 120), new string('q', 300) })
        {
            Code code = Encode(text);
            string why;
            bool structure = CheckStructure(code, out why);
            ok &= Check(log, $"구조 검사({code.byteCount}바이트, 버전 {code.version}, 마스크 {code.mask}, {code.errorCorrection}){why}", structure);
        }

        // 6. 그리기: 여백 4모듈이 전부 흰색이고, 모듈 하나가 정확히 정수 픽셀 네모이며, 위아래가 뒤집히지 않았는지
        {
            Code code = Encode(DESIGN_EXAMPLE);
            int pixelsPerModule = PixelsPerModule(code.size, 300);
            int imagePixels;
            Color32[] pixels = Rasterize(code, pixelsPerModule, out imagePixels);
            bool raster = pixelsPerModule >= MIN_PIXELS_PER_MODULE && imagePixels == (code.size + 8) * pixelsPerModule;
            for (int y = 0; y < imagePixels && raster; y++)
            {
                for (int x = 0; x < imagePixels; x++)
                {
                    int moduleX = x / pixelsPerModule - QUIET_ZONE_MODULES;
                    int moduleY = y / pixelsPerModule - QUIET_ZONE_MODULES;
                    bool inside = moduleX >= 0 && moduleY >= 0 && moduleX < code.size && moduleY < code.size;
                    bool dark = inside && code.modules[moduleY, moduleX];
                    if ((pixels[y * imagePixels + x].r == 0) != dark)
                    {
                        raster = false;
                        break;
                    }
                }
            }

            ok &= Check(log, $"그리기(모듈당 {pixelsPerModule}px, {imagePixels}x{imagePixels}px, 여백 4모듈)", raster);

            Texture2D texture = CreateTexture(code, pixelsPerModule);
            Color32[] stored = texture.GetPixels32();
            // 텍스처는 아래 줄이 먼저입니다. 모듈 (x, y) 의 가운데 픽셀이 텍스처의 위에서 y 번째 줄에 있어야 합니다.
            bool orientation = texture.filterMode == FilterMode.Point && texture.width == imagePixels && texture.height == imagePixels;
            for (int y = 0; y < code.size && orientation; y++)
            {
                int rowFromBottom = imagePixels - 1 - ((y + QUIET_ZONE_MODULES) * pixelsPerModule + pixelsPerModule / 2);
                for (int x = 0; x < code.size; x++)
                {
                    int column = (x + QUIET_ZONE_MODULES) * pixelsPerModule + pixelsPerModule / 2;
                    if ((stored[rowFromBottom * imagePixels + column].r == 0) != code.modules[y, x])
                    {
                        orientation = false;
                        break;
                    }
                }
            }

            UnityEngine.Object.DestroyImmediate(texture);
            ok &= Check(log, "텍스처 방향(위아래)과 Point 필터", orientation);
        }

        // 7. 내용 만들기
        {
            string error;
            string payload = BuildPayload("192.168.25.5", 7300, "PLKTEST1", "AAECAwQFBgcICQoLDA0ODw", new[] { "172.30.224.1" }, out error);
            ok &= Check(log, "QR 내용이 설계 4.1절의 예와 같음", payload == DESIGN_EXAMPLE);
            ok &= Check(log, "주소가 하나뿐이면 a= 를 생략", BuildPayload("10.0.0.2", 7300, "PLKTEST1", "AAECAwQFBgcICQoLDA0ODw", null, out error) == "http://10.0.0.2:7300/c#v=1&s=PLKTEST1&t=AAECAwQFBgcICQoLDA0ODw");
            ok &= Check(log, "다른 주소는 3개까지, 주 주소와 같은 것은 뺌",
                BuildPayload("192.168.100.100", 65535, "PLKTEST1", "AAECAwQFBgcICQoLDA0ODw", new[] { "192.168.100.100", "172.16.100.100", "10.100.100.100", "192.168.200.200", "192.168.1.1" }, out error)
                == "http://192.168.100.100:65535/c#v=1&s=PLKTEST1&t=AAECAwQFBgcICQoLDA0ODw&a=172.16.100.100,10.100.100.100,192.168.200.200");
            ok &= Check(log, "잘못된 값은 거절", BuildPayload("my-pc", 7300, "PLKTEST1", "AAECAwQFBgcICQoLDA0ODw", null, out error) == null
                && BuildPayload("192.168.0.2", 7300, "plktest1", "AAECAwQFBgcICQoLDA0ODw", null, out error) == null
                && BuildPayload("192.168.0.2", 7300, "PLKTEST1", "short", null, out error) == null
                && BuildPayload("192.168.0.2", 0, "PLKTEST1", "AAECAwQFBgcICQoLDA0ODw", null, out error) == null);
            ok &= Check(log, "토큰 가리기", MaskToken(DESIGN_EXAMPLE) == "http://192.168.25.5:7300/c#v=1&s=PLKTEST1&t=…&a=172.30.224.1"
                && MaskToken("http://10.0.0.2:7300/c#v=1&s=PLKTEST1&t=AAECAwQFBgcICQoLDA0ODw") == "http://10.0.0.2:7300/c#v=1&s=PLKTEST1&t=…");
        }

        report = log.ToString();
        return ok;
    }

    private static bool Check(StringBuilder log, string name, bool passed)
    {
        log.Append(passed ? "  통과  " : "  실패  ").AppendLine(name);
        return passed;
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    // 인코더가 알려 준 값(버전, 마스크, 오류 정정)과 행렬에 실제로 그려진 것이 맞는지 인코더와 무관한 코드로 확인합니다.
    private static bool CheckStructure(Code code, out string why)
    {
        why = string.Empty;
        int n = code.size;
        if (n != 17 + 4 * code.version)
        {
            why = ": 크기";
            return false;
        }

        // 위치 찾기 무늬(7x7)와 그 둘레의 흰 띠
        int[][] corners = { new[] { 0, 0 }, new[] { n - 7, 0 }, new[] { 0, n - 7 } };
        foreach (int[] corner in corners)
        {
            for (int dy = -1; dy <= 7; dy++)
            {
                for (int dx = -1; dx <= 7; dx++)
                {
                    int x = corner[0] + dx;
                    int y = corner[1] + dy;
                    if (x < 0 || y < 0 || x >= n || y >= n)
                    {
                        continue;
                    }

                    int ring = Mathf.Max(Mathf.Abs(dx - 3), Mathf.Abs(dy - 3));
                    bool expected = ring != 2 && ring != 4;
                    if (code.modules[y, x] != expected)
                    {
                        why = ": 위치 찾기 무늬";
                        return false;
                    }
                }
            }
        }

        // 타이밍 무늬(6번 행과 6번 열, 번갈아)
        for (int i = 8; i < n - 8; i++)
        {
            if (code.modules[6, i] != (i % 2 == 0) || code.modules[i, 6] != (i % 2 == 0))
            {
                why = ": 타이밍 무늬";
                return false;
            }
        }

        // 항상 검은 모듈
        if (!code.modules[n - 8, 8])
        {
            why = ": 고정 모듈";
            return false;
        }

        // 형식 정보 15비트(왼쪽 위 사본): 오류 정정 2비트 + 마스크 3비트 + BCH 10비트, 0x5412 로 XOR
        int[] xs = { 8, 8, 8, 8, 8, 8, 8, 8, 7, 5, 4, 3, 2, 1, 0 };
        int[] ys = { 0, 1, 2, 3, 4, 5, 7, 8, 8, 8, 8, 8, 8, 8, 8 };
        int bits = 0;
        for (int i = 0; i < 15; i++)
        {
            if (code.modules[ys[i], xs[i]])
            {
                bits |= 1 << i;
            }
        }

        // 두 번째 사본(오른쪽 위와 왼쪽 아래)도 같아야 합니다.
        int second = 0;
        for (int i = 0; i < 8; i++)
        {
            if (code.modules[8, n - 1 - i])
            {
                second |= 1 << i;
            }
        }

        for (int i = 8; i < 15; i++)
        {
            if (code.modules[n - 15 + i, 8])
            {
                second |= 1 << i;
            }
        }

        if (bits != second)
        {
            why = ": 형식 정보 사본 불일치";
            return false;
        }

        int raw = bits ^ 0x5412;
        int data = raw >> 10;
        int remainder = data;
        for (int i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        if ((raw & 0x3FF) != (remainder & 0x3FF))
        {
            why = ": 형식 정보 BCH";
            return false;
        }

        // 오류 정정 비트: L = 1, M = 0, Q = 3, H = 2
        string[] names = { "M", "L", "H", "Q" };
        if (names[data >> 3] != code.errorCorrection || (data & 7) != code.mask)
        {
            why = ": 형식 정보의 오류 정정/마스크";
            return false;
        }

        return true;
    }
}
