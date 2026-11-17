# 06. Unity 통합과 끝에서 끝까지 검증

작성: 통합 워커, 2026-11-14 새벽(PC 시계 약 01:20~02:10). 대상: 안드로이드 앱, Unity 서버(U1), Unity 창/QR(U2).

지킨 것: 실제 폰(SM-F731N)에는 adb 명령, 설치, 실행, 입력, 화면 캡처, 연결 시도를 **한 번도 하지 않았습니다.** adb 는 전부 `-s emulator-5554` 였고 `adb kill-server` 는 쓰지 않았습니다. 커밋 없음, Unity 종료 없음, 방화벽 변경과 UAC 없음. 토큰 값은 문서, 로그 출력, 스크린샷 어디에도 없습니다(QR 내용은 SHA-256 과 가린 값으로만 비교).

## 1. 한 줄 결론

가상 기기에서 **창의 QR 에 든 주소 그대로 → 착륙 페이지 → [앱으로 열기] → 앱이 아무것도 입력하지 않고 연결 → 게임 영상 표시**까지 됩니다. 통합하면서 앱 결함 3개를 찾아 고쳤고, 그중 하나(설치 직후 첫 실행에서 영상이 안 나옴)는 실제 폰에서도 났을 가능성이 큰 것입니다. 가상 기기의 네트워크 한계 때문에 "QR 주소로 APK 내려받기 완료"는 확인하지 못했습니다(5장).

## 2. 통합한 것

### Unity 쪽 (`catjump/Assets/04_Scripts/Editor/GameViewRemote/`)

| 항목 | 한 일 |
|---|---|
| U2 의 리플렉션 어댑터 | `GameViewRemoteHostGUI.cs` 의 `GameViewRemoteHostLink` 클래스를 통째로 없애고 U1 API(`GameViewRemoteHost`, `GameViewRemoteFirewall`, `GameViewRemoteAppAdb`, `GameViewRemoteSeats.Viewer`)를 직접 부릅니다. `GameViewRemoteWindow.cs` 의 두 군데도 같이 바꿈 |
| 임시 `GameViewRemote.AllowLan` | 속성 삭제. 옛 EditorPrefs 키를 지우는 코드만 남김 |
| 인증 없는 Wi-Fi 토글 | 창과 서버 양쪽에 없음을 확인(코드 검색 + 컴파일된 형식에 `AllowLan` 없음) |
| 페이지의 [여기서 보기] | `GameViewRemotePage.html` 에 이미 있어 그대로 둠 |
| `AppAdb` 의 가상 기기 제한 | `EMULATOR_ONLY = true` 유지. 끄는 방법을 코드 주석과 README 에 적고, 창의 USB 구역에 "지금은 가상 기기에만 동작" 안내를 추가 |
| README | 폰에 보이는 글과 창의 글을 맞춘 표, APK 넣는 방법, USB 버튼을 실제 폰에 쓰는 방법 추가 |
| 착륙 페이지 글 | 받기 안내를 Chrome 이 실제로 묻는 두 가지("안전하게 다운로드할 수 없음", "유해할 수 있는 파일")에 맞게 고침. "아래에서 먼저 설치" → "위의 [앱 설치(APK 받기)]로" (그 상태에서는 설치 칸이 위에 있음) |

### APK 를 도구에 넣기 (반복 가능)

```powershell
cd C:\Users\dbrud\Developemnt\SeePlay\GameViewRemoteApp\android
.\build.ps1 -Publish                  # 단위 시험 + 빌드 + dist + Unity 도구의 App~ 로 복사
..\tools\publish-to-unity.ps1         # 이미 빌드된 dist 의 것을 복사만(sha256 확인)
```

지금 들어 있는 것: `App~/GameViewRemote.apk` = 0.1.1 (versionCode 2), 3,500,117 바이트, sha256 `db8ccdba…d81c17`, `App~/app.json` 일치. Unity 재시작이나 컴파일 없이 바로 반영됩니다.

### 앱 쪽에서 고친 것 (설계와 어긋났거나 통합에서 드러난 것)

| # | 증상 | 원인 | 고친 곳 |
|---|---|---|---|
| A1 | **설치 직후 첫 실행에서 "연결됨. 화면을 기다리는 중"에 멈춤.** 연결과 인증, 디코더 생성은 되는데 영상이 안 나옴. 앱을 껐다 켜면 나옴. 3번 재현 | `Decoder.kt` 의 초기화 순서 경합. 화면 박자 루프를 시작하는 `init` 블록이 `running = true` 선언보다 **앞**에 있어서, 생성자가 느린 첫 실행에서는 박자 스레드가 `running == false` 를 보고 루프를 시작하지 않음. 한 장도 표시하지 않음 | `init` 블록을 `running` 과 콜백 선언 뒤로 옮김. 고친 뒤 "삭제 → 설치 → 첫 실행" 3번 모두 0.7초 안에 재생 |
| A2 | 다른 기기에 자리를 내줬다가 [여기서 보기]로 되찾으면 "화면을 기다리는 중" 안내가 영상을 계속 가림 | 같은 디코더를 이어 쓰는데 "첫 프레임" 알림은 디코더당 한 번만 나감 | `Decoder.expectFirstFrame()` 추가, `Session.kt` 가 대기/대기실 → 보는 중으로 바뀔 때 부름. **A1 을 고친 뒤 이 경우만 따로 다시 시험하지는 못했습니다**(6장) |
| A3 | Play 밖(대기실)에서 QR 로 붙은 뒤 Play 를 누르면 자리를 가져오지 못할 수 있음(설계 5.1 과 다름) | `take=1` 을 첫 연결에만 보냄. 대기실은 자리 배정이 없어 4410 뒤 재접속 때 사라짐 | `Session.kt` 에 `takePending` 을 두어 자리 배정을 받을 때까지 유지. 코드로만 고쳤고 기기에서 이 순서를 돌려 보지는 않았습니다(Play 를 끄고 켜야 해서) |
| A4 | `tools/env.sh` 의 기본 시리얼이 실제 폰 | 안전 문제 | 기본값을 `emulator-5554` 로 바꿈 |

버전은 0.1.0(1) → 0.1.1(2). 실제 폰에 깔린 0.1.0 과 구별하기 위한 것이고, 폰에는 아직 이 버전이 없습니다.

### 넣었다가 뺀 것

APK 를 보낸 뒤 소켓을 천천히 닫는 코드를 `GameViewRemoteFiles.cs` 에 넣었다가 뺐습니다. 컴파일 오류(`SelectMode` 이름 충돌)를 냈고, 대조 시험에서 문제의 원인이 Unity 서버가 아니라 가상 기기의 네트워크임이 드러났기 때문입니다(5장). 그 사이 약 15분 동안 Unity 는 이전에 컴파일된 코드로 돌았습니다. 지금 파일은 U1 이 쓴 그대로입니다.

## 3. 쓰는 방법 (처음 쓰는 사람용)

1. PC 와 폰을 **같은 Wi-Fi** 에 연결합니다.
2. Unity 메뉴 **Tools > Game View Remote** 를 엽니다.
3. 창에서 **[Start]** 를 누릅니다. QR 이 보입니다. (Windows 가 방화벽을 물으면 README 2장을 봅니다.)
4. 폰의 **기본 카메라**로 QR 을 찍고, 뜨는 주소를 누릅니다. 브라우저에 "Game View Remote" 페이지가 열립니다.
5. 처음이면 **[앱 설치(APK 받기)]** 를 누릅니다. 브라우저가 "안전하게 다운로드할 수 없음"이나 "유해할 수 있는 파일"이라고 물으면 계속 받기를 고르고, 받은 파일을 열어 **설치**합니다. "출처를 알 수 없는 앱"을 물으면 그 브라우저를 허용합니다. Play 프로텍트가 물으면 **검사 없이 설치**를 고릅니다(검사는 앱 파일을 Google 로 보냅니다).
6. 페이지로 돌아와 **[여기를 눌러 연결]** (이미 앱이 있으면 처음부터 **[앱으로 열기]**)을 누릅니다.
7. Unity 에서 **Play** 를 누르면 폰에 게임 화면이 나오고, 폰을 터치하면 게임이 반응합니다.
8. "다른 기기가 보고 있습니다"가 나오면 **[여기서 보기]** 를 누릅니다.

글자를 입력하는 단계는 없습니다. 안 될 때는 도구 폴더의 `README.md` 문제 해결 표를 봅니다.

## 4. 검증 표

조건: Unity 2022.3.62f2, **Play 중**, 창의 서버 Start 상태(LAN `192.168.25.5:7300` + PC 안쪽 `127.0.0.1:7300`), H.264 720x1280 약 6Mbps 60fps. 가상 기기 `emulator-5554`(Android 15, Chrome 124). 화면 조작은 `uiautomator dump` 로 글자 위치를 찾아 `input tap`.

서버는 제가 [Start] 를 누른 것이 아니라 U1 이 켜 둔 상태 그대로 썼습니다(창의 Stop → Start 는 눌러 보지 않음).

| # | 시험 | 가상 기기에서 | 결과와 근거 |
|---|---|---|---|
| 1a | 창의 QR 을 캡처해서 읽기 | PC | 창이 그리는 QR 텍스처를 PNG 로 저장해 서로 다른 디코더 2개(zxing-cpp, OpenCV)로 읽음. 둘이 같고, 서버가 만든 값과 SHA-256 이 같음(`0602610a…`). 형식 `http://192.168.25.5:7300/c#v=1&s=…&t=…`, 66자, QR 버전 5 |
| 1b | 그 주소를 기기에서 열기 | **함** | `am start -a android.intent.action.VIEW -d '<QR 에서 읽은 주소 그대로>'`. **주소를 바꾸지 않았습니다**(실제 QR 과 같은 LAN 주소). 착륙 페이지 표시: `06_evidence/01_landing_from_qr_url.png` |
| 1c | [앱으로 열기] → 앱이 연결하고 영상 표시, 입력 없음 | **함** | 앱 기록: `DIALING 192.168.25.5:7300 → HANDSHAKING → WAITING → PLAYING`(0.4~0.8초). 삭제 후 새로 설치한 첫 실행으로 3번. Unity 창 상태 "연결됨: sdk_gphone64_x86_64 무선, 보는 중", 앱 버전 2. 화면: `09_app_video_via_landing.png` |
| 2a | 앱이 없을 때 설치 안내로 넘어감 | **함** | [앱으로 열기] → 주소가 `/c?noapp=1` 로 바뀌고 "앱이 아직 없는 것 같습니다" + 설치 칸이 위로: `02_noapp_fallback.png` |
| 2b | APK 내려받기 | **일부** | QR 주소(LAN)로는 **끝나지 않음**: 받은 파일이 3,500,101 / 3,500,117 바이트에서 멈춤. 가상 기기 네트워크가 바이트를 잃기 때문(5장). 같은 서버를 `localhost:7300`(adb reverse)으로 열면 **완료, sha256 일치**. Chrome 이 묻는 창 3개: `03`, `04`, `05` |
| 2c | 패키지 설치 프로그램 | **함** | 받은 파일 열기 → "출처를 알 수 없는 앱" → 설정에서 Chrome 허용(바꾼 보안 설정은 이것 하나) → [Install] → Play 프로텍트 "Install without scanning" → "App installed". `06`, `07`, `08` |
| 2d | 설치 뒤 [여기를 눌러 연결] | 안 함 | 내려받기를 localhost 주소로 했기 때문에 그 페이지에는 연결 정보가 없었습니다. 설치 뒤 연결은 1c 방식으로 확인 |
| 3a | 틀린 토큰 | **함** + 스크립트 | 앱: "PC를 확인할 수 없습니다. Unity 창의 QR 을 다시 찍으세요." (`FAILED_TOKEN`), `11_app_wrong_token.png`. 스크립트 `hosttest.js reject` 9/9, `auth` 9/9 |
| 3b | 토큰 없음 | **함** + 스크립트 | 앱은 연결을 시도하지 않고 "Unity 창의 QR 을 찍으세요": `12_app_no_token.png`. 서버는 헤더 없는 LAN 연결을 거부(`reject`) |
| 3c | 다른 PC 의 QR | **함** | "이 QR 은 다른 PC의 것입니다" (`FAILED_SERVER`) |
| 3d | 다시 보내기(replay), 반사, 다른 채널의 증명, 침묵 | 스크립트만 | `hosttest.js abuse`: 전부 4401. 앱으로는 만들 수 없는 공격이라 스크립트로만 |
| 4 | 자리 규칙: 앱 + 스크립트 페이지 | **함** | 새 시험 `hosttest.js withapp` 6/6. 앱이 보는 중 → 페이지는 대기, 프레임 0장 → 페이지가 take → 페이지에 H.264, 앱에 "다른 기기가 보고 있습니다" + [여기서 보기](`13_app_standby_take_button.png`) → 기기에서 그 버튼을 누름 → 페이지가 대기로 돌아가고 프레임 0장 |
| 5 | 터치가 Unity 에 닿음 | **함** | 제자리 누름 3번(`input swipe 540 880 540 880 120`, 게임 오버 화면의 점수 글자 위). `/stats`: `pointerMessagesReceived` 0 → 6, `pointerEventsApplied` 18 → 24. 게임 진행은 하지 않았습니다 |
| 6 | Chrome 페이지 경로(루프백)가 H.264 | 스크립트 + 일부 | `hosttest.js legacy` 14/14, 4초에 H.264 218장. 가상 기기 Chrome 에서 `http://localhost:7300/` 페이지가 열리고 서버가 보내기 시작함(`15_chrome_page_loopback.png`). 페이지 안에서 영상이 그려지는 것까지는 보지 않았습니다 |

### 4의 시험에서 쓴 주소 (QR 과 다른 점)

자리 시험(4)과 거부 시험(3a~3c)은 앱을 `127.0.0.1:7320` 으로 붙였습니다. 이것은 PC 안의 중계(`tools/device-lab/lan-relay.js`, 127.0.0.1 에서만 듣고 내용은 기록하지 않음)를 거쳐 **같은 LAN 쪽 서버 `192.168.25.5:7300`** 으로 가는 길입니다. 인증과 자리 처리는 QR 주소로 붙을 때와 같은 코드가 합니다(창에도 "무선"으로 표시). 다른 점은 주소 글자와, 가상 기기의 불안정한 네트워크 대신 adb 터널을 탄다는 것뿐입니다. 5장의 손실 때문에 넣었고, 나중에 A1 이 진짜 원인임을 알고 나서 1c 와 5 는 QR 주소 그대로 다시 했습니다. 중계는 시험 뒤 내렸습니다.

### 실패한 시험 1건

`hosttest.js abuse` 의 "너무 긴 auth 메시지 → 4401" 이 4번 중 3번 `close null` 로 실패했습니다(U1 때는 통과). 서버는 그 연결을 받아들이지 않습니다(거부는 맞음). 닫기 코드 4401 이 클라이언트에 도착하기 전에 연결이 끊기는 경우가 있는 것으로 보이고(읽지 않은 데이터가 남은 채 닫음), 정상 앱은 이런 메시지를 보내지 않습니다. 고치지 않았습니다.

## 5. 가상 기기 네트워크의 바이트 손실 (제품 문제가 아님)

가상 기기에서 PC 의 LAN 주소(`192.168.25.5`)나 `10.0.2.2` 로 받으면 TCP 내용이 드문드문 빠집니다.

- APK(3.5MB)를 6번 받아 6번 모두 5~20바이트 모자람. 원본과 맞춰 보니 중간중간 1바이트씩 11곳 + 마지막 1바이트.
- **Unity 가 아닌 node 로 같은 파일을 내줘도 똑같이 모자랐습니다.** PC 에서 같은 주소로 받으면 매번 완전하고 sha256 이 맞습니다.
- adb reverse(`127.0.0.1`)로 받으면 매번 완전합니다.
- 착륙 페이지(6.5KB)도 6번 중 3번 1바이트가 빠졌고, 한 번은 제목만 보이는 빈 페이지가 됐습니다(`14_landing_broken_over_emulator_nat.png`). 다시 열면 됩니다.
- 영상은 이 경로로도 20초 넘게 끊김 없이 재생됐습니다.

그래서 "QR 주소로 APK 내려받기 완료"는 가상 기기로 확인할 수 없고 실제 폰에서 봐야 합니다. 실제 Wi-Fi 에서 이런 손실이 날 이유는 찾지 못했지만, 확인한 것은 아닙니다.

시험 도중(01:30 쯤) 가상 기기 프로세스가 사라져 `start-avd.ps1` 로 다시 띄웠습니다. 그동안 `04_device_lab.md` 첫 줄을 NOT READY 로 바꿨다가 되돌렸습니다. 꺼진 원인은 보지 않았습니다. 그때 실행한 `start-avd.ps1` 프로세스가 끝나지 않고 남아 있는데, 끝내면 가상 기기도 같이 꺼질 수 있어 그대로 두었습니다.

## 6. 소유자 확인 목록 (가상 기기로는 못 하는 것)

| # | 확인할 것 | 기대 |
|---|---|---|
| 1 | 폰 **기본 카메라**로 창의 QR 찍기 | 주소가 뜨고 누르면 착륙 페이지 |
| 2 | **삼성 인터넷**에서 [앱으로 열기], 앱이 없을 때 설치 안내로 넘어가는지 | Chrome 과 같음. 삼성 인터넷은 시험한 적이 없습니다 |
| 3 | **QR 주소로 APK 내려받기가 끝까지 되는지**, 받은 파일이 설치되는지 | 완료 후 설치. 삼성의 자동 차단이 막는지도 |
| 4 | 설치 뒤 **[여기를 눌러 연결]** | 앱이 열리고 연결 |
| 5 | **새로 설치한 0.1.1 의 첫 실행**에서 영상이 나오는지 | 나옴(A1). 폰의 0.1.0 은 이 결함이 있을 수 있습니다 |
| 6 | 실제 Wi-Fi 에서 연결, **Windows 방화벽 물음**(UAC) | README 2장대로 |
| 7 | 손가락 탭, 드래그, 두 손가락 | 게임이 반응 |
| 8 | 60/120Hz 에서 끊김 | 가상 기기는 60Hz 고정이라 못 봄 |
| 9 | 자리 되찾기([여기서 보기]) 뒤 영상이 다시 보이는지 | 보임(A2, 고친 뒤 미확인) |
| 10 | Play 를 끈 채 QR 로 붙고 나서 Play | 자리를 가져와 영상(A3, 미확인) |
| 11 | USB 버튼을 폰에 쓸지 결정 | 쓰려면 `GameViewRemoteAppAdb.cs` 의 `EMULATOR_ONLY` 를 `false` 로 |

## 7. 아직 열려 있는 것

- 6장의 항목 전부. 특히 3, 5, 9, 10.
- 창의 Stop → Start 흐름을 이번에 직접 눌러 보지 않았습니다.
- `abuse` 시험의 닫기 코드 1건(4장).
- U1 이 비워 둔 EditorPrefs `GVR_WirelessAddress`(백업 `GVR_WirelessAddress_Backup`)는 그대로입니다.
- `05_build_and_test.md` 는 0.1.0 기준 그대로이고 `-Publish` 설명이 없습니다(이 문서 2장과 도구 README 에만 있음).
- U1 문서의 알려진 차이(Origin 의 포트를 보지 않음 등)는 그대로입니다.

## 8. 끝낸 상태

| 항목 | 상태 |
|---|---|
| Unity 컴파일 | 오류 0건(`scriptCompilationFailed = False`). 콘솔의 오류는 게임 코드 `BattlePassManager.cs:1580` 의 NullReferenceException 뿐(이 작업과 무관, 전부터 있음) |
| Unity 에디터 | **Play 켜짐**, 서버 **Start 상태**(시작할 때와 같음), 테스트 패턴 **꺼짐**, 연결된 기기 0 |
| 가상 기기 | 실행 중, 앱 0.1.1(2) 최종 빌드 설치됨(adb 로 설치), 앱과 Chrome 은 종료, reverse 는 `tcp:7300`, `tcp:7311` 만. Chrome 이 설치 출처로 허용된 상태로 남음 |
| 임시로 띄운 것 | 중계(7320)와 대조용 node(7314) 모두 내림 |

### 만든 파일과 바꾼 파일

- Unity 도구: `GameViewRemoteHostGUI.cs`, `GameViewRemoteWindow.cs`, `GameViewRemote.cs`, `GameViewRemoteAppAdb.cs`, `GameViewRemoteLanding.html`, `README.md`, `App~/GameViewRemote.apk`, `App~/app.json`
- 앱: `video/Decoder.kt`, `session/Session.kt`, `app/build.gradle.kts`, `android/build.ps1`
- 도구: `tools/publish-to-unity.ps1`(새로), `tools/device-lab/lan-relay.js`(새로), `tools/unity-host/hosttest.js`(`withapp` 시험 추가), `tools/env.sh`
- 문서: 이 파일, `docs/06_evidence/*.png` 15장, `docs/04_device_lab.md` 첫 줄(바꿨다가 되돌림)
- `dist/GameViewRemote-0.1.1.apk`, `dist/app.json`
