# 03. 설계 리뷰 — `03_design.md`

- 작성: 2026-11-14 (4단계 워커, 설계를 쓰지 않은 독립 리뷰). 이 단계에서 쓴 파일은 이 문서뿐입니다. catjump 저장소, Unity, 폰, 방화벽은 건드리지 않았습니다.
- 대조한 것: `00_BRIEF.md`(3.1, 3.2 포함), `02_PRD.md`, `00_current_system_and_history.md`, `01_research.md`(빌드 부분), Unity 도구 코드 전체(`GameViewRemoteServer.cs`, `GameViewRemote.cs`, `GameViewRemoteCapture.cs`, `GameViewRemoteH264.cs`, `GameViewRemoteInput.cs`, `GameViewRemoteAdb.cs`, `GameViewRemoteWindow.cs`, `GameViewRemotePage.html`).
- 코드 시점: 리뷰할 때 `.cs`와 `.html`의 크기와 수정 시각이 설계 머리말에 적힌 것과 같았습니다(`GameViewRemoteServer.cs` 11-13 22:07:48, `GameViewRemotePage.html` 23:30:02 등). 설계의 줄 번호는 그대로 유효합니다.

표시: **[코드]** 직접 읽음 / **[실행]** 이 PC에서 명령을 돌려 확인 / **[문서]** 공식 문서를 검색해 읽음 / **[미확인]** 기억이나 추론이고 확인하지 못함.

## 0. 판정 요약

**조건부 승인입니다.** 프로토콜 설명은 지금 서버 코드와 맞고, Android API는 전부 실제로 있으며 계획한 최소 SDK에서 쓸 수 있고, 빌드 도구도 이 PC에 있습니다. 5단계의 M0 ~ M2(영상, 계측)는 지금 설계대로 시작해도 됩니다.

고쳐야 진행할 수 있는 것은 두 묶음입니다.

| 언제까지 | 무엇 |
|---|---|
| M3(탭 비교) 전 | 차단 B1, B2와 주요 M5. 지금 시험 계획으로는 앱과 페이지를 같은 뜻의 숫자로 비교하지 못합니다 |
| 6단계(Unity) 전 | 주요 M1 ~ M4, M6, M8. 인증을 건너뛰는 길 하나, 토큰이 저장소에 올라갈 길 하나, 공존 규칙이 Play 재시작에서 깨지는 것 하나가 있습니다 |

| 심각도 | 수 | 번호 |
|---|---|---|
| 차단 | 2 | B1, B2 |
| 주요 | 8 | M1 ~ M8 |
| 사소 | 15 | m1 ~ m15 |

조정자가 정해야 하는 것은 [5장](#5-조정자가-정할-것)에 모았습니다.

## 1. 확인한 것 (문제 없음)

### 1.1 프로토콜 설명과 지금 서버 코드

| 설계의 진술 | 코드 | 결과 |
|---|---|---|
| 업그레이드는 `Upgrade: websocket`과 `Sec-WebSocket-Key`만 봄 | `GameViewRemoteServer.cs:360~364` | 맞음 |
| 영상 소켓 첫 메시지는 `{"t":"hello","v":2}` | `:375~376`(전송 스레드 시작 전에 직접 씀) | 맞음 |
| 서버 → 앱 프레임: 마스크 없음, 항상 FIN, 길이 125 이하 / 126 + u16 BE / 127 + u64 BE | `WriteFrameHeader` `:838~863` | 맞음 |
| 앱 → 서버: 마스크 처리, 64KB 초과면 닫음, ping에 pong | `ReadLoop` `:480~565` | 맞음(64KB 검사는 프레임 단위) |
| `{`로 시작하지 않는 영상 소켓 텍스트는 전부 수신 확인 | `HandleText` `:569~616` | 맞음 |
| `caps`는 `"caps"`와 `"h264":1` 문자열 포함 검사, `key`는 `"key"` 포함 검사 | `:572~579` | 맞음 |
| `{"t":"take"}`, `{"t":"auth",…}`는 고치기 전 서버가 무시 | 같은 곳. base64url에는 따옴표가 없어 `"key"`와 겹치지 않음 | 맞음 |
| 헤더 28바이트 리틀 엔디언, 오프셋과 필드 | `GameViewRemoteCapture.WriteHeader` `:935~949` | 맞음 |
| `flags` 1/2/4/8 | `:31~34`, `:907` | 맞음 |
| 설정 = avcC, 프레임 = AVCC(길이 4바이트 + NAL), Annex B 아님 | `GameViewRemoteH264.cs:233~254`(FLV 태그에서 5바이트를 뗌) | 맞음 |
| 받는 클라이언트가 바뀌면 인코더가 스스로 다시 시작해 설정 + 키프레임 | `EncodeH264` `:840` | 맞음 |
| 영상 클라이언트가 2개면 둘 다 MJPEG | `SingleH264Client` `:736~758` | 맞음 |
| 닫기 코드는 1000뿐 | `SendClose` `:785~794` | 맞음 |
| 입력 JSON 필드, ping 답 `{"t":"P","ts":…}` | `GameViewRemoteInput.cs:120~176` | 맞음(단 `t` 판정은 M6) |
| 입력 번호는 입력 연결이 0 → 1일 때만 0으로 | `:102~110` | 맞음 |
| 페이지가 보내는 `caps` 글자 | `GameViewRemotePage.html:752` | 맞음 |
| 페이지는 `stale:true`의 `msg`를 그대로 표시 | `:1024~1026` | 맞음. 대기 문구가 페이지 수정 없이 보입니다 |
| 9.4절의 줄 번호(S2 360~384, G1 350, G2 470~473·479~482, W1 57~64, W2 106) | 해당 파일 | 맞음 |

설계가 적지 않았지만 앱 구현에 필요한 사실 하나: 설정 메시지의 `captureTime`은 캡처 시각이 아니라 **헤더를 쓴 시각**입니다(`:905`, `captureTicks = config ? Stopwatch.GetTimestamp() : …`). 설계는 설정 메시지를 표시 일정 계산에 넣지 않으므로 지금은 문제가 없습니다. `FrameHeader` 주석에 남겨 두세요.

### 1.2 Android API와 API 레벨

이 PC의 SDK `platforms/android-36/data/api-versions.xml`에서 직접 읽었습니다 **[실행]**. 폰은 Android 16(API 36), 계획한 최소 SDK는 26입니다.

| API | 도입 | 설계의 조건 | 결과 |
|---|---|---|---|
| `MediaFormat.KEY_LOW_LATENCY`, `CodecCapabilities.FEATURE_LowLatency` | 30 | "API 30 이상" | 맞음 |
| `MediaCodecInfo.isHardwareAccelerated()` | 29 | "API 29, 그 아래는 이름으로" | 맞음 |
| `KEY_PRIORITY`, `KEY_OPERATING_RATE` | 23 | 23 | 맞음 |
| `MediaCodec.setCallback(cb, Handler)`, `setOnFrameRenderedListener` | 23 | 조건 없음 | 최소 26이라 됨 |
| `releaseOutputBuffer(int, long)` | 21 | 조건 없음 | 됨 |
| `Surface.setFrameRate(float, int)` / 3인자 | 30 / 31 | "API 31, API 30은 2인자" | 맞음 |
| `FRAME_RATE_COMPATIBILITY_FIXED_SOURCE` / `CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS` | 30 / 31 | 같음 | 맞음 |
| `LayoutParams.preferredDisplayModeId` | 23 | 조건 없음 | 됨 |
| `Choreographer.postVsyncCallback`, `FrameData`, `FrameTimeline.getExpectedPresentationTimeNanos` | 33 | "API 33 이상" | 맞음 |
| `WifiManager.WIFI_MODE_FULL_LOW_LATENCY` | 29 | "API 29 이상" | 맞음 |
| `Network.bindSocket(Socket)` | 21 | 조건 없음 | 됨 |
| `NetworkCapabilities.getTransportInfo()` | 29 | "API 31 이상은 이쪽" | 됨 |
| `WifiManager.getConnectionInfo()` | 1, 31에서 폐기 표시 | "그 아래는 이쪽" | 맞음 |
| `LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES` / `ALWAYS` | 28 / 30 | 같음 | 맞음 |

동작 설명을 공식 문서와 맞춰 본 것 **[문서]**:

- `releaseOutputBuffer(index, 시각)`: 시각은 `System.nanoTime`과 1초 안이어야 하고, 같은 VSYNC에 여러 장을 보내면 마지막 것만 보이고 나머지는 버려지며, 원하는 표시 시각의 약 2 VSYNC 전에 부르라고 되어 있습니다. 설계 7.4절의 방식과 맞습니다.
- 크기가 같은 설정 변경을 키프레임 앞에 SPS/PPS를 붙여 넘기는 것은 MediaCodec 문서가 허용하는 방식입니다. 맞습니다.
- Chrome의 `intent://` 문법, `package`, `S.browser_fallback_url`은 Chrome 개발자 문서에 그대로 있습니다. 커스텀 스킴은 서명 검증이 필요 없으므로 사내 사이드로드 앱에서 동작하는 선택입니다.
- Chrome 154(2026년 10월)부터 "항상 보안 연결 사용"이 기본으로 켜졌지만 대상은 공개 사이트이고, 사설 주소의 HTTP는 경고 없이 열린다고 보도됐습니다. 착륙 페이지(`http://192.168.x.x`)가 이 경고에 걸릴 가능성은 낮습니다. Google 블로그 원문은 읽지 못했고 보도 요약으로 확인했습니다. M0에서 실기기로 확인하면 됩니다.

### 1.3 빌드 도구 **[실행]**

| 항목 | 확인 |
|---|---|
| `…\6000.2.10f1\…\AndroidPlayer\OpenJDK` | `java -version` = Temurin 17.0.9 |
| `Tools\gradle\lib\gradle-launcher-8.13.jar` | 있음 |
| `SDK\platforms` | android-34, 35, 36 |
| `SDK\build-tools` | 36.0.0 |
| Gradle 캐시의 AGP | 8.13.0 있음 |
| node | v22.18.0 |
| Python OpenCV | 4.13.0 (QR 다시 읽기용) |
| Android Studio | `C:\Program Files\Android\Android Studio` 있음(JBR 포함). 필요하지는 않음 |

- 없는 것: Gradle 래퍼 배포본 8.13(`~\.gradle\wrapper\dists`에는 7.5.1뿐), Kotlin Gradle 플러그인, AndroidX, CameraX, ML Kit. 첫 빌드에서 내려받습니다. 래퍼 내려받기가 안 되면 런처 jar로 돌리는 대안이 실제로 있습니다. 빌드는 됩니다.
- 설계의 가정 17(`127.0.0.1:포트`와 `<LAN 주소>:포트`를 함께 들음): node로 시험 포트 7399에 두 주소를 동시에 열어 봤고 둘 다 열렸습니다. OS 수준에서는 성립합니다. Mono에서도 그런지는 U1에서 확인해야 합니다.

### 1.4 스레드 모델

영상의 길(`gvr-video-rx` → MediaCodec → `gvr-codec` → `gvr-pacer` → SurfaceFlinger)에 UI 스레드가 없다는 주장은 구조상 맞습니다. `Choreographer`는 Looper가 있는 스레드마다 따로 생기므로 `gvr-pacer`의 VSYNC 콜백은 UI 스레드가 터치를 처리하는 동안에도 옵니다. 수신 확인을 수신 스레드가 디코딩 전에 보내는 것도 서버의 과부하 오판을 막는 올바른 자리입니다. 남는 문제는 m1, m2입니다.

### 1.5 인증 절차

- 증명 값에 역할, 채널, 서버 표시, 양쪽 임의 값이 모두 들어가 반사와 재전송이 막힙니다. 서버가 임의의 `cn`에 "srv" 증명을 내줘도 128비트 키를 오프라인으로 찾을 수는 없습니다.
- 토큰은 연결 위로 흐르지 않고, QR의 `#` 뒤 값은 서버 요청 줄에 실리지 않습니다.
- 인증 뒤의 평문 연결을 가로채는 것은 막지 않습니다. PRD NF-63에 적힌 한계와 같습니다.
- 인증을 건너뛰는 길과 토큰이 새는 길은 M1, M2, M7, m3에 있습니다.

## 2. 차단

### B1. 앱과 페이지의 "표시 시각"이 서로 다른 뜻이라 11.3절의 판정이 성립하지 않습니다

- 근거:
  - 설계 8.2, 8.3절: 앱의 `shown` = `renderedNs`(`OnFrameRenderedListener`), 없으면 `targetNs`. 페이지의 표시 시각은 그리기 호출 시각입니다(`GameViewRemotePage.html:659`, `drawnAt = performance.now()`).
  - 11.3절 합격 줄: "터치 → 표시 평균 ≤ 페이지 + 5ms". 앱 쪽 값에는 버퍼를 넘긴 뒤 합성까지의 시간(1 ~ 2 VSYNC)이 더 들어갑니다. 앱이 페이지만큼 빨라도 이 줄에서 떨어집니다.
  - `renderedNs`가 오지 않으면 `targetNs`로 계산한다는 대체안은 **앱이 스스로 요청한 시각의 간격**을 재는 것입니다. 합성기가 프레임을 놓쳐도 고르지 않은 간격이 0%로 나옵니다. NF-2 ~ NF-4, NF-10의 근거가 될 수 없습니다.
  - 공식 문서 **[문서]**: `onFrameRendered`는 "정보 제공용"이고 크게 늦거나 묶여서 올 수 있습니다. Android 14부터 프레임마다 온다는 보장만 있습니다. 시각이 실제 표시 시각인지는 문서가 말하지 않습니다.
- 바꿀 것:
  1. 두 클라이언트를 **견주는** 숫자(터치 → 표시, 멈춘 갱신, 건너뛴 프레임)는 앱의 `releaseNs`(버퍼를 넘긴 시각)로 계산합니다. 페이지의 그리기 호출 시각과 같은 자리입니다. `renderedNs` 기준 값은 "앱 단독" 열로 따로 냅니다. 8.2절, 8.5절(`drawnAt` 칸에 무엇을 넣는지), 11.3절 합격 줄을 이렇게 고칩니다.
  2. M2의 "끝났다고 말할 조건"에 넣습니다: `renderedNs`를 `adb shell dumpsys SurfaceFlinger --latency <앱의 SurfaceView 레이어>`의 실제 표시 시각과 한 번 맞춰 본다. 앱의 SurfaceView 레이어는 버퍼 1장 = 영상 1장이라 이 값이 뜻을 가집니다. 어긋나면 앱 단독 수치의 출처를 이 덤프로 바꿉니다.
  3. `targetNs`로만 계산한 수치로는 NF-2 ~ NF-4, NF-10을 "충족"으로 적지 않는다고 11.3절에 명시합니다.
  4. 가정 19(SurfaceFlinger 덤프로 Chrome도 같은 정의로 잰다)는 낮춰 적습니다. Chrome의 레이어는 합성 프레임이 올라올 뿐 어느 프레임에 새 영상이 들었는지 알 수 없습니다 **[미확인: 추론]**.

### B2. 무선 비교가 "경로"와 "클라이언트"를 동시에 바꿉니다

- 근거: PRD 5.1 합격 조건 1과 설계 11.4절. 앱은 LAN 직접(`relay.js`), 페이지는 무선 adb 터널로 잽니다(W5). 페이지가 LAN 직접에서 H.264를 못 쓰기 때문입니다. PRD 4.7의 링크 수치로는 두 경로의 정체 분포가 다릅니다(도착 간격 40ms 이상이 20초에 9 ~ 11회 대 23 ~ 25회). 앱이 더 나쁘게 나와도 앱 탓인지 경로 탓인지 가를 수 없고, 더 좋게 나와도 마찬가지입니다.
- 바꿀 것: 11.4절에 **W5b: 앱, 무선 adb 터널(`adb reverse`, `127.0.0.1:7300`, 창 "영상 경로: 무선 우선")** 을 넣습니다. 브리프 3.2의 2번이 이 창 설정 변경을 허락했습니다. 그러면 표가 세 줄이 됩니다.

  | 줄 | 클라이언트 | 경로 | 무엇을 답하나 |
  |---|---|---|---|
  | W5 | 페이지 | 무선 adb 터널 | 기준선 |
  | W5b | 앱 | 무선 adb 터널 | 같은 경로에서 클라이언트 차이 |
  | W2 | 앱 | LAN 직접 | 같은 클라이언트에서 경로 차이 |

  보고는 "앱 대 페이지"를 W5 대 W5b로, "LAN 직접의 이득"을 W5b 대 W2로 적습니다. W5와 W2를 직접 견준 숫자는 결론의 근거로 쓰지 않습니다.

## 3. 주요

### M1. 기존 "Wi-Fi 접속 허용" 토글이 인증 없이 LAN에 여는 길로 남습니다

- 근거: `GameViewRemoteServer.cs:147`은 `allowLan`이면 `IPAddress.Any`에 바인드합니다. 설계 9.4절 S2는 기존 서버에서 `Admit(…, lan: false, …)`를 부르고, `lan`이 false이면 인증 헤더가 없는 연결을 `Legacy`로 받습니다. G1은 Host가 켜져 있을 때만 토글을 무시합니다. 그래서 Host를 끄고 토글을 켠 채 Play 하면 LAN의 누구나 토큰 없이 `/video`, `/input`, `/stats`, `/debug`에 닿습니다. PRD F-36("루프백에서 온 연결만"), F-44, NF-60과 어긋납니다. 설계 9.9절은 이것을 "지금과 같음"으로 적어 두었습니다.
- 바꿀 것:
  1. `Admit`가 `lan` 값을 호출한 쪽에서 받지 않고 **소켓의 원격 주소**로 정합니다: `!IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint).Address)`. 루프백이 아닌데 인증 헤더가 없으면 어느 리스너로 들어왔든 401입니다.
  2. S9와 기존 `/`, `/stats`, `/debug` 분기 앞에도 같은 검사를 넣어 루프백이 아니면 404로 답합니다.
  3. 토글은 없애거나 숨깁니다(W2). 남긴다면 "앱은 QR로, 페이지는 USB로"라는 문구와 함께 꺼진 상태로 고정합니다.
- 이 변경으로 잃는 것: LAN 주소로 Chrome 페이지를 여는 기능. 그 경로는 지금도 MJPEG만 되고 방화벽에 막혀 있어 쓰이지 않습니다(00 문서 2.1).

### M2. 토큰 파일 위치가 이 저장소에서는 버전 관리 대상입니다

- 근거: 설계 9.1절은 토큰을 `UserSettings/GameViewRemote.json`에 평문으로 두고 "보통 버전 관리에서 빠져 있음"이라고 적었습니다. catjump에서는 그렇지 않습니다 **[실행]**: `git ls-files UserSettings`에 `UserSettings/EditorUserSettings.asset` 등이 추적 중으로 나오고, `git check-ignore UserSettings/GameViewRemote.json`은 일치하는 규칙이 없습니다. `git add .` 한 번이면 토큰이 사내 저장소에 올라갑니다. 창의 경고만으로는 막지 못합니다.
- 바꿀 것: 서버 표시와 토큰은 `Library/GameViewRemote/secret.json`에 둡니다. `Library/`는 이 저장소 `.gitignore` 1행에서 무시되고(`git check-ignore`로 확인) Unity 프로젝트 관례로도 올리지 않습니다. 프로젝트별, PC별이라는 요구(F-101)도 그대로 만족합니다. Library를 지우면 토큰이 새로 만들어져 QR을 다시 찍어야 하는데, 받아들일 만한 대가입니다. 비밀이 아닌 설정(`autoArm`, `hostAddress`, `apkPath`)은 `UserSettings/`에 그대로 둡니다.

### M3. 자리 선호가 서버 Stop마다 지워져 Play 재시작 뒤 앱이 다시 뺏습니다

- 근거: 9.4절 S10이 `server.Stop()` 끝에 `Seats.Reset()`을 부릅니다. Stop은 Play 종료와 도메인 리로드마다 일어납니다(`GameViewRemote.cs:485~500`). 3.5절 규칙 3은 선호가 없으면 "붙어 있는 앱 무리 중 가장 먼저 붙은 것"을 고릅니다. 그래서 사용자가 페이지에서 [여기서 보기]를 눌러 페이지로 보고 있어도, 앱이 대기 상태로 열려 있으면 다음 Play나 스크립트 저장 뒤에 앱이 자리를 가져갑니다. Unity 작업에서 가장 잦은 사건입니다. 브리프 3.1의 B2(서로 뺏지 않음)와 PRD F-39가 깨집니다.
- 바꿀 것: 선호 무리 키(`"page"` 또는 `"app:<did>"`)를 `SessionState`에 두고 `Reset()`이 지우지 않게 합니다. 지우는 때는 Host의 [Stop]과 [새 QR 발급]뿐입니다. 11.5절 공존 시험에 "페이지가 선호인 상태에서 Play 종료 → 시작, 스크립트 재컴파일 뒤에도 페이지가 active"를 넣습니다.

### M4. 인증 전 연결 제한이 5.2절에 적힌 대로 걸리지 않습니다

- 근거:
  - 5.2절은 "ACCEPTED ~ CHALLENGED 상태의 연결은 동시에 8개"라고 하지만 9.2절 `Admit`의 4번은 **HTTP 헤더를 다 읽은 뒤** 셉니다. 헤더를 읽는 중인 연결은 세지 않습니다.
  - 제한 시간은 `ReceiveTimeout = 3000`인데 이것은 읽기 한 번마다의 제한입니다. 2.9초마다 1바이트씩 보내면 16KB를 채울 때까지 스레드 하나를 붙잡습니다.
  - `/c`, `/apk`, `/app.json`에는 제한이 없고 연결마다 스레드를 만듭니다. `/apk`는 수 MB를 내줍니다.
  - 합치면 같은 Wi-Fi의 누구든 Unity 에디터 프로세스 안에 스레드를 한없이 만들 수 있습니다. PRD F-44(필수)와 NF-44에 어긋납니다.
- 바꿀 것:
  1. Host의 수락 루프에서 셉니다: 인증을 마치지 않은 연결(HTTP GET 포함)이 16개면 받자마자 닫습니다. `/video`·`/input` 업그레이드 대기는 그중 8개까지.
  2. 연결을 받은 시각을 기록하고 헤더 읽기와 인증 메시지 읽기를 합쳐 **절대 기한 3초**를 둡니다(읽기 루프에서 `Stopwatch`를 확인하거나, 기한이 지난 소켓을 닫는 감시 타이머 하나).
  3. `/apk` 동시 전송은 2개까지, 넘으면 503.
  4. 11.5절 인증 시험에 "1바이트씩 천천히 보내는 연결이 3초 뒤 닫힘"과 "17번째 연결이 바로 닫힘"을 넣습니다.

### M5. 11.3절 시험 표가 지금 폰 상태와 다릅니다

- 근거: T1은 "폰의 지금 설정(표준 60Hz)", 7.5절과 가정 5도 표준 60Hz를 전제로 합니다. 브리프 3.2는 "폰은 지금 가변 120Hz 모드다"라고 합니다. 보고 3의 기준선 수치(멈춘 갱신 0.1/초, 정지 최대 50ms, 터치 → 표시 66.0ms)는 표준 60Hz에서 잰 것이라 오늘의 페이지와 견줄 수 없습니다. 페이지의 60Hz 수치는 워커가 다시 잴 방법이 없습니다(폰 설정 변경 금지).
- 바꿀 것:
  - T1: "페이지, 가변 120Hz(잰 주사율을 같이 적음)". 같은 날 다시 잰 값만 기준선으로 씁니다.
  - T2의 기본 비교는 앱 `rr=none`(페이지와 같은 조건: 주사율을 요청하지 않음)으로 하고, `rr=match`, `force60`, `max`는 앱 안에서의 비교로 둡니다.
  - 합격 줄의 "멈춘 갱신 ≤ 0.2/초, 정지 최대 ≤ 50ms"는 60Hz 기준선에서 온 값이므로 "같은 날 T1에서 잰 값 이하"로 바꿉니다.
  - 가정 5의 문구를 "가변 120Hz 설정에서 `force60`이 먹는가"로 고칩니다. 페이지의 60Hz 수치는 소유자 확인 목록으로 옮깁니다.

### M6. 입력 소켓은 `t`의 첫 글자만 봅니다. 확장 규칙 R2가 지금 코드에서는 성립하지 않습니다

- 근거: `GameViewRemoteInput.cs:137` `char type = message.t[0];` 뒤로 `p`, `q`, `d`, `m`, `u`, `c`를 글자 하나로 가릅니다. 설계 15.1절 R2는 "입력 소켓은 모르는 `t`면 `null`"이라고 적었지만, 새 메시지 이름이 이 여섯 글자 중 하나로 시작하면 기존 메시지로 처리됩니다. 예: `{"t":"mouse",…}`는 포인터 0의 move, `{"t":"cfg",…}`는 cancel, `{"t":"pen",…}`는 ping입니다. 15.2절은 새 입력 종류를 "입력 소켓의 새 `t`"로 붙이라고 안내합니다.
- 바꿀 것:
  1. R2에 규칙을 더합니다: 입력 소켓의 새 `t`는 **두 글자 이상이고 `c d m p q u`로 시작하지 않는다.** 고치기 전 서버와도 안전해야 하므로 서버를 고치는 것만으로는 부족합니다.
  2. 6단계에서 `GameViewRemoteInput.HandleMessage`에 한 줄을 넣습니다: `if (message.t.Length != 1) return null;`. 9.4절의 "`GameViewRemoteInput.cs`: 바꾸지 않습니다"를 이 한 줄로 고칩니다.

### M7. 배포하고 측정하는 APK가 디버그 빌드입니다

- 근거: 10.3절 "시제품은 `debug` 빌드를 배포합니다".
  - 측정: Android 문서는 디버그 가능 플래그가 "실행 성능을 크게 떨어뜨린다"고 하고, 벤치마크 도구는 디버그 가능 앱을 오류로 처리합니다 **[문서]**. 영상의 무거운 일은 네이티브 디코더가 하지만 수신 루프, AVCC 변환, 표시 일정은 앱 코드입니다. PRD 4.2의 수치를 이 빌드로 확정하면 실제보다 나쁜 값이 기준이 됩니다.
  - 보안: 4.2절의 디버그 전용 링크 값(`legacy=1`, `listen=<포트>`, `linktest?url=`)이 배포본에 살아 있습니다. 인텐트 필터가 `BROWSABLE`이므로 폰에서 연 아무 웹 페이지의 링크가 `seeplay-gvr://<남의 주소>/c?…&legacy=1`로 앱을 인증 없는 서버에 붙이거나 듣는 포트를 열게 할 수 있습니다.
  - 디버그 값이 없어도 남는 문제: 웹 페이지가 자기 토큰과 자기 서버로 만든 딥링크를 열면 상호 인증이 통과하고, 앱은 그 서버를 "가장 최근 서버"로 기억해 다음 실행부터 그쪽으로 붙습니다(5.1절 `ServerStore`).
- 바꿀 것:
  1. 빌드 유형을 하나 더 둡니다: `proto`(`initWith(release)`, `isDebuggable = false`, 난독화 끔, 같은 키, `BuildConfig.DIAG = true`). 배포와 합격 수치 측정은 이 빌드로 합니다. 기록 파일은 앱 전용 외부 저장소에서 `adb pull`로 가져오고(가정 18), 안 되면 그때만 `debug` 빌드를 씁니다. 수치마다 빌드 유형을 적습니다.
  2. 진단 값(`legacy`, `listen`, `linktest`, `rec` 등)은 인텐트에 `CATEGORY_BROWSABLE`이 **없을 때만** 받습니다. 브라우저가 연 인텐트에는 이 범주가 붙고 `adb shell am start`에는 붙지 않습니다. M0에서 Chrome과 삼성 인터넷 둘 다 확인합니다.
  3. `BROWSABLE`로 들어온 링크의 서버 표시가 기억한 것과 다르면 "새 PC `<name>`에 연결할까요?"를 한 번 묻습니다. 기억한 서버가 없을 때(처음 설치)는 묻지 않습니다. 앱 안 스캐너와 adb 인텐트는 묻지 않습니다.

### M8. 9.4절 고리 목록에 빠진 것과 순서가 어긋난 것이 있습니다

Unity 쪽 변경이 작고 얹기 쉬운 것은 맞습니다(기존 파일 4개에 고리 15개, 대부분 한두 줄). 다만 목록대로만 넣으면 동작하지 않는 곳이 넷 있습니다.

| # | 문제 | 근거 | 바꿀 것 |
|---|---|---|---|
| a | 대기 상태로 들어온 페이지가 대기 문구를 잃습니다 | `GameViewRemoteServer.cs:366` `connection.pendingText = … lastStatus`. S5는 `PublishStatus`와 `Seed`만 막습니다. 마지막 상태가 `stale:false`이면 전송 스레드가 그것을 보내 페이지의 "다른 기기가 보고 있습니다"를 지웁니다(`GameViewRemotePage.html:1025~1026`) | S4에 추가: `pendingText`는 `standby`가 아닐 때만 `lastStatus`로 채움 |
| b | `hello`와 `seat`의 순서가 3.2절과 반대입니다 | 3.2절은 `hello` → `seat`. S4는 `OnJoin`을 `hello`를 쓰기 전에 부르고, 9.3절은 `OnJoin` 안에서 `SendText`로 알립니다 | `OnJoin`은 `standby`만 정하고, 들어온 연결 자신에게 보내는 알림은 `hello`를 쓴 직후로 옮김(`Seats.Announce(server, connection)` 한 줄). 앱은 순서에 기대지 않게 구현 |
| c | 4403으로 닫을 방법이 없습니다 | 5.2절은 [새 QR 발급] 때 기존 서버의 앱 연결을 4403으로 닫는다고 합니다. `SendClose`는 `private static`이고 1000 고정입니다(`:785~794`) | S11 추가: `public void CloseWith(Connection c, ushort code)` |
| d | `GameViewRemoteAdb`는 "한 단어씩" 둘로는 컴파일되지 않습니다 | `Device`가 `private struct`(`:39`)라 `ListReadyDevices`만 `internal`로 바꾸면 접근 수준 불일치 오류가 납니다. `Run`은 "백그라운드 스레드 전용"이고 `runLock`은 `RunInBackground`(`private`, `:441`) 안에서 잡습니다 | `Device`, `ListReadyDevices`, `Run`, `RunInBackground`, `ReversePreferUsb` 다섯 곳을 `internal`로 |

## 4. 사소

| # | 문제 | 근거 | 바꿀 것 |
|---|---|---|---|
| m1 | 6장 표는 UI 스레드가 "디코더를 직접 부르지 않음"이라고 하지만 7.2절 `surfaceDestroyed`는 Surface가 사라지기 전에 디코더를 멈춰야 합니다 | 이 콜백은 UI 스레드에서 오고 돌아오면 Surface가 무효입니다. 그 뒤 `gvr-pacer`가 `releaseOutputBuffer(…, 시각)`을 부르면 예외가 납니다 | 6장에 예외로 적음: `surfaceDestroyed`에서 UI 스레드가 `gvr-codec`의 정지 완료를 최대 500ms 기다림. `Pacer`는 "Surface 없음" 표시를 본 뒤에는 버리기만 함. 예외는 잡아서 셈 |
| m2 | 입력 큐에 상한이 없습니다 | 6장 `LinkedBlockingQueue<ByteArray>`. 무선이 막혀 쓰기가 멈추면 move가 쌓였다가 한꺼번에 늦게 갑니다. 메시지마다 배열과 노드를 할당합니다 | 큐에 move가 이미 있는 포인터는 새 위치로 덮어씀. down/up/cancel은 그대로. 길이 상한 64 |
| m3 | 토큰이 로그에 남을 수 있는 자리가 둘 있습니다 | ① `GameViewRemoteAdb`는 실행한 명령을 창에 보여 줍니다(`:299~301`의 `lastOutput`). 9.8절 [USB로 연결]의 명령에는 토큰이 들어 있습니다. ② 커스텀 스킴 주소의 `?` 뒤는 폰 logcat의 액티비티 시작 줄에 찍힐 수 있습니다 **[미확인]** | ① AppAdb는 창과 콘솔에 토큰을 `t=…`로 가려 적음. ② M0에서 `adb logcat`으로 확인하고, 찍히면 [USB로 연결]은 토큰을 `--es t <토큰>` 추가 값으로 넘김. `Exporter`의 `info`와 `05_build_and_test.md`에도 토큰을 적지 않는다고 8.5절에 명시 |
| m4 | 입력 연결 수를 두 곳에서 셉니다 | 9.3절 Seats와 9.4절 G2가 각자 `OnInputConnected/Disconnected`를 부릅니다. Seats가 `standby`를 바꾸는 순간과 실제 끊김이 겹치면 두 번 줄거나 안 줄 수 있습니다. 수가 어긋나면 `GameViewRemoteInput.cs:214~215`의 "연결됨" 판정이 틀어져 마우스 시뮬레이션과 EventSystem 포커스가 잘못 켜집니다 | 세는 것을 Seats 한 곳으로: G2는 `Seats.OnInputJoin/Leave`만 부르고 Seats가 자기 잠금 안에서 `GameViewRemoteInput`을 부름 |
| m5 | Seats가 `GetConnections()` 복사본으로 무리를 봅니다 | `OnJoin`은 목록에 넣기 전에 불리므로 들어오는 연결이 복사본에 없고, 영상과 입력 소켓이 동시에 들어오면 서로를 못 봅니다 | Seats가 자기 잠금 안에 자기 구성원 표를 둠 |
| m6 | `Adopt`와 `Stop`이 겹치면 닫히지 않는 연결이 남습니다 | `Stop`이 연결 목록을 복사한 뒤(`:173~177`) 들어온 연결은 닫히지 않고 `ReadLoop`에 남습니다(기존 `HandleClient`도 같음) | `RunWebSocket`에서 목록에 넣은 직후 `running`을 다시 보고 아니면 닫음 |
| m7 | Host를 켠 적이 없는 세션에서 토큰을 기억한 앱이 USB로 붙으면 종료 상태 오류가 납니다 | 9.2절: 토큰이 없으면 인증 헤더가 있는 요청은 403 `unknown_server` → 앱은 "이 QR은 다른 PC의 것입니다"(다시 시도 안 함). 예전 Start 버튼만 쓰는 날에 생깁니다 | 설정 파일이 있으면 Host 정적 생성자에서 항상 읽어 둠(`Armed`와 무관). 파일이 없을 때만 403 |
| m8 | `frameId` 연속성 검사가 JPEG 구간에서 틀립니다 | 7.4절 `idGaps`는 건너뛰면 "버그로 기록". MJPEG는 최신 1장 슬롯이라 덮어쓰면 번호가 빠집니다(`PublishFrame` `:236~244`). 접속 직후 `caps` 전의 JPEG 구간에서 정상적으로 생깁니다 | 설정 메시지를 받을 때마다 검사를 새로 시작하고 H.264 메시지끼리만 봄 |
| m9 | NF-10의 "몰려 도착" 정의가 두 문서에서 다릅니다 | PRD 4.2: 6ms 안에 3장 이상. 설계 11.3절: 12ms 안 | PRD 값으로 통일하거나 PRD를 고침 |
| m10 | 탭 표본이 작고 주입 방법의 영향을 가르지 않습니다 | 60초 × 2회 = 탭 약 80회. NF-10의 5%는 4회입니다. `adb shell input swipe`는 부를 때마다 폰에서 프로세스를 새로 띄웁니다 | 조건당 탭 120회 이상, 비율과 함께 횟수를 적음. 대조 구간 1회 추가: 같은 주기로 `adb shell input keyevent 0`(터치 없음)을 넣어 프로세스 기동만의 영향을 봄 |
| m11 | 자동 발견(DNS-SD)에 대한 결정이 설계에 없습니다 | 브리프 3장이 묻는 항목이고 PRD F-12는 권장입니다. 설계에는 언급이 없습니다 | 5.1절에 한 줄: "F-12는 시제품에서 하지 않음. 주소가 바뀌면 `a=` 후보와 60초 뒤 [QR 다시 찍기] 안내로 대응" |
| m12 | 방화벽 스크립트를 임시 파일로 써서 승격 실행합니다 | 9.7절 `-File <임시 스크립트>` + `-ExecutionPolicy Bypass`. 쓰기와 UAC 동의 사이에 같은 사용자의 다른 프로세스가 파일을 바꿀 수 있습니다 | `-EncodedCommand`로 내용을 인자에 실어 넘김 |
| m13 | 도메인 리로드 동안의 재시도 간격 | Play 진입 때 Host가 수 초 닫힙니다. 앱은 1000을 받고 0.25초부터 4초 상한까지 간격을 늘리므로 Play가 뜬 뒤 최대 4초를 더 기다립니다 | 1000으로 닫힌 뒤 30초 동안은 간격 상한 1초 |
| m14 | QR의 정확한 주소는 이 PC에서 폰으로 열 수 없습니다 | 방화벽 허용 전에는 Host가 폰에서 닿지 않습니다. 11.5절은 `relay.js`의 다른 포트나 `localhost`로 검증합니다 | 요구사항 10의 보고에 "QR 주소와 포트만 다른 주소로 확인했고, QR 그대로는 소유자가 UAC를 누른 뒤 확인"이라고 적도록 11.5절에 명시 |
| m15 | `Application.productName`을 네트워크 스레드에서 읽으면 안 됩니다 | 3.2절 `name`. Unity API는 메인 스레드 전용입니다. 9.1절의 정적 필드 복사 목록에 없습니다 | `Open()`에서 읽어 정적 필드에 둠 |

확인하지 못해 발견으로 올리지 않은 것 **[미확인]**: ① ML Kit 번들 스캐너가 사용 통계를 Google로 보내는지(사내 도구라면 ZXing이 대안). ② 대상 SDK 36에서 큰 화면 기기가 방향 고정을 무시하는지(Z Flip5 주 화면은 해당 없음, 다른 기기에서 다시 쓸 때의 문제). 둘 다 M6과 다음 범위에서 문서를 확인하면 됩니다.

## 5. 조정자가 정할 것

| # | 결정 | 권장 |
|---|---|---|
| 1 | "Wi-Fi 접속 허용" 토글을 없앨지(M1) | 없앰. LAN으로 페이지를 여는 기능이 사라지지만 지금도 쓰이지 않습니다 |
| 2 | 토큰 저장 위치(M2) | `Library/GameViewRemote/secret.json` |
| 3 | 배포와 측정에 쓰는 빌드 유형(M7) | 디버그 불가 `proto` 빌드. 진단 기능은 `BuildConfig` 값으로 켬 |
| 4 | 딥링크로 새 서버가 들어올 때 확인 창 1번(M7의 3) | 넣음. 처음 설치 흐름에는 뜨지 않습니다 |
| 5 | 페이지의 60Hz 기준선(M5) | 소유자 확인 목록으로. 워커는 가변 120Hz에서만 잽니다 |
| 6 | 무선 adb 터널로 앱을 재는 구간 W5b(B2) | 넣음. 창의 "무선 우선" 설정을 잠깐 바꿨다가 되돌립니다(브리프 3.2의 2번 범위) |

## 6. 확인하지 못한 것

- Unity 안에서 실행해 본 것은 없습니다. Mono에서 두 주소 동시 바인드, `ReceiveTimeout` 동작, PowerShell COM 읽기는 설계의 [미검증] 그대로입니다.
- 폰에서 실행해 본 것은 없습니다. `OnFrameRenderedListener`의 시각이 무엇인지, `setFrameRate`가 먹는지, 삼성 인터넷의 `intent://` 처리, logcat에 딥링크가 어떻게 찍히는지는 5단계에서 확인해야 합니다.
- 방화벽 규칙의 모양(9.7절)은 읽기만 했습니다. 겹치지 않는 포트 범위의 차단과 허용이 의도대로 동작한다는 설계의 판단은 Windows 방화벽의 "차단 우선" 규칙과 모순되지 않지만 실제로 넣어 보지 않았습니다.
- 측정 스크립트(`catjump/docs/GameViewRemote/measure/`)와 보고 3은 읽지 않았습니다. 8.2절과 8.5절이 `analyze.js`와 형식이 맞는지는 대조하지 않았습니다.
- QR 인코더 소스가 Unity 2022.3에서 컴파일되는지는 확인하지 않았습니다.

## 7. 판정

**조건부 승인.**

- 지금 시작해도 되는 것: 5단계 M0, M1, M2. 프로토콜, API, 빌드 도구에서 막히는 것을 찾지 못했습니다.
- M3 전에 설계에 반영: B1, B2, M5. 반영하지 않으면 "앱이 페이지와 같거나 낫다"는 판정을 낼 수 없습니다.
- M5(앱의 인증과 링크) 전에 반영: M7.
- 6단계 전에 반영: M1, M2, M3, M4, M6, M8.
- 사소 항목은 해당 이정표에서 같이 고치면 됩니다.

## 출처

- [MediaCodec.OnFrameRenderedListener](https://developer.android.com/reference/android/media/MediaCodec.OnFrameRenderedListener)
- [MediaCodec](https://developer.android.com/reference/android/media/MediaCodec)
- [Android Intents with Chrome](https://developer.chrome.com/docs/android/intents)
- [Macrobenchmark instrumentation arguments](https://developer.android.com/topic/performance/benchmarking/macrobenchmark-instrumentation-args)
- [Profile your app performance](https://developer.android.com/studio/profile)
- [Chrome to warn about HTTP connections by default from 2026 (heise)](https://heise.de/-10962985)
- [Chrome enforces secure connections by default in October 2026 (PPC Land)](https://ppc.land/chrome-enforces-secure-connections-by-default-in-october-2026/)
