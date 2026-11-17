using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// 이 PC(Windows 사용자)의 신원과 페어링한 폰 목록. 프로젝트가 아니라 Windows 사용자마다 하나입니다.
//   %LOCALAPPDATA%\PlayLink\identity.dat   pcId, PC 이름, QR 비밀, 폰 목록(phoneId, 라벨, 페어링 키, 마지막 주소)
// 파일 내용은 DPAPI(CurrentUser)로 감쌉니다: 같은 Windows 사용자만 풀 수 있습니다.
// 키와 QR 비밀은 콘솔, 로그, 창의 글자로 내보내지 않습니다.
// 어느 스레드에서 불러도 됩니다. 읽는 쪽은 Current(바뀌지 않는 객체를 통째로 바꿔 끼움)만 봅니다.
public static class PlayLinkIdentity
{
    private const string FOLDER_NAME = "PlayLink";
    private const string FILE_NAME = "identity.dat";
    private const string TEST_FOLDER_VARIABLE = "PLAYLINK_TEST_IDENTITY_FOLDER";
    private const int QR_SECRET_BYTES = 16;
    private static readonly byte[] ENTROPY = Encoding.ASCII.GetBytes("PlayLink identity v1");

    [Serializable]
    private class PhoneData
    {
        public string id;
        public string label;
        public string key;
        public string host;
        public int port;
    }

    [Serializable]
    private class FileData
    {
        public string pcId;
        public string pcName;
        public string qrSecret;
        public List<PhoneData> phones = new List<PhoneData>();
    }

    public sealed class Phone
    {
        public readonly string id;
        // 창에 보이는 이름(처음에는 폰이 알려 준 이름, 창에서 고칠 수 있음)
        public readonly string label;
        public readonly byte[] key;
        // 마지막으로 연결한 주소(없으면 빈 값). USB 로 연결한 주소는 남기지 않습니다.
        public readonly string host;
        public readonly int port;

        public Phone(string id, string label, byte[] key, string host, int port)
        {
            this.id = id;
            this.label = label;
            this.key = key;
            this.host = host ?? string.Empty;
            this.port = port;
        }
    }

    public sealed class Snapshot
    {
        public string pcId;
        public string pcName;
        public byte[] qrSecret;
        public Phone[] phones = new Phone[0];

        public Phone Find(string phoneId)
        {
            foreach (Phone phone in phones)
            {
                if (phone.id == phoneId)
                {
                    return phone;
                }
            }

            return null;
        }
    }

    private static readonly object gate = new object();
    private static volatile Snapshot current;

    // 저장이 바뀌었을 때(아무 스레드). 창을 다시 그리게 하는 데 씁니다.
    public static Action onChanged;
    // 파일을 읽거나 쓰지 못한 이유(없으면 null). 창이 보여 줍니다.
    public static string LastError { get; private set; }

    public static Snapshot Current
    {
        get
        {
            Snapshot snapshot = current;
            if (snapshot != null)
            {
                return snapshot;
            }

            lock (gate)
            {
                if (current == null)
                {
                    current = Load();
                }

                return current;
            }
        }
    }

    public static string FilePath
    {
        get
        {
            string folder = Environment.GetEnvironmentVariable(TEST_FOLDER_VARIABLE);
            if (string.IsNullOrEmpty(folder))
            {
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FOLDER_NAME);
            }

            return Path.Combine(folder, FILE_NAME);
        }
    }

    // 시험용: 사용자의 실제 저장 대신 이 폴더를 씁니다(null 이면 원래 자리). 이 프로세스의 환경 변수에 두어 도메인 리로드 뒤에도 유지됩니다.
    public static void UseFolderForTests(string folder)
    {
        lock (gate)
        {
            Environment.SetEnvironmentVariable(TEST_FOLDER_VARIABLE, folder);
            current = null;
        }
    }

    public static void SetPcName(string name)
    {
        string clean = PlayLinkCrypto.CleanName(name);
        if (clean.Length == 0)
        {
            return;
        }

        Change(data => data.pcName = clean);
    }

    // 페어링이 끝났을 때. 이미 있던 폰이면 키를 바꾸고 라벨은 그대로 둡니다.
    public static void SavePhone(string phoneId, string phoneName, byte[] key, string host, int port)
    {
        Change(data =>
        {
            PhoneData phone = data.phones.Find(p => p.id == phoneId);
            if (phone == null)
            {
                phone = new PhoneData { id = phoneId, label = DefaultLabel(phoneName) };
                data.phones.Add(phone);
            }

            phone.key = PlayLinkCrypto.Base64Url(key);
            phone.host = host ?? string.Empty;
            phone.port = port;
        });
    }

    public static void SetPhoneLabel(string phoneId, string label)
    {
        string clean = PlayLinkCrypto.CleanName(label);
        if (clean.Length == 0)
        {
            return;
        }

        Change(data =>
        {
            PhoneData phone = data.phones.Find(p => p.id == phoneId);
            if (phone != null)
            {
                phone.label = clean;
            }
        });
    }

    public static void SetPhoneAddress(string phoneId, string host, int port)
    {
        Phone known = Current.Find(phoneId);
        if (known == null || (known.host == host && known.port == port))
        {
            return;
        }

        Change(data =>
        {
            PhoneData phone = data.phones.Find(p => p.id == phoneId);
            if (phone != null)
            {
                phone.host = host;
                phone.port = port;
            }
        });
    }

    public static void RemovePhone(string phoneId)
    {
        Change(data => data.phones.RemoveAll(p => p.id == phoneId));
    }

    // QR 비밀은 한 번 쓰면 새로 만듭니다(PROTOCOL.md 2장).
    public static void RenewQrSecret()
    {
        Change(data => data.qrSecret = PlayLinkCrypto.Base64Url(PlayLinkCrypto.RandomBytes(QR_SECRET_BYTES)));
    }

    private static string DefaultLabel(string phoneName)
    {
        string clean = PlayLinkCrypto.CleanName(phoneName);
        return clean.Length > 0 ? clean : "폰";
    }

    private static void Change(Action<FileData> edit)
    {
        lock (gate)
        {
            FileData data = ToData(Current);
            edit(data);
            current = ToSnapshot(data);
            Save(data);
        }

        onChanged?.Invoke();
    }

    private static Snapshot Load()
    {
        LastError = null;
        string path = FilePath;
        FileData data = null;
        try
        {
            if (File.Exists(path))
            {
                byte[] plain = Dpapi(File.ReadAllBytes(path), false);
                data = JsonUtility.FromJson<FileData>(Encoding.UTF8.GetString(plain));
            }
        }
        catch (Exception e)
        {
            // 다른 Windows 사용자의 파일이거나 깨진 파일: 옆에 남겨 두고 새로 만듭니다(폰은 다시 페어링해야 함).
            LastError = "저장된 페어링 정보를 읽지 못해 새로 만들었습니다(" + e.GetType().Name + "). 폰을 다시 페어링하세요.";
            try
            {
                File.Copy(path, path + ".bad", true);
            }
            catch (Exception)
            {
            }

            data = null;
        }

        bool fresh = data == null || !PlayLinkCrypto.IsId(data.pcId);
        if (fresh)
        {
            data = new FileData { pcId = PlayLinkCrypto.NewId() };
        }

        bool changed = fresh;
        if (string.IsNullOrEmpty(PlayLinkCrypto.CleanName(data.pcName)))
        {
            string machine = PlayLinkCrypto.CleanName(Environment.MachineName);
            data.pcName = machine.Length > 0 ? machine : "PC";
            changed = true;
        }

        byte[] secret = PlayLinkCrypto.FromBase64Url(data.qrSecret);
        if (secret == null || secret.Length != QR_SECRET_BYTES)
        {
            data.qrSecret = PlayLinkCrypto.Base64Url(PlayLinkCrypto.RandomBytes(QR_SECRET_BYTES));
            changed = true;
        }

        if (data.phones == null)
        {
            data.phones = new List<PhoneData>();
        }

        if (changed)
        {
            Save(data);
        }

        return ToSnapshot(data);
    }

    private static void Save(FileData data)
    {
        string path = FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] sealedBytes = Dpapi(Encoding.UTF8.GetBytes(JsonUtility.ToJson(data)), true);
            // 쓰다가 끊겨도 예전 파일이 남도록 옆에 쓴 뒤 바꿉니다.
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, sealedBytes);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
        }
        catch (Exception e)
        {
            // 저장하지 못해도 이번 에디터 세션 동안은 메모리의 값으로 동작합니다.
            LastError = "페어링 정보를 저장하지 못했습니다(" + e.GetType().Name + "). Unity 를 다시 켜면 폰을 다시 페어링해야 합니다.";
        }
    }

    private static Snapshot ToSnapshot(FileData data)
    {
        List<Phone> phones = new List<Phone>();
        foreach (PhoneData phone in data.phones)
        {
            byte[] key = PlayLinkCrypto.FromBase64Url(phone.key);
            if (PlayLinkCrypto.IsId(phone.id) && key != null && key.Length == PlayLinkCrypto.KEY_BYTES)
            {
                phones.Add(new Phone(phone.id, DefaultLabel(phone.label), key, phone.host, phone.port));
            }
        }

        return new Snapshot
        {
            pcId = data.pcId,
            pcName = data.pcName,
            qrSecret = PlayLinkCrypto.FromBase64Url(data.qrSecret),
            phones = phones.ToArray(),
        };
    }

    private static FileData ToData(Snapshot snapshot)
    {
        FileData data = new FileData
        {
            pcId = snapshot.pcId,
            pcName = snapshot.pcName,
            qrSecret = PlayLinkCrypto.Base64Url(snapshot.qrSecret),
        };
        foreach (Phone phone in snapshot.phones)
        {
            data.phones.Add(new PhoneData { id = phone.id, label = phone.label, key = PlayLinkCrypto.Base64Url(phone.key), host = phone.host, port = phone.port });
        }

        return data;
    }

    // DPAPI(CurrentUser). Unity 의 Mono 에는 ProtectedData(System.Security.dll)가 참조돼 있지 않아 crypt32 를 직접 부릅니다.
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int size;
        public IntPtr data;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    // protect 가 true 면 감싸고 false 면 풉니다. 실패하면 예외(다른 사용자의 파일이거나 손상).
    public static byte[] Dpapi(byte[] bytes, bool protect)
    {
        GCHandle inputHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(ENTROPY, GCHandleType.Pinned);
        Blob output = default(Blob);
        try
        {
            Blob input = new Blob { size = bytes.Length, data = inputHandle.AddrOfPinnedObject() };
            Blob entropy = new Blob { size = ENTROPY.Length, data = entropyHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok)
            {
                throw new CryptographicException("DPAPI 실패(오류 " + Marshal.GetLastWin32Error() + ")");
            }

            byte[] result = new byte[output.size];
            Marshal.Copy(output.data, result, 0, output.size);
            return result;
        }
        finally
        {
            if (output.data != IntPtr.Zero)
            {
                LocalFree(output.data);
            }

            inputHandle.Free();
            entropyHandle.Free();
        }
    }
}
