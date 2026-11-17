# 03. 설계 — Game View Remote 안드로이드 앱과 Unity 쪽 변경

- 작성: 2026-11-14 새벽 (3단계 워커). 이 단계에서 쓴 파일은 `docs/02_PRD.md`(개정)와 이 문서뿐입니다. 폰, Unity, 방화벽, catjump 저장소는 건드리지 않았습니다.
- 입력: `00_BRIEF.md`(3.1절 포함), `00_current_system_and_history.md`("00 문서"), `01_research.md`("조사"), `02_PRD.md`("PRD"), `02_PRD_review.md`("리뷰"), 진단 워커 보고 3(`GVR_tap_stutter_report3.md`, "보고 3"), `catjump/docs/GameViewRemote/measure/`("측정 스크립트"), Unity 도구 코드.
- 읽은 코드의 시점: `GameViewRemoteServer.cs`(11-13 22:07:48, 34328B), `GameViewRemote.cs`·`GameViewRemoteAdb.cs`·`GameViewRemoteWindow.cs`(23:11:46), `GameViewRemoteCapture.cs`(21:47:59), `GameViewRemoteInput.cs`(21:55:36), `GameViewRemotePage.html`(23:30:02, 50310B). 조정자가 "폴더가 다시 바뀌었다"고 알린 뒤(23:44 이후) 다시 확인했을 때 `.cs`와 `.html`의 크기와 시각은 그대로였고 `README.md`만 23:44:37로 바뀌어 있었습니다. 이 문서의 줄 번호는 그 시점 것입니다. **6단계는 시작할 때 다시 읽어 대조합니다.**

> **5단계 개정(2026-11-14)**: 설계 리뷰(`03_design_review.md`)의 모든 항목에 대한 결정과 5단계 구현에서 바뀐 것을 [16장](#16-설계-리뷰-결정-기록과-5단계에서-바뀐-것)에 적고, 본문의 해당 절을 고쳤습니다. 고친 곳에는 `(리뷰 M1)` 처럼 근거를 붙였습니다. **Unity 쪽 워커는 3장(프로토콜), 5.2절, 9장과 16장을 기준으로 삼습니다.**

표시 규칙: **[코드]** 직접 읽음 / **[측정·출처]** 누가 잰 값 / **[계산]** 이 문서를 쓰며 PC에서 계산해 확인 / **[문서]** 조사 문서가 인용한 1차 자료 / **[설계]** 이 문서의 결정 / **[미검증]** 실기기나 Unity 에서 아직 확인하지 않은 가정. 미검증 항목은 [13장](#13-미검증-가정과-확인-시점)에 모았습니다.

## 0. 한눈에 보는 결정

| # | 결정 | 이유 |
|---|---|---|
| 1 | 전송은 지금의 WebSocket(TCP) + 프로토콜 2 프레임 형식을 그대로 쓴다. 앱 전용 전송을 만들지 않는다 | 프레임 형식, 수신 확인, 역압이 이미 동작하고 Chrome 페이지와 서버 코드를 공유한다. UDP 는 무선 MAC 계층의 정체를 없애지 못한다(PRD 4.6, 4.7) |
| 2 | 인증은 WebSocket 업그레이드의 **헤더 한 쌍 + 첫 메시지 1개**로 한다. 헤더가 없으면 지금 서버와 완전히 같다 | 왕복 1번. Unity 를 고치기 전에도 앱이 USB 로 붙는다(첫 이정표) |
| 3 | H.264 는 앱에서 AVCC → Annex B 로 바꾼다(제자리 덮어쓰기) | 서버와 페이지를 건드리지 않는다 |
| 4 | QR 은 `http://<PC>:<포트>/c#v=1&s=…&t=…` 하나. 토큰은 `#` 뒤. 앱 딥링크는 커스텀 스킴 `seeplay-gvr://` | 모든 카메라가 http 링크를 연다. 토큰이 서버 요청 줄에 실리지 않는다 |
| 5 | Unity 쪽은 새 파일의 "앞단"(Host)이 QR 주소에만 따로 듣고, 인증을 마친 연결을 기존 서버에 넘긴다. 기존 서버의 루프백 리스너와 수명은 그대로 | 기존 파일 수정이 고리 몇 개로 끝나고 Chrome 페이지 경로가 바뀌지 않는다 |
| 6 | 영상을 받는 클라이언트는 1개. 나머지는 연결을 유지한 채 대기. 자동 재접속은 자리를 뺏지 않는다 | 리뷰 B2 |
| 7 | 앱의 영상 경로(수신 → 디코더 → Surface)에는 UI 스레드가 없다. 터치는 UI 스레드에서 받아 큐에 넣기만 한다 | 탭 끊김의 원인이 "터치 중 메인 스레드가 영상을 받지 못함"이었다(보고 3) |
| 8 | 표시 방식 2가지 × 지터 버퍼 3가지 × 주사율 정책 4가지를 실행 중 바꿀 수 있게 하고, 전부 같은 계측으로 잰다 | 무선에서의 맞바꿈(부드러움 대 지연)은 실측으로만 정할 수 있다 |
| 9 | 계측의 정의와 내보내기 형식을 측정 스크립트(`analyze.js`)와 맞춘다 | 새 웹 페이지가 기준선이다. 같은 정의로 비교해야 한다 |
| 10 | 구현 순서: 영상(USB, Unity 수정 없음) → 계측과 표시 일정 → 입력 → 무선 → 인증/딥링크/재접속 → 스캐너 → Unity 쪽 | 스캐너가 늦어져도 측정이 밀리지 않는다 |
| 11 | 다른 프로젝트에서 다시 쓰고 기능을 붙일 수 있게 한다(소유자 요구사항 12): 기능 협상(`caps`), 모르는 메시지는 무시한다는 규칙, 채널을 늘릴 자리, 프로젝트별 설정 파일, 게임 코드 의존 없음. **구체 기능은 미리 만들지 않고 붙일 자리만 둔다.** [15장](#15-확장-지점과-다른-프로젝트에서-다시-쓰기) | 브리프 요구사항 12 |

## 1. 전체 구조

```
┌──────────────────────── PC ────────────────────────┐          ┌─────────────── 폰 ────────────────┐
│ Unity Editor                                        │          │ 앱 kr.co.seeplay.gvr               │
│  ┌ 기존 ───────────────────────────────┐            │          │  UI 스레드: 화면, 터치 수신, HUD    │
│  │ GameViewRemoteServer                │            │          │  conn 스레드: 상태 기계, 접속 경주  │
│  │  127.0.0.1:7300 (Play 중에만)       │◀─ adb reverse (USB) ──│  video-rx: 수신 → 확인 → 디코더 입력│
│  │  /  /video /input /stats /debug     │            │          │  codec 스레드: MediaCodec 콜백      │
│  └───────────▲─────────────────────────┘            │          │  pacer 스레드: vsync 마다 표시 결정 │
│     Adopt()  │ (인증을 마친 소켓)                    │          │  input-tx / input-rx                │
│  ┌ 새 파일 ──┴─────────────────────────┐            │          │  MediaCodec ─▶ SurfaceView          │
│  │ GameViewRemoteHost (앞단)           │            │          └────────────────────────────────────┘
│  │  <QR 주소>:7300 (Start 켠 동안 내내) │◀──── Wi-Fi LAN 직접 (기본 경로) ────  폰이 PC로 건다
│  │  /c  /apk  /app.json                │            │
│  │  /video /input: 인증 → 넘김 / 대기실 │            │          시험 전용: tools/relay.js (node.exe, LAN 포트 → 127.0.0.1:7300)
│  │ Gate(인증·Origin) Seats(자리) QR 창  │            │                     tools/dial.js  (PC가 폰의 듣는 포트로 건다, F-41)
│  └─────────────────────────────────────┘            │                     tools/replay.js (프로토콜 2 재생 서버 + 인증)
└─────────────────────────────────────────────────────┘
```

경로 세 가지와 쓰임:

| 경로 | TCP 를 거는 쪽 | 서버 쪽 주소 | 인증 | 쓰임 |
|---|---|---|---|---|
| **LAN 직접**(기본) | 폰 | Host `<QR 주소>:포트` | 필수 | 제품의 기본 경로. 이 PC에서는 방화벽 허용(UAC) 뒤에만 Unity.exe 가 직접 받음 |
| USB adb reverse(대체) | 폰(→ 127.0.0.1) | 기존 서버 `127.0.0.1:포트` | 앱이 토큰을 갖고 있으면 함. 고치기 전 서버에는 없음 | 개발 기준선, 관리자 동의가 안 되는 PC |
| PC가 거는 방식(실험 F-41) | PC | 폰 `wlan0:7301` | 시험 도구가 구현한 만큼 | 방화벽도 중계의 인바운드 허용도 없이 LAN 직접 수치를 잼 |

## 2. 구성 요소와 책임

### 2.1 안드로이드 앱 (`GameViewRemoteApp/android/`, 패키지 `kr.co.seeplay.gvr`)

| 모듈(클래스) | 책임 | 스레드 |
|---|---|---|
| `MainActivity` | 단일 Activity(`singleTask`). 수명 주기, 전체 화면, 화면 켜짐 유지, 방향, 뒤로 가기, 인텐트(딥링크, adb 명령) 수신, 화면 전환(스캔 / 상태 / 재생) | UI |
| `ui.VideoSurface` | `SurfaceView` 를 영상 비율에 맞춰 가운데 배치. Surface 생성/소멸을 `Decoder` 에 알림. 터치를 `TouchMapper` 에 넘김 | UI |
| `ui.StatusOverlay` | 상태 문구(PRD 2.6)와 버튼([여기서 보기], [QR 다시 찍기], [업데이트]) | UI |
| `ui.HudView` | 진단 HUD. 0.5초마다 `Counters` 의 스냅숏을 글자로 표시. [기록 시작/끝] | UI |
| `ui.Scanner` | CameraX 미리보기 + **ZXing core**(QR 인식, 기기 안에서만, 네트워크 없음). 읽은 문자열을 `LinkParser` 에 넘김. (5단계 결정: ML Kit 대신. 리뷰가 확인하지 못한 사용 통계 전송 문제를 피하고 APK 가 작아짐) | UI + CameraX 실행기 |
| `link.LinkParser` | QR 주소와 딥링크를 같은 `ServerLink` 값으로 해석(4장). 세 입구 공용 | 호출한 스레드 |
| `link.ServerStore` | 기억한 서버(서버 표시, 토큰, 성공한 주소들, 마지막 경로). `SharedPreferences` | conn |
| `net.WsClient` | `java.net.Socket` 위의 최소 WebSocket 클라이언트(3.1절). 업그레이드 요청/응답 헤더를 그대로 다룸. 메시지 1개 = `OutputStream.write` 1번 | 호출한 스레드 |
| `net.Auth` | 인증 값 계산과 검증(3.3절). 순수 함수, 시험 벡터 단위 시험 포함 | 호출한 스레드 |
| `net.Dialer` | 주소 후보를 동시에 걸어 먼저 닿는 쪽을 고름. LAN 주소는 Wi-Fi 망에 묶음. 연결 제한 3초 | conn + 후보당 임시 스레드 |
| `net.ReverseListener` | (실험 F-41) `ServerSocket` 으로 듣고, 들어온 소켓의 첫 줄(`GVR-REVERSE/1 <채널>`)로 채널을 정해 `WsClient` 에 넘김 | 전용 스레드 |
| `session.Session` | 연결 상태 기계(5장). 영상/입력 채널 생성과 정리, 재접속 일정, 오류 분류 | conn(단일 스레드 실행기) |
| `session.VideoChannel` | 영상 소켓 읽기 루프: 메시지 수신 → 도착 시각 기록 → `a` 전송 → 헤더 해석 → `Decoder` 로. `caps`, `key`, `take` 전송 | video-rx |
| `session.InputChannel` | 입력 소켓: 보내기 큐와 쓰기 스레드, 읽기 스레드(ping 응답, 닫힘 감지) | input-tx, input-rx |
| `session.Channel`(인터페이스), `session.TextRouter` | 채널의 공통 모양(`path`, `open(WsClient)`, `close()`)과, 받은 텍스트를 `t` 값으로 처리기에 나눠 주는 표(모르는 `t` 는 버림). 채널과 메시지를 늘릴 자리(15.2절) | 채널의 스레드 |
| `video.FrameHeader` | 28바이트 헤더 해석(3.4절) | video-rx |
| `video.AvcConfig` | avcC → SPS/PPS(시작 코드 붙임), AVCC → Annex B 제자리 변환, 길이 검증 | video-rx |
| `video.Decoder` | MediaCodec 선택, 설정, 비동기 콜백, 입력 큐, 설정 메시지 처리, 오류 복구(7.3절) | video-rx + codec |
| `video.Pacer` | 디코딩된 버퍼를 언제 Surface 로 내보낼지 결정(7.4절). 버림 판단 | codec + pacer |
| `video.RefreshPolicy` | 주사율 정책 적용과 현재 주사율 추적(7.5절) | UI(창 속성) + pacer(읽기) |
| `input.TouchMapper` | `MotionEvent` → 0~1 좌표 → 입력 JSON. 포인터별 상태, 입력 번호 `s` | UI |
| `diag.Counters` | 원자 카운터와 분포 누적기(8장). 프레임마다 할당 없음 | 여러 스레드(쓰기), UI(읽기) |
| `diag.TraceRing` | 프레임별/터치별 원자료 고리 버퍼(원시 배열) | 여러 스레드 |
| `diag.Exporter` | 기록 구간을 JSON 파일로 저장(측정 스크립트 형식) | 낮은 우선순위 스레드 |
| `diag.LinkInfo` | 경로(무선 직접 / USB adb / PC가 건 연결), 서버 주소, Wi-Fi 주파수·링크 속도·RSSI | conn |

### 2.2 Unity 쪽 (`catjump/Assets/04_Scripts/Editor/GameViewRemote/`)

기존 파일의 책임은 00 문서 1.3 그대로입니다. 새로 생기는 것만 적습니다(파일별 변경은 9장).

| 새 파일 | 책임 |
|---|---|
| `GameViewRemoteHost.cs` | 앞단. "Start 켜짐" 상태(도메인 리로드를 넘어 유지), QR 주소의 리스너, HTTP 경로(`/c`, `/apk`, `/app.json`), Play 밖 대기실, 인증된 연결을 기존 서버에 넘기기, 서버 표시와 토큰 보관, [새 QR 발급] |
| `GameViewRemoteGate.cs` | 입장 심사. `Origin` 검사, 앱 인증 절차(101 응답 쓰기, 클라이언트 증명 읽기), 인증 전 연결 수와 시간 제한. 앞단과 기존 서버가 함께 씀 |
| `GameViewRemoteAuth.cs` | 인증 값 계산(HMAC-SHA256), base64url, 상수 시간 비교, 헤더 값 해석. 순수 함수. 메뉴 `Tools/Game View Remote/Self Test` 가 시험 벡터를 검사 |
| `GameViewRemoteSeats.cs` | 자리 배정(누가 영상을 받고 누가 대기인지), [여기서 보기] 처리, 접속 기기 목록 |
| `GameViewRemoteWs.cs` | 작은 WebSocket 도우미: 클라이언트 프레임 1개 읽기, 텍스트/닫기 쓰기(메시지 1개 = 쓰기 1번). Gate 와 대기실이 씀 |
| `GameViewRemoteSettings.cs` | 프로젝트별 설정 파일(`UserSettings/GameViewRemote.json`) 읽기/쓰기 |
| `GameViewRemoteAppInfo.cs` | 앱에 묶인 상수를 한곳에: 패키지 이름, 딥링크 스킴, 기능 목록(`caps`), 인증/QR 버전. 다른 이름으로 앱을 내려면 이 파일과 앱의 `build.gradle.kts` 만 바꿈 |
| `GameViewRemoteChannels.cs` | 채널 등록 표(경로 → 처리기). 시제품에서는 비어 있음. 새 채널을 붙일 자리(15.1절) |
| `GameViewRemoteNet.cs` | 어댑터 나열과 주 주소 선택(조사 3.5 규칙) |
| `GameViewRemoteQr.cs` | QR 내용 만들기, 인코더 호출, `Texture2D` 그리기, PNG 내보내기 |
| `ThirdParty/QrCodeGenerator/` | MIT 인코더 소스(자체 네임스페이스) + `LICENSE` |
| `GameViewRemoteLanding.html` | 착륙 페이지(요청 때마다 디스크에서 읽음) |
| `GameViewRemoteFirewall.cs` | Windows 방화벽 상태 읽기, [방화벽 허용]/[되돌리기] 스크립트 실행 |
| `GameViewRemoteAppAdb.cs` | [USB 로 앱 설치], [USB 로 연결], [연결 점검] |
| `GameViewRemoteHostGUI.cs` | 창의 새 구역(Start/Stop, QR, 방화벽, 접속 기기, USB 버튼) |
| `App~/GameViewRemote.apk`, `App~/app.json` | 도구에 든 APK 와 그 버전 정보. `~` 로 끝나는 폴더는 Unity 가 가져오지 않음 |

### 2.3 시험 도구 (`GameViewRemoteApp/tools/`, Node, 5단계에서 만듦)

| 파일 | 하는 일 |
|---|---|
| `replay.js` | 프로토콜 2 재생 서버. ffmpeg 를 실제 서버와 같은 인자로 돌려(테스트 패턴 영상 입력) FLV 태그를 읽고 28바이트 헤더를 붙여 보냄. 수신 확인 역압, `caps`/`key`, 입력 소켓의 ping 응답과 `inputSeq` 반영(받은 입력 번호를 다음 프레임 헤더에 넣음), 3장의 인증과 닫기 코드, 대기실/자리 상태까지 구현. `--auth`, `--port`, `--bind` |
| `relay.js` | TCP 중계: `<LAN 주소>:<시험 포트>` → `127.0.0.1:7300`. 받은 덩어리를 그대로 한 번에 씀. 측정하는 동안만 띄움(인증 없음) |
| `dial.js` | PC가 폰으로 거는 시험(F-41): 폰 `wlan0:7301` 로 걸고 첫 줄을 보낸 뒤 `127.0.0.1:7300` 또는 `replay.js` 와 잇는다 |
| `static.js` | 착륙 페이지 초안과 빈 APK 를 내주는 정적 서버(5단계 첫 작업, PRD 5.1) |
| `linktest-native.md` | 측정 스크립트의 `linktest.js` 와 같은 조건(12.5KB, 16.7ms)을 앱으로 받는 방법 |

- LAN 에 여는 시험 서버는 전부 `node.exe` 로 띄웁니다(이 PC에서 공용 프로필 인바운드가 이미 허용. 보고 3에서 `0.0.0.0:7312` 로 실제로 닿았음). **Python 으로 LAN 에 열지 않습니다**(Windows 알림 창이 뜨고 취소되면 차단 규칙이 생김, 조사 2.5).
- 포트: 실세션 7300 은 쓰지 않습니다. 재생 서버 7311, 중계 7310, 정적 서버 7314, 폰이 듣는 포트 7301. 7312/7313 은 측정 스크립트(`linktest.js`)가 씁니다.

## 3. 프로토콜

### 3.0 버전

| 이름 | 지금 값 | 어디서 확인 | 다르면 |
|---|---|---|---|
| 프레임 프로토콜 | 2 | 서버의 첫 텍스트 `{"t":"hello","v":2}`, 인증 헤더의 `proto` | 앱은 연결을 닫고 "버전이 맞지 않습니다" 오류(종료 상태) |
| 인증 절차 | 1 | `X-GVR-Auth` 의 `v` | 서버가 426 으로 거절 / 앱이 종료 상태 오류 |
| QR 형식 | 1 | QR 주소의 `v` | 앱이 "앱을 업데이트하세요" |
| 앱 버전(versionCode) | 1부터 | 인증 헤더의 `app`(양방향) | 연결은 되고, 앱이 낮으면 [업데이트] 안내(권장) |
| 기능 목록 | `h264`, `seat`, `take` | 인증 헤더의 `caps`(양방향) | 버전을 올리지 않고 기능을 더하는 길. 한쪽이 모르는 기능은 쓰지 않을 뿐 연결은 됨(15.1절) |

버전 번호를 올리는 것은 **옛 쪽이 새 쪽의 메시지를 잘못 해석하게 되는 변경**(헤더 28바이트의 뜻, 인증 계산식)뿐입니다. 메시지나 채널을 더하는 것은 `caps` 로 합니다.

### 3.1 WebSocket 수준 (지금 서버 그대로)

요청(앱이 보냄. **요청 전체를 `write` 1번으로**):

```
GET /video HTTP/1.1\r\n            (입력 채널은 /input)
Host: <호스트>:<포트>\r\n
Upgrade: websocket\r\n
Connection: Upgrade\r\n
Sec-WebSocket-Key: <무작위 16바이트의 base64>\r\n
Sec-WebSocket-Version: 13\r\n
X-GVR-Auth: <3.2절>\r\n            (인증할 때만)
\r\n
```

- `Origin` 헤더를 보내지 않습니다(F-43 의 검사는 `Origin` 이 있는 요청에만 적용).
- 확장(permessage-deflate)과 하위 프로토콜을 제안하지 않습니다. 서버는 `Sec-WebSocket-Key` 만 봅니다 [코드: `GameViewRemoteServer.cs:360~364`].
- 응답은 `\r\n\r\n` 까지 읽어 상태 줄과 헤더를 해석합니다. 101 이 아니면 3.5절의 오류로 처리합니다. `Sec-WebSocket-Accept` 는 검증합니다(SHA-1(key + `258EAFA5-E914-47DA-95CA-C5AB0DC85B11`) 의 base64).

프레임:

| 방향 | 형식 | 근거 |
|---|---|---|
| 서버 → 앱 | 마스크 없음, 항상 FIN. 바이트 0 = `0x80 \| opcode`(텍스트 1, 바이너리 2, 닫기 8, pong 0xA). 길이: 125 이하면 바이트 1, 126~65535 면 바이트 1 = 126 뒤 u16 **빅 엔디언**, 그 이상이면 바이트 1 = 127 뒤 u64 빅 엔디언 | [코드: `WriteFrameHeader`] |
| 앱 → 서버 | 마스크 필수: 바이트 1 의 최상위 비트 1, 길이 뒤에 마스크 4바이트, 내용은 `payload[i] ^ mask[i & 3]`. 항상 FIN, 조각 없음. 메시지 1개 최대 64KB(넘으면 서버가 닫음) | [코드: `ReadLoop`, `MAX_CLIENT_MESSAGE_BYTES`] |

- 앱이 보내는 메시지는 전부 125바이트 이하의 텍스트라 프레임은 `0x81, 0x80|len, mask×4, 내용` 입니다. 미리 만든 배열에 채워 `write` 1번으로 보냅니다(00 문서 L1). 예: 수신 확인 `a` = 7바이트 `81 81 m0 m1 m2 m3 (0x61^m0)`.
- 소켓 옵션: `tcpNoDelay = true`, `keepAlive = true`, 수신 버퍼 256KB(`setReceiveBufferSize`, 연결 전에). 읽기는 64KB `BufferedInputStream` 을 거치고, 쓰기는 버퍼 없이 `socket.getOutputStream()` 에 직접 합니다.
- 서버는 ping 을 보내지 않습니다. 앱은 WebSocket ping 대신 입력 채널의 `{"t":"p"}` 를 씁니다(3.6절). 서버가 보낸 ping(0x9)을 받으면 pong 으로 답합니다(지금은 올 일이 없음).
- 닫기: 앱이 끝낼 때는 닫기 프레임(코드 1000)을 보내고 소켓을 닫습니다. 서버의 닫기 프레임을 받으면 코드(내용 앞 2바이트, 빅 엔디언)를 읽어 3.5절대로 처리합니다. 닫기 프레임 없이 TCP 가 끊기면 "끊김"으로 다시 시도합니다.

### 3.2 인증 절차 (인증 1)

```
앱 → 서버   GET /video …  +  X-GVR-Auth: v=1; sid=<서버 표시>; cn=<클라이언트 임의 값>; proto=2; app=<versionCode>; did=<기기 표시>; dev=<기기 이름>; take=<0|1>
서버 → 앱   HTTP/1.1 101 … +  X-GVR-Auth: v=1; sn=<서버 임의 값>; mac=<서버 증명>; proto=2; app=<도구에 든 APK 의 versionCode>
앱          서버 증명을 검증. 틀리면 소켓을 닫고 "PC를 확인할 수 없습니다. Unity 창의 QR 을 다시 찍으세요."(종료 상태)
앱 → 서버   첫 WebSocket 메시지(텍스트): {"t":"auth","mac":"<클라이언트 증명>"}
서버        3초 안에 오지 않거나 틀리면 닫기 코드 4401 로 닫음. 맞으면 이 연결을 기존 서버(또는 대기실)에 넣음
서버 → 앱   {"t":"hello","v":2}  →  {"t":"seat",…}(3.5절)  →  이후는 프로토콜 2 그대로
```

헤더 값의 문법: `이름=값` 을 `; `(세미콜론 + 공백 1개)로 잇습니다. 받는 쪽은 `;` 로 나누고 앞뒤 공백을 지운 뒤 첫 `=` 에서 가릅니다. 모르는 이름은 무시합니다. 순서는 상관없습니다.

| 이름 | 방향 | 값 | 비고 |
|---|---|---|---|
| `v` | 양쪽 | `1` | 인증 절차 버전 |
| `sid` | 앱 → | 서버 표시. `[A-Z2-7]{8}`(무작위 40비트의 base32) | QR 의 `s` |
| `cn` | 앱 → | 무작위 16바이트의 base64url(패딩 없음, 22자) | 연결마다 새로. `SecureRandom` |
| `sn` | ← 서버 | 무작위 16바이트의 base64url(22자) | 연결마다 새로. `RNGCryptoServiceProvider` |
| `mac` | ← 서버 | 서버 증명(base64url 43자) | 아래 |
| `proto` | 양쪽 | `2` | 프레임 프로토콜 번호 |
| `app` | 양쪽 | 정수 | 앱 → : 자기 versionCode. ← 서버: 도구에 든 APK 의 versionCode(없으면 0) |
| `did` | 앱 → | 설치마다 한 번 만든 무작위 `[a-z0-9]{12}` | 자리 배정에서 "같은 기기"를 알아보는 키. 영상과 입력 소켓이 같은 값 |
| `dev` | 앱 → | `Build.MODEL` 에서 `[A-Za-z0-9_-]` 만 남긴 것, 최대 24자 | 창 표시용(권장 항목) |
| `take` | 앱 → | `1` = 사람이 방금 연결을 시작함(QR, 딥링크, [여기서 보기], 앱 아이콘으로 실행). `0` = 자동 재접속 | 자리 배정(3.5절) |
| `caps` | 양쪽 | 기능 이름을 `,` 로 이은 목록. 이름은 `[a-z0-9.-]+`. 지금 앱: `h264,seat`. 지금 서버: `h264,seat,take` | **기능 협상**(15.1절). 양쪽이 모두 적은 기능만 씀. 모르는 이름은 무시. 없으면 빈 목록(프로토콜 2 의 기본 동작만) |
| `name` | ← 서버 | 프로젝트 표시 이름(`Application.productName` 에서 `[A-Za-z0-9_-]` 만 남긴 것, 최대 24자) | 앱 화면에 "어느 프로젝트"인지 표시. 같은 PC의 여러 프로젝트 구분 |

증명 값:

```
K        = 토큰의 원래 바이트(16바이트. QR 의 t 를 base64url 로 풀어 얻음)
문자열   = "GVR-AUTH-1|" + 역할 + "|" + 채널 + "|" + sid + "|" + cn + "|" + sn        (ASCII)
           역할: 서버 증명은 "srv", 클라이언트 증명은 "cli".  채널: "video" 또는 "input".
           cn, sn 은 헤더에 실린 base64url 글자 그대로.
증명     = base64url( HMAC-SHA256(K, 문자열) )     (32바이트 전체, 패딩 없음, 43자)
```

- 검증은 상수 시간 비교로 합니다. 받은 `cn`/`sn` 이 22자의 base64url 이 아니면 거절합니다.
- 역할 문자열이 달라서 서버가 만든 값을 클라이언트 증명으로 되쓸 수 없고(반사 공격), 채널이 묶여 있어 영상 소켓의 증명을 입력 소켓에 쓸 수 없고, 양쪽 임의 값이 들어가 다시 보내기(재전송 공격)가 안 됩니다. 서버가 임의의 `cn` 에 대해 "srv" 증명을 내주는 것은 "cli" 증명을 만드는 데 도움이 되지 않습니다.
- 토큰은 어느 방향으로도 전송되지 않습니다.
- **인증은 연결의 시작만 보호합니다.** 그 뒤의 평문 TCP 를 엿보거나 가로채 터치를 넣는 것은 막지 않습니다(PRD NF-63).

**시험 벡터** [계산: 이 PC의 Python `hmac`/`hashlib` 으로 계산]. 앱(`net.Auth` 단위 시험), 시험 서버(`replay.js`), Unity(`Self Test` 메뉴)가 모두 이 값을 내야 합니다.

- 주의(5단계): 아래 벡터의 `sid` `GVRTEST1` 은 HMAC 계산 확인용 글자일 뿐이고 **서버 표시 형식 `[A-Z2-7]{8}` 에는 맞지 않습니다**('1'은 base32 글자가 아님). `Proof()` 시험에는 그대로 쓰되, QR/링크 해석 시험과 시험 서버(`replay.js` 기본값)는 `GVRTEST2` 를 씁니다. 형식 검사(`LinkParser`, Gate 의 `sid` 비교)에 `GVRTEST1` 을 넣으면 거절되는 것이 정상입니다.

```
토큰 바이트 (hex)   000102030405060708090a0b0c0d0e0f
토큰 t              AAECAwQFBgcICQoLDA0ODw
sid                 GVRTEST1
cn  (바이트 10..1f) EBESExQVFhcYGRobHB0eHw
sn  (바이트 20..2f) ICEiIyQlJicoKSorLC0uLw

video / srv   문자열 GVR-AUTH-1|srv|video|GVRTEST1|EBESExQVFhcYGRobHB0eHw|ICEiIyQlJicoKSorLC0uLw
              증명   zlYoCj8gUA86fWTs4qY2TadXPchzopR2enL1BEPxpJI
video / cli   문자열 GVR-AUTH-1|cli|video|GVRTEST1|EBESExQVFhcYGRobHB0eHw|ICEiIyQlJicoKSorLC0uLw
              증명   Mi9NohBmpvSBE-2kTt0VfZBmIWgNa6EWB0QnffebIdo
input / srv   증명   Kk7tLqrnHm_XeaEg6zmPoh0GPiqeXig3W5oM7UBACbE
input / cli   증명   HvOPi01ERvyDRbwzKcVNWcHperDPX6_b4mrWq30vi3w
```

**인증 헤더가 없는 경우(호환)**

| 서버 | 앱이 `X-GVR-Auth` 를 보냄 | 앱이 안 보냄 |
|---|---|---|
| 고치기 전 서버 | 헤더를 무시하고 평범한 101(응답에 `X-GVR-Auth` 없음). 앱은 **대상이 루프백(127.0.0.1)일 때만** 인증 없는 서버를 받아들이고 HUD 에 "인증 없음(구버전 서버)"를 표시. 대상이 LAN 주소면 닫고 "PC를 확인할 수 없습니다" | 지금과 같음 |
| 고친 서버, **원격 주소가 루프백** | 인증 절차를 진행(토큰이 틀리면 거절) | 지금처럼 받음(Chrome 페이지). `Origin` 검사만 추가 |
| 고친 서버, **원격 주소가 루프백이 아님**(어느 리스너로 들어왔든) | 인증 절차를 진행 | 401 로 거절 |

- "LAN 인가"는 호출한 쪽이 넘기는 값이 아니라 **소켓의 원격 주소**로 정합니다(리뷰 M1). 인증 없는 LAN 리슨은 없습니다(브리프 3.3의 1).

- 시험 전용 예외: 진단 빌드(`BuildConfig.DIAG`, 10.3절)에서 **브라우저가 연 인텐트가 아닐 때**(4.2절) 링크에 `legacy=1` 이 있으면 LAN 주소의 인증 없는 서버도 받아들입니다(`relay.js`·`dial.js` 로 고치기 전 Unity 서버에 붙을 때). HUD 에 `none(LEGACY, LAN)` 으로 표시합니다. `DIAG` 가 꺼진 릴리스 빌드에서는 이 분기가 동작하지 않습니다.

### 3.3 인증 전 오류 (HTTP 응답)

업그레이드 전에 거절할 때 서버는 HTTP 응답 1개를 쓰고 닫습니다. 본문은 JSON 이고 같은 코드를 헤더에도 넣습니다.

```
HTTP/1.1 <상태>\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: <n>\r\nX-GVR-Error: <코드>\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n
{"t":"err","code":"<코드>","msg":"<사람이 읽는 한국어 문장>"}
```

| 상태 | 코드 | 뜻 | 앱의 처리 |
|---|---|---|---|
| 401 | `auth_required` | LAN 연결인데 `X-GVR-Auth` 가 없음 | (앱은 항상 보내므로 시험 도구용) |
| 403 | `unknown_server` | `sid` 가 이 서버의 것이 아님(다른 PC의 QR, 서버 표시를 새로 만듦) | 종료 상태: "이 QR 은 다른 PC의 것입니다. Unity 창의 QR 을 다시 찍으세요." |
| 426 | `auth_version` | `v` 를 모름 | 종료 상태: 버전 불일치 |
| 426 | `proto_version` | `proto` 가 서버와 다름 | 종료 상태: 버전 불일치(어느 쪽이 낮은지 본문 `msg` 에) |
| 400 | `bad_request` | `cn`·`did` 형식 오류 등 | 종료 상태: 내부 오류로 표시(버그) |
| 403 | `bad_origin` | `Origin` 이 자기 페이지가 아님(F-43) | (브라우저용) |
| 404 | `not_found` | LAN 에 열리지 않은 경로(`/`, `/stats`, `/debug` 포함) | — |
| 503 | `busy` | 인증 전 연결이 8개 | 1초 뒤 다시 시도 |

### 3.4 `/video` 메시지

서버 → 앱 바이너리 = **헤더 28바이트(리틀 엔디언) + 내용** [코드: `GameViewRemoteCapture.WriteHeader`, 00 문서 1.5.3].

| 오프셋 | 타입 | 필드 | 앱에서의 쓰임 |
|---|---|---|---|
| 0 | u32 | `frameId` | 기록 키. 설정 메시지도 번호를 하나 씀(연속성 검사에 포함) |
| 4 | u16 | `width` | 디코더 설정, 화면 비율 |
| 6 | u16 | `height` | 같음 |
| 8 | i32 | `inputSeq` | 터치 → 표시 측정(8.3절) |
| 12 | u32 | `encodeUs` | HUD |
| 16 | u32 | `captureToEncodedUs` | HUD |
| 20 | u32 | `flags` | 비트로 판정: 1 = 같은 화면 재전송(JPEG), 2 = H.264, 4 = 키프레임, 8 = 설정(avcC) |
| 24 | u32 | `captureTime` | 0.1ms 단위, 서버 Start 뒤 흐른 시간. 표시 일정의 기준 시계. 약 119시간에 한 바퀴(무시) |
| 28 | — | 내용 | 아래 |

내용의 형식:

| `flags` | 내용 | 앱의 처리 |
|---|---|---|
| `2 \| 8`(설정. `4` 가 같이 서 있어도 설정) | avcC: `[0]=1, [1]=profile, [2]=compat, [3]=level, [4]=0xFC \| (길이 크기−1), [5]=0xE0 \| SPS 수, {u16 BE 길이, SPS}×n, PPS 수(1바이트), {u16 BE 길이, PPS}×m`. 뒤에 더 있으면 무시 | `[4] & 3 == 3`(길이 4바이트)이 아니면 오류. 첫 SPS 와 첫 PPS 를 꺼내 각각 `00 00 00 01` 을 붙여 보관. 7.3절 |
| `2` 또는 `2 \| 4` | AVCC: `{u32 BE 길이, NAL}` 반복 = 프레임 1장 | 각 길이 4바이트를 `00 00 00 01` 로 **같은 자리에서** 덮어씀. 길이의 합 + 4×개수가 내용 길이와 다르면 손상으로 세고 버림 + 키프레임 요청 |
| `2` 가 없음 | JPEG(MJPEG) | 표시하지 않음. 확인은 보냄. 2초 넘게 이어지면 상태 "PC가 H.264 를 보낼 수 없습니다" |

서버 → 앱 텍스트:

| 메시지 | 뜻 | 근거 |
|---|---|---|
| `{"t":"hello","v":2}` | 프레임 프로토콜 번호. 접속 직후 첫 메시지 | [코드] |
| `{"t":"s","stale":true,"msg":"…"}` / `{"t":"s","stale":false}` | 게임 화면이 멈춘 이유 / 풀림. 앱은 `msg` 를 화면 아래에 그대로 표시 | [코드] |
| `{"t":"seat","s":"active"}` | 이 연결이 영상을 받는 자리 | [설계] 새로 추가(`caps` 에 `seat` 를 적은 앱 연결에만 보냄) |
| `{"t":"seat","s":"standby","who":"page\|app","msg":"…"}` | 자리를 내줌. 영상이 오지 않고 입력이 주입되지 않음 | [설계] |
| `{"t":"seat","s":"lobby","msg":"…"}` | Play 밖 대기실. Play 가 시작되면 서버가 4410 으로 닫고 앱이 곧바로 다시 붙음 | [설계] |

- 앱은 모르는 `t` 를 무시합니다. JSON 은 `org.json` 으로 해석합니다(받는 쪽은 형식에 민감하지 않게).
- 순서: 서버는 `hello` 를 먼저, `seat` 를 그 뒤에 보냅니다(리뷰 M8-b). 앱은 순서에 기대지 않게 구현했습니다(`TextRouter` 가 온 순서대로 처리, `seat` 가 먼저 와도 동작).

앱 → 서버 텍스트. **서버의 판정이 문자열 검사이므로 아래 글자 그대로 보냅니다**(JSON 라이브러리로 만들지 않고 상수 바이트 배열) [코드: `HandleText`]:

| 보내는 글자 | 언제 | 주의 |
|---|---|---|
| `a` | 바이너리 메시지 1개를 다 읽은 직후, 디코딩 전에, 수신 스레드에서. 설정 메시지와 JPEG 에도 | 영상 소켓에서 `{` 로 시작하지 않는 텍스트는 전부 확인으로 세어짐. 다른 글자를 보내지 않음 |
| `{"t":"caps","h264":1}` | `hello` 를 받은 직후 1번 | 공백 없이. `"h264":1` 이 그대로 있어야 함 |
| `{"t":"key"}` | 7.3절의 조건에서만. 2초에 1번 이하 | 서버는 ffmpeg 를 다시 시작(약 0.43초 공백) |
| `{"t":"take"}` | 사용자가 [여기서 보기]를 누름 | 고친 서버만 이해. 고치기 전 서버는 무시(`"caps"`도 `"key"`도 아님) |
| `{"t":"auth","mac":"…"}` | 인증 절차의 첫 메시지 | 3.2절 |

수신 확인과 역압의 서버 쪽 규칙(00 문서 1.5.5)은 그대로입니다. 앱이 지킬 것은 "받자마자 1개씩"뿐입니다.

### 3.5 닫기 코드와 자리 상태

서버 → 앱 닫기 프레임의 코드(고친 서버):

| 코드 | 뜻 | 앱의 처리 |
|---|---|---|
| 1000 | 서버 Stop(Play 종료, 리로드, 에디터 종료) | 다시 시도(간격 늘림) |
| 4401 | 클라이언트 증명이 틀리거나 3초 안에 오지 않음 | 종료 상태: "이 QR 은 더 이상 쓸 수 없습니다. Unity 창의 QR 을 다시 찍으세요." |
| 4403 | 토큰이 폐기됨([새 QR 발급]) | 종료 상태: 같은 문구. 기억한 토큰을 지움 |
| 4410 | 다시 붙으세요(대기실 → Play 시작, 주소 변경으로 리스너를 다시 엶) | 간격 없이 곧바로 다시 시도 |

- 닫기 프레임 없이 끊기거나 고치기 전 서버(코드 1000 만 씀)에서는 전부 "다시 시도"입니다.

자리 배정 규칙(`GameViewRemoteSeats`) [설계]:

1. "무리" = 같은 클라이언트의 소켓들. 앱은 `did` 로 묶고, 인증 헤더가 없는 연결(Chrome 페이지)은 전부 한 무리 `page` 입니다.
2. 서버는 `선호 무리` 하나를 기억합니다. 처음에는 없습니다. **서버 Stop(Play 종료, 도메인 리로드)에도 지우지 않습니다**: 키(`"page"` 또는 `"app:<did>"`)를 `SessionState` 에 두고, 지우는 때는 Host 의 [Stop]과 [새 QR 발급]뿐입니다(리뷰 M3).
3. **영상을 받는 무리(active)** = 선호 무리가 지금 붙어 있으면 그 무리. 아니면 붙어 있는 앱 무리 중 가장 먼저 붙은 것. 앱이 없으면 `page`.
4. 선호 무리가 바뀌는 때는 둘뿐입니다: ① `take=1` 인 앱 연결이 인증을 마침 ② 영상 소켓으로 `{"t":"take"}` 가 옴(앱이든 페이지든). **자동 재접속(`take=0`)과 페이지의 0.7초 재접속은 선호를 바꾸지 않습니다.**
5. active 가 아닌 무리의 모든 연결은 `standby` 입니다: 영상 프레임과 상태 텍스트를 받지 않고, 입력 소켓의 포인터 메시지는 버려지고(ping 은 답함), 눌려 있던 손가락은 취소됩니다.
6. active 가 바뀌면: 새 active 에 `seat active`(앱) 또는 `{"t":"s","stale":false}` 와 마지막 상태(페이지)를 보내고, 나머지에 `seat standby`(앱) 또는 `{"t":"s","stale":true,"code":"standby","msg":"다른 기기가 보고 있습니다."}`(페이지)를 보냅니다. H.264 인코더는 받는 클라이언트가 바뀐 것을 보고 스스로 다시 시작해 설정 + 키프레임을 새로 냅니다 [코드: `EncodeH264` 의 `h264Client != client`].
7. 입력 번호: active 무리의 입력 연결만 `GameViewRemoteInput.OnInputConnected` 로 세어, 자리가 바뀔 때 서버의 `lastInjectedSeq` 가 0 으로 돌아가게 합니다(00 문서 L8).

- 페이지는 지금도 `stale:true` 의 `msg` 를 화면에 표시하므로 [코드: `showServerStatus`] 페이지를 고치지 않아도 대기 문구가 보입니다. [여기서 보기] 버튼은 페이지에 작은 수정이 필요합니다(9.3절, 권장).
- 같은 종류의 페이지가 둘 붙으면 지금처럼 둘 다 MJPEG 입니다(범위 밖, 바꾸지 않음).

### 3.6 `/input` 메시지 (지금 형식 그대로)

앱 → 서버 텍스트(JSON, 서버가 `JsonUtility` 로 해석) [코드: `GameViewRemoteInput.HandleMessage`]:

| 메시지 | 예 | 규칙 |
|---|---|---|
| down | `{"t":"d","id":0,"x":0.5123,"y":0.3310,"s":41}` | `id` = Android 포인터 ID. `x`,`y` = 그려진 영상 영역 기준 0~1(왼쪽 위가 0), 소수 4자리, 범위 밖은 0~1 로 자름. `s` = 입력 번호 |
| move | `{"t":"m",…}` | `MotionEvent` 1개당 위치가 바뀐 포인터마다 1개(7.6절) |
| up | `{"t":"u",…}` | 즉시 |
| cancel | `{"t":"c",…}` | `ACTION_CANCEL`, 앱이 뒤로 감 |
| ping | `{"t":"p","ts":123456.789}` | 1초마다. `ts` = `SystemClock.elapsedRealtimeNanos()/1e6`. 서버 답 `{"t":"P","ts":<같은 값>}` 으로 왕복 시간 |
| 보고 | `{"t":"q","dm":3.1,"dp":0,"fr":…}` | 2초마다(권장, F-94). `dm` = 최근 60장 디코딩 시간의 중앙값(접속 뒤 첫 2초는 0), `dp` = 지난 보고 뒤 앱이 디코더가 밀려 버린 수. 나머지 필드는 0 |

- 입력 번호 `s`: 연결마다 1부터, 포인터 메시지(move 포함)를 보낼 때마다 1 증가. 문자열은 `StringBuilder` 를 재사용해 만들고 숫자는 `Locale.ROOT` 형식(소수점은 항상 `.`)입니다.
- 서버 → 앱은 `{"t":"P","ts":…}` 뿐입니다.
- **입력 소켓의 `t` 는 지금 서버가 첫 글자만 봅니다**(`message.t[0]`). 그래서 새 메시지의 `t` 는 **두 글자 이상이고 `c d m p q u` 로 시작하지 않아야** 합니다. 6단계에서 `GameViewRemoteInput.HandleMessage` 에 `if (message.t.Length != 1) return null;` 한 줄을 넣습니다(리뷰 M6).
- 입력 큐(앱): 상한 64. 차 있으면 move 는 버리고(`pointerDropped`) down/up/cancel 은 그대로 넣습니다(리뷰 m2 를 단순화: 포인터별 덮어쓰기 대신 버림).

### 3.7 PC가 거는 방식의 첫 줄 (실험 F-41)

TCP 방향만 뒤집고 그 위의 HTTP/WebSocket 역할은 그대로입니다(폰이 요청을 보내고 PC가 101 로 답함).

```
폰: ServerSocket(7301) 으로 듣는다
PC: 폰 wlan0:7301 로 연결 → 첫 줄을 보냄:  "GVR-REVERSE/1 video\n"  (또는 "… input\n", ASCII, 최대 32바이트)
폰: 첫 줄을 읽어 채널을 정하고, 그 소켓 위에서 3.1절의 업그레이드 요청을 보냄 → 이후는 같음
```

- 영상과 입력용으로 PC가 두 번 겁니다. 폰은 줄을 2초 안에 못 읽으면 닫습니다.
- 시제품에서는 `dial.js` 만 이 방식을 씁니다. 폰 주소는 `adb -s <serial> shell ip -f inet addr show wlan0` 로 읽습니다.

## 4. QR 내용과 링크

### 4.1 QR 내용 (QR 형식 1)

```
http://<주 주소>:<포트>/c#v=1&s=<서버 표시 8자>&t=<토큰 22자>[&a=<다른 주소>[,<다른 주소>…]]
```

| 부분 | 값 | 규칙 |
|---|---|---|
| 주 주소 | IPv4 점 표기 | `GameViewRemoteNet` 이 고른 1개(9.6절). 호스트 이름은 쓰지 않음 |
| 포트 | 도구 포트(기본 7300) | 주소에 항상 적음 |
| `/c` | 착륙 페이지 경로 | 고정 |
| `v` | `1` | QR 형식 버전 |
| `s` | `[A-Z2-7]{8}` | PC마다 한 번 만들어 저장 |
| `t` | base64url 22자(16바이트) | PC에 저장. [새 QR 발급]으로만 바뀜 |
| `a` | IPv4 를 `,` 로 이음, 최대 3개 | 주 주소가 틀렸을 때 앱이 같이 시도. 포트는 같음. 없으면 생략 |

- `#` 뒤는 브라우저가 서버로 보내지 않습니다. 글자는 `[A-Za-z0-9._~:/#=&,-]` 뿐이고 공백이 없습니다.
- 길이: 예 `http://192.168.25.5:7300/c#v=1&s=GVRTEST1&t=AAECAwQFBgcICQoLDA0ODw&a=172.30.224.1` 은 81자 [계산]. 오류 정정 수준 M, 바이트 모드로 인코딩합니다. 인코더가 고른 QR 버전은 6단계에서 확인합니다(조사 3.3 의 표 값으로는 버전 5~6).
- 그리기: 흰 바탕, 검은 모듈, 둘레 여백 4모듈, 모듈당 정수 픽셀(4 이상), 필터 `Point`.

### 4.2 앱 딥링크

```
seeplay-gvr://<호스트>:<포트>/c?v=1&s=<서버 표시>&t=<토큰>[&a=…][&via=lan|usb][&take=1]
```

- 스킴 `seeplay-gvr`(식별자에서 딴 긴 이름, 리뷰 m6). 매니페스트 인텐트 필터: `ACTION_VIEW` + `DEFAULT` + `BROWSABLE` + `<data android:scheme="seeplay-gvr"/>`.
- 값은 QR 과 같고, `#` 대신 `?` 에 둡니다(`intent://` 문법이 `#` 을 씀). 이 주소는 폰 안에서만 오갑니다.
- `via=usb` 면 호스트는 `127.0.0.1` 이고 앱은 Wi-Fi 망에 묶지 않습니다. 생략하면 호스트로 판단합니다(127.0.0.0/8 이면 usb).
- 진단 값(시험 자동화용): `legacy=1`, `listen=<포트>`(F-41: 걸지 않고 듣기), `pace=now|sched`, `buf=low|auto|smooth|<ms>`, `rr=none|match|force60|max`, `wifilock=0|1`, `recreate=0|1`, `hold=<1~8>`, `decoder=<이름>`, `hud=1`, `rec=<초>`(PLAYING 이 되면 그만큼 기록하고 파일로 저장), `name=<기록 이름>`. **`BuildConfig.DIAG` 가 켜진 빌드에서, 인텐트에 `CATEGORY_BROWSABLE` 이 없을 때만 받습니다**(리뷰 M7: 브라우저가 연 링크에는 이 범주가 붙고 `adb shell am start` 에는 붙지 않음). `--es cmd …` 명령도 같은 조건입니다.
- adb 로 열 때 토큰을 주소에 넣지 않고 `--es t <토큰>` 추가 값으로 넘길 수 있습니다(같은 조건에서만 받음, 리뷰 m3: 액티비티 시작 줄에 주소가 logcat 에 남는 것을 피함).
- 브라우저가 연(`BROWSABLE`) 링크의 서버 표시가 기억한 것과 다르면 "새 PC에 연결할까요?" 확인 창을 1번 띄웁니다. 기억한 서버가 없으면(처음 설치) 묻지 않습니다. 앱 안 스캐너와 adb 인텐트는 묻지 않습니다(리뷰 M7-3, 브리프 3.3의 4).

`LinkParser` 의 해석 규칙(세 입구 공용):

1. 스킴이 `http` 이고 경로가 `/c` 면 값은 `#` 뒤에서, 스킴이 `seeplay-gvr` 이면 `?` 뒤에서 읽습니다.
2. `v` 가 1 이 아니면 "앱을 업데이트하세요"(QR 형식이 더 새로움).
3. `s` 와 `t` 가 형식에 맞지 않으면 "QR 을 읽을 수 없습니다".
4. 결과 `ServerLink { sid, token, port, hosts = [주 주소] + a, via }`.

### 4.3 착륙 페이지가 만드는 `intent://`

```
intent://<호스트>:<포트>/c?v=1&s=…&t=…&a=…&take=1#Intent;scheme=seeplay-gvr;package=kr.co.seeplay.gvr;S.browser_fallback_url=<http://<호스트>:<포트>/c?noapp=1 을 URL 인코딩한 것>;end
```

- 페이지의 스크립트가 `location.host` 와 `location.hash` 로 이 주소를 만들어 [앱으로 열기] 버튼의 `href` 에 넣습니다. 페이지 HTML 에는 토큰이 없습니다.
- `package=` 를 항상 넣습니다. 앱이 없으면 브라우저가 대체 주소(`?noapp=1`)로 가고, 페이지는 그때 설치 안내를 위로 올려 보여 줍니다. 대체 주소로 넘어가면 `#` 뒤 값이 사라지므로, 페이지는 처음 열릴 때 `#` 뒤 값을 `sessionStorage` 에 넣어 두고 `?noapp=1` 에서 다시 꺼냅니다(같은 탭, 같은 출처).
- 사용자 동작 없이 자동으로 열지 않습니다(Chrome 이 막음, 조사 3.1). 버튼 1번이 필요합니다.
- 삼성 인터넷에서의 동작은 [미검증]입니다(PRD 5.1 의 5단계 첫 작업).

### 4.4 adb 로 여는 방법 (요구사항 10의 검증과 [USB 로 연결])

```sh
# 딥링크(앱 직접). & 때문에 폰 셸 쪽에서 작은따옴표로 감쌉니다.
adb -s <serial> shell "am start -W -a android.intent.action.VIEW -d 'seeplay-gvr://127.0.0.1:7300/c?v=1&s=<sid>&t=<token>&via=usb&take=1' kr.co.seeplay.gvr"
# QR 이 가리키는 주소(브라우저 → 착륙 페이지)
adb -s <serial> shell "am start -a android.intent.action.VIEW -d 'http://192.168.25.5:7300/c#v=1&s=<sid>&t=<token>'"
# 기록 시작/끝(앱이 떠 있을 때)
adb -s <serial> shell am start -n kr.co.seeplay.gvr/.MainActivity --es cmd rec_start
adb -s <serial> shell am start -n kr.co.seeplay.gvr/.MainActivity --es cmd rec_stop
```

## 5. 연결과 재접속의 상태 기계

### 5.1 앱

```
                 ┌────────────┐  QR/딥링크/기억한 서버   ┌────────────┐ 소켓 연결 ┌─────────────┐
   (서버 없음) ──▶│  SCANNING  │────────────────────────▶│  DIALING   │──────────▶│ HANDSHAKING │
                 └────────────┘                          └─────▲──────┘           └──────┬──────┘
                                                               │ 간격 뒤                  │ 영상 소켓 인증 + hello,
                                                         ┌─────┴──────┐  끊김/실패        │ 입력 소켓 인증
                                                         │ RECONNECT  │◀──────────────────┤
                                                         └─────▲──────┘                   ▼
                        ┌────────┐ seat lobby            ┌─────┴──────────────────────────────┐
                        │ LOBBY  │◀──────────────────────│ CONNECTED                          │
                        └────────┘                       │  ├ WAITING  (프레임이 아직 없음)    │
                        ┌────────┐ seat standby          │  ├ PLAYING  (H.264 프레임 표시 중)  │
                        │STANDBY │◀─────────────────────▶│  └ NOCODEC  (JPEG 만 옴)            │
                        └────────┘ [여기서 보기] → take  └────────────────────────────────────┘
   종료 상태(다시 시도하지 않음): FAILED_TOKEN(4401, 4403, 서버 증명 불일치), FAILED_VERSION(426, hello v≠2, QR v≠1), FAILED_SERVER(403)
   앱이 뒤로 감: 어느 상태에서든 → BACKGROUND(소켓 닫음, 디코더 해제). 앞으로 오면 기억한 서버로 DIALING(take=0)
```

| 상태 | 들어올 때 하는 일 | 나가는 조건 |
|---|---|---|
| SCANNING | 기억한 서버가 없거나 종료 상태에서 [QR 다시 찍기]. 카메라 권한이 없으면 요청, 거부되면 "기본 카메라로 Unity 창의 QR 을 찍으세요" 안내 | 링크를 얻음 → DIALING(take=1) |
| DIALING | 주소 후보를 동시에 연결(`Dialer`): 마지막으로 성공한 주소를 먼저 걸고 150ms 뒤 나머지를 건다. 후보마다 제한 3초. LAN 주소는 Wi-Fi 망에 묶음(Wi-Fi 망이 없으면 "Wi-Fi 가 꺼져 있습니다"로 곧바로 실패) | 먼저 연결된 소켓 → HANDSHAKING(나머지는 닫음). 전부 실패 → RECONNECT |
| HANDSHAKING | 영상 소켓: 업그레이드(+인증) → `hello` 를 2초 안에 받음 → `caps` 전송. 이어서 같은 주소로 입력 소켓: 업그레이드(+인증) | 둘 다 성공 → CONNECTED/WAITING. HTTP 오류나 닫기 코드 → 표에 따라 종료 상태 또는 RECONNECT |
| WAITING | "연결됨. 화면을 기다리는 중"(게임 화면이 멈춰 있으면 프레임이 안 오는 것이 정상) | 첫 H.264 프레임을 표시 → PLAYING. JPEG 가 2초 넘게 옴 → NOCODEC |
| PLAYING | 상태 문구 숨김. 전체 화면, 화면 켜짐 유지, Wi-Fi 잠금(무선일 때) | 끊김 → RECONNECT. `seat standby` → STANDBY. `seat lobby` → LOBBY |
| NOCODEC | "PC가 H.264 를 보낼 수 없습니다(ffmpeg 를 확인하세요)". 확인은 계속 보냄 | H.264 프레임이 오면 PLAYING |
| STANDBY | "다른 기기가 보고 있습니다." + [여기서 보기]. 연결 유지, 디코더는 그대로 둠. **스스로 뺏지 않음** | 버튼 → `{"t":"take"}` 전송, `seat active` 를 기다림. `seat active` → WAITING |
| LOBBY | "Unity 에서 Play 를 누르면 화면이 나옵니다." 연결 유지 | 4410 → 곧바로 DIALING(take 는 처음 값 유지) |
| RECONNECT | "다시 연결 중" + 시도한 주소. 디코더 해제(마지막 화면은 Surface 에 남음) | 간격이 지나면 DIALING(take=0. 단 사람이 시작한 첫 시도가 아직 한 번도 성공하지 못했으면 take=1 유지) |
| 종료 상태 | 문구(PRD 2.6)와 [QR 다시 찍기] | 버튼 또는 새 딥링크 |

재시도 간격: 0.25 → 0.5 → 1 → 2 → 4초(상한 4초), 각 ±20% 무작위. CONNECTED 가 5초 이어지면 처음 값으로 되돌립니다. 4410 과 "앞으로 돌아옴"은 간격 없이 바로. 루프백 주소에서 "연결 거부"(Play 밖의 USB)면 문구를 "Unity 에서 Play 를 누르세요(USB)"로 바꾸고 상한을 2초로 합니다. 60초 동안 한 번도 못 붙으면 문구에 "PC 주소가 바뀌었을 수 있습니다. [QR 다시 찍기]"를 덧붙이되 시도는 계속합니다.

- 자동 발견(DNS-SD, PRD F-12)은 시제품에서 하지 않습니다. 주소가 바뀌면 `a=` 후보와 60초 뒤 [QR 스캔] 안내로 대응합니다(리뷰 m11).
- 닫기 코드 1000 을 받은 뒤 30초 동안은 재시도 간격 상한을 1초로 둡니다(리뷰 m13: Play 진입 때의 도메인 리로드).

살아 있는지 확인: 입력 소켓 ping 을 1초마다 보내고, 5초 동안 답이 없으면 두 소켓을 닫고 RECONNECT. 영상이 안 오는 것은 끊김의 근거로 쓰지 않습니다(같은 화면이면 서버가 안 보냄). 두 소켓 중 하나가 끊기면 다른 하나도 닫습니다.

세대: 연결을 새로 맺을 때마다 `generation` 을 1 올리고, 프레임 기록과 디코더 콜백에 붙여 이전 연결의 것을 버립니다. 서버가 다시 시작하면 `frameId` 와 `captureTime` 이 0부터 시작하므로 표시 일정의 시계 대응(7.4절)과 입력 번호도 연결마다 새로 시작합니다.

기억하는 것(`ServerStore`, 서버 표시별): 토큰, 성공한 주소(최근 순 최대 4개), 포트, 마지막 경로, 마지막 성공 시각. 앱 아이콘으로 실행하면 가장 최근 서버로 DIALING(take=1)합니다. 여러 PC를 고르는 화면은 만들지 않습니다(권장 항목, 가장 최근 것만 씀).

### 5.2 서버 쪽 연결 하나의 상태 (Host/Gate)

```
ACCEPTED ──(HTTP 헤더, 3초 제한, 최대 16KB)──▶ ROUTED
   ├ GET /c, /apk, /app.json → 응답 후 닫음
   ├ 그 밖의 경로, GET 아님    → 404 / 405 후 닫음
   └ /video, /input 업그레이드
        ├ X-GVR-Auth 없음(LAN) → 401 후 닫음
        ├ 형식/버전/sid 오류   → 400/426/403 후 닫음
        └ 통과 → CHALLENGED(101 + 서버 증명을 씀) ──(첫 메시지 {"t":"auth"}, 3초 제한)──▶
              ├ 틀림/시간 초과 → 닫기 4401
              └ 맞음 → AUTHENTICATED
                    ├ 기존 서버가 돌고 있음(Play) → server.Adopt() → 자리 배정 → SEATED(active|standby)
                    └ 아님                         → 대기실: hello + seat lobby, ping 에 답함
                                                      Play 가 시작돼 기존 서버가 열리면 4410 으로 닫음
```

- (리뷰 M4) 제한은 Host 의 **수락 루프에서** 셉니다: 인증을 마치지 않은 연결(HTTP GET 포함)이 16개면 받자마자 닫습니다. 그중 `/video`·`/input` 업그레이드 대기는 8개까지이고 넘으면 503 `busy`. `/apk` 동시 전송은 2개까지, 넘으면 503.
- 제한 시간은 읽기 한 번마다가 아니라 **연결을 받은 때부터 헤더 읽기와 인증 메시지 읽기를 합쳐 절대 기한 3초**입니다(감시 타이머가 기한이 지난 소켓을 닫음. `ReceiveTimeout = 3000` 은 보조). 인증이 끝나면 제한을 풉니다.
- [새 QR 발급]: 토큰을 바꾸고, 대기실과 기존 서버에 있는 모든 앱 연결을 4403 으로 닫습니다.
- 도메인 리로드/Stop: 대기실 연결은 1000 으로 닫습니다. 리로드 뒤 "Start 켜짐"이면 같은 포트와 토큰으로 다시 엽니다(9.1절).

## 6. 스레드 모델 (앱)

| 스레드 | 수 | 우선순위 | 하는 일 | 막히는 곳 |
|---|---|---|---|---|
| UI(main) | 1 | 기본 | 수명 주기, 뷰, 터치 수신(`TouchMapper` → 큐), HUD 글자 갱신(0.5초), 상태 문구 | 없음(네트워크, 디코더, 파일을 직접 부르지 않음) |
| `gvr-conn` | 1 | 기본 | 상태 기계, 재시도 타이머, `ServerStore`, 후보 연결 스레드 시작 | 짧은 작업만 |
| `gvr-dial-N` | 후보당 1(잠깐) | 기본 | `Socket.connect(주소, 3000)` | connect |
| `gvr-video-rx` | 1 | `THREAD_PRIORITY_DISPLAY`(−4) | 영상 소켓 읽기 → 도착 시각 → `a` 쓰기 → 헤더 해석 → 변환 → 디코더 입력 큐 | 소켓 read |
| `gvr-codec`(HandlerThread) | 1 | −4 | MediaCodec 콜백: 입력 버퍼 가능, 출력 버퍼 가능, 형식 변경, 오류. "즉시 표시"일 때 `releaseOutputBuffer` | 없음 |
| `gvr-pacer`(HandlerThread + Choreographer) | 1 | `THREAD_PRIORITY_URGENT_DISPLAY`(−8) | vsync 콜백마다 어느 버퍼를 내보낼지 결정하고 `releaseOutputBuffer(index, 시각)` | 없음 |
| `gvr-input-tx` | 1 | −4 | 입력 큐(`LinkedBlockingQueue<ByteArray>`)에서 꺼내 `write` 1번. ping 타이머 | 큐 take, 소켓 write |
| `gvr-input-rx` | 1 | 기본 | 입력 소켓 읽기(`P` 응답, 닫기) | 소켓 read |
| `gvr-diag` | 1 | `THREAD_PRIORITY_BACKGROUND` | 기록 파일 쓰기(요청 때만) | 파일 |
| CameraX 분석 실행기 | 1 | 기본 | 스캔 화면에서만 | — |

**터치가 표시를 늦출 수 없는 이유** [설계]:

1. 영상의 길은 `gvr-video-rx` → (MediaCodec 내부) → `gvr-codec` → `gvr-pacer` → `releaseOutputBuffer` → SurfaceFlinger 입니다. **UI 스레드는 이 길에 없습니다.** `SurfaceView` 의 Surface 는 앱의 뷰 그리기와 별개의 레이어이고 디코더가 직접 채웁니다.
2. 터치는 UI 스레드의 `onTouchEvent` 에서 좌표 변환과 문자열 조립(할당 없는 `StringBuilder` 재사용) 뒤 큐에 넣는 것으로 끝납니다. 소켓 쓰기는 `gvr-input-tx` 가 하고, 영상과 **다른 소켓**입니다.
3. 영상 쪽 스레드가 UI 스레드와 공유하는 잠금은 없습니다. 공유하는 것은 원자 변수(`Counters`), 잠금 없는 고리 버퍼(`TraceRing`: 쓰는 스레드별 구역), `volatile` 설정값뿐입니다. HUD 는 0.5초마다 스냅숏을 읽기만 합니다.
4. 영상 소켓에 쓰는 것은 `gvr-video-rx`(확인)와 `gvr-conn`(`caps`, `key`, `take`)뿐이고, 짧은 잠금 하나로 메시지 단위 직렬화합니다. UI 스레드는 이 잠금을 잡지 않습니다([여기서 보기]는 `gvr-conn` 에 요청을 넘김).
5. 확인(`a`)은 수신 스레드가 직접 보냅니다. 디코더가 밀려도, UI 가 바빠도 확인은 늦지 않습니다(서버의 과부하 오판 방지, 00 문서 L3).
6. HUD 가 켜져 있으면 영상 위에 뷰 레이어가 하나 더 합성됩니다. HUD 켬/끔의 수치 차이를 M2 에서 재서 적습니다(00 문서 L7). 측정은 기본적으로 HUD 를 끄고 합니다.

예외 하나(리뷰 m1): `surfaceDestroyed` 에서는 UI 스레드가 `Decoder.setSurface(null)` 로 디코더 해제가 끝날 때까지 기다립니다(디코더 잠금 1개, 프레임 1장을 넣거나 넘기는 동안만 잡힘). Surface 가 무효가 된 뒤에 버퍼를 넘기지 않게 하기 위해서이고, 넘기다 난 예외는 잡아서 `releaseErrors` 로 셉니다.

이것이 실제로 성립하는지는 PRD NF-10(누름 구간)으로 잽니다(11장).

## 7. 영상: 디코더, 표시, 주사율, 입력, 수명 주기

### 7.1 화면 구성

- `FrameLayout`(검정) 안에 `SurfaceView` 를 영상 비율로 가운데에 놓습니다(여백은 검정). 크기는 헤더의 `width`/`height` 가 바뀔 때 UI 스레드에서 다시 계산합니다. `SurfaceHolder.setFixedSize` 는 쓰지 않습니다(디코더 출력 크기를 합성기가 뷰 크기로 맞춤).
- `TextureView` 는 쓰지 않습니다(앱의 UI 렌더링에 묶임).
- 그 위에 `StatusOverlay`, `HudView`(평소 `GONE`).

### 7.2 창과 수명 주기

| 항목 | 방법 |
|---|---|
| 전체 화면 | `WindowCompat.setDecorFitsSystemWindows(window, false)`, `WindowInsetsControllerCompat.hide(systemBars())`, `BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE`. 컷아웃: `layoutInDisplayCutoutMode = SHORT_EDGES`(API 28), API 30 이상은 `ALWAYS`. 대상 SDK 36 은 가장자리까지 그리기가 기본이라 인셋을 직접 처리 [미검증: 5단계에서 문서와 실기기 확인, 리뷰 m13] |
| 화면 켜짐 유지 | CONNECTED/LOBBY/STANDBY 동안 `FLAG_KEEP_SCREEN_ON`. 종료 상태와 SCANNING 에서는 해제 |
| 방향 | 영상이 세로(`height >= width`)면 `SCREEN_ORIENTATION_PORTRAIT`, 가로면 `SENSOR_LANDSCAPE`. 영상이 오기 전에는 세로 |
| 구성 변경 | 매니페스트 `configChanges="orientation\|screenSize\|screenLayout\|smallestScreenSize\|keyboardHidden\|density\|uiMode"` 로 Activity 와 Surface 를 다시 만들지 않음(접힘/펼침 포함) |
| 뒤로 가기 | `onBackPressedDispatcher` 콜백. PLAYING 에서 첫 번째는 "한 번 더 누르면 나갑니다"(2초), 두 번째에 `finish()`. 매니페스트 `enableOnBackInvokedCallback="true"` [미검증: 예측 제스처 동작, m13] |
| `onStart` | 기억한 서버가 있으면 DIALING |
| `onStop` | 눌려 있는 포인터마다 `c` 를 보내고, 두 소켓에 닫기 프레임을 보낸 뒤 닫고, 디코더와 Wi-Fi 잠금을 해제. 뒤에서 연결을 유지하지 않음(서버가 "읽지 않는 클라이언트"를 과부하로 본 전례, 00 문서 3.3 #5) |
| `surfaceDestroyed` | 디코더를 즉시 `stop` + `release`(Surface 가 사라지기 전에). 연결은 유지하고 확인은 계속 보냄 |
| `surfaceCreated`(연결 중) | 디코더가 없으므로 `{"t":"key"}` 를 보내 설정 + 키프레임을 다시 받음 |
| 커버 화면(Z Flip5) | 권장 항목. `configChanges` 로 깨지지 않게만 하고 따로 맞추지 않음 |

권한(매니페스트): `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE`, `WAKE_LOCK`, `CAMERA`(런타임 동의, 스캔 화면에 들어갈 때만 요청). `android:usesCleartextTraffic="true"`(F-64). `<uses-feature android:name="android.hardware.camera" android:required="false"/>`.

Wi-Fi(무선 경로일 때만):

- 망 지정: `ConnectivityManager.registerNetworkCallback(NetworkRequest(TRANSPORT_WIFI))` 로 Wi-Fi `Network` 를 얻어 두고, 소켓을 만들 때 `network.bindSocket(socket)` 뒤에 `connect` 합니다(F-32). 루프백 대상은 묶지 않습니다.
- 저지연 잠금: `WifiManager.createWifiLock(WIFI_MODE_FULL_LOW_LATENCY, "gvr")`(API 29 이상), PLAYING 에 들어갈 때 `acquire`, 나갈 때 `release`. 설정 `wifilock` 으로 끌 수 있습니다(PRD 4.7 의 가설을 켬/끔으로 잼).
- `LinkInfo`: 주파수(MHz), 링크 속도, RSSI 를 2초마다 읽어 HUD 와 기록에 넣습니다(API 31 이상은 `NetworkCapabilities.getTransportInfo()` 의 `WifiInfo`, 그 아래는 `WifiManager.getConnectionInfo()`).

### 7.3 디코더 (MediaCodec)

선택(API 30 이상 기준, 아래 버전은 해당 단계를 건너뜀):

1. `MediaCodecList(REGULAR_CODECS)` 에서 `video/avc` 디코더 중 하드웨어(`isHardwareAccelerated`, API 29. 그 아래는 이름이 `OMX.google.`/`c2.android.` 로 시작하지 않는 것)만 남깁니다.
2. 이름이 `.low_latency` 로 끝나는 것 → `FEATURE_LowLatency` 를 지원하는 것 → 나머지 순으로 첫 번째를 씁니다. 이 폰에는 `c2.qti.avc.decoder.low_latency` 가 있습니다 [조사 1.1 실측]. `.secure` 는 제외합니다.
3. 설정 `decoder=<이름>` 으로 직접 고를 수 있습니다(비교용). 고른 이름을 HUD 와 기록에 남깁니다.

형식(`MediaFormat.createVideoFormat("video/avc", width, height)`):

| 키 | 값 | 조건 |
|---|---|---|
| `csd-0` | `00 00 00 01` + SPS | 항상 |
| `csd-1` | `00 00 00 01` + PPS | 항상 |
| `KEY_LOW_LATENCY` | 1 | API 30 이상 |
| `KEY_PRIORITY` | 0(실시간) | API 23 이상 |
| `KEY_OPERATING_RATE` | `Short.MAX_VALUE` | 이름에 `qti`/`qcom` 이 있을 때. 그 밖에는 120 |
| `vendor.qti-ext-dec-low-latency.enable` | 1 | 이름에 `qti`/`qcom` [문서: 조사 5.2] |
| `vendor.qti-ext-dec-picture-order.enable` | 1 | 같음(디코딩 순서대로 출력) |
| `vendor.rtc-ext-dec-low-latency.enable` | 1 | 이름에 `exynos` [문서: 조사 5.2] |
| `KEY_MAX_INPUT_SIZE` | 1MB | 큰 키프레임 대비 |

- 모르는 키는 디코더가 무시합니다. 어떤 키를 넣었는지와 `codec.getInputFormat()`/`getOutputFormat()` 을 로그와 기록에 1번 남깁니다(F-53). 다른 제조사 키는 시제품에 넣지 않습니다(이 폰 1대로만 시험).
- `codec.setCallback(callback, codecHandler)` → `configure(format, surface, null, 0)` → `setOnFrameRenderedListener(listener, codecHandler)` → `start()`.

입력:

- `gvr-video-rx` 가 변환을 마친 프레임을 `pendingInput`(잠금 하나로 보호하는 덱)에 넣고, `gvr-codec` 의 `onInputBufferAvailable` 이 빈 버퍼 번호를 `freeInputs` 에 넣습니다. 둘 중 뒤에 온 쪽이 같은 잠금 안에서 짝을 맞춰 `getInputBuffer(i).put(...)` + `queueInputBuffer(i, 0, size, ptsUs, flags)` 를 부릅니다.
- `ptsUs` = `captureTime × 100`(0.1ms → µs). 프레임마다 다르고 단조 증가입니다. 출력 쪽에서 `ptsUs` 로 프레임 기록(64칸 고리, 선형 탐색)을 찾아 `frameId`, `captureTime`, `inputSeq`, 도착 시각을 되찾습니다(00 문서 L9).
- 키프레임(`flags & 4`)이면 `BUFFER_FLAG_KEY_FRAME`.
- 수신 버퍼: 메시지는 풀에서 꺼낸 `ByteArray`(64KB, 모자라면 그 메시지만 새로 할당)에 읽고, 디코더 입력 버퍼로 복사한 뒤 돌려줍니다. 프레임마다 새 배열을 만들지 않습니다.

설정 메시지(avcC)가 왔을 때:

| 상황 | 처리 |
|---|---|
| 디코더가 없음(첫 연결, Surface 재생성, 오류 뒤) | SPS/PPS 로 새로 만듦. 다음 키프레임부터 넣음 |
| 디코더가 있고 `width`/`height` 가 같음 | 디코더를 유지하고, **다음 키프레임 버퍼 앞에 `00 00 00 01 SPS 00 00 00 01 PPS` 를 붙여** 넣음(MediaCodec 문서가 허용하는 방식, 조사 5.1). 설정 `recreate=1` 이면 이 경우에도 새로 만듦(화면이 깨지면 이쪽으로 바꿔 비교) |
| 크기가 다름 | `stop` + `release` 뒤 새로 만듦. `VideoSurface` 비율과 방향을 다시 계산 |

키프레임을 기다리는 상태: 디코더를 새로 만든 뒤 첫 키프레임이 오기 전의 델타 프레임은 버립니다(확인은 보냄, `droppedPreKey` 로 셈).

`{"t":"key"}` 를 보내는 조건(전부 "2초에 1번 이하"로 제한):

- `onError`(디코더 오류) → 디코더를 새로 만들고 요청.
- AVCC 길이 검증 실패.
- Surface 가 다시 만들어졌는데 연결은 그대로일 때.
- 디코더 입력이 밀림: `pendingInput` 의 가장 오래된 프레임이 500ms 를 넘김 → 대기 중인 것을 전부 버리고 요청.
- 연결 직후에는 보내지 않습니다(받는 클라이언트가 바뀌면 서버가 스스로 설정 + 키프레임을 냄).
- 프레임이 안 오는 것은 조건이 아닙니다.

첫 측정 항목(00 문서 7.2 의 모르는 것): 디코더가 프레임을 붙잡는지 = 넣은 수 − 나온 수(`decoderHeld`)가 정상 재생 중 0~1 에 머무는지, 디코딩 시간(넣음 → 출력 가능)의 분포.

### 7.4 표시 일정 (Pacer)

시계: 앱 안의 모든 시각은 `System.nanoTime()`(단조 시계)입니다. `Choreographer` 의 프레임 시각과 `MotionEvent.getEventTime()`(ms)도 같은 시계 계열입니다.

**방식 ① 즉시 표시(`pace=now`)**: `onOutputBufferAvailable` 에서 곧바로 `releaseOutputBuffer(index, System.nanoTime())`. 디코딩이 끝난 프레임이 다음 화면 갱신에 나갑니다. 한 갱신 주기에 두 장이 나가면 합성기가 뒤의 것만 보여 줍니다(그 수는 `onFrameRendered` 가 오지 않은 것으로 셈). 지연이 가장 작고 흔들림에 가장 약합니다.

**방식 ② 표시 일정(`pace=sched`, 기본)**: 디코딩된 버퍼를 `ready` 목록(최대 `HOLD_LIMIT = 4`장)에 두고, `gvr-pacer` 가 vsync 콜백마다 결정합니다.

```
캡처 시각 → 앱 시계의 대응:
  base  = 최근 10초 동안의 min( decodedAtNs − captureNs )        (가장 빨리 온 프레임. 두 기기의 시계 차이 포함)
  excess(프레임) = decodedAtNs − captureNs − base                 (그 프레임이 가장 빠른 것보다 얼마나 늦게 준비됐나)
  delay = 지터 버퍼 설정에 따라:
            low    : 0
            auto   : clamp( p95(최근 120장의 excess 중 100ms 이하인 것) + 2ms, 0, 2 × 원본 프레임 간격 )   (기본)
            smooth : 50ms 고정
            <ms>   : 그 값 고정
  due(프레임) = captureNs + base + delay                          (이 시각에 화면에 나가야 함)

vsync 콜백(frameTimeNanos):
  P = 다음에 내보내면 화면에 나갈 예상 시각
      API 33 이상: Choreographer.postVsyncCallback 의 FrameData.preferredFrameTimeline.expectedPresentationTimeNanos
      그 아래   : frameTimeNanos + 2 × 갱신 주기(잰 값)
  후보 = ready 중 due ≤ frameTimeNanos + 갱신 주기/2 인 것      (5단계에서 고침: due 는 "이때 Surface 로 넘긴다"는 시각. P 와 견주면 P 가 1~2주기 앞이라 전부 곧바로 후보가 되어 초당 6장을 버렸음)
  후보가 있으면: **가장 오래된 것** 1장을 releaseOutputBuffer(index, P) 로 내보내고 나머지는 다음 vsync 로 미룸.
                 단, 그 다음 후보의 due 가 frameTimeNanos − 갱신 주기/2 보다도 이르면(한 장 이상 밀림) 오래된 것을 버리고(skipped) 다음 것으로 넘어감
                 (5단계에서 고침: "가장 새 것만 내보내고 나머지는 버림"은 60fps 원본에서 초당 1장을 버렸음)
  후보가 없으면: 아무것도 안 함(화면은 이전 프레임을 유지)
  ready 가 HOLD_LIMIT 를 넘으면: 가장 오래된 것부터 버림(skipped)
```

- 원본 프레임 간격은 최근 캡처 시각 차이의 중앙값으로 잽니다(60fps 면 16.7ms). 갱신 주기는 vsync 콜백 간격의 중앙값입니다.
- `auto` 에서 100ms 넘는 excess 를 빼는 이유: 정체 한 번(100~470ms)이 2초 동안 지연 목표를 끌어올리지 않게 하기 위해서입니다. 그런 정체는 어차피 버퍼로 가릴 수 없습니다(PRD 4.7).
- 디코더가 출력 버퍼를 몇 장까지 앱에 맡겨도 멈추지 않는지는 기기마다 다릅니다. `HOLD_LIMIT = 4` 에서 디코더 입력이 밀리면(`pendingInput` 이 2장 넘게 쌓임) 한도를 2로 낮춥니다 [미검증: M2 에서 확인].

**늦은 프레임과 빠진 프레임**

| 경우 | 동작 | 세는 곳 |
|---|---|---|
| 프레임이 자기 `due` 보다 늦게 준비됨 | 다음 vsync 에 내보냄(늦었으므로 후보 조건을 이미 만족). `auto` 면 지연 목표가 따라 올라감 | `late`, `holds` |
| 정체 뒤 여러 장이 한꺼번에 옴 | 전부 디코딩(델타 프레임은 건너뛸 수 없음), 한 vsync 에 가장 새 것만 내보내고 나머지는 버림. 지연이 쌓이지 않음 | `skipped`, `bursts` |
| 서버가 프레임을 만들지 않음(같은 화면, Unity 가 튐, 인코딩 전 건너뜀) | 화면 유지. 캡처 시각 간격이 벌어진 것으로 구분되므로 앱의 끊김으로 세지 않음 | `sourceGaps` |
| 60.2fps 원본과 60Hz 화면의 차이 | 몇 초에 한 번 한 갱신에 두 장이 걸려 1장 버림(또는 한 갱신 건너뜀). 피할 수 없음(보고 3에서도 어느 경로에나 남음) | `skipped` / `holds` 에 포함, HUD 에 "beat" 로 따로 표시(직전 프레임들이 고르게 오고 있었는데 생긴 것) |
| 120Hz 화면에 60fps 원본 | 프레임마다 2갱신 유지. `due` 가 갱신 경계 근처면 1갱신/3갱신으로 흔들릴 수 있음 → `P + 주기/2` 조건으로 가까운 쪽에 붙임 | `holds`, `jumps` |
| TCP 라서 프레임 손실은 없음 | `frameId` 가 건너뛰면(설정 메시지 포함해 셈) 버그로 기록 | `idGaps` |

### 7.5 주사율

| 정책(`rr=`) | 방법 | 비고 |
|---|---|---|
| `none` | 아무 요청도 하지 않음 | **쓰면 안 됨(5단계 실측)**: 가변 120Hz 설정의 이 폰은 요청이 없으면 패널을 **30Hz** 로 내려 34fps 만 표시됐습니다 |
| `match`(기본) | `surface.setFrameRate(60f, FRAME_RATE_COMPATIBILITY_FIXED_SOURCE, CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS)`(API 31. API 30 은 2인자) | 60 은 원본 프레임 간격에서 반올림한 값. "보장 없음"(조사 5.3) |
| `force60` | `window.attributes.preferredDisplayModeId` = 지금 해상도에서 60Hz 인 모드 | 폰 설정을 바꾸지 않고 이 창에서만 |
| `max` | 같은 방법으로 지금 해상도에서 가장 높은 주사율의 모드 | |

- 지금 주사율은 `Display.getRefreshRate()` 와 vsync 콜백 간격 둘 다로 재고, `DisplayManager.DisplayListener` 로 바뀐 횟수와 시각을 기록합니다(`rrSwitches`). 탭 순간 전환이 생기는지를 이 기록으로 봅니다.
- (5단계 실측) `match` 는 이 폰의 가변 120Hz 설정에서 먹습니다: 무터치 60Hz 유지. 터치하면 시스템이 120Hz 로 올립니다(요청은 `ONLY_IF_SEAMLESS` 라 막지 않음). 나머지 정책의 결과는 `05_build_and_test.md`.
- 요청이 실제로 먹는지는 기기마다 [미검증]입니다. 특히 폰이 "표준(60Hz)" 설정이면 `max` 가 무시될 수 있습니다. 측정 결과에는 요청한 정책과 **실제로 잰 주사율**을 같이 적습니다.
- 폰의 "가변 120Hz" 설정 자체는 워커가 바꿀 수 없습니다. 보고 3 시점에 폰은 표준 60Hz 였습니다.

### 7.6 입력

- `VideoSurface.onTouchEvent` 에서 처리합니다. 좌표는 `SurfaceView` 안의 위치를 뷰 크기로 나눈 것입니다(뷰가 영상 영역과 같으므로 여백 계산이 필요 없음). 여백(부모)을 누른 것은 부모가 받아 가장 가까운 가장자리로 잘라 같은 경로로 보냅니다.
- `ACTION_DOWN`/`POINTER_DOWN` → `d`, `ACTION_UP`/`POINTER_UP` → `u`, `ACTION_CANCEL` → 눌려 있던 모든 포인터에 `c`.
- `ACTION_MOVE`: Android 는 이동 이벤트를 화면 갱신마다 1번으로 묶어 전달하므로(지난 표본은 `getHistorical*` 에 있음) 이벤트 1개당 마지막 위치만, 직전에 보낸 위치와 달라진 포인터마다 `m` 1개를 보냅니다(F-71). `requestUnbufferedDispatch` 는 쓰지 않습니다.
- 포인터별로 `down 을 보냈는가`를 기억해 서버 규칙(00 문서 1.5.7)과 맞춥니다. STANDBY/LOBBY/종료 상태에서는 보내지 않습니다.
- 터치 기록: 포인터 메시지마다 `(s, 종류, eventTimeNs, sendEnqueueNs)` 를 `TraceRing` 에 넣습니다. down/up 시각이 NF-10 의 누름 구간입니다.

## 8. 진단: 카운터와 HUD

### 8.1 프레임별 기록 (`TraceRing`, 16384칸, 원시 배열)

`generation, frameId, flags, bytes, captureNs(서버 시계), inputSeq, recvNs(메시지를 다 읽은 때), queuedNs(디코더에 넣은 때), decodedNs(출력 가능), dueNs, releaseNs(Surface 로 넘긴 때, 버렸으면 −1), targetNs(내보낼 때 지정한 시각), renderedNs(시스템이 알려 준 표시 시각, 안 오면 0)`

- `renderedNs` 는 `MediaCodec.OnFrameRenderedListener` 의 `nanoTime` 입니다. **(5단계 실측) 이 폰에서 이 값은 넘긴 모든 프레임에 오지만, 앱이 `releaseOutputBuffer(index, 시각)` 에 지정한 `targetNs` 와 정확히 같습니다(차이 0.0ms). 실제 표시 시각의 독립된 근거가 아닙니다.** 그래서 `renderedNs`/`targetNs` 로만 계산한 수치로는 NF-2 ~ NF-4, NF-10 을 "충족"으로 적지 않습니다(리뷰 B1-3).
- 프레임마다 로그를 찍지 않고 내용을 읽지 않습니다(F-91).

### 8.2 카운터의 정의

측정 스크립트(`measure.js`, `analyze.js`)와 같은 정의를 씁니다 [코드: `analyze.js:18~36`]. `cap(i)` = 그 프레임의 캡처 시각. **`shown(i)` 는 `releaseNs`(앱이 버퍼를 Surface 로 넘긴 시각)입니다**(리뷰 B1). 페이지의 표시 시각이 그리기 호출 시각(`drawnAt = performance.now()`)이므로 같은 자리이고, 페이지와 **견주는** 숫자(멈춘 갱신, 건너뛴 프레임, 화면 정지, 터치 → 표시)는 전부 이 기준입니다. `renderedNs` 기준 값은 "앱 단독" 열로 따로 냅니다(`tools/app-report.js` 가 둘 다 출력). 주의: `pace=now` 에서는 한 갱신에 두 장을 넘겨도 `releaseNs` 로는 보이지 않으므로(합성기가 앞의 것을 버림), 페이지와 견주는 수치는 한 vsync 에 한 장만 넘기는 `pace=sched` 의 것을 씁니다.

| 카운터 | 정의 | 스크립트의 이름 |
|---|---|---|
| **멈춘 갱신(repeated vsync)** `holds` | 연속 표시 두 장에서 `d = (shown(i) − shown(i−1)) − (cap(i) − cap(i−1))` 가 +8ms 를 넘는 수. 원본이 움직이던 구간(`cap` 간격 25ms 미만)만 셈. 초당 값으로 표시 | holds (repeated vsync) |
| 이른 넘김 `jumps` | `d < −8ms` 인 수 | early jumps |
| **건너뛴 프레임** `skipped` | 받았는데(H.264 프레임) 표시하지 않은 수 = 버린 것 + 표시 통지가 오지 않은 것. 초당 | skipped frames |
| **가장 긴 정지** `longestHoldMs` | 원본이 움직이던 구간의 표시 간격 최댓값(1초 창과 기록 구간 전체) | longest hold |
| 고르지 않은 표시 간격 | 표시 간격이 9ms 미만 또는 25ms 초과인 비율(PRD NF-2~4) | — |
| 표시 fps | 표시된 수 / 시간 | shown fps |
| **디코딩 시간** | `decodedNs − queuedNs` 의 p50 / p95 / 최대. `decoderHeld` = 넣은 수 − 나온 수 | decode ms |
| 도착 | `recvNs` 간격의 p50 / p99 / 최대, 40ms 이상 수, 3ms 안에 몰려 온 수(`bursts`). 도착 지연 초과 = `recvNs − captureNs − min(…)` 의 p50 / p99 / 최대와 25ms·40ms 이상 비율 | transit excess, arrival gaps, stalls |
| 캡처 → 표시 | `shown − captureNs − base` 의 평균 / p90 | capture → display |
| **터치 → 표시** | 8.3절 | touch → display |
| 그 밖 | 받은 수(H.264 / 설정 / JPEG), `droppedPreKey`, `keyRequests`, `idGaps`, `sourceGaps`, `late`, 지연 목표 `delay`, 주사율과 `rrSwitches`, 왕복 시간(입력 ping), 재접속 수 | — |

### 8.3 터치 → 표시

- 포인터 메시지를 보낼 때 `(s, eventTimeNs, sendEnqueueNs)` 를 기록합니다. 표시된 프레임의 `inputSeq` 가 `s` 이상이 되는 첫 프레임의 표시 시각에서 뺍니다.
- 두 값을 냅니다: **보냄 → 표시**(`shown − sendEnqueueNs`. 페이지의 `touchToFrame` 과 같은 정의라 비교에 씀)와 **이벤트 → 표시**(`shown − eventTimeNs`. 시스템의 터치 전달 지연 포함).
- down 과 up 만 표본으로 씁니다(move 는 너무 많음). 연결 직후 2초, STANDBY, 인증 없는 구버전 서버에 페이지와 함께 붙은 경우는 뺍니다(00 문서 L8).

### 8.4 HUD (F-90)

오른쪽 위 작은 `i` 를 길게 눌러 켜고 끕니다(게임 조작과 겹치지 않게 길게 누름). 0.5초마다 갱신, 글자만.

```
LAN 192.168.25.5:7300  auth ok  seat active        ← 경로와 주소. USB 면 "USB(adb) 127.0.0.1:7300", 구버전 서버면 "인증 없음"
Wi-Fi 2412MHz 104Mbps -45dBm  lock on  rtt 6ms
720x1280 H.264  c2.qti.avc.decoder.low_latency  ll=1 qti=1
recv 59.8/s  dec 59.8  shown 59.6  held 0          ← 받은 수 / 디코더 출력 / 표시 / 디코더가 붙잡은 수
arrive p50 16.6 p99 31 max 212  >=40ms 3  burst 2
decode p50 3.1 p95 4.4 max 9.0
holds 0.1/s  skipped 0.0/s  longest 50ms  uneven 0.4%
touch→shown 64ms (n=12)  cap→shown 27ms
pace sched  buf auto 7ms  panel 60.0Hz (rr match, switches 0)
[기록 시작]
```

### 8.5 기록 내보내기 (F-93)

- [기록 시작/끝] 또는 adb 명령(4.4절), 또는 링크의 `rec=<초>`. 끝나면 `gvr-diag` 가 `TraceRing` 의 그 구간을 파일로 씁니다.
- 위치: `Android/data/kr.co.seeplay.gvr/files/rec/rec-<yyyyMMdd-HHmmss>.json`(앱 전용 외부 저장소, 권한 불필요). 가져오기: `adb -s <serial> pull /sdcard/Android/data/kr.co.seeplay.gvr/files/rec/`. (5단계 실측) Android 16 의 이 폰에서 `adb pull` 로 읽힙니다. `run-as` 가 필요 없어 디버그 불가 `proto` 빌드로 잽니다.
- 형식: 측정 스크립트 `record.js` 가 만드는 JSON 과 같은 필드 이름과 배열 순서로 맞춥니다(`frames` = `[id, cap(ms), recvAt(ms), drawnAt(ms) = releaseNs, dec(ms), seq, due(ms)]`, `arrivals` = `[id, at]`, 화면 갱신 시각 배열, 포인터 이벤트, `info`) [코드: `analyze.js:11~24`]. 그러면 `analyze.js` 와 `touchstall.js` 를 그대로 돌릴 수 있습니다. 앱에만 있는 값(`releaseNs`, `targetNs`, `renderedNs`, 설정, 링크 정보)은 `app` 아래에 따로 둡니다. `unity`, `encodes`, `samples` 는 PC 쪽에서 같은 시간대의 `/stats` 를 읽어 합칩니다(`tools/merge-stats.js`, `record.js` 의 해당 부분을 옮김). `unity`, `encodes`, `samples`, `t2f` 는 빈 배열로 둡니다(`merge-stats.js` 는 만들지 않음: 5단계는 `tools/app-report.js` 로 분석). **기록 파일과 문서에 토큰을 적지 않습니다**(리뷰 m3).

## 9. Unity 쪽 변경 (파일별)

원칙: 새 기능은 새 파일에 두고, 기존 파일에는 **몇 줄짜리 고리**만 넣습니다. 진단 워커가 끝나 지금은 이 폴더를 고치는 사람이 없지만(조정자 메시지), 고리를 작게 두면 6단계가 시작될 때 폴더가 또 바뀌어 있어도 다시 얹기 쉽습니다. 6단계는 조정자가 시작합니다(브리프 C3). 아래 줄 번호는 머리말의 읽은 시점 기준입니다.

### 9.1 `GameViewRemoteHost.cs` (새 파일, 앞단)

상태와 저장:

새 설정은 `EditorPrefs`(PC 전체, 같은 PC의 모든 프로젝트가 공유)가 아니라 **프로젝트별 파일** `<프로젝트>/UserSettings/GameViewRemote.json` 에 둡니다(`GameViewRemoteSettings.cs`, 새 파일. `UserSettings/` 는 Unity 가 사용자별 설정을 두는 폴더이고 보통 버전 관리에서 빠져 있음). 같은 PC에서 여러 프로젝트가 이 도구를 써도 서버 표시와 토큰이 섞이지 않습니다(요구사항 12). 기존 `GVR_*` `EditorPrefs` 키는 이번에 옮기지 않습니다(15.3절).

| 값 | 저장 | 내용 |
|---|---|---|
| "Start 켜짐" | `SessionState` `GVR_HostArmed` | 도메인 리로드를 넘어 유지(에디터 세션 동안. `SessionState` 는 에디터 프로세스별이라 프로젝트끼리 섞이지 않음). `GameViewRemoteHost.Armed` 는 이 값을 직접 읽음(정적 생성자 순서와 무관) |
| 에디터를 켤 때 자동으로 열기 | 설정 파일 `autoArm`(기본 false) | 켜져 있으면 에디터 시작 때 "Start 켜짐"으로 |
| 서버 표시 | **`Library/GameViewRemote/secret.json`** 의 `serverId` | 없으면 만듦(무작위 5바이트의 base32 8자). 프로젝트(와 PC)마다 다름 |
| 토큰 | **`Library/GameViewRemote/secret.json`** 의 `token` | 없으면 만듦(무작위 16바이트의 base64url). [새 QR 발급]으로 교체. 평문. (리뷰 M2, 브리프 3.3의 2: catjump 는 `UserSettings/` 를 git 이 추적하므로 비밀은 `Library/` 에 둠. Library 를 지우면 새로 만들어져 QR 을 다시 찍어야 함) |
| 프로젝트 표시 이름 | 정적 필드 | `Open()` 에서(메인 스레드) `Application.productName` 을 읽어 둠. 네트워크 스레드는 이것만 읽음(리뷰 m15) |
| 고른 주 주소 | 설정 파일 `hostAddress`(빈 값 = 자동) | 창의 목록에서 고른 값 |
| APK 경로 | 설정 파일 `apkPath`(빈 값 = 도구 폴더의 `App~/GameViewRemote.apk`) | 10.4절 |

- `secret.json` 이 있으면 Host 정적 생성자에서 **`Armed` 와 무관하게 항상 읽어 둡니다**(리뷰 m7: 예전 Start 만 쓰는 날에도 토큰을 기억한 앱이 USB 로 인증됨). 파일이 없을 때만 인증 헤더 있는 요청이 403 `unknown_server` 입니다.
- 설정 파일 읽기/쓰기는 메인 스레드에서만 하고, 열 때 정적 필드에 복사해 두어 네트워크 스레드는 그것만 읽습니다(00 문서 L11 과 같은 이유).

수명:

| 사건 | 동작 |
|---|---|
| 창의 [Start] | `Armed = true` → `Open()`: 주 주소와 후보를 정하고(9.6절), **QR 에 넣는 주소마다** `TcpListener(주소, 포트)` 를 열고 수락 스레드(`GVR Host Accept`)를 시작. Play 중이면 `GameViewRemote.Start()` 도 부름. 방화벽 상태 읽기를 시작(9.7절) |
| 창의 [Stop] | `Armed = false` → 대기실 연결을 1000 으로 닫고 리스너를 닫음. `GameViewRemote.Stop()` |
| 도메인 리로드 직전(`beforeAssemblyReload`) | 리스너와 대기실을 닫음(`Armed` 는 그대로) |
| 리로드 뒤(정적 생성자, `[InitializeOnLoad]`) | `Armed` 면 `EditorApplication.delayCall` 에서 `Open()`. Play 중이면 `GameViewRemote.Start()`(기존 코드의 Play 중 리로드 복구와 겹쳐도 `Start()` 는 이미 돌고 있으면 그냥 돌아옴 [코드: `GameViewRemote.cs:318`]) |
| `EnteredPlayMode` | `Armed` 면 `GameViewRemote.Start()`. 성공하면 대기실 연결을 4410 으로 닫음(앱이 곧바로 다시 붙어 기존 서버로 넘겨짐) |
| `ExitingPlayMode` | 기존 코드가 서버를 닫음(앱은 1000 을 받고 다시 붙어 대기실로) |
| 에디터 종료 | 닫음 |
| 주 주소가 바뀜(목록에서 고름, 어댑터 변화를 5초마다 확인) | 리스너를 다시 열고 QR 을 다시 그림. 대기실은 4410 |

- 포트는 기존 설정 `GameViewRemote.Port` 를 씁니다. 기존 서버는 `127.0.0.1:포트`, Host 는 `<LAN 주소>:포트` 라 주소가 달라 겹치지 않습니다. 기존의 "Wi-Fi 접속 허용"(`0.0.0.0` 바인드)은 **없앱니다**(리뷰 M1, 브리프 3.3의 1: 인증 없는 LAN 리슨은 없음. 9.4절 G1, W2). 6단계에서 README 와 개발 문서에 "무엇이 없어졌고 대신 QR 연결을 쓴다"를 적습니다.
- 리스너가 열리지 않으면(주소가 사라짐, 포트 사용 중) 창에 이유를 보이고 QR 을 그리지 않습니다.

연결 처리(연결마다 `GVR Host Client` 스레드, 5.2절의 상태):

1. `client.NoDelay = true`, `ReceiveTimeout = 3000`. HTTP 헤더를 읽음(최대 16KB).
2. 경로 분기:

| 요청 | 응답 |
|---|---|
| `GET`/`HEAD` `/c` | `GameViewRemoteLanding.html` 을 디스크에서 읽어 `text/html; charset=utf-8`. 쿼리는 무시(`?noapp=1` 은 페이지의 스크립트가 봄) |
| `GET`/`HEAD` `/apk` | `App~/GameViewRemote.apk`. `Content-Type: application/vnd.android.package-archive`, `Content-Disposition: attachment; filename="GameViewRemote-<versionName>.apk"`, `Content-Length`, `Cache-Control: no-store`. 헤더를 쓴 뒤 64KB 씩 보냄. `Range` 는 무시하고 전체를 200 으로. 파일이 없으면 404 + 창에 경고 |
| `GET` `/app.json` | `App~/app.json` 그대로(10.4절) |
| `/video`, `/input` + `Upgrade: websocket` | `GameViewRemoteGate.Admit(…, lan: true)`(9.2절). 통과하면 3번 |
| `/x/<이름>` + `Upgrade: websocket` | `GameViewRemoteChannels` 에 그 이름이 등록돼 있으면 Gate 로 인증(채널 문자열 = `<이름>`)한 뒤 소켓을 처리기에 넘김. 시제품에는 등록된 채널이 없으므로 항상 404(15.1절 R5) |
| 그 밖(`/`, `/stats`, `/debug`, `/favicon.ico` 포함) | 404 `not_found` |
| `GET`/`HEAD` 가 아님 | 405 |

3. 인증된 연결: `GameViewRemote.IsRunning` 이면 `GameViewRemote.AdoptClient(client, kind, session)`(9.4절 G3 → 서버의 `Adopt`). 이 호출은 연결이 끝날 때까지 돌아오지 않습니다(읽기 루프). 아니면 대기실: `GameViewRemoteWs` 로 `{"t":"hello","v":2}` 와 `{"t":"seat","s":"lobby","msg":"Unity 에서 Play 를 누르면 화면이 나옵니다."}` 를 보내고, 들어오는 프레임을 읽어 입력 채널의 `{"t":"p"…}` 에는 `{"t":"P","ts":…}` 로 답하고 나머지는 버립니다.

### 9.2 `GameViewRemoteGate.cs`, `GameViewRemoteAuth.cs`, `GameViewRemoteWs.cs` (새 파일)

```csharp
public sealed class Session            // 인증된 앱 연결 하나의 정보. Connection.tag 에 담김
{
    public string deviceId;            // did
    public string deviceName;          // dev (없으면 "")
    public int appVersion;             // app
    public bool take;                  // take=1
    public bool viaLoopback;           // 기존 서버의 루프백 리스너로 들어옴(USB)
    public string remote;              // 원격 주소
    public GameViewRemoteServer.ChannelKind kind;
}

// 결과: Legacy = 인증 헤더 없는 루프백 연결(호출한 쪽이 지금처럼 101 을 씀)
//       Admitted = 인증 완료(101 과 서버 증명을 이미 썼고 클라이언트 증명까지 확인함)
//       Rejected = 오류 응답 또는 닫기 프레임을 이미 씀(호출한 쪽은 소켓만 닫음)
public enum Verdict { Legacy, Admitted, Rejected }

public static Verdict Admit(TcpClient client, NetworkStream stream, string path,
    Dictionary<string, string> headers, string acceptKey, out Session session);
// lan 인자는 없앴습니다(리뷰 M1): Admit 가 소켓의 원격 주소로 스스로 정합니다.
//   bool lan = !IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint).Address);
```

`Admit` 의 순서:

1. `Origin` 헤더가 있으면 `http://localhost:<포트>` 또는 `http://127.0.0.1:<포트>` 가 아니면 403 `bad_origin`(F-43).
2. `X-GVR-Auth` 가 없으면: `lan` 이면 401 `auth_required`, 아니면 `Legacy`.
3. 헤더를 해석해 `v`, `proto`, `sid`, `cn`, `did` 를 검사(3.3절의 오류 코드).
4. 인증 전 연결 수 제한은 5.2절(수락 루프에서 셈, 리뷰 M4). 여기서는 업그레이드 대기 8개 초과에 503 `busy`.
5. `sn` 을 만들고 서버 증명을 계산해 101 응답(`Sec-WebSocket-Accept` + `X-GVR-Auth`)을 **쓰기 1번**으로 보냄.
6. `GameViewRemoteWs.ReadClientText(stream, 최대 256바이트)` 로 첫 메시지를 읽음(소켓의 3초 제한). `{"t":"auth","mac":"…"}` 에서 `mac` 을 꺼내(`JsonUtility`) 상수 시간 비교. 틀리면 닫기 4401.
7. `ReceiveTimeout = 0` 으로 되돌리고 `Admitted`.

- 토큰과 서버 표시는 `GameViewRemoteHost` 의 정적 필드에서 읽습니다. Host 가 켜진 적이 없어 토큰이 없으면 인증 헤더가 있는 요청은 403 `unknown_server` 입니다.
- `GameViewRemoteAuth`: `Proof(byte[] key, string role, string channel, string sid, string cn, string sn)`, `Base64Url`, `FixedTimeEquals`, `ParseHeader`. `System.Security.Cryptography.HMACSHA256` 를 씁니다.
- `Tools/Game View Remote/Self Test` 메뉴: 3.2절의 시험 벡터 4개를 계산해 콘솔에 통과/실패를 찍습니다.

### 9.3 `GameViewRemoteSeats.cs` (새 파일)와 페이지

```csharp
public static void OnJoin(GameViewRemoteServer server, GameViewRemoteServer.Connection c);   // 연결이 목록에 들어가기 직전
public static void OnLeave(GameViewRemoteServer server, GameViewRemoteServer.Connection c);  // 연결이 목록에서 빠진 직후
public static void OnTake(GameViewRemoteServer server, GameViewRemoteServer.Connection c);   // 영상 소켓으로 {"t":"take"}
public static void Reset();                                                                  // 서버 Stop
public static List<Viewer> Snapshot();                                                       // 창 표시용(무리, 주소, 경로, 보는 중/대기, 기기 이름, 앱 버전)
```

- 무리 키: `c.tag is Session s ? "app:" + s.deviceId : "page"`.
- 3.5절의 규칙대로 active 무리를 다시 정하고(`OnJoin` 에서 `Session.take` 면 선호를 그 무리로), 바뀐 연결마다 `c.standby` 를 고친 뒤 알립니다. 알림은 `server.SendText(c, json)`(기존 공개 메서드, 메시지 단위 잠금 안에서 바로 씀)로 영상 연결에만 보냅니다.
- 입력 연결의 `standby` 가 바뀔 때: 대기로 가면 `GameViewRemoteInput.OnInputDisconnected(c.id)`(손가락 취소 + 연결 수 감소), 다시 active 가 되면 `GameViewRemoteInput.OnInputConnected(c.id)`(연결 수 증가, 0 → 1 이면 입력 번호 초기화) [코드: `GameViewRemoteInput.cs:102~117`]. 실제 접속/끊김 때의 같은 호출은 9.4절 G2 에서 "대기가 아닐 때만" 하도록 막아 두 번 세지 않게 합니다.
- active 가 된 페이지 연결에는 `{"t":"s","stale":false}` 를 보낸 뒤 서버의 마지막 상태 텍스트가 있으면 이어 보냅니다(S8 의 `LastStatus`).
- (리뷰 m4) 입력 연결 수를 세는 곳은 Seats 하나입니다: G2 는 `Seats.OnInputJoin/OnInputLeave` 만 부르고, Seats 가 자기 잠금 안에서 `GameViewRemoteInput.OnInputConnected/Disconnected` 를 부릅니다.
- (리뷰 m5) Seats 는 `GetConnections()` 복사본이 아니라 **자기 잠금 안의 자기 구성원 표**로 무리를 봅니다(목록에 들어가기 전의 연결, 동시에 들어오는 영상/입력 소켓도 보임).
- (리뷰 M8-b) `OnJoin` 은 `standby` 만 정합니다. 들어온 연결 자신에게 보내는 `seat` 알림은 서버가 `hello` 를 쓴 직후 `Seats.Announce(server, connection)` 로 보냅니다.
- 잠금: `Seats` 자체 잠금 하나. 그 안에서 서버의 `connections` 잠금을 잡지 않습니다(`OnJoin`/`OnLeave` 는 서버가 `connections` 잠금 **밖**에서 부름. 목록은 `server.GetConnections()` 의 복사본으로 봄).

`GameViewRemotePage.html` 의 변경(권장, 작음): 상태 텍스트에 `code === 'standby'` 가 있으면 문구 옆에 [여기서 보기] 버튼을 보이고, 누르면 영상 소켓으로 `{"t":"take"}` 를 보냅니다(워커 경로에서는 메인 → 워커 `postMessage({t:'take'})` → 워커가 `socket.send`). 이 변경이 없어도 페이지는 "화면이 멈춤: 다른 기기가 보고 있습니다." 를 보이고, 앱이 떠나면 스스로 active 로 돌아옵니다.

### 9.4 기존 파일에 넣는 고리

**`GameViewRemoteServer.cs`**

| # | 자리 | 바꾸는 것 |
|---|---|---|
| S1 | `Connection` 클래스(48행~) | 필드 2개 추가: `public volatile bool standby;` `public object tag;` |
| S2 | `HandleClient` 의 업그레이드 분기(360~384행) | ① 101 을 쓰기 전에 `GameViewRemoteGate.Admit(client, stream, path, headers, ComputeAccept(key), out session)` 를 부르고(LAN 여부는 Admit 가 원격 주소로 판정, 리뷰 M1), `Rejected` 면 닫고 돌아감, `Legacy` 면 지금처럼 101 을 씀, `Admitted` 면 101 을 건너뜀. ② 그 뒤의 "연결 객체 만들기 ~ `ReadLoop`"(365~383행)를 새 메서드 `RunWebSocket(TcpClient client, ChannelKind kind, object tag)` 로 옮기고 여기서는 그것을 부름 |
| S3 | 새 공개 메서드 | `public void Adopt(TcpClient client, ChannelKind kind, object tag)`: `running` 이 아니면 닫고 돌아감. `try { RunWebSocket(client, kind, tag); } catch(IO/Socket/ObjectDisposed) { } finally { 닫기 }`(`HandleClient` 의 예외 처리와 같은 모양). Host 의 스레드에서 불리고 연결이 끝날 때까지 돌아오지 않음 |
| S4 | `RunWebSocket` 안 | 연결 객체를 만든 직후 `connection.tag = tag; GameViewRemoteSeats.OnJoin(this, connection);` 를 `connections.Add` **앞**에(`OnJoin` 은 `standby` 만 정함). `pendingText` 는 **`standby` 가 아닐 때만** `lastStatus` 로 채움(리뷰 M8-a: 대기 페이지의 문구가 지워지지 않게). `hello` 를 쓴 직후 `GameViewRemoteSeats.Announce(this, connection);`(M8-b). 목록에 넣은 직후 `running` 을 다시 보고 아니면 닫음(리뷰 m6) |
| S5 | `PublishFrame`(231행 근처), `PublishStatus`(259행 근처), `SingleH264Client`(743행 근처)의 반복문 | 영상 연결 검사 옆에 `\|\| connection.standby` 를 더해 대기 연결을 건너뜀. `Seed`(275행) 맨 앞에 `if (connection.standby) return;` |
| S6 | `HandleText`(567행~) | ① 영상 + `{` 분기에 `else if (text.Contains("\"take\"")) { GameViewRemoteSeats.OnTake(this, connection); }` ② 입력 분기(618행) 앞에 `if (connection.standby && !text.StartsWith("{\"t\":\"p\"", StringComparison.Ordinal)) { return; }` |
| S7 | `Close`(880행~) | `removed` 일 때 `onDisconnected` **앞**에 `GameViewRemoteSeats.OnLeave(this, connection);` |
| S8 | 속성 추가 | `public string LastStatus { get { return lastStatus; } }` |
| S9 | `HandleClient` 의 404 분기(398~401행). **이 분기와 기존 `/`, `/stats`, `/debug` 분기 앞에 "원격 주소가 루프백이 아니면 404" 검사를 넣음**(리뷰 M1-2) | `else if (GameViewRemoteHost.TryServeLoopback(path, stream)) { }` 를 404 앞에: Play 중 루프백에서도 `/c`, `/apk`, `/app.json` 을 내줌(USB 로 착륙 페이지와 딥링크를 검증할 수 있게) |
| S10 | `Stop` | 끝에 `GameViewRemoteSeats.Reset();`. **`Reset()` 은 구성원 표만 비우고 선호 무리는 지우지 않음**(리뷰 M3) |
| S11 | 새 공개 메서드 | `public void CloseWith(Connection c, ushort code)`: 닫기 프레임을 그 코드로 쓰고 닫음. [새 QR 발급]의 4403 에 씀(리뷰 M8-c. 지금 `SendClose` 는 `private static`, 1000 고정) |

- S6 에서 같이 고치기를 권합니다(같은 줄들이라 덤으로 드는 수정이 없음): 기존의 `text.Contains("\"caps\"")`, `text.Contains("\"key\"")` 를 `"\"t\":\"caps\""`, `"\"t\":\"key\""` 로 좁히고 `take` 도 `"\"t\":\"take\""` 로 봅니다. 페이지가 보내는 글자(`JSON.stringify({t:'caps',h264:…})` = `{"t":"caps","h264":1}`)는 그대로 맞습니다 [코드: `GameViewRemotePage.html:752`]. 이렇게 해야 나중에 더하는 메시지의 값에 `key` 같은 단어가 들어가도 오판하지 않습니다(15.1절).
- `{"t":"take"}` 는 `"caps"` 도 `"key"` 도 포함하지 않으므로 기존 판정과 겹치지 않습니다. `{"t":"auth",…}` 는 Gate 가 연결 등록 전에 읽으므로 `HandleText` 에 오지 않습니다.
- `standby` 인 영상 연결의 전송 스레드는 그대로 돌지만 보낼 것이 없어 100ms 마다 깨어나 확인만 합니다. 수신 확인 대기열은 비어 있으므로 시간 초과도 없습니다.

**`GameViewRemote.cs`**

| # | 자리 | 바꾸는 것 |
|---|---|---|
| G1 | `Start()` 의 `server.Start(Port, AllowLan)`(350행) | `server.Start(Port, false)`: 기존 서버는 항상 루프백에만 바인드(리뷰 M1). `AllowLan` 설정과 그 `EditorPrefs` 키는 읽지 않음 |
| G2 | `OnConnected`(470~473행), `OnDisconnected`(479~482행)의 입력 분기 | `GameViewRemoteInput.OnInputConnected/OnInputDisconnected` 를 직접 부르지 않고 `GameViewRemoteSeats.OnInputJoin/OnInputLeave(connection)` 를 부름(리뷰 m4) |
| G3 | 새 공개 메서드 | `public static bool AdoptClient(TcpClient client, GameViewRemoteServer.ChannelKind kind, object tag)`: `server` 가 돌고 있으면 `server.Adopt(…)` 를 부르고 true(끝난 뒤 돌아옴), 아니면 false |

- 기존의 Start 조건("Play 모드에서만"), Play 종료 때 Stop, Play 중 리로드 복구는 **그대로 둡니다.** Play 밖에서 열려 있는 것은 Host 입니다.

**`GameViewRemoteWindow.cs`**

| # | 자리 | 바꾸는 것 |
|---|---|---|
| W1 | `OnGUI`(57~64행) | `DrawWarnings();` 다음 줄에 `GameViewRemoteHostGUI.Draw(this);` |
| W2 | `DrawControls` 의 "Wi-Fi 접속 허용" 토글(106행) | 토글을 **없애고** 그 자리에 한 줄 안내: "Wi-Fi 연결은 위의 QR 로 합니다(인증 없는 Wi-Fi 접속은 없앴습니다)". Wi-Fi URL 목록도 없앰(리뷰 M1) |

**`GameViewRemoteAdb.cs`**: `Device`(struct), `ListReadyDevices`, `Run`, `RunInBackground`, `ReversePreferUsb` **다섯 곳**을 `private` → `internal` 로(리뷰 M8-d: `Device` 가 `private` 이면 접근 수준 불일치로 컴파일되지 않고, `Run` 은 `RunInBackground` 의 `runLock` 안에서만 불러야 함). `GameViewRemoteAppAdb.cs` 가 다시 씁니다. 실제 이름과 시그니처는 6단계에서 다시 읽어 확인합니다 [미검증: 이번에는 호출부(302행)만 읽음].

**`GameViewRemoteInput.cs`**: `HandleMessage` 에 `if (message.t.Length != 1) return null;` 한 줄(리뷰 M6).

**`GameViewRemoteCapture.cs`, `GameViewRemoteH264.cs`, `GameViewRemoteTrace.cs`**: 바꾸지 않습니다. 프레임 형식, `PROTOCOL_VERSION`(2), 인코더 인자는 그대로입니다.

### 9.5 `GameViewRemoteHostGUI.cs`, `GameViewRemoteQr.cs`, `GameViewRemoteLanding.html` (새 파일)

창의 새 구역(맨 위):

```
[ Start ] / [ Stop ]         ← Host 를 켜고 끔. Play 여부와 무관하게 누를 수 있음
상태: 대기 중(Play 를 누르면 화면이 나갑니다) | 연결됨: SM-F731N 192.168.25.60 무선, 보는 중 | …
⚠ Windows 방화벽이 폰에서 들어오는 연결을 막고 있습니다.  [방화벽 허용…] [연결 점검]
┌───────────┐  폰 카메라나 앱의 스캐너로 찍으세요.
│   QR      │  주소: 192.168.25.5:7300  [▼ 다른 주소]   [QR 이미지 저장] [새 QR 발급]
└───────────┘  ※ 같은 Wi-Fi 의 누구나 이 QR 로 접속할 수 있습니다. 공용 Wi-Fi 에서 쓰지 마세요.
[USB 로 앱 설치] [USB 로 연결]     앱 버전: 0.1.0 (1)
```

- 기기가 연결돼 있으면 QR 을 접고 [QR 보기]를 냅니다(F-02 권장).
- `GameViewRemoteQr`: `BuildPayload()`(4.1절) → `QrCode.EncodeBinary(bytes, Ecc.Medium)`(가져온 인코더) → 모듈 행렬을 `Texture2D`(`TextureFormat.RGB24`, `FilterMode.Point`, 여백 4모듈, 모듈당 `max(4, floor(표시 크기 / (모듈 수 + 8)))` 픽셀)로. 토큰이나 주소가 바뀔 때만 다시 만듭니다. [QR 이미지 저장] 은 `EncodeToPNG` 를 `Temp/GameViewRemote/qr.png` 에 씁니다(검증용, 요구사항 10).
- 인코더: `manuelbl/QrCodeGenerator`(MIT)의 핵심 파일을 `ThirdParty/QrCodeGenerator/` 에 네임스페이스 `GameViewRemoteThirdParty.QrCodeGenerator` 로 넣고 `LICENSE` 를 같이 둡니다. Unity 2022.3(C# 9, .NET Standard 2.1)에서 컴파일되는지는 [미검증]. 안 되면 `QRCoder` 의 `QRCodeGenerator` 부분, 그것도 안 되면 직접 구현(조사 8장의 순서).
- 착륙 페이지(외부 자원 없음, 한 파일, 8KB 이하):

```
Game View Remote                                  (앱 버전 0.1.0 은 /app.json 에서 읽어 표시)
[ 앱으로 열기 ]                                   ← href = 4.3절의 intent:// (스크립트가 만듦)
처음이면 먼저 앱을 설치하세요.
[ 앱 설치(APK 받기) ]                             ← href = /apk
 1. 받기를 누르고, 브라우저가 물으면 "그래도 받기"
 2. 받은 파일을 열고 "설치"
 3. "출처를 알 수 없는 앱" 을 물으면 이 브라우저를 허용
 4. Play 프로텍트가 물으면 "검사 없이 설치"
 5. 삼성 폰에서 설치가 막히면 설정 > 보안 및 개인정보 보호 > 자동 차단을 확인
[ 설치가 끝났으면 여기를 눌러 연결 ]              ← 같은 intent://. [앱 설치]를 누른 뒤 크게 강조(F-27)
```

  - `#` 뒤 값이 없으면(카메라 앱이 떼어 버림 등) [앱으로 열기] 대신 "앱을 열고 스캐너로 QR 을 찍으세요"를 보입니다.

### 9.6 `GameViewRemoteNet.cs` (새 파일): 어느 주소를 QR 에 넣는가

`NetworkInterface.GetAllNetworkInterfaces()` 에서 `OperationalStatus.Up` 인 IPv4 를 모아 아래로 걸러 점수를 매깁니다(조사 3.5).

| 단계 | 규칙 |
|---|---|
| 제외 | 루프백, 169.254.x.x, `NetworkInterfaceType.Tunnel`/`Ppp`, 이름·설명에 `vEthernet`, `Hyper-V`, `WSL`, `VirtualBox`, `VMware`, `Loopback`, `TAP`, `VPN`, `WireGuard`, `Tailscale`, `ZeroTier` 가 있는 것, 사설 대역(10/8, 172.16/12, 192.168/16)이 아닌 것 |
| 1순위 | adb 로 보이는 폰의 `wlan0` 주소와 같은 서브넷(USB 가 붙어 있을 때만 알 수 있음. `GameViewRemoteAppAdb` 가 읽어 둔 값) |
| 2순위 | 기본 게이트웨이가 있는 어댑터(`GatewayAddresses` 가 비어 있지 않음) 중 유선/무선 물리 어댑터 |
| 나머지 | 후보(`a=`)로, 최대 3개 |

- 이 PC에서는 `192.168.25.5` 가 주 주소가 되고 `172.30.224.1`(WSL)은 제외됩니다 [조사 1.2 실측값에 규칙을 적용].
- Host 는 주 주소와 후보 **전부에** 리스너를 엽니다(QR 에 넣은 주소 = 듣는 주소, F-44). 제외된 주소로는 듣지 않습니다.
- 창의 [▼ 다른 주소] 는 제외되지 않은 주소 목록에서 고르게 합니다(입력 아님, F-05).

### 9.7 `GameViewRemoteFirewall.cs` (새 파일)

**읽기(관리자 불필요)**: `powershell -NoProfile -NonInteractive -Command <스크립트>` 를 백그라운드 스레드에서 실행해 JSON 을 받습니다(Start 때와 창의 [다시 확인]). 스크립트는 COM `HNetCfg.FwPolicy2` 로 ① 현재 프로필 ② 그 프로필의 기본 인바운드 동작 ③ 이 Unity.exe(`EditorApplication.applicationPath`)의 켜진 인바운드 규칙(이름, 동작, 프로필, 프로토콜, 로컬 포트, 원격 주소)을 읽습니다(조사 1.3: 이 방법이 186ms, `Get-NetFirewallRule` 은 2분 넘게 걸림).

판정(현재 프로필에 걸리고 TCP·도구 포트에 해당하는 규칙만 봄):

| 조건 | 창의 표시 |
|---|---|
| 차단 규칙이 하나라도 있음 | "Windows 방화벽이 막고 있습니다(규칙: <이름>)" + [방화벽 허용…] |
| 차단은 없고 허용 규칙이 있음 | "Windows 방화벽은 열려 있습니다" |
| 규칙이 없고 기본 동작이 차단 | "Windows 가 처음 한 번 허용 여부를 물을 수 있습니다. 물으면 [액세스 허용]을 누르세요. 창이 뜨지 않았으면 [방화벽 허용…]" |
| 방화벽 꺼짐 | "Windows 방화벽은 꺼져 있습니다" |

- 다른 보안 제품은 이 판정에 나오지 않습니다(리뷰 m4). 실제 도달은 [연결 점검]으로만 확인합니다: USB 로 붙은 폰에서 `adb -s <serial> shell toybox nc -w 3 <주 주소> <포트>` 가 연결되는지(조사 1.3 에서 쓴 방법).
- Unity(Mono)에서 PowerShell 을 띄워 COM 을 읽는 것은 [미검증]입니다(조사는 PowerShell 창에서 직접 실행).

**[방화벽 허용…]**: 누르면 무엇을 바꾸는지 대화 상자로 보여 준 뒤, 스크립트 내용을 임시 파일로 쓰지 않고 **`-EncodedCommand`**(UTF-16LE base64)로 인자에 실어 `Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -EncodedCommand <…>'` 로 실행합니다(UAC 창 1번. 리뷰 m12: 쓰기와 동의 사이에 파일이 바뀔 틈을 없앰). 스크립트가 하는 일:

1. 이 Unity.exe 의 켜진 인바운드 **차단** 규칙의 이름, 프로필, 프로토콜, 포트를 `%LOCALAPPDATA%\GameViewRemote\firewall-backup.json` 에 저장하고 그 규칙들을 **끕니다**(지우지 않음).
2. 같은 프로필로 대신할 차단 규칙을 넣습니다(그룹 `Game View Remote`): 이 실행 파일, 인바운드, TCP, 로컬 포트 `1-<포트−1>` 과 `<포트+1>-65535` / 이 실행 파일, 인바운드, UDP, 모든 포트. (Unity.exe 가 `0.0.0.0` 에서 듣는 다른 포트가 열리지 않게, 리뷰 m3)
3. 허용 규칙을 넣습니다: 이 실행 파일, 인바운드, TCP, 로컬 포트 `<포트>`, 원격 주소 `LocalSubnet`, **현재 프로필만**(리뷰 m5).
4. 끝나면 도구가 읽기를 다시 실행해 결과를 표시합니다.

**[되돌리기]**: 그룹 `Game View Remote` 의 규칙을 지우고 저장해 둔 이름의 규칙을 다시 켭니다(같은 승격 방식).

- 포트나 Unity 버전(실행 파일 경로)이 바뀌면 다시 해야 합니다. 창이 그때 다시 "막고 있습니다"를 보입니다.
- **이 절의 쓰기 부분은 워커가 시험할 수 없습니다**(UAC 는 사람이 눌러야 하고, 브리프 C1 이 누르지 말라고 했습니다). 6단계는 스크립트를 만들고 `-WhatIf` 에 해당하는 "무엇을 할지 출력만 하는" 실행까지만 확인합니다. 규칙의 모양(특히 2번의 대체 차단 규칙이 3번의 허용보다 우선하지 않는지: 포트 범위가 겹치지 않으므로 우선하지 않는다고 봄)은 [미검증]이고 소유자가 누른 뒤 확인합니다.

### 9.8 `GameViewRemoteAppAdb.cs` (새 파일)

| 버튼 | 명령(기기가 여러 줄이면 USB 기기의 일련번호로 `-s`) |
|---|---|
| [USB 로 앱 설치] | `install -r "<App~/GameViewRemote.apk>"`. 서명이 달라 실패하면(`INSTALL_FAILED_UPDATE_INCOMPATIBLE`) "기존 앱을 지우고 다시 설치할까요?"를 물은 뒤 `uninstall kr.co.seeplay.gvr` → `install` |
| [USB 로 연결] | Play 중이 아니면 "Play 를 먼저 누르세요". `reverse tcp:<포트> tcp:<포트>`(기존 `GameViewRemoteAdb` 의 USB 우선 규칙을 따름) → `shell "am start -W -a android.intent.action.VIEW -d 'seeplay-gvr://127.0.0.1:<포트>/c?v=1&s=<sid>&t=<token>&via=usb&take=1' kr.co.seeplay.gvr"` |
| [연결 점검] | 9.7절 |

- (리뷰 m3) [USB 로 연결]은 토큰을 주소에 넣지 않고 `--es t <토큰>` 으로 넘기고(앱이 진단 빌드에서 받음. `DIAG` 가 꺼진 빌드를 배포하게 되면 주소에 넣는 방식으로 되돌려야 하므로 그때 logcat 을 다시 확인), 창과 콘솔에 보이는 명령에서는 `t=…` 로 가립니다.
- 폰 `wlan0` 주소를 읽어(`shell ip -f inet addr show wlan0`) `GameViewRemoteNet` 에 넘깁니다(9.6절 1순위).

### 9.9 기존 Chrome 페이지 클라이언트가 그대로 동작하는 이유

| 항목 | 바뀌는가 |
|---|---|
| 접속 주소 `http://localhost:7300/`(adb reverse) | 그대로. 기존 서버의 루프백 리스너, Play 중에만 |
| `GET /`, `/stats`, `/debug` | 그대로(루프백에서). LAN 주소로는 404(지금은 "Wi-Fi 접속 허용"을 켜야만 열렸음) |
| WebSocket 업그레이드 | 페이지는 `X-GVR-Auth` 를 보내지 않으므로 `Legacy` 로 지금과 같은 101. 페이지가 보내는 `Origin`(`http://localhost:7300`)은 검사를 통과 |
| `hello`, 프레임 형식, `caps`/`a`/`key`, 입력 형식, 역압, 자동 프레임 간격 | 그대로 |
| 앱이 없을 때 | 무리가 `page` 하나뿐이라 항상 active. `standby` 는 한 번도 서지 않음 = 지금과 같은 동작 |
| 앱이 같이 붙을 때 | 지금: 둘 다 MJPEG. 바뀐 뒤: 3.5절(한쪽만 영상, 다른 쪽은 대기 문구) |
| 측정 스크립트(`measure/`) | `Origin` 없이 붙는 Node 클라이언트(`wsclient.js`)와 `/stats` 읽기는 루프백이라 그대로 |
| "Wi-Fi 접속 허용" 토글 | **없어짐**(리뷰 M1). LAN 주소로 Chrome 페이지를 여는 기능이 사라짐(그 경로는 MJPEG 만 됐고 이 PC에서는 방화벽에 막혀 쓰이지 않았음). 대신 QR 로 앱 연결 |

- 주의: PC 브라우저에서 `http://127.0.0.1:7300/` 이나 `http://localhost:7300/` 이 아닌 주소(예: PC 이름)로 페이지를 열면 `Origin` 검사에 걸립니다. 지금 안내하는 주소는 `localhost` 뿐입니다.
- 확인(6단계 끝, PRD NF-45): 고리를 넣기 전과 후에 측정 스크립트 `measure.js idle 10` 과 `measure.js tap 10 --press=120 --still` 을 USB 로 돌려 수치가 같은 범위인지, `wsclient.js mjpeg`/`h264`/`h264-key`/`h264-noack` 가 그대로 통과하는지 봅니다.

## 10. 빌드, 서명, APK 제공

### 10.1 도구와 버전

| 항목 | 값 | 근거 |
|---|---|---|
| JDK | Unity 6000.2.10f1 의 OpenJDK 17.0.9 (`C:\Program Files\Unity\Hub\Editor\6000.2.10f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK`) | 조사 1.4 에서 이 구성으로 빈 앱을 실제로 빌드 |
| Gradle | 8.13. **같은 Unity 의 `Tools\gradle\lib\gradle-launcher-8.13.jar` 를 직접 실행**(5단계 결정: 래퍼 배포본을 따로 내려받지 않음. `android/build.sh`, `build.ps1`) | 조사 1.4, 5단계 실측 |
| Android Gradle Plugin | 8.13.0 | 조사 1.4(캐시에 있음) |
| SDK | 같은 Unity 의 `SDK`(platforms 36, build-tools 36.0.0). `local.properties` 의 `sdk.dir` | 조사 1.4 |
| `compileSdk` / `targetSdk` / `minSdk` | 36 / 36 / 26 | PRD D13 |
| 언어 | Kotlin(JVM 17). Kotlin Gradle 플러그인 **2.2.20**(5단계 실측: AGP 8.13.0 + Gradle 8.13 + JDK 17 에서 빌드됨). `buildToolsVersion = "36.0.0"` 을 명시(안 하면 AGP 가 35.0.0 을 내려받으려다 라이선스 오류) | PRD D11 |
| 의존성 | `androidx.core:core-ktx`, `androidx.activity:activity-ktx`, `androidx.activity:activity-ktx:1.9.3`, CameraX 1.4.2(`camera-core`, `camera-camera2`, `camera-lifecycle`, `camera-view`), **`com.google.zxing:core:3.5.3`**(ML Kit 대신, 5단계 결정). 네트워크 라이브러리 없음(OkHttp 등 쓰지 않음). Compose 없음 | PRD D4, D11 |
| ABI | `arm64-v8a`, `armeabi-v7a`. APK 가 15MB 를 넘으면 `arm64-v8a` 만 | PRD NF-42 |
| 식별자 / 이름 | `kr.co.seeplay.gvr` / `Game View Remote` | PRD O5(권장값으로 진행) |
| 버전 | `versionCode` 1부터, `versionName` `0.1.0`. 배포할 때마다 `versionCode` 를 올림 | |

- 처음 빌드할 때 Kotlin 플러그인, AndroidX, CameraX, ML Kit 를 Google Maven / Maven Central 에서 내려받습니다. 요청에 개인 정보는 들어가지 않습니다(좌표와 버전만).
- 스캐너 의존성(CameraX, ML Kit)은 M6 에서 넣습니다. 그 전의 빌드는 `androidx.core` 와 `activity` 뿐입니다.

### 10.2 프로젝트 구조

```
GameViewRemoteApp/
  android/
    settings.gradle.kts, build.gradle.kts, gradle.properties, gradlew(.bat), gradle/wrapper/
    local.properties            (sdk.dir, 커밋하지 않음)
    build.ps1                   (JAVA_HOME 과 sdk.dir 을 잡고 gradlew 를 실행, 결과를 dist/ 로 복사하고 app.json 을 만듦)
    keystore/gvr-proto.jks      (시제품 전용 키)
    app/
      build.gradle.kts
      src/main/AndroidManifest.xml
      src/main/java/kr/co/seeplay/gvr/{MainActivity, ui/, link/, net/, session/, video/, input/, diag/}
      src/main/res/…
      src/test/java/…           (LinkParser, Auth 시험 벡터, AvcConfig, FrameHeader 의 단위 시험: JVM 에서 돎)
  tools/                        (2.3절)
  dist/                         (GameViewRemote-<versionName>.apk, app.json)
  docs/
```

빌드 명령(한 줄): `powershell -File android\build.ps1`. 내용은 `$env:JAVA_HOME = <위 JDK>` → `android\gradlew.bat -p android assembleDebug` → `apksigner verify --print-certs` 로 서명 확인 → `dist\` 로 복사 → `app.json` 생성. (F-103)

### 10.3 서명

- 시제품 전용 키 저장소를 프로젝트에 넣습니다: `keytool -genkeypair -keystore android/keystore/gvr-proto.jks -alias gvr -keyalg RSA -keysize 2048 -validity 10950 -storepass <비밀번호> -keypass <비밀번호> -dname "CN=GVR Prototype"`.
- `signingConfigs` 하나를 `debug` 와 `release` 빌드 유형이 함께 씁니다. v2 서명(AGP 기본). 어느 PC에서 빌드해도 같은 키라 덮어 설치됩니다(F-22).
- 이 키는 저장소에 든 **공개된 것과 같은 키**입니다. 사내 배포 전에 따로 보관하는 키로 바꿉니다(PRD O6, NF-63 ③). 키를 바꾸면 기존 설치는 지우고 다시 깔아야 합니다.
- (리뷰 M7, 브리프 3.3의 3) 빌드 유형은 셋입니다: `debug`(디버그 가능, 개발용), **`proto`**(`initWith(release)`, `isDebuggable = false`, 난독화 끔, 같은 키, `BuildConfig.DIAG = true`), `release`(`DIAG = false`). **배포와 합격 수치 측정은 `proto` 빌드**로 하고 수치마다 빌드 유형을 적습니다. 기록 파일은 `adb pull` 로 가져옵니다(`run-as` 불필요). `DIAG` 가 꺼진 `release` 에서 진단 분기가 동작하지 않는지는 8단계에서 확인합니다.

### 10.4 Unity 서버가 APK 를 내주는 방법

1. 5단계: `build.ps1` 이 `dist/GameViewRemote-<versionName>.apk` 와 `dist/app.json` 을 만듭니다.

```json
{"versionCode":1,"versionName":"0.1.0","size":7340032,"sha256":"<APK 의 SHA-256 hex>","proto":2,"auth":1,"package":"kr.co.seeplay.gvr"}
```

2. 6단계: 두 파일을 `catjump/Assets/04_Scripts/Editor/GameViewRemote/App~/` 에 `GameViewRemote.apk`, `app.json` 으로 복사합니다(창의 [APK 가져오기] 버튼 또는 손으로). `App~` 는 Unity 가 가져오지 않는 폴더라 `.meta` 가 생기지 않고 빌드에도 들어가지 않습니다 [미검증: 조사 9장에서 문서로 확인하지 못한 규칙. 6단계에서 폴더를 만들어 확인].
3. Host 가 `GET /apk` 에 그 파일을(9.1절), `GET /app.json` 에 그 JSON 을 내줍니다. 인증 헤더의 `app`(서버 → 앱)도 이 `versionCode` 입니다.
4. APK 경로는 프로젝트별 설정 `apkPath` 로 바꿀 수 있습니다(빈 값 = `App~/GameViewRemote.apk`, 9.1절). 5단계와 6단계 사이에는 `dist/` 의 파일을 직접 가리키게 해서 복사 없이 시험할 수 있습니다.
5. 갱신: 앱이 인증 응답의 `app` 이 자기 `versionCode` 보다 크면 "새 버전이 있습니다 [업데이트]" 를 보이고(권장, F-25), 누르면 `http://<서버>:<포트>/c` 를 브라우저로 엽니다. 앱은 APK 를 직접 받거나 설치하지 않습니다(설치 권한을 선언하지 않음, F-23).

- QR 설치는 폰이 PC의 Host 에 닿아야 하므로 이 PC에서는 [방화벽 허용] 뒤에만 됩니다(조사 2.3). 그 전의 설치는 [USB 로 앱 설치]입니다. 워커의 설치도 adb 입니다(브리프 C2).

## 11. 시험 계획

### 11.1 원칙

- 모든 수치에 경로(USB adb / LAN 직접(중계) / LAN 직접(PC가 검) / LAN 직접(Host)), Wi-Fi 주파수와 링크 속도, 패널 주사율(요청한 정책과 잰 값), 표시 방식, 지터 버퍼 설정, Wi-Fi 잠금, 서버 종류(실제 Unity / 재생), 서버 `transportStride` 를 같이 적습니다(00 문서 L0, L3).
- 주입 탭은 **누르는 시간이 있는 것만** 씁니다: `adb -s <serial> shell input swipe <x> <y> <x> <y> 120`(제자리 120ms. 측정 스크립트의 `--press=120 --still` 과 같음). 위치는 게임 UI 버튼이 아닌 빈 곳(보고 3은 (540,1000)).
- 끊김을 잴 때는 테스트 패턴을 켭니다(`/debug?pattern=1`, 끝나면 끔). 실제 게임 탭 구간은 따로 잽니다.
- adb 명령에는 항상 `-s <일련번호>`. 다른 세션의 터널(7300)을 건드리지 않고 시험 포트(7310, 7311, 7314, 7301)를 씁니다. 끝나면 시험용 reverse/forward 와 중계를 내립니다.
- 폰을 깨우고 앱을 앞으로 띄운 횟수와 시각, 실제 서버에 붙은 시간을 `05_build_and_test.md` 에 적습니다. 실제 서버를 쓴 뒤에는 Chrome 페이지가 다시 H.264 로 붙었는지 `/stats` 의 `extra.codec` 으로 확인합니다(브리프 C5).

### 11.2 측정 도구

| 대상 | 도구 | 나오는 것 |
|---|---|---|
| 앱 | 앱의 기록 파일(8.5절) → `analyze.js`, `touchstall.js`(측정 스크립트 그대로) + `tools/app-report.js`(앱에만 있는 값: 표시 통지 시각, 디코더, 주사율 전환) | 멈춘 갱신/초, 건너뛴 프레임/초, 화면 정지 최대, 표시 fps, 터치 → 표시, 누름 구간의 전달 간격과 정지, 도착 지연 분포 |
| Chrome 페이지(기준선) | `measure.js <idle\|tap\|drag> <초> [x y] --press=120 --still`, `record.js` + `analyze.js`(측정 스크립트 README) | 같은 항목(표시 시각은 그리기 호출 시각) |
| 서버 | `/stats`(`snapshot`, `frames`, `encodes`). `tools/merge-stats.js` 가 기록 구간에 맞춰 합침 | 탭 전후 200ms 의 Unity 프레임 시간, `transportStride`, 수신 확인 시간 |
| 링크만 | `linktest.js`(측정 스크립트) + 앱의 링크 시험 모드(디버그 전용 링크 `seeplay-gvr://linktest?url=ws://<주소>:7312/s&name=<이름>`: 페이지와 똑같이 받은 메시지마다 `a<번호>` 로 답하고 끝에 `{done,name,arr}` 를 보냄) | Chrome 과 네이티브 앱의 도착 지연 분포를 같은 서버로 비교. Wi-Fi 잠금 켬/끔 |
| 두 클라이언트 공통 보조 지표 | `adb shell dumpsys SurfaceFlinger --latency <레이어>` 를 1초마다 읽어 실제 표시 시각의 간격을 냄 | 앱과 페이지에 같은 정의를 쓸 수 있는 유일한 후보. 레이어 이름 찾기와 값의 쓸모는 [미검증], 읽는 것 자체가 부하가 될 수 있으므로 본 측정과 다른 회차로 |
| 눈 | 테스트 패턴의 네모가 고르게 움직이는지. 소유자 확인 | 수치와 눈이 다를 수 있음(00 문서 L2) |

### 11.3 탭 끊김에 대한 판정 (같은 폰, 두 표시 모드, Chrome 페이지와 비교)

조건: USB adb reverse, 실제 Unity 서버, H.264 720x1280 6Mbps, 테스트 패턴 60fps, 전체 화면, HUD 끔. 구간마다 60초 이상, 탭은 1.5초마다 120ms 제자리 누름(40회 이상), 2회 반복. 앱과 페이지를 같은 날 번갈아(앱 → 페이지 → 앱 → 페이지) 잽니다.

| # | 클라이언트 | 표시 모드 | 구간 |
|---|---|---|---|
| T1 | Chrome 페이지(워커 + 표시 일정, 기본) | **폰의 지금 설정 = 가변 120Hz**(잰 주사율을 같이 적음). 기준선은 같은 모드에서 잰 보고 4 와 개발 문서 12.9 의 표. 표준 60Hz 수치(보고 3)는 참고 열(브리프 3.3의 5) | 무터치, 탭, 드래그 |
| T2 | 앱 `pace=sched buf=auto rr=match`(기본값) | 가변 120Hz(무터치 60Hz, 터치 뒤 120Hz. 잰 값으로 확인) | 무터치, 탭, 드래그 |
| T3 | 앱 `pace=now` | 60Hz | 무터치, 탭 |
| T4 | 앱 `rr=force60` | 60Hz 고정 요청 | 탭 |
| T5 | 앱 `rr=max`(먹으면 120Hz) | 잰 값 | 무터치, 탭 |
| T6 | 앱 `rr=none` | 시스템이 정함. 리뷰 M5 는 이것을 기본 비교로 권했으나 **5단계 실측에서 패널이 30Hz 로 내려가 비교 대상이 될 수 없었습니다**(7.5절). 기록만 남김 | 무터치 |
| T9 | 대조: 터치 없이 같은 주기로 `adb shell input keyevent 0`(리뷰 m10: 주입 프로세스 기동만의 영향) | T2 설정 | — |
| T7 | 앱, 실제 게임 화면에서 탭(게임이 반응하는 화면) | T2 설정 | 탭 60초 + `/stats` 대조 |
| T8 | (소유자) 앱과 페이지, 실제 손가락, 폰을 가변 120Hz 로 바꾼 상태 포함 | — | 앱은 HUD [기록 시작/끝], 페이지는 `record.js` |

"두 표시 모드": 앱은 T2/T4(60Hz)와 T5(최대)로 폰 설정을 바꾸지 않고 둘 다 잽니다. `rr=max` 가 표준 60Hz 설정에서 무시되면 120Hz 쪽은 소유자가 폰을 가변 모드로 바꾼 뒤(T8)에만 잴 수 있고, 그렇게 보고합니다. 페이지는 주사율을 요청할 수 없으므로 폰 설정대로만 잽니다(가변 모드의 워커 경로 수치는 아직 없음, 보고 3).

판정(탭 구간, PRD NF-4와 NF-10, 기준선 = T1 과 보고 3의 표):

| 결과 | 판정 |
|---|---|
| 앱(T2)의 탭 구간이 **같은 모드의 페이지 기준선(보고 4: 멈춘 갱신 0.1/초, 건너뛴 프레임 0.0/초, 화면 정지 최대 38ms, 터치 → 표시 57.3ms) 이하**(표본 차이를 감안해 멈춘 갱신·건너뜀은 +0.2/초, 정지 최대는 50ms 까지), NF-10 ≤ 5%, 터치 → 표시 평균 ≤ 페이지 + 5ms. **전부 `releaseNs` 기준**(리뷰 B1, M5) | **앱이 탭 끊김 없이 페이지와 같거나 낫다** |
| 위를 못 채우고, 같은 시각 서버 `frames` 의 `deltaMs` 가 25ms 를 넘거나 `encodes` 에 건너뜀이 있음 | Unity 쪽(증거 첨부). 페이지에서도 같은 시각에 같은 현상이 있는지 확인 |
| 위를 못 채우고, 그 시각 앱의 도착 간격이 벌어져 있음(USB 에서는 드묾) | 링크 |
| 도착은 고른데 표시가 고르지 않음 | **앱의 결함.** 표시 방식(T3), 주사율 정책(T4~T6), 디코더(`decoderHeld`, 디코딩 시간)를 바꿔 가며 원인을 좁혀 고친 뒤 다시 잼 |

- `targetNs`/`renderedNs` 로만 계산한 수치로는 "충족"으로 적지 않습니다(8.1절). 탭 표본은 조건당 120회 이상을 목표로 하고 비율과 함께 횟수를 적습니다(리뷰 m10).
- 누름 구간의 핵심 지표(보고 3과 같은 것): 누르는 동안 프레임 전달 간격의 최댓값(앱: `recvNs` 간격), 누른 뒤 화면 정지의 최댓값, 손을 뗀 직후 **6ms 안에 3장 이상**(PRD 4.2 의 정의로 통일, 리뷰 m9) 몰려 전달된 횟수. 고치기 전 페이지는 128~155ms / 117~136ms 였고 워커 경로는 정지 최대 50ms 입니다.

### 11.4 무선

조건: 폰 2.4GHz(지금 상태), 실제 Unity 서버(중계 또는 PC가 거는 방식으로. 방화벽 허용 전에는 Host 로 직접 못 받음), 그 밖은 11.3 과 같음.

| # | 무엇 | 비교 |
|---|---|---|
| W0 | 양성 대조: `static.js` 를 LAN 포트에 띄워 폰 `nc` 로 닿는지 | 5단계 첫 작업(PRD 5.1) |
| W1 | 링크만: `linktest.js` 를 Chrome 과 앱의 링크 시험 모드로 각각, Wi-Fi 직접. 앱은 Wi-Fi 잠금 켬/끔 | 정체가 받는 프로그램이나 절전에 따라 달라지는지(PRD 4.7 의 가설) |
| W2 | 앱, LAN 직접(`relay.js`), `buf=low` / `auto` / `smooth`, 무터치와 탭 | 지터 버퍼 설정별 멈춘 갱신, 화면 정지 분포(50ms·100ms 초과 횟수), 터치 → 표시 |
| W3 | 앱, LAN 직접(PC가 검, `dial.js`), `buf=auto` | F-41 의 성립 여부와 W2 대비 차이(중계가 끼는 것은 같고 TCP 방향만 다름) |
| W4 | 앱, 6Mbps 대 3Mbps(`replay.js` 로. 실제 서버의 비트레이트는 창 설정이라 조정자가 허락한 시간에만) | 정체가 보내는 양에 따라 달라지는지 |
| W5 | Chrome 페이지, 무선 adb 터널(창의 "영상 경로: 무선 우선"은 소유자 설정이므로 조정자가 허락한 시간에만) | 기준선(보고 3: 탭에서 멈춘 갱신 0.1/초, 정지 최대 50ms, 터치 → 표시 71.7ms) |
| W5b | **앱, 무선 adb 터널**(`adb reverse`, `127.0.0.1:7300`, 창 "영상 경로: 무선 우선". 끝나면 되돌림) | 리뷰 B2: "앱 대 페이지"는 **W5 대 W5b**(같은 경로), "LAN 직접의 이득"은 **W5b 대 W2**(같은 클라이언트)로만 적습니다. W5 와 W2 를 직접 견준 숫자는 결론의 근거로 쓰지 않습니다 |
| W6 | Chrome 페이지를 `http://<PC IP>:7310/`(중계)로 열었을 때의 형식 | "LAN 직접에서 브라우저는 MJPEG 로 떨어진다"(보고 3, 00 문서 L12)의 실기기 확인. `/stats` 의 `extra.codec` 과 페이지 HUD |
| W7 | 재전송: 측정 전후 `netstat -s -p tcp` 의 재전송 수 차이 | PRD 4.6(약한 증거) |
| W8 | (소유자가 폰을 5GHz 로 옮긴 뒤) W1, W2 를 다시 | 가장 큰 변수. 미측정 |

보고 형식: PRD 4.2 의 표를 경로와 지터 버퍼 설정별로 채우고, "앱이 만든 정체"(같은 시각의 도착 정체로 설명되지 않는 표시 정체)와 "링크가 만든 정체"를 나눠 셉니다. 무선 합격은 PRD 5.1 조건 2의 세 갈래로 판정합니다. 2.4GHz 에서 100ms 넘는 정체가 남는 것은 예상된 결과이고(PRD 4.7) 그대로 적습니다.

### 11.5 기능 시험

| 항목 | 방법 | 기준 |
|---|---|---|
| 단위 시험 | `gradlew test`: 인증 시험 벡터 4개, `LinkParser`(정상, `v` 다름, 값 누락, `a` 여러 개, http 와 커스텀 스킴이 같은 결과), `AvcConfig`(avcC 해석, AVCC 변환, 길이 오류), `FrameHeader` | 전부 통과 |
| 인증(NF-60) | `replay.js --auth` 와 6단계의 Host 에: 토큰 맞음 / 틀림(서버 증명 불일치로 앱이 닫음) / 헤더 없음(401) / `sid` 다름(403) / 클라이언트 증명 틀림(4401) / 3초 침묵(4401) / 9번째 인증 전 연결(503). LAN 주소로 `/`, `/stats`, `/debug` → 404 | 표대로 |
| QR 내용(요구사항 10) | [QR 이미지 저장] 의 PNG 를 PC 의 Python OpenCV `cv2.QRCodeDetector` 로 읽어(이 PC에 설치돼 있음, 조사 8장) `BuildPayload()` 의 문자열과 비교. `LinkParser` 단위 시험에 같은 문자열을 넣어 값이 맞는지 | 글자 단위로 일치 |
| QR 이 가리키는 주소(요구사항 10) | (리뷰 m14: 방화벽 허용 전에는 QR 그대로의 주소가 폰에서 닿지 않으므로 보고에 "QR 주소와 포트만 다른 주소로 확인했고, QR 그대로는 소유자가 UAC 를 누른 뒤 확인"이라고 적음) 4.4절의 두 명령. Play 중 USB 로는 `http://localhost:7300/c#…`(S9), LAN 은 방화벽 허용 전이면 `relay.js` 가 Host 포트로 중계한 주소로 | 착륙 페이지가 뜨고, [앱으로 열기] 가 앱을 열어 연결됨. 딥링크를 직접 넣어도 연결됨 |
| 설치 흐름 | 5단계 첫 작업(PRD 5.1): 삼성 인터넷과 Chrome 에서 `/apk` 내려받기와 설치 화면 기록. **폰의 보안 설정을 바꾸는 화면은 누르지 않고** 그 화면이 나온 데까지 화면 내용을 적음(브리프 C2). 실제 설치는 adb | 화면 순서와 탭 수 기록 |
| 인증 전 제한(리뷰 M4) | 1바이트씩 천천히 보내는 연결이 3초 뒤 닫힘, 17번째 인증 전 연결이 바로 닫힘, `/apk` 3번째 동시 전송이 503 | 표대로(6단계) |
| 재접속(NF-41) | Play 종료/시작 10회, 앱 뒤로/앞으로 10회(`input keyevent HOME` → `am start`), Play 밖 스크립트 재컴파일 5회(6단계 뒤), 중계를 내렸다 올리기 3회 | 매번 사람 손 없이 복구, 첫 프레임까지 3초 이하(NF-20~22) |
| 공존(F-39) | 페이지가 보는 중에 앱 연결(`take=1`) → 앱이 active, 페이지는 대기 문구, 둘 다 MJPEG 아님(`extra.codec` 이 H.264). 앱을 뒤로 → 페이지가 스스로 active. 페이지에서 [여기서 보기] → 앱 STANDBY, 앱이 스스로 뺏지 않음(10초 관찰). 페이지 새로 고침 → 앱이 active 를 유지하는지(선호가 앱일 때) | 서로 뺏는 반복이 없음. **페이지가 선호인 상태에서 Play 종료 → 시작, 스크립트 재컴파일 뒤에도 페이지가 active**(리뷰 M3) |
| 기존 페이지(NF-45) | 9.9절의 확인 | 고리 전후 같은 범위 |
| 연속 30분(NF-9, NF-40) | 테스트 패턴, USB 1회 + 무선 1회. 소유자가 폰을 쓰는 것으로 보이면 미룸(브리프 C4) | 크래시 0, ANR 0, 캡처 → 표시 평균이 처음 1분 대비 +10ms 이내 |
| APK | 크기, `apksigner verify`, 선언된 권한 목록(`aapt dump permissions`) | 15MB 이하, v2, F-23 의 목록과 일치 |

### 11.6 소유자 확인 목록 (최종 보고에 넣을 것)

카메라로 QR 찍기(기본 카메라와 앱 스캐너), [방화벽 허용](UAC)과 그 뒤 Host 로 직접 연결, 5GHz, 실제 손가락 탭(T8), 폰을 가변 120Hz 로 바꾼 상태, 인터넷 없는 Wi-Fi 와 폰 VPN, 실제 Wi-Fi 끊김, QR 설치에서 보안 설정 화면 이후, 커버 화면.

## 12. 구현 순서와 이정표

5단계(앱)는 M0 ~ M6, 6단계(Unity)는 U1 ~ U5 입니다. 각 이정표는 "끝났다고 말할 조건"이 있습니다.

| 이정표 | 내용 | 끝났다고 말할 조건 |
|---|---|---|
| **M0** 사전 확인(앱 코드 전) | PRD 5.1 의 첫 작업 3개(`static.js` 양성 대조, 설치 화면 기록, `intent://` 동작). `replay.js` 최소판(인증 없이 프로토콜 2 스트림). 빈 Kotlin 프로젝트가 `build.ps1` 로 빌드되고 adb 로 설치됨 | 결과가 `05_build_and_test.md` 에 있음. QR 설치 흐름이 "막힘"이면 조정자에게 보고 |
| **M1 영상(USB, Unity 수정 없음)** | `WsClient`, `VideoChannel`, `FrameHeader`, `AvcConfig`, `Decoder`(즉시 표시), `VideoSurface`. 연결 정보는 딥링크(`seeplay-gvr://127.0.0.1:<포트>/c?…&via=usb`, 토큰은 아무 값). ① `replay.js`(7311, `adb reverse tcp:7311 tcp:7311`)로 화면이 나옴 ② **지금 있는 실제 Unity 서버**(7300, 이미 걸려 있는 reverse 를 그대로 씀)로 짧게 화면이 나옴 | 폰에 게임 화면이 H.264 로 나오고, 서버 창/`/stats` 의 `ackMs` 가 페이지와 같은 범위이고 `transportStride` 가 1. 받은 수 = 디코더 출력 수 |
| **M2** 계측과 표시 일정 | `Counters`, `TraceRing`, `Exporter`, `HudView`, `Pacer`(두 방식, 지터 버퍼 3가지), `RefreshPolicy`, `onFrameRendered`. 기록 파일이 `analyze.js` 로 읽힘 | 11.3 의 T2, T3 무터치 구간 수치가 나옴. `renderedNs` 의 쓸모, `HOLD_LIMIT`, 디코더가 붙잡는 프레임 수, HUD 켬/끔 차이를 확인해 13장의 해당 항목을 닫음 |
| **M3** 입력과 탭 비교 | `InputChannel`, `TouchMapper`, 터치 → 표시, 누름 구간 지표. 11.3 의 T1 ~ T7 | 11.3 의 판정이 문서에 있음. PRD 4.2 의 USB 열을 실측으로 고침 |
| **M4** 무선 | `Dialer`(Wi-Fi 망 지정), Wi-Fi 잠금, `LinkInfo`, `ReverseListener`, `relay.js`, `dial.js`, 링크 시험 모드. 11.4 의 W0 ~ W7 | 2.4GHz LAN 직접 수치가 지터 버퍼 설정별로 있음. F-41 결과. PRD 4.2 의 무선 열과 4.7 을 실측으로 고침. 표시 방식과 지터 버퍼의 기본값을 확정 |
| **M5** 인증, 링크, 재접속, 수명 주기 | `Auth`, `LinkParser`, `ServerStore`, `Session` 상태 기계 전체(5.1절), `StatusOverlay`, 닫기 코드, 자리 상태. `replay.js --auth`(대기실, 자리, 닫기 코드 포함) | 11.5 의 단위 시험, 인증, 재접속 통과(Host 가 없는 항목은 `replay.js` 로) |
| **M6** 스캐너 | CameraX + ML Kit, 카메라 권한 흐름 | PC 화면에 띄운 시험 QR 을 앱이 읽어 연결되는 경로를 코드로 확인(같은 문자열을 `LinkParser` 에 넣는 단위 시험 + 스캔 화면이 뜨고 권한 창이 나오는 것까지 adb 로). 실제로 찍는 것은 소유자 |
| **U1** Host 와 인증 | `Auth`(Self Test), `Ws`, `Gate`, `Host`(리스너, 대기실, 리로드 복구), 고리 S1 ~ S4, S7 ~ S10, G1, G3 | 앱이 `relay.js` → Host 경로 또는 USB(S2)로 인증하고 화면이 나옴. Play 밖에서 대기실, Play 시작 때 4410 → 자동 연결. 스크립트 재컴파일 뒤 다시 열림 |
| **U2** 자리 | `Seats`, 고리 S5, S6, G2, 페이지의 [여기서 보기] | 11.5 의 공존 시험 |
| **U3** QR 과 착륙 페이지 | `Net`, `Qr`, 인코더 가져오기, `Landing.html`, `/apk`, `/app.json`, `HostGUI`, W1, W2 | 11.5 의 QR 내용/주소 시험 |
| **U4** 방화벽과 USB 버튼 | `Firewall`(읽기와 스크립트, 누르지 않음), `AppAdb` | 창이 이 PC를 "막고 있습니다"로 판정. [USB 로 앱 설치]/[USB 로 연결] 동작 |
| **U5** 기존 경로 확인 | 9.9절의 확인 | NF-45 |
| 8단계 | 실제 서버 + Host 로 11.3, 11.4 를 다시(소유자가 방화벽을 허용했으면 중계 없이), 30분 시험, 소유자 확인 목록 | PRD 5.1 의 합격 조건 |

M1 의 ②에 대한 주의: 고치기 전 서버는 영상 클라이언트가 둘이면 둘 다 MJPEG 로 보냅니다 [코드: `SingleH264Client`]. 폰의 Chrome 페이지가 붙어 있는 채로 앱이 붙으면 앱에는 JPEG 만 옵니다(앱은 NOCODEC 을 보임). 그래서 ②는 조정자가 허락한 짧은 시간에, 페이지가 떨어진 상태에서 합니다: 앱을 앞으로 띄우면 Chrome 이 뒤로 가지만 페이지의 소켓이 바로 닫히지는 않을 수 있으므로, `/stats` 의 `extra.codec` 이 MJPEG 면 측정 스크립트의 `nav.js` 로 폰 탭을 잠시 `about:blank` 로 보냈다가 끝난 뒤 되돌리고 다시 H.264 로 붙었는지 확인합니다(브리프 C5 가 허락한 범위. 한 일을 보고에 적음).

## 13. 미검증 가정과 확인 시점

| # | 가정 | 틀리면 | 확인 |
|---|---|---|---|
| 1 | `c2.qti.avc.decoder.low_latency` 를 직접 쓰면 프레임을 붙잡지 않는다 | 지연이 프레임 단위로 늘어남 → 다른 디코더, 다른 키 | M1/M2 |
| 2 | `OnFrameRenderedListener` 의 시각이 프레임마다 오고 화면 갱신 격자에 맞는다 | **틀림(5단계)**: 프레임마다 오지만 앱이 지정한 목표 시각과 같은 값. 견주는 수치는 `releaseNs` 로(8.2절) | 닫음 |
| 3 | 출력 버퍼를 4장까지 쥐고 있어도 디코더가 멈추지 않는다 | `HOLD_LIMIT` 을 낮춤(지터 버퍼 상한이 줄어듦. `smooth` 50ms 는 3장이 필요) | M2 |
| 4 | 같은 크기의 설정 변경은 키프레임 앞에 SPS/PPS 를 붙이는 것으로 깨짐 없이 넘어간다 | `recreate=1` 을 기본으로 | M1(키프레임 요청으로 재현) |
| 5 | 가변 120Hz 설정에서 `setFrameRate(60)` 이 60Hz 를 유지시키고 `force60`/`max` 가 먹는다(리뷰 M5 로 문구 고침. 5단계 결과는 `05_build_and_test.md`) | 120Hz 쪽 비교는 소유자가 폰 설정을 바꾼 뒤에만 | M2 |
| 6 | `Network.bindSocket` 으로 묶은 소켓이 인터넷 없는 Wi-Fi 에서 LAN 에 닿는다 | 다른 방법 조사 | 구현은 M4, 실기기 확인은 소유자(폰 설정 변경 필요) |
| 7 | Wi-Fi 저지연 잠금이 2.4GHz 의 정체를 줄인다 | 효과 없음으로 기록(가설 기각) | M4 W1 |
| 8 | 삼성 인터넷이 스크립트가 만든 `intent://` 와 대체 주소를 Chrome 처럼 처리하고, `#` 뒤 값이 카메라 → 브라우저로 그대로 넘어온다 | 착륙 페이지를 "앱 스캐너로 찍으세요" 중심으로 바꿈. 최악의 경우 토큰을 `?` 로 옮기는 것을 조정자와 다시 정함 | M0(브라우저), 소유자(카메라) |
| 9 | 평문 사설 주소에서 APK 를 받을 때 브라우저가 막지 않는다(경고만) | QR 설치를 USB 설치로 안내 | M0 |
| 10 | 대상 SDK 36 에서 `usesCleartextTraffic` 없이도 raw `Socket` 은 되고, 전체 화면/뒤로 가기가 7.2절대로 동작한다 | 매니페스트/창 설정 조정 | M1 |
| 11 | Kotlin 플러그인이 AGP 8.13 + Gradle 8.13 + JDK 17 과 맞는 판이 있다 | **맞음(5단계)**: 2.2.20 | 닫음 |
| 12 | `relay.js`(node) 가 끼어도 지연 분포가 크게 달라지지 않는다 | 중계 수치를 따로 표시하고 `dial.js`/Host 수치와 비교 | M4 |
| 13 | Unity(Mono)에서 PowerShell 로 `FwPolicy2` 를 읽을 수 있다 | `netsh advfirewall firewall show rule` 출력 해석으로 바꿈(느림) | U4 |
| 14 | 9.7절의 방화벽 규칙 모양이 의도대로 동작한다 | 규칙을 고침 | 소유자가 누른 뒤 |
| 15 | `App~` 폴더를 Unity 가 가져오지 않는다 | APK 를 프로젝트 밖 경로(설정 `apkPath`)에 둠 | U3 |
| 16 | `manuelbl/QrCodeGenerator` 소스가 Unity 2022.3 에서 컴파일된다 | 조사 8장의 차선 | U3 |
| 17 | `127.0.0.1:포트`(기존 서버)와 `<LAN 주소>:포트`(Host)를 같은 프로세스에서 함께 들을 수 있다(기존 리스너는 `ReuseAddress` 를 켬) | Host 포트를 `포트 + 1` 로 바꾸고 QR 에 그 포트를 넣음 | U1 |
| 18 | `adb pull` 로 앱 전용 외부 저장소의 기록 파일을 읽을 수 있다(Android 16) | **맞음(5단계)** | 닫음 |
| 19 | `dumpsys SurfaceFlinger --latency` 로 앱과 Chrome 의 표시 간격을 같은 정의로 잴 수 있다 | **틀림(5단계)**: 이 폰(Android 16)에서는 앱 SurfaceView 레이어 이름을 어떻게 주어도 주기 한 줄만 나옴. Chrome 은 원래도 합성 프레임이라 불가(리뷰 B1-4). 실제 표시 시각의 독립 근거는 없음 | 닫음 |
| 20 | 보고 3의 링크 수치(Chrome 이 받음)가 네이티브 앱에서도 비슷하다 | PRD 4.7 을 실측으로 고침 | M4 W1 |

## 14. 조정자가 정할 것

| # | 결정 | 권장 |
|---|---|---|
| 1 | M1 의 ②(실제 서버 7300 에 앱이 붙는 시간). 폰의 Chrome 페이지가 붙어 있으면 잠시 떼어야 합니다(12장 주의) | 진단 워커가 끝났으므로 5단계에서 한 번에 몇 분씩 허락(브리프 C5 대로) |
| 2 | 무선 기준선(W5)과 3Mbps 비교(W4)는 창의 설정("영상 경로: 무선 우선", 비트레이트)을 바꿔야 합니다 | 워커가 바꾸고 끝나면 되돌리는 것을 허락하거나, 재생 서버 수치로 대신 |
| 3 | `GameViewRemotePage.html` 에 [여기서 보기] 버튼을 넣는 작은 수정(9.3절) | 넣음. 없어도 공존은 동작 |
| 4 | 식별자 `kr.co.seeplay.gvr` 와 스킴 `seeplay-gvr`(PRD O5). 한 번 배포하면 바꾸기 어렵습니다 | 권장값으로 진행 |
| 5 | 6단계 시작 시점. 폴더를 고치는 다른 워커는 이제 없습니다 | 5단계의 M5 까지 끝난 뒤(인증과 자리 상태를 `replay.js` 로 먼저 맞춰 봄) |
| 6 | 5GHz 측정과 방화벽 허용(UAC)을 소유자에게 언제 요청할지 | 5GHz 는 M4 결과가 나오면 바로(2.4GHz 의 한계가 수치로 보일 때). UAC 는 U4 뒤 |
| 7 | PRD 4.2 의 수치 기준은 전부 잠정입니다. M3/M4 뒤 실측으로 고칠 때 기준을 누가 확정할지 | 5단계 워커가 실측과 근거를 적어 제안하고 조정자가 확정 |


## 15. 확장 지점과 다른 프로젝트에서 다시 쓰기

소유자 요구사항 12: 앱은 "다른 곳에서 쓰기 좋게, 기능을 더 붙일 수 있게" 진행합니다. 어떤 기능을 붙일지는 정해지지 않았으므로 **기능은 만들지 않고 붙일 자리와 규칙만** 정합니다. 이 장에 없는 일반화(플러그인 구조, 설정 화면 틀 등)는 하지 않습니다.

### 15.1 프로토콜의 확장 규칙

| 규칙 | 내용 | 지금 코드에서 이미 성립하는가 |
|---|---|---|
| R1 기능 협상 | 새 기능은 이름 하나를 정해 `X-GVR-Auth` 의 `caps` 에 넣는다. **양쪽이 모두 적은 기능만 쓴다.** 상대가 적지 않은 기능의 메시지는 보내지 않는다 | 새로 넣음(3.2절). 인증 헤더가 없는 연결(Chrome 페이지, 고치기 전 서버)은 빈 목록 = 프로토콜 2 의 기본 동작 |
| R2 모르는 텍스트 메시지는 무시 | 양쪽 모두 모르는 `t` 와 모르는 필드를 오류로 다루지 않고 버린다. **입력 소켓의 새 `t` 는 두 글자 이상이고 `c d m p q u` 로 시작하지 않는다**(지금 서버가 첫 글자만 보므로, 리뷰 M6) | 서버: 영상 소켓의 `{…}` 는 `caps`/`key` 가 아니면 버림, 입력 소켓은 모르는 `t` 면 `null` [코드: `HandleText`, `HandleMessage`]. 페이지: `hello`/`s` 가 아니면 버림 [코드: `showServerStatus`]. 앱: `TextRouter` 가 버림 |
| R3 영상 소켓의 앱 → 서버 텍스트는 반드시 `{` 로 시작 | `{` 로 시작하지 않는 텍스트는 전부 수신 확인으로 세어진다. 새 메시지는 `{"t":"<이름>",…}` 꼴로만. `t` 의 판정은 `"t":"<이름>"` 으로(9.4절 S6 의 권고) | 9.4절에서 좁힘 |
| R4 바이너리 메시지의 `flags` | 비트 0~3 은 지금 뜻(같은 화면, H.264, 키프레임, 설정). 비트 4~7 은 영상 프레임의 추가 속성용으로 남김(모르는 비트는 무시하고 프레임으로 처리). **비트 8~15 중 하나라도 서 있으면 영상 프레임이 아닌 확장 메시지**: 모르는 클라이언트는 확인(`a`)만 보내고 내용을 버린다. 서버는 해당 기능을 `caps` 로 밝힌 클라이언트에게만 보낸다 | 앱의 `VideoChannel` 이 이 순서로 판정. 서버는 지금 비트 0~3 만 씀 [코드: `FLAG_*`] |
| R5 새 채널 = 새 WebSocket 경로 | 영상과 입력 밖의 흐름(예: 소리, 센서, 파일)은 기존 소켓에 끼워 넣지 않고 경로 `/x/<이름>` 의 소켓을 따로 연다. 인증의 채널 문자열은 `<이름>`. 기능 이름은 `ch.<이름>`. 모르는 서버는 404 를 주고 앱은 그 기능이 없는 것으로 본다. 영상 소켓의 수신 확인/역압 규칙은 다른 채널에 적용되지 않는다 | Unity: `GameViewRemoteChannels.Register(name, handler)`(빈 표). Gate 가 `/x/<이름>` 의 채널 문자열을 경로에서 뽑고, 등록된 처리기가 있으면 인증 뒤 소켓을 넘김. 앱: `session.Channel` 구현을 `Session` 에 추가 |
| R6 버전을 올리는 경우 | 옛 쪽이 새 메시지를 **잘못 해석**하게 되는 변경만: 28바이트 헤더의 뜻(프레임 프로토콜), 인증 계산식(인증 절차), QR 필드의 뜻(QR 형식). 올리면 옛 쪽은 3.3절의 오류로 깨끗하게 거절된다 | 3.0절 |
| R7 QR 과 딥링크 | 모르는 이름의 값은 무시한다. 새 값은 이름을 더해 넣는다(`v` 는 그대로) | `LinkParser`(4.2절) |
| R8 `/app.json` | 모르는 필드는 무시한다 | 10.4절 |

지금 정의된 기능 이름:

| 이름 | 뜻 | 없을 때 |
|---|---|---|
| `h264` | 앱: H.264 를 디코딩함(영상 소켓의 `{"t":"caps","h264":1}` 과 같은 뜻. 영상 소켓 메시지는 호환을 위해 그대로 보냄). 서버: H.264 를 보낼 수 있음 | — |
| `seat` | `{"t":"seat",…}` 를 이해함/보냄 | 서버는 `seat` 를 보내지 않고 앱은 자리 상태를 모른 채 동작(고치기 전 서버와 같음) |
| `take` | 서버가 `{"t":"take"}` 를 처리함 | 앱은 [여기서 보기]를 보이지 않음 |

### 15.2 기능을 붙이는 자리

| 붙이려는 것 | Unity 쪽에서 손대는 곳 | 앱에서 손대는 곳 | 프로토콜 |
|---|---|---|---|
| 서버 → 앱 상태/알림 한 가지 | 보내는 곳에서 `server.SendText` | `TextRouter` 에 `t` 처리기 1개 + `StatusOverlay`/HUD | 기능 이름 1개(R1), 메시지 1개(R2) |
| 앱 → 서버 명령 한 가지(영상 소켓) | `HandleText` 의 `{` 분기에 `"t":"<이름>"` 한 줄 → 새 파일의 처리기 | 보내는 곳 1개 | R1, R3 |
| 새 입력 종류(예: 키, 센서) | 양이 적으면 입력 소켓의 새 `t`(`GameViewRemoteInput.HandleMessage` 에 분기. 지금의 `Message` 클래스에 필드 추가). 많으면 새 채널 | `InputChannel` 에 보내는 함수, 또는 새 `Channel` | R1, R2 또는 R5 |
| 새 흐름(소리 등) | `GameViewRemoteChannels.Register` + 새 파일 | 새 `Channel` 구현 + 그 스레드 | R5 |
| 다른 영상 형식(HEVC 등) | 인코더 인자와 `flags` 의 형식 표시 | `Decoder` 의 MIME 과 설정 해석(`AvcConfig` 옆에 새 해석기) | 기능 이름으로 고름(R1). 헤더의 뜻이 바뀌면 R6 |
| 다른 전송(UDP 등) | 새 리스너(Host 옆) | `net` 아래 새 전송, `Session` 이 고름 | 기능 이름으로 제안하고 TCP 로 되돌아갈 수 있게 |
| HUD 항목, 측정값 | — | `Counters` 에 필드, `HudView` 에 줄, `Exporter` 의 `app` 아래 | — |
| 창의 버튼/표시 | `GameViewRemoteHostGUI` | — | — |
| 착륙 페이지의 내용 | `GameViewRemoteLanding.html`(디스크에서 매번 읽음) | — | — |

앱의 모듈 경계(2.1절)에서 지키는 의존 방향: `ui` → `session` → (`net`, `video`, `input`) → `diag`. `net` 은 Android UI 를 모르고, `video` 는 소켓을 모르고(`VideoChannel` 이 바이트를 넘겨줌), `link` 와 `net.Auth` 는 순수 함수라 JVM 단위 시험으로 돕니다. 새 기능은 이 방향을 거스르지 않는 자리에 넣습니다.

### 15.3 다른 Unity 프로젝트에서 다시 쓰려면

지금 무엇에 묶여 있고 무엇을 바꿔야 하는지입니다. **패키지로 묶는 작업 자체는 이번 범위가 아니고**(PRD N5), 아래는 묶을 때 걸리지 않게 이번 설계가 지키는 것과 그때 할 일입니다.

| 항목 | 지금 상태 | 이번 설계가 지키는 것 | 다른 프로젝트로 옮길 때 할 일 |
|---|---|---|---|
| 게임 코드 의존 | 도구의 `.cs` 는 `System`, `UnityEngine`, `UnityEditor` 네임스페이스만 씀. `GameViewRemoteTouchPoC.cs` 만 `GameManager` 를 이름으로 찾음(1단계 검증 도구, 본 도구는 의존하지 않음) [코드: 폴더의 `using` 과 `GameManager` 검색] | 새 파일도 게임 코드와 프로젝트의 다른 스크립트를 참조하지 않음. 새 파일끼리와 기존 도구 클래스만 참조 | `GameViewRemoteTouchPoC.cs` 는 빼고 옮김 |
| 폴더 위치 | `Assets/04_Scripts/Editor/GameViewRemote/`. 페이지는 `AssetDatabase.FindAssets("GameViewRemotePage")` 로 이름으로 찾음 [코드: `LoadPage`] | 새 파일이 폴더 경로를 글자로 적지 않음: 착륙 페이지와 `App~` 는 **도구 폴더를 스크립트 자신의 위치에서 찾아**(`MonoScript`/`AssetDatabase` 로 `GameViewRemoteHost.cs` 의 경로를 얻어 그 폴더) 읽음 | 폴더째 복사. 패키지로 만들면 `Packages/<이름>/Editor/` 아래로 |
| 어셈블리 | asmdef 없음(`Assembly-CSharp-Editor` 에 들어감) | 새 파일을 따로 asmdef 로 떼지 않음(asmdef 어셈블리는 `Assembly-CSharp-Editor` 의 기존 파일을 참조할 수 없으므로 반만 떼면 컴파일이 깨짐) | 패키지화 때 폴더 전체에 Editor 전용 asmdef 하나 |
| PC별/프로젝트별 설정 | 기존 설정은 `EditorPrefs` `GVR_*`(PC 전체 공유): 포트, ffmpeg 경로, 비트레이트 등 | 새 설정(서버 표시, 토큰, 주소, APK 경로)은 프로젝트별 파일 `UserSettings/GameViewRemote.json`(9.1절). 코드에 이 PC의 주소, 경로, 일련번호를 적지 않음 | 같은 PC에서 두 프로젝트를 **동시에** 열면 포트 7300 이 겹침 → 포트를 프로젝트별 설정으로 옮김(기존 `GVR_Port` 를 설정 파일로). 패키지화 때 나머지 `GVR_*` 도 "PC에 속한 것(ffmpeg 경로)"과 "프로젝트에 속한 것(포트, 해상도, 비트레이트)"으로 나눔 |
| 프로젝트 이름 | 없음 | 인증 응답의 `name`(= `Application.productName`)과 서버 표시가 프로젝트마다 달라 앱이 구분해 기억 | — |
| 앱 식별자와 스킴 | `kr.co.seeplay.gvr`, `seeplay-gvr` | Unity 쪽은 `GameViewRemoteAppInfo.cs` 한 파일, 앱 쪽은 `build.gradle.kts` 의 `applicationId` 와 매니페스트 자리 표시자(`manifestPlaceholders["gvrScheme"]`) 한 곳. 코드의 다른 곳에 글자로 적지 않음 | 다른 이름으로 내려면 그 두 곳과 서명 키를 바꿔 다시 빌드. **같은 앱 하나로 여러 프로젝트에 붙는 것은 바꿀 것 없이 됨**(앱은 프로젝트를 모름) |
| APK | 도구 폴더의 `App~/` | 경로는 설정으로 바꿀 수 있음 | 패키지에 같이 넣거나 공용 위치를 가리킴 |
| Unity 버전 | 2022.3.62f2 에서만 확인. 내부 API 4개에 의존(`Input.SimulateTouch`, `PlayModeView.m_TargetTexture`, `EventSystem.m_HasFocus`, `HostView.actualView`, 00 문서 1.2) | 새 파일은 내부 API 를 쓰지 않음(공개 API 와 .NET 만) | 버전마다 내부 API 4개가 그대로 있는지 확인. adb 경로는 `EditorApplication.applicationContentsPath` 기준이라 버전을 따름 |
| 입력 방식 | 레거시 Input Manager(`Input.touches`)에 주입 | — | 새 Input System 만 쓰는 프로젝트에서는 터치가 들어가지 않음. 별도 작업(입력 주입 계층 추가)이 필요. 이번 범위 아님 |
| 인코더 | ffmpeg 외부 실행 파일 + NVIDIA `h264_nvenc`. PC마다 경로 지정 | — | AMD/Intel PC는 인자 변경 필요(00 문서 7.1 #9). ffmpeg 를 PC에 설치해야 함 |
| 플랫폼 | Windows 에디터 | 방화벽 코드는 `GameViewRemoteFirewall.cs` 한 파일에 모음. 다른 OS 에서는 "판정 못 함"으로 돌려주고 나머지는 동작하게 `#if UNITY_EDITOR_WIN` 으로 감쌈 | macOS 에디터는 캡처 경로(D3D11 가정)부터 확인이 필요. 이번 범위 아님 |
| 라이선스 | — | QR 인코더는 `ThirdParty/` 에 `LICENSE` 와 함께. 다른 프로젝트에 같은 인코더가 있어도 네임스페이스가 달라 충돌하지 않음 | 그대로 |
| 메뉴와 창 | `Tools > Game View Remote` | 새 메뉴 항목도 같은 아래에 | 그대로 |

옮기는 절차(지금 형태, 패키지화 전): ① 도구 폴더를 대상 프로젝트의 아무 `Editor` 폴더 아래로 복사(PoC 파일 제외) ② `App~/` 에 APK 와 `app.json` ③ 창을 열고 ffmpeg 경로 지정 ④ [Start]. 서버 표시와 토큰은 그 프로젝트의 `UserSettings/` 에 새로 만들어집니다. 폰의 앱은 다시 깔 필요가 없고 새 프로젝트의 QR 만 찍으면 됩니다.

6단계의 확인 항목: 새 파일만 모아 빈 Unity 프로젝트의 `Editor` 폴더에 넣었을 때 기존 도구 파일 외에 없는 형식이 없는지(= 게임 코드 의존이 없는지)를 `using` 과 형식 이름 검색으로 확인합니다. 빈 프로젝트에서 실제로 여는 시험은 패키지화 범위입니다.

---

## 16. 설계 리뷰 결정 기록과 5단계에서 바뀐 것

> **먼저 16.6 을 보세요.** 통합, 코드 리뷰, 최종 검증에서 바뀐 것을 모아 둔 절이고, 앞 장의 내용과 다르면 16.6 이 코드의 실제 동작입니다(닫기 코드 4409, 표시 일정, 진단 입구, 확인 창, 방화벽 범위 등).

2026-11-14, 5단계 워커가 적었습니다. 리뷰(`03_design_review.md`)의 모든 항목에 대한 결정입니다. "받음"은 이 문서의 해당 절을 고쳤다는 뜻이고, 반영 시점이 6단계인 것은 Unity 쪽 코드가 생길 때 지켜야 하는 것입니다. Unity 쪽 워커가 스스로 정한 것은 `06_unity_server_addendum.md`, `06_unity_window_addendum.md` 에 있고, 이 문서와 다르면 그 문서가 서버의 실제 동작입니다.

### 16.1 막는 문제

| 항목 | 결정 | 고친 곳 / 이유 |
|---|---|---|
| B1 앱과 페이지의 표시 시각 자리가 다름 | **받음** | 8.1, 8.2, 8.5, 11.3, 13(가정 2, 19). 견주는 수치는 전부 `releaseNs`(버퍼를 넘긴 시각) 기준, `renderedNs` 기준은 "앱 단독" 열. 5단계 실측으로 `OnFrameRenderedListener` 값이 앱이 지정한 목표 시각과 같다는 것(차이 0.0ms)과 `dumpsys SurfaceFlinger --latency` 가 이 폰에서 앱 레이어의 표시 기록을 내주지 않는다는 것을 확인했으므로, 실제 표시 시각의 독립 근거는 없습니다. `tools/app-report.js` 가 두 기준을 같이 냅니다 |
| B2 무선 측정에서 경로와 클라이언트가 같이 바뀜 | **받음** | 11.4 에 W5b(앱 + 무선 adb 터널) 추가, 결론 문장 규칙. 5단계의 실제 서버 측정은 케이블이 빠져 W5b 조건에서만 했습니다(`05_build_and_test.md`) |

### 16.2 주요 문제

| 항목 | 결정 | 고친 곳 / 이유 |
|---|---|---|
| M1 인증 없는 LAN 리슨, `lan` 인자 | **받음**(브리프 3.3의 1) | 3.2 호환 표, 9.1, 9.2(`Admit` 가 원격 주소로 판정), 9.4(G1 = 항상 루프백, S9 에 루프백 검사, W2 토글 제거), 9.9. 6단계 구현 |
| M2 토큰 저장 위치 | **받음**(브리프 3.3의 2) | 9.1: `Library/GameViewRemote/secret.json`. 6단계 구현 |
| M3 자리 선호가 서버 Stop 에 지워짐 | **받음** | 3.5, 9.4(S10), 11.5. 6단계 구현 |
| M4 인증 전 연결 제한이 닿지 않음 | **받음** | 5.2, 9.2, 11.5. 6단계 구현. 앱 쪽 영향 없음(앱은 503 `busy` 를 재시도 대상으로 처리) |
| M5 측정 조건(주사율 설정, 기준선) | **받음, 단 한 가지는 실측으로 바꿈** | 7.5, 11.3, 13(가정 5). 기준선 = 같은 모드(가변 120Hz)의 보고 4. 리뷰가 권한 "`rr=none` 을 기본 비교로"는 **받지 않음**: 실측에서 요청이 없으면 패널이 30Hz 로 내려가 34fps 만 표시됐습니다. 기본값은 `rr=match`. 앱 60Hz 측정은 소유자 목록(브리프 3.3의 5) |
| M6 입력 소켓 `t` 첫 글자 | **받음** | 3.6, 9.4(Input 한 줄), 14(R2). 앱이 보내는 입력 소켓 메시지는 지금 `d m u c p` 뿐입니다(`take`, `key`, `caps` 는 영상 소켓) |
| M7 디버그 가능 APK 배포 | **받음**(브리프 3.3의 3, 4) | 4.2, 10.3. `proto` 빌드(디버그 불가, `DIAG=true`), 진단 값은 `BROWSABLE` 이 아닌 인텐트에서만, 새 서버 확인 창. 5단계 구현 완료: `aapt2 dump badging` 에 `application-debuggable` 없음 |
| M8 서버 삽입 지점 4곳 | **받음** | 9.3, 9.4(S4, S11), 9.4의 Adb 접근 수준 다섯 곳. 6단계 구현 |

### 16.3 작은 문제

| 항목 | 결정 | 내용 |
|---|---|---|
| m1 `surfaceDestroyed` 가 기다림 | 받음 | 6장에 예외를 적음. 구현: `Decoder.setSurface(null)` 이 잠금 안에서 디코더를 해제, 넘기다 난 예외는 `releaseErrors` |
| m2 입력 큐 | 받음(단순화) | 3.6: 상한 64, 차면 move 를 버림. 포인터별 덮어쓰기는 하지 않음(USB/LAN 에서 큐가 찰 일이 드물어 시제품에는 과함) |
| m3 토큰이 logcat/기록에 남음 | 받음 | 4.2(`--es t`), 8.5, 9.8. 앱은 토큰을 로그·HUD·기록 파일에 쓰지 않음 |
| m4 입력 연결 수를 한 곳에서 | 받음 | 9.3, 9.4(G2). 6단계 |
| m5 Seats 가 자기 표로 봄 | 받음 | 9.3. 6단계 |
| m6 Stop 과 겹친 연결 | 받음 | 9.4(S4). 6단계 |
| m7 Host 꺼진 날의 USB 인증 | 받음 | 9.1. 6단계 |
| m8 `0.0.0.0` 바인드의 주소 검사 | 받음 | 9.2 는 그대로(`LocalEndPoint` 가 활성 IPv4 목록에 있는지). 6단계에서 Wi-Fi + 유선 PC 로 확인할 것을 11.5 의 소유자 목록에 둠 |
| m9 몰림 정의 | 받음 | 11.3: "6ms 안에 3장 이상"(PRD 4.2). `app-report.js` 의 `burst3` |
| m10 탭 표본 수, 대조 구간 | 받음 | 11.3(T9, 120회 목표). 5단계 실측은 조건당 40~120회로, 목표에 못 미친 조건은 05 문서에 횟수를 적음 |
| m11 자동 발견 안 함 | 받음 | 5.1 에 한 줄 |
| m12 UAC 스크립트 | 받음 | 9.7. 6단계 |
| m13 Play 진입 리로드 때 재접속 | 받음 | 5.1: 코드 1000 뒤 30초 동안 재시도 상한 1초 |
| m14 QR 주소 확인의 한계 | 받음 | 11.5 |
| m15 `Application.productName` | 받음 | 9.1. 6단계 |

### 16.4 5단계 구현에서 설계와 달라진 것

| # | 설계 | 실제 | 이유 |
|---|---|---|---|
| 1 | QR 인식 = ML Kit 번들 | ZXing core 3.5.3 + CameraX `ImageAnalysis` | 리뷰가 미확인으로 남긴 사용 통계 전송이 없고, APK 3.5MB. QR 하나만 읽으면 됨 |
| 2 | Gradle 래퍼 | Unity 에 든 Gradle 8.13 런처 jar 를 직접 실행(`build.sh`, `build.ps1`) | 내려받는 것을 줄임. 내려받은 것은 Maven 의존성뿐 |
| 3 | 시험 벡터 sid `GVRTEST1` | 형식 시험과 시험 서버는 `GVRTEST2` | `GVRTEST1` 은 `[A-Z2-7]{8}` 에 맞지 않음(3.2 에 주의를 적음). HMAC 벡터 값은 그대로 유효 |
| 4 | 표시 일정: 후보 = due ≤ P + 주기/2, 가장 새 것만 | 후보 = due ≤ vsync + 주기/2, 가장 오래된 것부터, 한 장 넘게 밀리면 버림, 직전 프레임과의 간격이 원본 간격보다 반 주기 넘게 짧으면 한 vsync 미룸(1.5주기 넘게 밀리면 따라잡음) | 실측: 원래 규칙은 초당 6장, 다음 규칙은 초당 1장을 버렸고, 간격 규칙이 없으면 터치 뒤 120Hz 에서 멈춘 갱신이 1.3~1.6/초였음(7.4, 05 문서) |
| 5 | 기본 `rr` 은 측정으로 정함 | `match` | `none` 은 30Hz 로 내려감 |
| 6 | 빌드 유형 debug 배포 | `proto` | M7 |
| 7 | `merge-stats.js` | 만들지 않음. `tools/app-report.js` 가 기록 파일 하나로 분석 | 서버 `/stats` 를 측정 중에 읽으면 7300 에 연결이 늘어남. 서버 쪽 수치는 구간 끝에 한 번 읽어 적음 |
| 8 | MJPEG(구버전 JPEG) 표시 | 표시하지 않고 "PC가 H.264 를 보내지 않습니다…" 문구(상태 `NOCODEC`) | 설계 범위대로. 실제 서버에서 Chrome 탭이 같이 붙어 있을 때 확인됨 |
| 9 | 시험 서버가 ffmpeg 를 실시간으로 돌림 | 시작할 때 20초를 한 번 인코딩해 두고 정밀한 간격으로 반복 재생 | Windows 타이머 때문에 `-re` 출력이 15.8/32ms 로 몰려 기준 소스로 쓸 수 없었음 |

### 16.5 Unity 쪽 워커가 알아야 할 앱의 실제 동작(5단계 구현 기준)

- 앱은 `/video` 와 `/input` 을 각각 건다. 업그레이드 요청에 `X-GVR-Auth` 를 **항상** 붙인다(토큰이 있을 때). 응답에 `X-GVR-Auth` 가 없으면 "구버전 서버"로 보고, 루프백(USB)일 때만 인증 없이 계속한다. LAN 에서는 끊는다(진단 빌드의 `legacy=1` 예외).
- 응답에 `X-GVR-Auth` 가 있으면 서버 증명(`mac`)을 검증하고, 맞으면 첫 메시지로 `{"t":"auth","mac":"…"}` 를 보낸다. 그 뒤 `/video` 에서는 `hello` 를 기다렸다가 `{"t":"caps","h264":1}` 을 **이 문자열 그대로** 보낸다. `take=1` 링크로 왔으면 헤더의 `take=1` 로 알리고, 대기 중 화면의 버튼은 `{"t":"take"}` 를 보낸다.
- 바이너리 메시지 하나마다(설정 메시지 포함) 수신 스레드에서 곧바로 `a` 를 보낸다.
- 키프레임 요청 `{"t":"key"}` 는 2초에 한 번 이하.
- HTTP 거절은 상태 코드와 `X-GVR-Error` 값으로 문구를 고른다: 401 `auth_required`/`bad_token`, 403 `unknown_server`(기억한 서버를 지우지는 않고 "QR 을 다시 찍으세요"), 426 `proto`(앱 업데이트 안내), 503 `busy`(재시도). 닫기 코드 4401(재시도 멈춤, QR 다시), 4403(토큰 폐기: 기억한 서버를 지우고 재시도 멈춤), 4410(간격 없이 곧바로 다시 붙음), 1000(재시도, 30초 동안 간격 상한 1초). 자리는 닫기 코드가 아니라 `seat` 메시지(`active`/`standby`/`lobby`)로 받는다.
- 위 인증·자리·닫기 코드 경로는 `tools/replay.js --auth` 를 상대로 만들었으나, **기기에서의 확인은 소유자 지시로 실제 폰 사용이 중단되어 하지 못했습니다**(단위 시험으로 확인한 것: 증명 계산 벡터, 링크 해석, 헤더 해석). 상세는 `05_build_and_test.md`.

### 16.6 통합, 코드 리뷰, 최종 검증에서 바뀐 것 (8단계, 2026-11-14)

최종 단계 워커가 합쳤습니다. 출처는 `06_unity_integration.md`, `08_fixes_android.md` 6장, `08_fixes_unity.md` 4장, `08_interop_notes.md` 입니다. **이 절이 앞의 장들과 다르면 이 절이 코드의 실제 동작입니다.** 앞 장의 본문은 고치지 않았고, 어느 절이 바뀌었는지는 표의 "바뀌는 곳" 열에 적었습니다. 프로토콜 2 와 인증 1 의 바이트는 바뀌지 않았습니다.

#### 프로토콜과 서버

| # | 바뀐 것 | 지금 동작 | 바뀌는 곳 |
|---|---|---|---|
| 1 | **닫기 코드 4409 (새로 생김)** | 같은 앱(`did`)이 같은 종류의 소켓(영상 또는 입력)을 새로 열면 서버가 그 앱의 옛 소켓을 4409 로 닫고 새 소켓이 자리를 이어받는다. 다른 기기의 소켓은 닫지 않는다. 앱은 4409 를 받은 세션을 다시 걸지 않고 멈춘다(문구 "이 기기의 다른 연결이 이 연결을 대신했습니다. 앱을 닫았다가 다시 여세요."). 앱을 홈으로 내렸다가 다시 열면 새로 연결한다 | 3.5, 5.1, 16.5 의 닫기 코드 목록 |
| 2 | 쓰기 제한과 닫기 | 모든 수락 소켓 `SendTimeout` 3초. 메인 스레드가 부르는 닫기(Stop, 새 QR 발급, 자리 교체, 대기실 내보내기)는 기다리지 않는다: 상대가 읽지 않으면 닫기 프레임 없이 끊는다. 거절할 때는 닫기 프레임 → 보내는 쪽만 닫기 → 남은 입력을 최대 200ms 읽어 버림. 앱은 닫기 코드 없는 끊김을 일반 끊김으로 재접속한다 | 5.2, 9.4 |
| 3 | 루프백 규칙 | Origin 은 `http://localhost:<들어온 포트>` 또는 `http://127.0.0.1:<들어온 포트>`. Origin 이 없는 요청(측정 스크립트)은 받는다. 루프백 HTTP 는 Host 가 `localhost`/`127.0.0.1`(또는 없음)일 때만, 아니면 404 | 9.2 |
| 4 | 한도 | 인증 전 연결: 주소별 6, 전체 16. 인증 대기 8 은 리스너별(앞단, 루프백 따로). `/apk`: 동시 3, 주소당 1, 전송 전체 120초, HEAD 는 자리를 쓰지 않음 | 5.2, 9.2 |
| 5 | 대기실 문구 | `seat lobby` 의 `msg` 가 두 가지: Play 를 누르지 않은 경우, Play 중인데 화면 전송만 멈춘 경우. 앱은 `msg` 를 그대로 보인다 | 3.5 |
| 6 | 자리 규칙의 설명(동작은 그대로) | 선호(마지막에 보던 기기)가 없으면 앱이 페이지보다 우선한다(3.5 규칙 3). "자동 재접속은 뺏지 않는다"는 선호가 있을 때의 말이다 | 3.5 |
| 7 | APK 가 없을 때 | `/app.json` 도 404, 인증 응답의 `app` 은 0, 착륙 페이지는 설치 버튼을 숨김. `apkPath` 설정은 확장자 `.apk` 인 파일만 | 10.4 |
| 8 | 착륙 페이지 | 패키지 이름과 스킴은 서버가 `GameViewRemoteAppInfo` 값으로 채운다. `#` 뒤의 연결 값은 형식 전체를 검사한 뒤 읽어 낸 값으로 다시 조립하고, 형식 밖이면 버튼을 만들지 않는다 | 4.3, 9.5 |
| 9 | QR 내용 | 만드는 곳이 `GameViewRemoteQr.BuildPayload` 한 곳(`Host.BuildQrPayload` 가 부름) | 4.1, 9.5 |
| 10 | 방화벽 | 허용 규칙: 이 Unity.exe, TCP, 도구 포트, 보내는 쪽 = **지금 네트워크의 대역 숫자**, 프로필 = **지금 프로필 하나**. 기록 파일과 규칙 이름에 실행 파일 경로의 해시 6자. 순서는 "끌 규칙 찾기 → 기록 쓰기 → 규칙 변경". 스크립트가 결과 파일과 종료 코드로 실패를 알리고 창이 보인다. 출력만 모드(`Firewall Dry Run`). **승격 실행은 아직 한 번도 하지 않았다** | 9.7 |
| 11 | USB 버튼(`GameViewRemoteAppAdb`) | 대상은 가상 기기만(`EMULATOR_ONLY = true`, 소스 상수). 대상이 둘 이상이면 고르지 않고 멈춘다. 모든 명령에 `-s`. 설치 제한 120초, 설치 뒤 기기의 버전을 다시 읽는다. 연결은 `am start -n kr.co.seeplay.gvr/.Diag -a VIEW -d <링크> --es t <토큰>` | 4.4, 9.8 |
| 12 | 창과 서버 사이 | 리플렉션 어댑터(`GameViewRemoteHostLink`)를 없애고 직접 호출. 임시 `GameViewRemote.AllowLan` 삭제 | 9.5 |
| 13 | 기존 도구의 자동 adb | 바꾸지 않음(코디네이터 결정). Play 중에 꽂힌 폰에 `adb reverse` 를 걸고, 저장된 무선 주소가 있으면 `adb connect` 를 시도한다. 저장된 주소는 지금 백업 키(`GVR_WirelessAddress_Backup`)로 옮겨져 비어 있다 | 9.4 |

#### 앱

| # | 바뀐 것 | 지금 동작 | 바뀌는 곳 |
|---|---|---|---|
| 14 | **표시 일정** | v3(16.4 의 4번)를 폐기. 웹 페이지의 `schedEnqueue / schedTake` 와 같은 위상 맞춤 규칙과 같은 상수(`video.Pacer`, Android 클래스 없음). 표시 예정 = 캡처 시각 + base + delay, delay = 최근 120장 흔들림의 95% + 2ms(3~25ms), vsync 마다 예정 + 위상 ≤ vsync 인 것 중 가장 새 것 1장, 대기열 6장. 페이지와 다른 점 하나: delay 는 프레임당 0.25ms 이하로 움직인다. **폰에서 재지 않았다**(옛 기록 17개 재현만) | 7.4, 16.4 의 4 |
| 15 | 화면 박자 루프의 시작 | 디코더의 초기화 순서 경합을 고침(설치 직후 첫 실행에서 한 장도 표시하지 않던 결함) | 7.3, 7.4 |
| 16 | 상태 문구 걷기 | 세션이 WAITING 이나 NOCODEC 에 들어갈 때마다 디코더에 "다음에 표시하는 프레임에서 알려 달라"고 건다(자리 되찾기, H.264 복귀) | 5.1, 7.3 |
| 17 | take 유지 | `take=1` 은 자리 배정(`active`/`standby`)을 받을 때까지 유지한다(대기실 → 4410 → 재접속에서도) | 5.1 |
| 18 | 세션 경계 | 막히는 단계마다 멈춤 확인, `stop()` 은 걸고 있는 소켓과 듣기 소켓까지 닫음. `Session.Ui` 콜백은 세션 자신을 넘기고 화면은 지금 세션의 것만 받음. `Decoder.attach(owner)`: 주인이 아닌 세션의 호출은 버림. 입력 번호는 연결마다 "입력 세대"와 함께 다시 시작 | 5.1, 6 |
| 19 | 진단 입구 | 진단 값(`pace, buf, rr, legacy, listen, hud, rec …`)과 `cmd` 는 `kr.co.seeplay.gvr/kr.co.seeplay.gvr.Diag`(권한 `android.permission.DUMP`)로 온 인텐트만 받고, 진단 빌드에서만. 이 입구의 `--es t <토큰>` 과 "확인 창 없이 연결"은 모든 빌드에서 받는다. 4.2 의 "브라우저가 아니면 adb" 판정은 폐기 | 4.2, 4.4 |
| 20 | 확인 창 | 밖에서 온 링크는, 기억한 서버가 있을 때 "서버 표시와 토큰이 같고, 주소가 기억한 주소 중 하나(같은 포트)이거나 루프백"일 때만 묻지 않는다. 처음 설치와 앱 안 스캐너는 묻지 않는다 | 4.2 |
| 21 | 받는 주소 | 사설·링크 로컬·100.64/10·루프백 IPv4 만(`a=` 포함). 앞의 0, 8·16진, 전각 숫자, 이름은 거절 | 4.1 |
| 22 | 403 의 해석 | `X-GVR-Error: unknown_server` 이거나 코드가 없는 403 만 멈추는 실패. 그 밖의 403 은 다시 시도 | 16.5 |
| 23 | 주사율 | `rr=match` = `Surface.setFrameRate(원본 프레임률)`(캡처 시각 간격의 중앙값으로 잼). 기록과 HUD 에 `modeHz, sourceFps, slowVsync, via, target, pct` | 7.5, 8.2 |
| 24 | Wi-Fi 고르기 | Wi-Fi 전송이 있고 VPN 전송이 없고 `NOT_VPN` 인 망만 | 7.2 |
| 25 | 터치 | 오른쪽 위 56dp 뷰와 HUD 누르기를 없앰. 진단 손짓: 세 손가락 1초 = HUD, HUD 가 켜진 채 네 손가락 1초 = 기록(그 손가락들도 게임에 전달). 영상 크기를 받기 전의 터치는 보내지 않음, 터치가 꺼질 때 취소를 보냄 | 7.6, 8.4 |
| 26 | `q` 보고 | `dm` = 디코딩 시간 지수 평균(ms), `dp` = 지난 2초에 표시 일정이 버린 프레임 수(전에는 0) | 3.6 |
| 27 | 그 밖 | `usesCleartextTraffic=false`(도구 주소는 코드에서 평문 허용 대역으로만), 내보낸 구성 요소는 `MainActivity` 와 `.Diag` 둘, hello 제한 2초, WsClient 한도(헤더 줄 수, 제어 프레임 125, 조각 합계 16MB) | 10.3 |
| 28 | 버전 | 0.1.2 (versionCode 3), `proto` 빌드. `build.ps1 -Publish` 가 단위 시험(29개) → 빌드 → `dist/` → Unity 도구의 `App~/` 복사 | 10.1 |

#### 결정이 남은 것

- 확장 채널(`GameViewRemoteChannels`)을 처음 쓸 때: 새 QR·Stop·리로드 때 닫기, 루프백에서도 받기, `caps` 를 등록과 맞추기를 계약으로 정할 것(리뷰 m13, 지금은 등록된 채널이 없음).
- 사설 대역 밖 주소를 창에서 고를 수 있게 할지(리뷰 m18). 지금은 서버도 앱도 받지 않는다.
- `App~` 에 넣을 빌드 종류(지금 `proto` = 디버그 불가 + 진단 켜짐)와 서명 키 관리, `app.json` 을 APK 에서 읽어 만들기(리뷰 M8 의 남은 부분).
- 표시 목표 시각을 한 주기 앞으로 당길지(`target=early`, 진단 값으로만 있음). 폰에서 잰 뒤 정한다.
