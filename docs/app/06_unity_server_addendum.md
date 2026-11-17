# 06. Unity 서버 쪽 addendum (워커 U1)

- 작성: 2026-11-14 (6단계 워커 U1). 설계 문서 `03_design.md` 는 워커 A 소유라 고치지 않았고, 설계에서 벗어나거나 설계가 비워 둔 것을 정한 내용은 전부 이 문서에 적습니다.
- 이 문서의 1장은 **워커 U2(창과 QR)가 코드를 맞춰 쓰는 인터페이스**입니다. 구현보다 먼저 적었고, 구현이 끝난 뒤 실제 코드와 다시 대조했습니다(달라진 것은 1.4절에 표시).

## 0. 진행 메모 (U1이 중단됐다 이어 갈 때 보는 곳)

> **정정(2026-11-14, 코드 리뷰 4.1 반영, Unity 쪽 수정 워커):** 아래 메모와 3.1절에 적힌 시각(01:00~02:20)은 PC 시계를 보지 않고 어림으로 적은 것이라 **실제보다 약 1시간 앞서 있습니다**. 파일 수정 시각으로 보면 이 작업은 00:48~01:23 사이에 있었습니다. 사건의 순서는 맞지만 시각은 근거로 쓰지 마세요.
> 3.1절의 폰 주소로의 `adb connect` 시도 횟수 "3번"도 틀렸습니다. **`Editor.log` 에는 7번** 있습니다(전부 "10초 안에 끝나지 않았습니다"로 끝났고 폰은 연결되지 않았습니다). 로그에 시각이 없어 7번 중 몇 번이 소유자 지시(폰 사용 중단) 뒤였는지는 가를 수 없습니다. 주소를 비운 뒤로는 시도가 없습니다.
> 5장의 Chrome 페이지 회귀는 수정 뒤 `wsclient.js` 네 가지로 다시 돌렸습니다. 결과는 `08_fixes_unity.md` 3장.


- [x] 1단계 01:00 — 새 파일 `AppInfo, Auth, Ws, Settings, Net, Files, Gate(+Session), Channels, Seats` 작성. 기존 파일 고리(`Server`: S1~S11, `GameViewRemote`: AllowLan 무력화·AdoptClient·CloseAppClients·입력 연결 수를 Seats 로, `Input`: 한 글자 검사) 적용. 적용 스크립트: scratchpad `hooks.py`(다시 돌리면 MISMATCH 로 멈춤 = 이미 적용됨).
- [x] 2단계 01:05 — 컴파일 통과, Self Test 벡터 4개 일치(MCP execute_code 로 RunSelfTest)
- [x] 3단계 01:15 — `GameViewRemoteHost.cs`, `GameViewRemoteFirewall.cs`, `GameViewRemoteLanding.html` 작성, `GameViewRemote.Start` 끝에 `GameViewRemoteHost.OnServerStarted()` 고리(컴파일 확인은 다음)
- [x] 3단계 컴파일 확인 완료(오류 0). Host 를 MCP execute_code 로 Arm(192.168.25.5:7300 리슨, 원래는 꺼져 있었음 → 끝나면 Disarm 뒤 `GameViewRemote.Start()` 로 기존 서버를 되살릴 것. Disarm 이 기존 서버도 멈추기 때문)
- [x] 4단계 01:40 — `GameViewRemoteApp/tools/unity-host/hosttest.js` 작성·실행: vectors 6/6, reject 9/9, paths 13/13(APK 없음 상태), auth 9/9(H.264 216장/4초), abuse 8/8, limits 6/6, legacy 14/14, seats 19/19, lobby 6/6(4410), hold(새 토큰 → 4403) 통과. 명령: `node hosttest.js <이름> --secret <catjump>/Library/GameViewRemote/secret.json`
- [x] 4단계 나머지 02:10 — prefer1/prefer2(선호가 리로드를 넘어 남는지) 통과, 자리 표시 APK 로 /apk 200 확인 뒤 자리 표시 파일 삭제
- 주의(01:45 발견): 기존 서버는 Start 할 때마다(Play 시작, Play 중 도메인 리로드, 내가 부른 Stop→Start) `AutoReverseAsync` 가 저장된 무선 주소(EditorPrefs `GVR_WirelessAddress` = 폰 주소)로 `adb connect` 를 시도함. 콘솔에 3번 찍혔고 전부 10초 시간 초과(연결 안 됨). 소유자 지시(폰에 adb connect 금지)와 부딪혀 코디네이터에게 물음 → 02:00 지시대로 EditorPrefs 를 비움(3.1절).
- [x] 5단계 02:05 — `GameViewRemoteAppAdb.cs`, `GameViewRemoteAdb.Run` 을 internal 로, 컴파일 오류 0
- [x] 6단계 02:20 — 본문(1.5절, 2~7장) 작성. 남은 것은 worker_done 보고뿐

## 1. U2가 쓰는 C# API (먼저 읽으세요)

### 1.1 파일 소유

U1이 만드는 새 파일(U2는 같은 이름의 파일을 만들지 마세요. 같은 이름의 클래스가 둘이면 컴파일이 깨집니다):

| 파일 | 클래스 | 내용 |
|---|---|---|
| `GameViewRemoteAppInfo.cs` | `GameViewRemoteAppInfo` | 패키지 이름, 스킴, 기능 목록, 버전 상수 |
| `GameViewRemoteAuth.cs` | `GameViewRemoteAuth` | HMAC 인증 계산, base64url, 헤더 해석, Self Test 메뉴 |
| `GameViewRemoteWs.cs` | `GameViewRemoteWs` | 작은 WebSocket 도우미 |
| `GameViewRemoteSettings.cs` | `GameViewRemoteSettings` | `UserSettings/GameViewRemote.json`(비밀 아님)과 `Library/GameViewRemote/secret.json`(서버 표시, 토큰) |
| `GameViewRemoteNet.cs` | `GameViewRemoteNet` | 어댑터 나열, 주 주소와 후보 선택 |
| `GameViewRemoteChannels.cs` | `GameViewRemoteChannels` | `/x/<이름>` 채널 등록 표(비어 있음) |
| `GameViewRemoteGate.cs` | `GameViewRemoteGate`, `GameViewRemoteSession` | 입장 심사(인증, Origin, 인증 전 제한) |
| `GameViewRemoteSeats.cs` | `GameViewRemoteSeats` | 자리 배정 |
| `GameViewRemoteFiles.cs` | `GameViewRemoteFiles` | 착륙 페이지, APK, `app.json` 내주기 |
| `GameViewRemoteHost.cs` | `GameViewRemoteHost` | 앞단(리스너, 대기실, 수명) |
| `GameViewRemoteFirewall.cs` | `GameViewRemoteFirewall` | 방화벽 상태 읽기, 허용/되돌리기 함수(워커는 부르지 않음) |
| `GameViewRemoteAppAdb.cs` | `GameViewRemoteAppAdb` | USB로 앱 설치, USB로 연결, 연결 점검 |
| `GameViewRemoteLanding.html` | — | 착륙 페이지 |
| `App~/` | — | `GameViewRemote.apk`, `app.json` 자리 |

U2 소유(이 문서 기준): `GameViewRemoteWindow.cs`, `GameViewRemotePage.html`, `GameViewRemoteQr.cs`, `GameViewRemoteHostGUI.cs`, `ThirdParty/QrCodeGenerator/`.

### 1.2 `GameViewRemoteHost` (public static)

```csharp
// --- Start / Stop ---
public static bool Armed { get; }            // "Start 켜짐". SessionState 에 있어 도메인 리로드를 넘어 유지됨. 메인 스레드 전용
public static void Arm();                    // 창의 [Start]. 리스너를 열고, Play 중이면 GameViewRemote.Start() 도 부름. 메인 스레드
public static void Disarm();                 // 창의 [Stop]. 리스너와 대기실을 닫고 GameViewRemote.Stop(). 메인 스레드
public static bool IsListening { get; }      // QR 주소에 실제로 듣고 있는가(아무 스레드)
public static string LastError { get; }      // 리스너를 못 열었을 때의 이유(한국어). 정상이면 null

// --- QR 에 넣을 것 ---
public static int Port { get; }              // 듣는 포트(= GameViewRemote.Port)
public static string PrimaryAddress { get; } // 주 주소(IPv4). 없으면 null
public static string[] ExtraAddresses { get; }   // QR 의 a= 에 들어가는 주소(0~3개)
public static string[] SelectableAddresses { get; } // 창의 [▼ 다른 주소] 목록(제외되지 않은 주소 전부)
public static string SelectedAddress { get; set; }  // "" = 자동. 바꾸면 리스너를 다시 엶. 메인 스레드
public static string ServerId { get; }       // 서버 표시 8자(없으면 null)
public static string BuildQrPayload();       // 설계 4.1 의 문자열 그대로. 듣고 있지 않으면 null. **토큰이 들어 있으니 로그에 찍지 마세요**
public static string BuildDeepLink(string host, bool usb, bool take);  // 설계 4.2 의 seeplay-gvr://… (adb 검증용)
public static int Revision { get; }          // QR 내용이 바뀔 수 있는 일(Arm/Disarm, 주소 변경, 새 토큰)이 있을 때마다 1 증가. QR 텍스처를 다시 만들지 판단하는 데 씀
public static event Action Changed;          // Revision 이 올랐거나 접속 기기 목록이 바뀜. 메인 스레드에서 불림(창은 Repaint 만 하세요)
public static void RenewToken();             // [새 QR 발급]. 토큰을 바꾸고 붙어 있는 앱 연결을 전부 4403 으로 닫음. 메인 스레드

// --- 표시용 ---
public static string StatusText { get; }     // 상태 한 줄(한국어): "꺼짐" / "대기 중(Play 를 누르면 화면이 나갑니다)" / "연결됨: …"
public static List<GameViewRemoteSeats.Viewer> Viewers { get; }  // 접속 기기 목록(복사본). 대기실 포함
public static bool AutoArm { get; set; }     // 에디터를 켤 때 자동으로 Start(설정 파일). 메인 스레드

// --- APK ---
public static string ApkPath { get; }        // 내줄 APK 의 전체 경로(파일이 없어도 경로는 돌려줌)
public static bool ApkExists { get; }
public static int ApkVersionCode { get; }    // app.json 의 값. 없으면 0
public static string ApkVersionName { get; } // 없으면 ""
```

```csharp
public struct GameViewRemoteSeats.Viewer
{
    public string group;       // "page" 또는 "app:<did>"
    public string deviceName;  // 앱의 dev. 페이지는 "Chrome 페이지"
    public string remote;      // 원격 주소(IP:포트)
    public bool usb;           // 루프백으로 들어옴(adb reverse)
    public int appVersion;     // 앱의 versionCode. 페이지는 0
    public string state;       // "active" | "standby" | "lobby"
    public bool video, input;  // 그 무리에 영상/입력 소켓이 붙어 있는가
}
```

### 1.3 "Wi-Fi 접속 허용" 토글 (브리프 3.3 의 1번)

- 인증 없는 LAN 리슨은 없앴습니다. 기존 서버는 이제 **항상 `127.0.0.1` 에만** 엽니다.
- `GameViewRemote.AllowLan` 속성은 U2의 창이 컴파일되도록 **남겨 두었지만 항상 `false` 를 돌려주고 `set` 은 아무 일도 하지 않습니다**(저장돼 있던 `GVR_AllowLan` 값은 지움). `GameViewRemoteServer.AllowLan` 도 항상 `false` 입니다.
- U2가 할 일: `GameViewRemoteWindow.cs` 의 토글(106~108행)과 그 값을 읽는 곳(183행의 "0.0.0.0, Wi-Fi 허용" 문구, 251행의 Wi-Fi URL 목록)을 없애고, 그 자리에 "같은 Wi-Fi 연결은 위의 QR(Start)로 합니다. Chrome 페이지는 USB(adb reverse)로만 열립니다."를 적어 주세요. 창에서 참조를 다 없앤 뒤에는 `GameViewRemote.AllowLan` 을 지워도 됩니다(통합 단계).

### 1.4 그 밖의 클래스

```csharp
// 방화벽(설계 9.7). 읽기는 관리자 권한이 필요 없고 백그라운드에서 돎. 워커는 RequestAllow/RequestRevert 를 부르지 않았음.
public static class GameViewRemoteFirewall
{
    public enum Verdict { Unknown, Checking, Blocked, Allowed, WillPrompt, Off, Unsupported }
    public static Verdict State { get; }
    public static string Message { get; }                // 창에 보일 한 줄(한국어)
    public static void RefreshAsync();                   // 상태를 다시 읽음(이미 읽는 중이면 건너뜀)
    public static string DescribeAllow();                // [방화벽 허용…] 을 누르기 전 대화 상자에 보일 "무엇을 바꾸는지"
    public static void RequestAllow();                   // UAC 창 1번. 버튼에서만 부르세요
    public static void RequestRevert();                  // [되돌리기]. UAC 창 1번
    public static bool HasBackup { get; }                // 되돌릴 것이 있는가
}

// USB 버튼(설계 9.8). 전부 백그라운드에서 돌고 결과는 LastOutput 에 남음(토큰은 가려서 적음).
public static class GameViewRemoteAppAdb
{
    public static bool Busy { get; }
    public static string LastOutput { get; }
    public static void InstallAsync();        // [USB 로 앱 설치]
    public static void ConnectUsbAsync();     // [USB 로 연결]
    public static void CheckReachAsync();     // [연결 점검]: 폰에서 PC 의 QR 주소로 TCP 가 닿는가
}

// 메뉴: Tools/Game View Remote/Self Test  (인증 시험 벡터 4개 + 헤더 해석)
```

QR 을 그릴 때 U2가 지킬 것: `BuildQrPayload()` 가 null 이면 QR 을 그리지 않고 `LastError`(있으면) 또는 "Start 를 누르면 QR 이 생깁니다"를 보입니다. 토큰이 든 문자열(`BuildQrPayload`, `BuildDeepLink`)은 `Debug.Log` 나 창의 글자로 내보내지 않습니다(QR 이미지와 `Temp/GameViewRemote/qr.png` 만).

### 1.5 구현 뒤 대조에서 달라진 것

- `GameViewRemoteHost.PhoneWifiAddress { get; set; }`(추가): [연결 점검]이 읽은 기기의 Wi-Fi 주소. 주 주소 선택의 1순위(같은 서브넷)에 씁니다. 창은 쓸 필요 없습니다.
- `GameViewRemoteHost.OnServerStarted()`(추가, public): `GameViewRemote.Start()` 끝에서 부르는 고리. 창은 부르지 않습니다.
- `GameViewRemoteHost.Post(Action)`, `GameViewRemoteFirewall.RefreshAsyncFromAnyThread()`(internal): 내부용.
- `GameViewRemoteAppAdb` 는 시험 기간 동안 **에뮬레이터(일련번호 `emulator-…`)만** 대상으로 삼습니다(3.2절). 실제 폰이 꽂혀 있으면 `LastOutput` 에 "실제 기기(시험 기간에는 에뮬레이터만 씁니다)"라고 나옵니다.
- 그 밖의 이름과 형은 1.2~1.4절 그대로입니다. U2의 `GameViewRemoteHostGUI.cs` 반사 어댑터가 찾는 이름과 맞는 것을 확인했습니다(`Viewer` 는 public 필드, 어댑터는 필드를 먼저 찾음).

## 2. 설계에서 정한 것, 벗어난 것

### 2.1 파일을 설계보다 잘게 나눈 것

| 설계 | 구현 | 이유 |
|---|---|---|
| `GameViewRemoteHost.cs` 가 파일 내주기까지 | `GameViewRemoteFiles.cs` 로 분리 | 기존 서버(루프백)도 `/c`, `/apk`, `/app.json` 을 내줘야 하는데 Server → Host 의존을 만들지 않으려고 |
| 세션 정보의 위치 미정 | `GameViewRemoteSession` 을 `GameViewRemoteGate.cs` 에 | 심사 결과물이라서. 서버는 `Connection.tag`(object)로만 들고 있음 |
| 설정과 비밀을 따로 | `GameViewRemoteSettings.cs` 하나가 두 파일(`UserSettings/GameViewRemote.json`, `Library/GameViewRemote/secret.json`)을 다룸 | 둘 다 메인 스레드에서 읽고 스냅숏을 넘기는 같은 규칙 |
| `app.json` 위치 | **APK 옆**(`apkPath` 를 바꾸면 그 폴더) | APK 와 버전 정보가 따로 놀지 않게 |

### 2.2 동작에서 설계와 다른 것

1. **Origin 검사의 포트**: 설계는 `http://localhost:<포트>` 정확히 일치. 구현은 루프백 연결에서 호스트가 `localhost` 또는 `127.0.0.1` 이면 **포트는 보지 않습니다**(adb reverse/forward 로 포트가 바뀌어 붙는 경우가 있어서). 앞단(LAN 리스너)에서는 `Origin` 헤더가 있기만 하면 403 `bad_origin` 입니다(브라우저는 앱 경로를 쓸 수 없음).
2. **시험 벡터의 sid `GVRTEST1`**: 설계의 서버 표시 형식은 base32 8자(`[A-Z2-7]{8}`)인데 벡터의 `GVRTEST1` 에는 `1` 이 들어 있어 형식에 맞지 않습니다. HMAC 계산 확인에는 문제가 없어(문자열로만 쓰임) 벡터는 그대로 통과시키고, Self Test 의 형식 검사는 `GVRTEST2` 로 합니다. 설계 문서의 주인(A)이 벡터 설명에 "형식 검사용 값은 아님"을 적어 두면 좋습니다.
3. **`did` 검사**: `[a-z0-9]{12}` 가 아니면 400 `bad_request`(설계는 형식만 적고 거절 여부는 비워 둠).
4. **대기에서 보기로 돌아올 때**: 그 연결의 보낼 것(`ordered`/`pending`)을 비우고 `keyframeRequested = true` 로 설정 → 키프레임부터 다시 보냅니다(시험에서 "설정 1, 키 1" 확인).
5. **입력 연결 수**: `GameViewRemoteInput.OnInputConnected/Disconnected` 는 이제 `GameViewRemoteSeats` 만 부릅니다(보는 무리의 입력 소켓만 셈). `GameViewRemote.OnConnected/OnDisconnected` 에서 부르던 줄은 뺐습니다.
6. **`Close` 에서 `Seats.OnLeave` 를 조건 없이** 부릅니다(여러 번 불려도 됨). 넘겨받는 도중 실패한 연결이 자리 표에 남지 않게.
7. **대기실의 상한 16**(설계에 수치 없음). 대기실이 찼거나 리스너가 닫힌 뒤면 1000 으로 닫습니다.
8. **주소 감시**: 5초마다 어댑터를 다시 보고, 주 주소나 고를 수 있는 목록이 바뀌면 리스너를 다시 엽니다(대기실은 4410).
9. **[새 QR 발급]** 은 토큰만 바꾸고 서버 표시(sid)는 그대로 둡니다. 예전 토큰의 앱은 4403 으로 닫힙니다.
10. **`GET`/`HEAD` 가 아닌 요청**(앞단): 405. 인증 헤더 없는 업그레이드: 401. 등록되지 않은 `/x/<이름>`: 404.
11. **선호는 take 로만 바뀝니다**: 보던 무리가 떠나 다른 무리가 이어받아도 선호 값은 그대로라, 보던 기기가 돌아오면(take=0 이어도) 자리를 되찾습니다(시험 `prefer2`).

### 2.3 "Wi-Fi 접속 허용" 제거 (브리프 3.3 의 1)

- 기존 서버는 `IPAddress.Loopback` 에만 엽니다. `GameViewRemoteServer.AllowLan` 은 없앴고 `GameViewRemote.AllowLan` 은 항상 false 인 빈 속성으로만 남겼습니다(창이 컴파일되도록). EditorPrefs `GVR_AllowLan` 은 정적 생성자에서 지웁니다.
- 기존 서버에 루프백이 아닌 주소로 들어온 `GET` 은 404 입니다(지금 구조에서는 일어날 수 없지만 방어).
- U2/통합이 할 일: 1.3절. README 와 개발 문서의 "Wi-Fi 접속 허용" 설명도 "같은 Wi-Fi 는 앱 + QR 로만"으로 고쳐야 합니다.

### 2.4 리뷰 항목 반영

| 항목 | 반영 |
|---|---|
| M1 인증 없는 LAN 경로 | 2.3절. LAN 에서 `/`, `/stats`, `/debug` 404(시험 `paths`) |
| M2 비밀의 위치 | `Library/GameViewRemote/secret.json`(catjump 의 `.gitignore` 가 `Library/` 를 뺌) |
| M3 선호가 재시작을 넘어 유지 | `Seats.Reset()` 은 구성원만 지움. 도메인 리로드 때는 `SessionState GVR_SeatPreferred` 에 저장/복원. Host Stop 과 새 토큰에서만 지움(시험 `prefer1/2`) |
| M4 인증 전 한도 | 연결을 받은 때부터 3초 절대 기한(헤더 + 인증 메시지), 인증 전 연결 16, 인증 대기 8(503), `/apk` 동시 2(503) (시험 `limits`, `abuse`) |
| M6 한 글자 `t` | `GameViewRemoteInput`: `t.Length != 1` 이면 버림 |
| M8 | a/b/c: 넘겨받기 `Adopt`, 대기 연결 건너뛰기, 닫기 코드. d: `GameViewRemoteAdb.Run` 만 internal 로(나머지는 AppAdb 가 직접 구현: 3.2절) |
| m3 | [USB 로 연결]은 토큰을 `--es t` 로 넘기고 출력에서 가림 |
| m4 | 방화벽 판정 문구에 한계를 적고 실제 도달은 [연결 점검] |
| m6 | 넘겨받는 사이 서버가 멈췄으면 닫음(`RunWebSocket` 의 `running` 재확인) |
| m7 | `secret.json` 이 있으면 `Armed` 와 무관하게 읽음(예전 Start 만 쓰는 날에도 USB 앱 인증) |
| m12 | 방화벽 스크립트는 임시 파일 없이 `-EncodedCommand` |
| m15 | 프로젝트 이름은 메인 스레드에서 읽어 둔 값만 네트워크 스레드가 씀 |

## 3. 소유자 지시와 관련해 바꾼 것

### 3.1 저장된 무선 adb 주소를 비움 (코디네이터 지시, 02:00)

- 발견: 기존 서버는 켜질 때마다(Play 시작, Play 중 도메인 리로드, Stop→Start) `GameViewRemoteAdb.AutoReverseAsync` 가, 준비된 기기가 없으면 EditorPrefs `GVR_WirelessAddress` 의 주소로 `adb connect` 를 시도합니다. 값이 실제 폰(`192.168.25.60:5555`)이어서 콘솔에 3번 시도가 찍혔습니다(그중 1번은 내가 대기실 시험에서 부른 Stop→Start, 나머지는 도메인 리로드). **전부 "10초 안에 끝나지 않았습니다"로 끝났고 폰은 연결되지 않았습니다.**
- 조치: 값을 `GVR_WirelessAddress_Backup` 에 복사하고 `GVR_WirelessAddress` 를 지웠습니다. 자동 재연결 코드는 바꾸지 않았습니다.
- 되돌리기(Unity 에서 한 줄): `EditorPrefs.SetString("GVR_WirelessAddress", EditorPrefs.GetString("GVR_WirelessAddress_Backup"));` 또는 창의 무선 연결 칸에서 다시 [연결]하면 저절로 저장됩니다.

### 3.2 내가 넣은 adb 명령의 대상

- `GameViewRemoteAppAdb` 의 모든 명령은 `-s <일련번호>` 를 붙입니다(`devices` 조회만 예외). 대상은 준비된 기기 중 무선이 아닌 것 하나이고, 상수 `EMULATOR_ONLY = true` 인 동안은 `emulator-` 로 시작하는 것만입니다.
- **통합 단계에서 할 일**: 실제 폰에서 쓰기 전에 소유자 확인을 받고 `EMULATOR_ONLY` 를 false 로.
- 설계 9.8 은 "기존 `GameViewRemoteAdb` 의 USB 우선 규칙을 따름"인데, 그 함수(`ReversePreferUsb`)는 연결된 모든 기기에 명령을 보내므로 쓰지 않고 고른 기기 하나에만 `reverse` 를 겁니다. 그래서 M8d 는 `Run` 하나만 internal 로 바꿨습니다.
- 기존 도구의 자동 `adb reverse`(서버 Start 때, 연결된 모든 준비 기기)는 그대로입니다. 02:05 의 리로드 때 `emulator-5554` 에 걸린 것이 콘솔에 있습니다.

## 4. 검증한 것 (명령과 결과)

전부 이 PC에서, 실제 폰 없이 했습니다. Unity 는 Play 중(게임 화면이 움직이는 상태)이었고 게임은 조작하지 않았습니다. Unity 안의 호출은 Unity MCP `execute_code`, 컴파일 확인은 `read_console`.

시험 스크립트: `GameViewRemoteApp/tools/unity-host/hosttest.js` — Unity 코드와 독립으로 쓴 Node 클라이언트(`net` + `crypto`, WebSocket 직접 구현). 공통 인자 `--secret C:/Users/dbrud/Developemnt/SeePlay/catjump/Library/GameViewRemote/secret.json`(아래 표에서는 `…`), 대상 기본값 `192.168.25.5:7300`(앞단. 같은 PC에서 LAN 주소로 접속).

| 명령 | 본 것 | 결과 |
|---|---|---|
| Unity 메뉴 `Tools/Game View Remote/Self Test`(= `GameViewRemoteAuth.RunSelfTest`) | 설계 시험 벡터 4개, base64url, 헤더 해석, 고정 시간 비교 | 전부 PASS |
| `node hosttest.js vectors` | 독립 구현으로 같은 벡터 | 6/6 |
| `node hosttest.js reject …` | 헤더 없음 401, sid 다름 403, v=2·proto=3 426, cn·did 형식 400, 앞단의 Origin 403, `/x/audio` 404 | 9/9 |
| `node hosttest.js paths …` | LAN `/`, `/index.html`, `/stats`, `/debug`, `/favicon.ico` 404. `/c` 200(6358B, 토큰 없음), HEAD, POST 405. APK 없음 → 404. 자리 표시 APK(300000B) → 200, 길이·sha256 일치, `filename="GameViewRemote-0.0.7-placeholder.apk"`, `/app.json` 200, 인증 응답 `app=7` | 13/13 (두 상태 모두) |
| `node hosttest.js auth …` | 영상·입력 인증(서버 증명을 클라이언트가 검증), `hello` → `seat active`, **H.264 216장/4초(설정 1, 키 1, 720x1280, 첫 프레임 466ms)**, 입력 ping 응답 | 9/9 |
| `node hosttest.js abuse …` | 틀린 토큰(서버 증명 불일치 + 4401), 반사 4401, 다시 보내기(같은 cn + 지난 증명, sn 이 달라 4401), 다른 채널의 증명 4401, 침묵 3002ms 뒤 4401, 긴 메시지 4401. 어느 경우도 `hello` 를 받지 못함 | 8/8 |
| `node hosttest.js limits …` | 0.4초마다 1바이트 → 3002ms 에 닫힘, 17번째 연결 1ms 에 닫힘, 말 없는 16개 3초 뒤 닫힘, 9번째 인증 대기 503 `busy`, 비운 뒤 정상 | 6/6 |
| `node hosttest.js legacy …` | 루프백 `/stats`·`/`(51833B)·`/c` 200, `Origin: http://localhost:7300` 업그레이드 101, **페이지 경로로 H.264 191장/4초**, seat 메시지 없음, Origin 없는 연결 101, 다른 Origin 403, 입력 ping, 루프백 앱 인증(USB 경로) 101, 루프백 틀린 토큰 4401 | 14/14 |
| `node hosttest.js seats …` | 앱 2 + 페이지 1: 한쪽만 영상, take=0 은 뺏지 않음, 대기 쪽 프레임 0장·ping 은 응답, 페이지 재접속 5번에 변화 없음, `{"t":"take"}` 로 넘김(설정 → 키프레임), 페이지 take, 떠나면 먼저 붙은 쪽이 이어받음, 보는 쪽은 계속 H.264 | 19/19 |
| Unity `GameViewRemote.Stop()` → `node hosttest.js lobby --wait-kick 60 …` → Unity `GameViewRemote.Start()` | 대기실: `hello` → `seat lobby`, ping 응답, 프레임 없음. 서버가 켜지면 두 소켓 4410, 다시 붙으면 30ms 안에 active. 그동안 `Host.Viewers` 에 `lobby`, `StatusText` "연결됨: node-test 무선, Play 대기" | 6/6 |
| `node hosttest.js hold …` + Unity `GameViewRemoteHost.RenewToken()` | 붙어 있던 앱이 4403, `Revision` 3 → 4 | 통과 |
| `node hosttest.js prefer1 --wait-kick 180 …` → 스크립트 수정으로 도메인 리로드 → `node hosttest.js prefer2 …` | 리로드 때 1000 으로 닫힘. 리로드 뒤 같은 주소·토큰으로 다시 열림. 다른 앱이 먼저 붙어 이어받아도, 보던 기기가 take=0 으로 돌아오면 자리를 되찾음 | 2/2, 3/3 |
| Unity `GameViewRemoteFirewall.RefreshAsync()` | 읽기만: `Blocked`, "규칙: Unity 2022.3.62f2 Editor"(이 PC의 실제 상태와 같음), `HasBackup=False` | 통과 |
| Unity `GameViewRemoteAppAdb.CheckReachAsync()`(04_device_lab.md 가 READY 인 에뮬레이터) | `emulator-5554` 에서 `192.168.25.5:7300` 닿음, 기기 Wi-Fi 주소 읽음 | 동작은 확인. 단 에뮬레이터의 통신은 PC 안의 프로세스에서 나가므로 **방화벽 통과의 증거는 아님** |
| (덤) 워커 A의 앱이 에뮬레이터에서 adb reverse 로 붙음 | 콘솔 `Video client #1 connected from 127.0.0.1`, `Viewers`: `sdk_gphone64_x86_64 / active / usb`, H.264 인코더 시작 | 내 스크립트가 아닌 실제 앱이 같은 심사를 통과 |

컴파일: 새 파일을 넣은 뒤와 고리를 넣은 뒤마다 콘솔의 `error CS` 0건, `execute_code` 에서 새 클래스 호출 성공. 콘솔의 `NullReferenceException`(BattlePassManager.cs:1580)은 게임 코드가 Play 중 도메인 리로드 뒤에 내는 것으로 이 도구와 무관합니다.

마지막 고리(AppAdb, `Post`) 뒤에는 `all` 을 다시 돌리지 않았습니다(take=1 로 붙으면 그때 에뮬레이터에서 보고 있던 다른 워커의 앱 자리를 뺏기 때문). 그 뒤에 돌린 것은 `prefer2`, `paths`, `auth` 입니다.

## 5. 검증하지 못한 것

- **기기에서의 모든 것**(소유자 지시로 실제 폰 사용 금지): Chrome 페이지를 USB adb reverse 로 열어 H.264 가 나오고 터치가 듣는지(PC 스크립트가 같은 경로·같은 Origin 으로 확인한 것이 전부), QR 을 카메라로 찍어 착륙 페이지가 열리는지, `intent://` 로 앱이 열리는지와 앱이 없을 때 `?noapp=1` 로 돌아오는지, APK 내려받기와 설치 안내, 실제 Wi-Fi 를 거친 연결.
- **방화벽 쓰기**: `RequestAllow`/`RequestRevert` 는 한 번도 실행하지 않았습니다(UAC, 인바운드 규칙 금지). PowerShell 스크립트는 코드로만 검토했습니다. 읽기 판정 중 `Allowed`/`WillPrompt`/`Off` 분기도 이 PC 상태(Blocked)로는 밟지 못했습니다.
- **방화벽을 실제로 통과하는 LAN 접속**: 시험은 같은 PC에서 LAN 주소로 붙은 것이라 방화벽을 지나지 않습니다. node 릴레이(7310)를 거친 접속은 하지 않았습니다.
- `GameViewRemoteAppAdb.InstallAsync`, `ConnectUsbAsync`: 실행하지 않았습니다(다른 워커가 쓰는 에뮬레이터의 앱을 건드리지 않으려고). 서명 불일치 대화 상자 경로도 미실행.
- 착륙 페이지의 스크립트(해시 보관, intent 주소 조립)는 브라우저에서 돌려 보지 않았습니다. 서버가 내주는 것과 크기·내용만 확인.
- 주소 감시(어댑터가 바뀌면 다시 열기), `AutoArm`, 에디터 종료 시 정리, 리스너를 못 열었을 때의 `LastError`, `Disarm` 은 일으켜 보지 않았습니다.
- 입력 주입: 인증된 앱 연결로 보낸 터치가 게임에 들어가는지는 보지 않았습니다(게임을 조작하지 말라는 규칙). 확인한 것은 ping 응답과 대기 중 입력이 버려지는 코드 경로뿐입니다.
- 실제 APK(3.5MB)의 느린 Wi-Fi 내려받기와 `/apk` 동시 2개 한도(503).

## 6. 통합 단계에 남은 것

1. U2: 창의 "Wi-Fi 접속 허용" 토글과 문구 제거(1.3절) → 그 뒤 `GameViewRemote.AllowLan` 삭제. README·개발 문서의 해당 설명 수정.
2. APK: 워커 A의 빌드 결과를 도구 폴더 `App~/GameViewRemote.apk` 로 복사하고 옆에 `app.json`(`{"versionCode":…,"versionName":"…"}`)을 둡니다(`App~/README.txt`). 지금 `App~` 에는 README 만 있어 `/apk` 는 404, 인증 응답의 `app` 은 0 입니다.
3. `GameViewRemoteAppAdb.EMULATOR_ONLY` 를 소유자 확인 뒤 false 로(3.2절). EditorPrefs `GVR_WirelessAddress` 복구 여부도 소유자가 정합니다(3.1절).
4. 5장의 기기 항목을 에뮬레이터/실제 폰에서 확인. 방화벽 [허용]은 소유자가 직접 눌러 확인.
5. 페이지 클라이언트: 앱이 보고 있을 때 페이지는 "다른 기기가 보고 있습니다"(`{"t":"s","stale":true,"code":"standby"}`)를 받고 프레임이 오지 않습니다. 페이지에서 가져오려면 `{"t":"take"}` 를 보내는 버튼이 필요합니다(페이지 파일은 U2 소유).
6. 설계 문서(A): 시험 벡터 sid 의 형식 주석(2.2의 2), Origin 포트 규칙(2.2의 1)을 맞춰 적기.

## 7. 끝났을 때의 에디터 상태

- **Host 는 켜 둔 채입니다**(`Armed` = true, `192.168.25.5:7300` 리슨. 시작할 때는 꺼져 있었음). 끄면(`Disarm`) 기존 서버도 멈춰, 지금 에뮬레이터에서 붙어 있는 다른 워커의 앱 연결이 끊기기 때문에 그대로 두었습니다. `Armed` 는 SessionState 라 에디터를 다시 켜면 꺼집니다. 끄려면 창의 [Stop] 또는 `GameViewRemoteHost.Disarm()` 뒤, Play 중이면 예전 Start 를 다시 누릅니다.
- 토큰은 시험 중 한 번 새로 발급했습니다(그 전 토큰은 내 스크립트만 썼음).
- EditorPrefs: `GVR_WirelessAddress` 비움(백업 있음, 3.1절), `GVR_AllowLan` 삭제.
- 테스트 패턴은 켜지 않았고(원래 꺼짐) 설정 파일 `UserSettings/GameViewRemote.json` 의 값은 바꾸지 않았습니다.
