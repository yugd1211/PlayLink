using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

// 연결 프로토콜(docs/PROTOCOL.md)의 암호 계산. 기준 구현은 tools/playlink-proto.js 이고 값은 tools/proto-vectors.json 과 같아야 합니다(Self Test 가 확인).
// Unity 2022.3 의 Mono 에는 ECDiffieHellman 이 없고 AesGcm 은 실행하면 실패해서, P-256 은 BigInteger 로 직접 계산하고
// 레코드는 AES-256-CTR(ECB 암호기로 만든 키 흐름) + HMAC-SHA256 으로 만듭니다.
// P-256 계산은 실행 시간이 일정하지 않습니다(PROTOCOL.md 6장). 임시 키는 연결마다 새로 만들어 한 번만 씁니다.
public static class PlayLinkCrypto
{
    public const int TAG_BYTES = 16;
    public const int KEY_BYTES = 32;
    public const int POINT_BYTES = 65;
    private const string BASE32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int NAME_MAX = 32;

    #region P-256

    private static readonly BigInteger P = FromHex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
    private static readonly BigInteger N = FromHex("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
    private static readonly BigInteger B = FromHex("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");
    private static readonly BigInteger GX = FromHex("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296");
    private static readonly BigInteger GY = FromHex("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5");

    // 야코비 좌표의 점(Z 가 0 이면 무한 원점)
    private struct Point
    {
        public BigInteger x;
        public BigInteger y;
        public BigInteger z;
    }

    // 새 임시 개인키(32바이트, 1 ~ n-1)
    public static byte[] NewPrivateKey()
    {
        while (true)
        {
            byte[] candidate = RandomBytes(KEY_BYTES);
            BigInteger d = ToInteger(candidate, 0, KEY_BYTES);
            if (d.Sign > 0 && d < N)
            {
                return candidate;
            }
        }
    }

    // 개인키의 공개키(압축 안 한 65바이트, 04 ‖ X ‖ Y)
    public static byte[] PublicKey(byte[] privateKey)
    {
        BigInteger d = ToInteger(privateKey, 0, KEY_BYTES);
        if (privateKey.Length != KEY_BYTES || d.Sign <= 0 || d >= N)
        {
            throw new CryptographicException("bad private key");
        }

        ToAffine(Multiply(d, GX, GY), out BigInteger x, out BigInteger y);
        byte[] result = new byte[POINT_BYTES];
        result[0] = 4;
        WriteInteger(x, result, 1);
        WriteInteger(y, result, 1 + KEY_BYTES);
        return result;
    }

    // ECDH 의 공유 X 좌표(32바이트). 상대 점이 곡선 위에 없으면 예외.
    public static byte[] SharedSecret(byte[] privateKey, byte[] peerPublicKey)
    {
        if (peerPublicKey == null || peerPublicKey.Length != POINT_BYTES || peerPublicKey[0] != 4)
        {
            throw new CryptographicException("bad public key");
        }

        BigInteger x = ToInteger(peerPublicKey, 1, KEY_BYTES);
        BigInteger y = ToInteger(peerPublicKey, 1 + KEY_BYTES, KEY_BYTES);
        // 곡선 위의 점인지 확인합니다: y² = x³ − 3x + b (mod p). P-256 은 여인수가 1 이라 이것으로 충분합니다.
        if (x >= P || y >= P || Mod(y * y - (x * x * x - 3 * x + B)) != BigInteger.Zero)
        {
            throw new CryptographicException("public key is not on the curve");
        }

        BigInteger d = ToInteger(privateKey, 0, KEY_BYTES);
        Point shared = Multiply(d, x, y);
        if (shared.z.IsZero)
        {
            throw new CryptographicException("bad shared point");
        }

        ToAffine(shared, out BigInteger sharedX, out BigInteger _);
        byte[] result = new byte[KEY_BYTES];
        WriteInteger(sharedX, result, 0);
        return result;
    }

    // k·(x, y): 위 비트부터 두 배 + 더하기
    private static Point Multiply(BigInteger k, BigInteger x, BigInteger y)
    {
        Point result = new Point { x = BigInteger.One, y = BigInteger.One, z = BigInteger.Zero };
        byte[] bits = k.ToByteArray();
        for (int i = bits.Length * 8 - 1; i >= 0; i--)
        {
            result = Double(result);
            if ((bits[i >> 3] >> (i & 7) & 1) != 0)
            {
                result = AddAffine(result, x, y);
            }
        }

        return result;
    }

    private static Point Double(Point p)
    {
        if (p.z.IsZero || p.y.IsZero)
        {
            return new Point { x = BigInteger.One, y = BigInteger.One, z = BigInteger.Zero };
        }

        BigInteger delta = Mod(p.z * p.z);
        BigInteger gamma = Mod(p.y * p.y);
        BigInteger beta = Mod(p.x * gamma);
        BigInteger alpha = Mod(3 * (p.x - delta) * (p.x + delta));
        BigInteger x3 = Mod(alpha * alpha - 8 * beta);
        BigInteger z3 = Mod((p.y + p.z) * (p.y + p.z) - gamma - delta);
        BigInteger y3 = Mod(alpha * (4 * beta - x3) - 8 * gamma * gamma);
        return new Point { x = x3, y = y3, z = z3 };
    }

    private static Point AddAffine(Point p, BigInteger x2, BigInteger y2)
    {
        if (p.z.IsZero)
        {
            return new Point { x = x2, y = y2, z = BigInteger.One };
        }

        BigInteger zz = Mod(p.z * p.z);
        BigInteger u2 = Mod(x2 * zz);
        BigInteger s2 = Mod(y2 * p.z * zz);
        BigInteger h = Mod(u2 - p.x);
        BigInteger r = Mod(s2 - p.y);
        if (h.IsZero)
        {
            return r.IsZero ? Double(p) : new Point { x = BigInteger.One, y = BigInteger.One, z = BigInteger.Zero };
        }

        BigInteger hh = Mod(h * h);
        BigInteger hhh = Mod(h * hh);
        BigInteger v = Mod(p.x * hh);
        BigInteger x3 = Mod(r * r - hhh - 2 * v);
        BigInteger y3 = Mod(r * (v - x3) - p.y * hhh);
        return new Point { x = x3, y = y3, z = Mod(p.z * h) };
    }

    private static void ToAffine(Point p, out BigInteger x, out BigInteger y)
    {
        BigInteger inverse = BigInteger.ModPow(p.z, P - 2, P);
        BigInteger inverse2 = Mod(inverse * inverse);
        x = Mod(p.x * inverse2);
        y = Mod(p.y * inverse2 * inverse);
    }

    private static BigInteger Mod(BigInteger value)
    {
        value %= P;
        return value.Sign < 0 ? value + P : value;
    }

    // 빅엔디언 부호 없는 바이트 → BigInteger
    private static BigInteger ToInteger(byte[] bytes, int offset, int count)
    {
        byte[] little = new byte[count + 1];
        for (int i = 0; i < count; i++)
        {
            little[i] = bytes[offset + count - 1 - i];
        }

        return new BigInteger(little);
    }

    // BigInteger → 빅엔디언 32바이트
    private static void WriteInteger(BigInteger value, byte[] target, int offset)
    {
        byte[] little = value.ToByteArray();
        for (int i = 0; i < KEY_BYTES; i++)
        {
            target[offset + KEY_BYTES - 1 - i] = i < little.Length ? little[i] : (byte)0;
        }
    }

    private static BigInteger FromHex(string hex)
    {
        byte[] bytes = HexToBytes(hex);
        return ToInteger(bytes, 0, bytes.Length);
    }

    #endregion

    #region 키 계산

    // 한 연결의 키(PROTOCOL.md 2장). i2r = PC → 폰, r2i = 폰 → PC
    public sealed class Keys
    {
        public byte[] encI2R;
        public byte[] macI2R;
        public byte[] encR2I;
        public byte[] macR2I;
        public byte[] pair;
        // 양쪽 화면에 보이는 6자리 숫자
        public string sas;
    }

    public static byte[] Sha256(params byte[][] parts)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return sha.ComputeHash(Concat(parts));
        }
    }

    public static byte[] Hmac(byte[] key, params byte[][] parts)
    {
        using (HMACSHA256 hmac = new HMACSHA256(key))
        {
            return hmac.ComputeHash(Concat(parts));
        }
    }

    // th = SHA256("PLK1" ‖ u16(len m1) ‖ m1 ‖ u16(len m2) ‖ m2 ‖ u16(len m3) ‖ m3)
    public static byte[] TranscriptHash(byte[] m1, byte[] m2, byte[] m3)
    {
        return Sha256(Encoding.ASCII.GetBytes("PLK1"), U16(m1.Length), m1, U16(m2.Length), m2, U16(m3.Length), m3);
    }

    // psk: paired = 페어링 키(32B), qr = SHA256(QR 비밀 16B), sas = 0 32개
    public static byte[] Psk(string mode, byte[] secret)
    {
        if (mode == "paired")
        {
            return secret;
        }

        return mode == "qr" ? Sha256(secret) : new byte[KEY_BYTES];
    }

    public static Keys DeriveKeys(byte[] transcriptHash, byte[] sharedSecret, byte[] psk)
    {
        byte[] prk = Hmac(transcriptHash, sharedSecret, psk);
        byte[] sasKey = Label(prk, "sas");
        uint number = ((uint)sasKey[0] << 24) | ((uint)sasKey[1] << 16) | ((uint)sasKey[2] << 8) | sasKey[3];
        return new Keys
        {
            encI2R = Label(prk, "enc i2r"),
            macI2R = Label(prk, "mac i2r"),
            encR2I = Label(prk, "enc r2i"),
            macR2I = Label(prk, "mac r2i"),
            pair = Label(prk, "pair"),
            sas = (number % 1000000).ToString("D6"),
        };
    }

    private static byte[] Label(byte[] prk, string label)
    {
        return Hmac(prk, Encoding.ASCII.GetBytes(label), new byte[] { 1 });
    }

    // 탐색 응답과 who 응답의 증명(HMAC 의 앞 16바이트, base64url)
    public static string DiscoveryMac(byte[] key, string pcId, string phoneId, string name, int port, bool want, string nonce)
    {
        byte[] full = Hmac(key, Encoding.UTF8.GetBytes("PLK1-DISC|" + pcId + "|" + phoneId + "|" + name + "|" + port + "|" + (want ? "1" : "0") + "|" + nonce));
        byte[] head = new byte[TAG_BYTES];
        Buffer.BlockCopy(full, 0, head, 0, TAG_BYTES);
        return Base64Url(head);
    }

    #endregion

    #region 작은 도우미

    public static byte[] RandomBytes(int count)
    {
        byte[] bytes = new byte[count];
        using (RandomNumberGenerator random = RandomNumberGenerator.Create())
        {
            random.GetBytes(bytes);
        }

        return bytes;
    }

    // pcId: 무작위 16자, [A-Z2-7]
    public static string NewId()
    {
        byte[] bytes = RandomBytes(16);
        char[] chars = new char[16];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = BASE32[bytes[i] & 31];
        }

        return new string(chars);
    }

    public static bool IsId(string text)
    {
        if (text == null || text.Length != 16)
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

    // 사람에게 보이는 이름: 글자 · 숫자 · 공백 · _-.() 만, 32자까지(기준 구현의 cleanName)
    public static string CleanName(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder(NAME_MAX);
        for (int i = 0; i < text.Length && builder.Length < NAME_MAX; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if ((char.IsLetter(text, i) || char.IsNumber(text, i)) && builder.Length + 2 <= NAME_MAX)
                {
                    builder.Append(c).Append(text[i + 1]);
                }

                i++;
            }
            else if (char.IsLetter(c) || char.IsNumber(c) || c == ' ' || c == '_' || c == '-' || c == '.' || c == '(' || c == ')')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // 형식이 틀리면 null
    public static byte[] FromBase64Url(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 4096 || text.IndexOfAny(new[] { '+', '/', '=' }) >= 0)
        {
            return null;
        }

        string padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // 길이가 같으면 내용과 무관하게 같은 시간이 걸리는 비교
    public static bool FixedTimeEquals(byte[] a, int aOffset, byte[] b, int bOffset, int count)
    {
        int diff = 0;
        for (int i = 0; i < count; i++)
        {
            diff |= a[aOffset + i] ^ b[bOffset + i];
        }

        return diff == 0;
    }

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

    public static byte[] HexToBytes(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    public static string ToHex(byte[] bytes)
    {
        StringBuilder builder = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
    }

    private static byte[] U16(int value)
    {
        return new[] { (byte)(value >> 8), (byte)value };
    }

    private static byte[] Concat(byte[][] parts)
    {
        if (parts.Length == 1)
        {
            return parts[0];
        }

        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] all = new byte[total];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, all, offset, part.Length);
            offset += part.Length;
        }

        return all;
    }

    #endregion
}

// 키 교환이 끝난 연결(PROTOCOL.md 3장). 쓰기는 레코드로 싸서 보내고, 읽기는 레코드를 풀어 이어 붙인 바이트 흐름으로 돌려줍니다.
//   레코드 = len(2B 빅엔디언) ‖ 암호문 ‖ 태그(16B)
//   암호문 = AES-256-CTR(처음 카운터 블록 = seq(8B) ‖ 0 8개), 태그 = HMAC-SHA256(seq ‖ len ‖ 암호문)[0..16]
// Write 한 번 = 소켓 쓰기 한 번입니다(나눠 쓰면 adb 터널에서 뒤쪽이 늦게 감, 개발 문서 12.7). 16384바이트가 넘으면 레코드 여러 개를 한 버퍼에 모아 보냅니다.
// 읽기는 한 스레드, 쓰기는 여러 스레드가 해도 됩니다(쓰기는 안에서 잠급니다).
public sealed class PlayLinkSecureStream : Stream
{
    private const int WRITE_CHUNK = 16384;
    private const int BLOCK = 16;
    private const int HEADER = 2;

    private sealed class Direction : IDisposable
    {
        public readonly ICryptoTransform cipher;
        public readonly HMACSHA256 hmac;
        public readonly byte[] macHead = new byte[10];
        public long seq;
        public byte[] counters = new byte[0];
        public byte[] keystream = new byte[0];
        private readonly Aes aes;

        public Direction(byte[] encKey, byte[] macKey)
        {
            aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = encKey;
            cipher = aes.CreateEncryptor();
            hmac = new HMACSHA256(macKey);
        }

        // 이 레코드(seq)의 키 흐름을 keystream 앞 count 바이트에 만듭니다.
        public void MakeKeystream(int count)
        {
            int padded = (count + BLOCK - 1) / BLOCK * BLOCK;
            if (counters.Length < padded)
            {
                counters = new byte[padded];
                keystream = new byte[padded];
            }

            for (int offset = 0, block = 0; offset < padded; offset += BLOCK, block++)
            {
                WriteSeq(counters, offset, seq);
                counters[offset + 8] = 0;
                counters[offset + 9] = 0;
                counters[offset + 10] = 0;
                counters[offset + 11] = 0;
                counters[offset + 12] = (byte)(block >> 24);
                counters[offset + 13] = (byte)(block >> 16);
                counters[offset + 14] = (byte)(block >> 8);
                counters[offset + 15] = (byte)block;
            }

            cipher.TransformBlock(counters, 0, padded, keystream, 0);
        }

        // 태그 = HMAC(seq(8B) ‖ len(2B) ‖ 암호문)
        public byte[] Tag(byte[] cipherText, int offset, int count)
        {
            WriteSeq(macHead, 0, seq);
            macHead[8] = (byte)(count >> 8);
            macHead[9] = (byte)count;
            hmac.Initialize();
            hmac.TransformBlock(macHead, 0, macHead.Length, null, 0);
            hmac.TransformFinalBlock(cipherText, offset, count);
            return hmac.Hash;
        }

        public void Dispose()
        {
            cipher.Dispose();
            hmac.Dispose();
            aes.Dispose();
        }

        private static void WriteSeq(byte[] target, int offset, long value)
        {
            for (int i = 0; i < 8; i++)
            {
                target[offset + i] = (byte)(value >> (8 * (7 - i)));
            }
        }
    }

    private readonly Stream inner;
    private readonly Direction output;
    private readonly Direction input;
    private readonly object writeLock = new object();
    private readonly byte[] lengthBytes = new byte[HEADER];
    private byte[] sendBuffer = new byte[0];
    private byte[] receiveBuffer = new byte[0];
    private int plainOffset;
    private int plainCount;

    public PlayLinkSecureStream(Stream inner, byte[] encOut, byte[] macOut, byte[] encIn, byte[] macIn)
    {
        this.inner = inner;
        output = new Direction(encOut, macOut);
        input = new Direction(encIn, macIn);
    }

    public override bool CanRead { get { return true; } }
    public override bool CanWrite { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (writeLock)
        {
            int records = (count + WRITE_CHUNK - 1) / WRITE_CHUNK;
            int total = count + records * (HEADER + PlayLinkCrypto.TAG_BYTES);
            if (sendBuffer.Length < total)
            {
                sendBuffer = new byte[Math.Max(total, sendBuffer.Length * 2)];
            }

            int position = 0;
            while (count > 0)
            {
                int chunk = Math.Min(count, WRITE_CHUNK);
                sendBuffer[position] = (byte)(chunk >> 8);
                sendBuffer[position + 1] = (byte)chunk;
                output.MakeKeystream(chunk);
                byte[] keystream = output.keystream;
                int body = position + HEADER;
                for (int i = 0; i < chunk; i++)
                {
                    sendBuffer[body + i] = (byte)(buffer[offset + i] ^ keystream[i]);
                }

                Buffer.BlockCopy(output.Tag(sendBuffer, body, chunk), 0, sendBuffer, body + chunk, PlayLinkCrypto.TAG_BYTES);
                output.seq++;
                position = body + chunk + PlayLinkCrypto.TAG_BYTES;
                offset += chunk;
                count -= chunk;
            }

            inner.Write(sendBuffer, 0, position);
        }
    }

    // 레코드 경계와 무관하게 읽습니다. 상대가 레코드 사이에서 닫았으면 0.
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (plainCount == 0 && !ReadRecord())
        {
            return 0;
        }

        int taken = Math.Min(count, plainCount);
        Buffer.BlockCopy(receiveBuffer, plainOffset, buffer, offset, taken);
        plainOffset += taken;
        plainCount -= taken;
        return taken;
    }

    // 레코드 하나의 평문을 통째로 읽습니다(마무리 메시지: 레코드 하나에 JSON 하나). 상대가 닫았으면 IOException.
    public byte[] ReadMessage()
    {
        if (plainCount == 0 && !ReadRecord())
        {
            throw new IOException("connection closed");
        }

        byte[] message = new byte[plainCount];
        Buffer.BlockCopy(receiveBuffer, plainOffset, message, 0, plainCount);
        plainCount = 0;
        return message;
    }

    private bool ReadRecord()
    {
        int first = inner.Read(lengthBytes, 0, HEADER);
        if (first <= 0)
        {
            return false;
        }

        if (first < HEADER)
        {
            ReadExactly(lengthBytes, first, HEADER - first);
        }

        int length = (lengthBytes[0] << 8) | lengthBytes[1];
        if (length == 0)
        {
            throw new IOException("empty record");
        }

        if (receiveBuffer.Length < length + PlayLinkCrypto.TAG_BYTES)
        {
            receiveBuffer = new byte[length + PlayLinkCrypto.TAG_BYTES];
        }

        ReadExactly(receiveBuffer, 0, length + PlayLinkCrypto.TAG_BYTES);
        byte[] expected = input.Tag(receiveBuffer, 0, length);
        if (!PlayLinkCrypto.FixedTimeEquals(expected, 0, receiveBuffer, length, PlayLinkCrypto.TAG_BYTES))
        {
            throw new IOException("bad record tag");
        }

        input.MakeKeystream(length);
        byte[] keystream = input.keystream;
        for (int i = 0; i < length; i++)
        {
            receiveBuffer[i] ^= keystream[i];
        }

        input.seq++;
        plainOffset = 0;
        plainCount = length;
        return true;
    }

    private void ReadExactly(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = inner.Read(buffer, offset, count);
            if (read <= 0)
            {
                throw new IOException("connection closed");
            }

            offset += read;
            count -= read;
        }
    }

    public override void Flush()
    {
        inner.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            output.Dispose();
            input.Dispose();
        }

        base.Dispose(disposing);
    }
}
