using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// 자체 점검: 연결 프로토콜의 암호 계산이 기준 구현(tools/playlink-proto.js)과 같은 값을 내는지 확인합니다.
//   메뉴 Tools > PlayLink > Self Test
//   batchmode: Unity.exe -batchmode -nographics -projectPath <프로젝트> -executeMethod PlayLinkSelfTest.RunBatch -logFile <로그>   (통과 0, 실패 1)
// 아래 값은 tools/proto-vectors.json 의 고정 시험 값을 그대로 옮긴 것입니다(실제 키가 아님).
public static class PlayLinkSelfTest
{
    private const string PRIV_I = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string PRIV_R = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string PUB_I = "040217e617f0b6443928278f96999e69a23a4f2c152bdf6d6cdf66e5b80282d4ed194a7debcb97712d2dda3ca85aa8765a56f45fc758599652f2897c65306e5794";
    private const string PUB_R = "04d65a93977caa3d1b081852ff57a79e465f1660577304baead505dd3a48589cf350185e895372df6221ea3a137557e473fddb6755f05bd507c3c533fce9c91285";
    private const string Z = "ccfc261f58193c98ca4ad4a53bbac6f0ee29bc4d48438090446908622ca79af6";
    private const string M1 = "{\"t\":\"hello\",\"v\":1,\"pc\":\"AAAAAAAAAAAAAAAA\"}";
    private const string M2 = "{\"t\":\"hello\",\"v\":1,\"ph\":\"BBBBBBBBBBBBBBBB\"}";
    private const string M3 = "{\"t\":\"key\"}";
    private const string TH = "7e343fa5c420680f003e723354da0bc44330250a4f1e86824c893ff80d8988b1";
    // 방식, 비밀, encI2R, macI2R, encR2I, macR2I, pair, 숫자
    private static readonly string[][] MODES =
    {
        new[] { "sas", "", "ec202dd40f5a7a0a777c40323d2664e43b2ebbee0200a1a6623d70f86b1b429b", "488a841028146430412675d1d596422b219c763a6e288b742f858b1aa35b56f1", "c9a6b9a6223c63b83f878e3bd857e9374b63f537aae90984a786c7eee57b2f24", "2f7c170ed27b341fb941fbff9ae37d97bf91cd6d277b61bd14508e2ca6be1137", "0a89c58ae9eca7f3d5affaa2cf6d9fce54e10c73cf1d4bcd1dd2ecb7c342a944", "082694" },
        new[] { "qr", "44444444444444444444444444444444", "525a23cb3ce00455c18b647b65b602daaa4c8cb26c826c33edbd5acfaff3783e", "75926289df5e80766d26488ecd1784e744b2235c19cde15a1c005ec869763399", "ea8ecc856beef7ab21956b5ba91c1bd363f2c34406b654bd46972423897156fc", "d0dff2039c280bfeecad84ee03075067ccc8b96bf75853cffec2be58445de035", "c0d96073d294a86b1e18a370a93d66933010d90ffea3abdd71a81b199d877f7f", "125262" },
        new[] { "paired", "3333333333333333333333333333333333333333333333333333333333333333", "faabd61a425586ebe5a62aa3bdcf33491aee9c993eb7abf9e744046ad9244172", "8e8f0aba48aaa6387c442214f082951548de289355c70350142bf873d8a2c3e7", "5ac60316862ebef9e50e5660c65725bb9925638591a2f0386458d0f8c4476df2", "97978ab1f5e836c5c9c8a02942755402c123684a84a6dd60d723f11dfc8aa1d6", "2f3f81b7515023e3124f327d5b7ab05f4c36945184232073bc0b5e0ccb0d05a6", "883657" },
    };
    private const string RECORD_PLAIN = "506c61794c696e6b207265636f7264207465737420766563746f722c2034302062797465732e2e21";
    private const string RECORD_SEQ0 = "0028f9fe917f6757ebe1074fd2879364ab9c62ccaac63991c832264a83e783d783996c7a2e230270158348247fa762ad58346aa541342dcbd973";
    private const string RECORD_SEQ5 = "00281cf51ebeebcfdb06f110c839d865aae269c170fe46f958522216a35baf013143e04987967dce19058263b3fd3222b0a7699fbe4bc5b8deba";
    private const string DISC_KEY = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string DISC_PC = "AAAAAAAAAAAAAAAA";
    private const string DISC_PH = "BBBBBBBBBBBBBBBB";
    private const string DISC_NAME = "Pixel 8";
    private const int DISC_PORT = 7300;
    private const bool DISC_WANT = true;
    private const string DISC_N = "AAECAwQFBgc";
    private const string DISC_MAC = "LfoKISQg8-O0--MsXKaefA";

    [MenuItem("Tools/PlayLink/Self Test", false, 40)]
    public static void RunMenu()
    {
        string report;
        if (Run(out report))
        {
            Debug.Log("[PlayLink] 자체 점검 통과\n" + report);
        }
        else
        {
            Debug.LogError("[PlayLink] 자체 점검 실패\n" + report);
        }
    }

    public static void RunBatch()
    {
        string report;
        bool passed = Run(out report);
        Debug.Log("[PlayLink] SELFTEST " + (passed ? "PASS" : "FAIL") + "\n" + report);
        EditorApplication.Exit(passed ? 0 : 1);
    }

    // 점검 결과를 한 줄씩 적습니다. 값(키)은 적지 않고 항목 이름과 통과 여부만 적습니다.
    public static bool Run(out string report)
    {
        StringBuilder log = new StringBuilder();
        int passed = 0;
        int failed = 0;
        Action<string, Func<bool>> check = (name, test) =>
        {
            bool ok;
            try
            {
                ok = test();
            }
            catch (Exception e)
            {
                ok = false;
                name += " (" + e.GetType().Name + ")";
            }

            if (ok)
            {
                passed++;
            }
            else
            {
                failed++;
            }

            log.Append(ok ? "  ok   " : "  FAIL ").Append(name).Append('\n');
        };

        byte[] privI = PlayLinkCrypto.HexToBytes(PRIV_I);
        byte[] privR = PlayLinkCrypto.HexToBytes(PRIV_R);
        byte[] pubI = PlayLinkCrypto.HexToBytes(PUB_I);
        byte[] pubR = PlayLinkCrypto.HexToBytes(PUB_R);
        byte[] z = PlayLinkCrypto.HexToBytes(Z);
        check("P-256 공개 키(PC 쪽)", () => Same(PlayLinkCrypto.PublicKey(privI), pubI));
        check("P-256 공개 키(폰 쪽)", () => Same(PlayLinkCrypto.PublicKey(privR), pubR));
        check("ECDH 공유 값(PC 쪽 계산)", () => Same(PlayLinkCrypto.SharedSecret(privI, pubR), z));
        check("ECDH 공유 값(폰 쪽 계산)", () => Same(PlayLinkCrypto.SharedSecret(privR, pubI), z));
        check("새 키 쌍끼리 공유 값이 같음", () =>
        {
            byte[] a = PlayLinkCrypto.NewPrivateKey();
            byte[] b = PlayLinkCrypto.NewPrivateKey();
            return Same(PlayLinkCrypto.SharedSecret(a, PlayLinkCrypto.PublicKey(b)), PlayLinkCrypto.SharedSecret(b, PlayLinkCrypto.PublicKey(a)));
        });
        check("곡선 위에 없는 점을 거부", () =>
        {
            byte[] bad = (byte[])pubR.Clone();
            bad[64] ^= 1;
            return Throws(() => PlayLinkCrypto.SharedSecret(privI, bad));
        });
        check("길이가 틀린 공개 키를 거부", () => Throws(() => PlayLinkCrypto.SharedSecret(privI, new byte[33])));
        check("0 으로 채운 공개 키를 거부", () =>
        {
            byte[] zero = new byte[PlayLinkCrypto.POINT_BYTES];
            zero[0] = 4;
            return Throws(() => PlayLinkCrypto.SharedSecret(privI, zero));
        });

        byte[] th = PlayLinkCrypto.TranscriptHash(Encoding.UTF8.GetBytes(M1), Encoding.UTF8.GetBytes(M2), Encoding.UTF8.GetBytes(M3));
        check("주고받은 내용의 해시(th)", () => Same(th, PlayLinkCrypto.HexToBytes(TH)));
        PlayLinkCrypto.Keys paired = null;
        foreach (string[] mode in MODES)
        {
            string name = mode[0];
            byte[] secret = mode[1].Length > 0 ? PlayLinkCrypto.HexToBytes(mode[1]) : null;
            PlayLinkCrypto.Keys keys = PlayLinkCrypto.DeriveKeys(th, z, PlayLinkCrypto.Psk(name, secret));
            if (name == "paired")
            {
                paired = keys;
            }

            check(name + ": enc i2r", () => Same(keys.encI2R, PlayLinkCrypto.HexToBytes(mode[2])));
            check(name + ": mac i2r", () => Same(keys.macI2R, PlayLinkCrypto.HexToBytes(mode[3])));
            check(name + ": enc r2i", () => Same(keys.encR2I, PlayLinkCrypto.HexToBytes(mode[4])));
            check(name + ": mac r2i", () => Same(keys.macR2I, PlayLinkCrypto.HexToBytes(mode[5])));
            check(name + ": pair", () => Same(keys.pair, PlayLinkCrypto.HexToBytes(mode[6])));
            check(name + ": 6자리 숫자", () => keys.sas == mode[7]);
        }

        byte[] plain = PlayLinkCrypto.HexToBytes(RECORD_PLAIN);
        byte[] seq0 = PlayLinkCrypto.HexToBytes(RECORD_SEQ0);
        byte[] seq5 = PlayLinkCrypto.HexToBytes(RECORD_SEQ5);
        check("레코드 싸기(seq 0)", () => Same(Seal(paired, plain, 0), seq0));
        check("레코드 싸기(seq 5)", () => Same(Seal(paired, plain, 5), seq5));
        check("레코드 풀기(seq 0)", () =>
        {
            using (PlayLinkSecureStream stream = new PlayLinkSecureStream(new MemoryStream(seq0), paired.encR2I, paired.macR2I, paired.encI2R, paired.macI2R))
            {
                return Same(stream.ReadMessage(), plain);
            }
        });
        check("고친 레코드를 거부(암호문 1비트)", () =>
        {
            byte[] bad = (byte[])seq0.Clone();
            bad[5] ^= 1;
            return Throws(() => new PlayLinkSecureStream(new MemoryStream(bad), paired.encR2I, paired.macR2I, paired.encI2R, paired.macI2R).ReadMessage());
        });
        check("고친 레코드를 거부(태그 1비트)", () =>
        {
            byte[] bad = (byte[])seq0.Clone();
            bad[bad.Length - 1] ^= 1;
            return Throws(() => new PlayLinkSecureStream(new MemoryStream(bad), paired.encR2I, paired.macR2I, paired.encI2R, paired.macI2R).ReadMessage());
        });
        check("순서가 바뀐 레코드를 거부(seq 5 를 처음에)", () =>
            Throws(() => new PlayLinkSecureStream(new MemoryStream(seq5), paired.encR2I, paired.macR2I, paired.encI2R, paired.macI2R).ReadMessage()));
        check("다른 키로는 풀리지 않음", () =>
            Throws(() => new PlayLinkSecureStream(new MemoryStream(seq0), paired.encR2I, paired.macR2I, paired.encR2I, paired.macR2I).ReadMessage()));
        check("큰 쓰기(100000바이트)를 여러 레코드로 보내고 그대로 받음", () =>
        {
            byte[] big = PlayLinkCrypto.RandomBytes(100000);
            MemoryStream wire = new MemoryStream();
            using (PlayLinkSecureStream writer = new PlayLinkSecureStream(wire, paired.encI2R, paired.macI2R, paired.encR2I, paired.macR2I))
            {
                writer.Write(big, 0, 1);
                writer.Write(big, 1, big.Length - 1);
            }

            byte[] sent = wire.ToArray();
            if (sent.Length <= big.Length + 7 * (2 + PlayLinkCrypto.TAG_BYTES))
            {
                return false;
            }

            byte[] back = new byte[big.Length];
            using (PlayLinkSecureStream reader = new PlayLinkSecureStream(new MemoryStream(sent), paired.encR2I, paired.macR2I, paired.encI2R, paired.macI2R))
            {
                int got = 0;
                while (got < back.Length)
                {
                    int read = reader.Read(back, got, Math.Min(7001, back.Length - got));
                    if (read <= 0)
                    {
                        return false;
                    }

                    got += read;
                }

                return Same(back, big) && reader.Read(back, 0, 1) == 0;
            }
        });

        byte[] discoveryKey = PlayLinkCrypto.HexToBytes(DISC_KEY);
        check("탐색 응답의 증명", () => PlayLinkCrypto.DiscoveryMac(discoveryKey, DISC_PC, DISC_PH, DISC_NAME, DISC_PORT, DISC_WANT, DISC_N) == DISC_MAC);
        check("증명은 값이 하나만 달라도 달라짐", () => PlayLinkCrypto.DiscoveryMac(discoveryKey, DISC_PC, DISC_PH, DISC_NAME, DISC_PORT, !DISC_WANT, DISC_N) != DISC_MAC
            && PlayLinkCrypto.DiscoveryMac(discoveryKey, DISC_PC, DISC_PH, DISC_NAME, DISC_PORT + 1, DISC_WANT, DISC_N) != DISC_MAC);
        check("고정 시간 비교", () => PlayLinkCrypto.FixedTimeEquals(DISC_MAC, DISC_MAC) && !PlayLinkCrypto.FixedTimeEquals(DISC_MAC, DISC_MAC.Substring(1) + "A")
            && !PlayLinkCrypto.FixedTimeEquals(DISC_MAC, "short") && !PlayLinkCrypto.FixedTimeEquals(DISC_MAC, null));
        check("base64url 왕복", () =>
        {
            byte[] bytes = PlayLinkCrypto.RandomBytes(33);
            return Same(PlayLinkCrypto.FromBase64Url(PlayLinkCrypto.Base64Url(bytes)), bytes) && PlayLinkCrypto.FromBase64Url("a+b/") == null;
        });
        check("새 ID 의 모양", () => PlayLinkCrypto.IsId(PlayLinkCrypto.NewId()) && !PlayLinkCrypto.IsId("short") && PlayLinkCrypto.NewId() != PlayLinkCrypto.NewId());
        check("DPAPI(이 Windows 사용자) 왕복", () =>
        {
            byte[] secret = PlayLinkCrypto.RandomBytes(200);
            byte[] wrapped = PlayLinkIdentity.Dpapi(secret, true);
            return !Same(wrapped, secret) && wrapped.Length > secret.Length && Same(PlayLinkIdentity.Dpapi(wrapped, false), secret);
        });
        check("DPAPI: 고친 내용은 풀리지 않음", () =>
        {
            byte[] wrapped = PlayLinkIdentity.Dpapi(new byte[64], true);
            wrapped[wrapped.Length - 3] ^= 1;
            return Throws(() => PlayLinkIdentity.Dpapi(wrapped, false));
        });

        log.Append("통과 ").Append(passed).Append(", 실패 ").Append(failed);
        report = log.ToString();
        return failed == 0;
    }

    // seq 번째 레코드로 싼 바이트(len ‖ 암호문 ‖ 태그)
    private static byte[] Seal(PlayLinkCrypto.Keys keys, byte[] plain, int seq)
    {
        MemoryStream wire = new MemoryStream();
        using (PlayLinkSecureStream stream = new PlayLinkSecureStream(wire, keys.encI2R, keys.macI2R, keys.encR2I, keys.macR2I))
        {
            long start = 0;
            for (int i = 0; i <= seq; i++)
            {
                start = wire.Length;
                stream.Write(plain, 0, plain.Length);
            }

            byte[] all = wire.ToArray();
            byte[] last = new byte[all.Length - start];
            Buffer.BlockCopy(all, (int)start, last, 0, last.Length);
            return last;
        }
    }

    private static bool Same(byte[] a, byte[] b)
    {
        return a != null && b != null && a.Length == b.Length && PlayLinkCrypto.FixedTimeEquals(a, 0, b, 0, a.Length);
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
