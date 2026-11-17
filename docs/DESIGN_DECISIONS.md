# PlayLink 설계 결정 (2026-11-17)

Game View Remote 를 PlayLink 로 옮기면서 정한 내용입니다. 구현은 아직 시작 전이고, 이 저장소의 코드는 옮겨 온 그대로(예전 구조)입니다.

## 왜 바꾸는가

예전 구조는 폰이 Wi-Fi 로 PC 의 Unity(TCP 7300)에 접속했습니다. PC 로 들어오는 연결이라 PC 마다 Windows 방화벽 규칙을 넣어야 했습니다.
회사에서 각자 PC 로 바로 쓰고, 나중에 오픈소스로 공개하려면 PC 쪽 설정이 없어야 합니다.

## 연결 구조

- 앱이 기다리고(포트를 열고), Start 를 누른 Unity 가 폰으로 접속합니다. PC 에서 나가는 연결이라 방화벽에 걸리지 않습니다.
- 폰 찾기: Unity 가 같은 Wi-Fi 에 UDP 브로드캐스트로 묻고, 앱이 켜진 폰이 답합니다(PC 가 먼저 보낸 것에 대한 답이라 방화벽을 통과).
  안 잡히는 망을 위해 앱 대기 화면에 폰 주소를 띄우고 Unity 창에 직접 입력할 수 있게 합니다.
- 폰에서 시작하기: 앱이 탐색 응답에 "연결해 달라"를 실어 보내면 Unity 가 접속합니다.
- 제거: QR 주소 → 브라우저 안내 페이지 → APK 서빙, 방화벽 허용/되돌리기, Chrome 페이지 뷰어.
- 유지: USB(adb) 연결.

## 페어링과 연결 규칙

- 첫 페어링은 둘 중 하나: ① 앱으로 Unity 창의 QR 을 찍음 ② Unity 창의 폰 목록에서 고르고, 양쪽에 뜬 같은 6자리 숫자를 확인한 뒤 폰에서 [허용].
- 양쪽이 서로를 기억하고 라벨을 붙입니다(PC 에서 폰 이름, 폰에서 PC 이름).
- PC 는 자동으로 붙지 않고, 앱이 켜진 페어링된 폰 목록에서 사람이 고릅니다. 앱에서도 Start 된 PC 목록에서 골라 시작할 수 있습니다.
- 페어링된 PC 의 요청: 폰이 비어 있으면 묻지 않고 받고, 다른 PC 화면을 보는 중이면 폰에서 바꿀지 묻습니다.
- 영상과 터치는 처음부터 암호화합니다(페어링 때 나눈 키).

## 앱

- PC 목록과 라벨, 대기 화면의 폰 주소, QR 스캔, 승인 창.
- 연결 상태 표시, 영상 중 메뉴, 설정 화면, 눈에 띄는 연결 끊기 버튼, 디버그 상태 보기.
- 업데이트 알림은 넣지 않습니다.

## 배포와 공개

- 이름 PlayLink. Unity 패키지 `io.playlink`, 앱 `io.playlink.app`, 딥링크 스킴 `playlink`.
  (소니의 PS4용 "PlayLink" 브랜드와 이름이 같다는 점은 알고 정했습니다.)
- GitHub 비공개 저장소로 시작, MIT, Windows 에디터 + 안드로이드, Unity 2022.3 이상.
- APK 는 GitHub Releases 에 두고 Unity 창에 다운로드 QR 을 띄웁니다. 공개 전에는 주소를 사내 위치로 설정합니다.
- 서명 키는 새로 만들어 저장소 밖에 보관합니다(`.gitignore` 가 `*.jks` 를 뺍니다).
- 인코더: ffmpeg 유지, NVIDIA → AMD → Intel → CPU 순으로 자동 선택.

## 진행 순서

1. (완료) 저장소 만들기, 코드와 문서 옮기기.
2. 이름 바꾸기: 클래스 `GameViewRemote*`, 앱 패키지 `kr.co.seeplay.gvr`, 스킴 `seeplay-gvr`, 설정 파일 경로.
3. 연결 방향 뒤집기, 탐색, 페어링, 암호화 (Unity 쪽 + 앱 쪽).
4. 앱 화면과 Unity 창을 새 흐름으로.
5. 예전 방식 제거, 인코더 자동 선택, 문서 정리.
6. 가상 기기 검증 뒤 실제 폰과 회사 망에서 확인(자동 탐색은 가상 기기로 확인할 수 없음).

## 옮겨 온 것

| 여기 | 원래 자리 |
|---|---|
| `unity/io.playlink/Editor/` | `catjump/Assets/04_Scripts/Editor/GameViewRemote/` |
| `android/`, `tools/` | `GameViewRemoteApp/android/`, `GameViewRemoteApp/tools/` |
| `docs/unity/` | `catjump/docs/GameViewRemote/` + 도구 폴더의 README |
| `docs/app/` | `GameViewRemoteApp/docs/` |

`docs/unity`, `docs/app` 의 문서는 예전 구조를 설명합니다. 구현하면서 고칩니다.
