# PlayLink

Unity 에디터의 Game View 를 같은 Wi-Fi 의 안드로이드 폰에서 보고 터치로 조작하는 도구입니다. 빌드 없이 에디터의 Play 화면을 폰으로 확인합니다. 에디터 전용이라 게임 빌드에는 들어가지 않습니다.

**PC 쪽 설정이 없습니다.** 폰의 앱이 기다리고 Unity 가 폰으로 접속하기 때문에 Windows 방화벽을 건드리지 않고 관리자 권한도 필요 없습니다. 영상과 터치는 암호화됩니다.

예전 이름은 Game View Remote 입니다.

## 지금 상태 (먼저 읽어 주세요)

가상 기기(안드로이드 에뮬레이터)에서 끝까지 확인했고, **실제 폰에서는 아직 한 번도 돌려 보지 않았습니다.**

| 항목 | 상태 |
|---|---|
| Unity 2022.3.62f2 에서 패키지 컴파일 | 확인 |
| 암호 계산이 기준 구현과 일치(Unity, 앱) | 확인 (자동 시험) |
| Unity ↔ 실제 앱: 숫자 확인 페어링, 연결, H.264 영상, 터치, Play 종료 후 대기 | 확인 (가상 기기, USB 경로) |
| 앱: QR 페어링, PC 바꾸기 묻기, 연결 끊기, 페어링 기억 | 확인 (가상 기기, PC 흉내 스크립트 상대) |
| **같은 Wi-Fi 에서 폰이 Unity 창 목록에 자동으로 뜨는 것(UDP 탐색)** | **실제 폰에서 미확인** (가상 기기는 구조상 확인 불가) |
| **Wi-Fi 로 Unity → 폰 직접 접속** | **실제 폰에서 미확인** |
| 앱 안의 카메라로 QR 찍기 | 미확인 (QR 내용을 링크로 넘기는 방식만 확인) |
| Unity 창의 모양과 글자 배치 | 사람 눈으로 미확인 (그리는 코드가 오류 없이 도는 것만 확인) |
| 인코더: NVIDIA, CPU(libx264) | 확인 |
| 인코더: AMD, Intel | 미확인 (이 PC 에 장치가 없음) |
| 회사 망 | 미확인 |

자동 탐색이 안 되면 **폰 주소 직접 입력**이나 **USB** 로 연결할 수 있습니다(아래 "안 될 때").

정한 내용은 [docs/DESIGN_DECISIONS.md](docs/DESIGN_DECISIONS.md), 통신 규칙은 [docs/PROTOCOL.md](docs/PROTOCOL.md) 에 있습니다.

## 필요한 것

| 항목 | 내용 |
|---|---|
| PC | Windows, Unity 2022.3 이상 |
| ffmpeg | 필요합니다(앱은 H.264 만 표시). 8.1 권장. 아래 2단계 |
| 그래픽카드 | 무엇이든 됩니다. NVIDIA → AMD → Intel → CPU 순으로 되는 인코더를 자동으로 고릅니다 |
| 폰 | Android 8.0(API 26) 이상, PC 와 같은 Wi-Fi(같은 공유기) |

## 설치하기

### 1. 저장소 받기

비공개 저장소라서 GitHub 계정에 권한이 있어야 합니다.

```powershell
git clone git@github.com:yugd1211/PlayLink.git
```

아래에서는 `C:\work\PlayLink` 에 받았다고 가정합니다.

### 2. ffmpeg 준비

1. <https://github.com/BtbN/FFmpeg-Builds/releases> 에서 `ffmpeg-n8.1-latest-win64-gpl-8.1.zip` 을 받아 압축을 풉니다.
2. `bin\ffmpeg.exe` 의 경로를 기억해 둡니다(5단계에서 Unity 창에 넣습니다). PATH 에 넣어 두면 입력하지 않아도 됩니다.

### 3. 폰에 앱 설치

저장소의 **Releases** 에서 `PlayLink-<버전>.apk` 를 받아 폰에 설치합니다. 방법은 아무거나 됩니다.

- 폰의 브라우저에서 GitHub 에 로그인해 Releases 의 APK 를 받기 (Unity 창의 "앱 다운로드" QR 이 이 페이지를 엽니다)
- PC 에서 받아 메신저, 클라우드, USB 로 폰에 옮긴 뒤 열기
- USB 디버깅이 켜진 폰이면 `adb install PlayLink-<버전>.apk`

"출처를 알 수 없는 앱" 설치를 허용해야 하고, Play 프로텍트가 물으면 [설치]를 고릅니다.
예전 Game View Remote 앱과는 다른 앱(`io.playlink.app`)이라 따로 설치됩니다.

직접 빌드하려면 아래 "개발"을 보세요.

### 4. Unity 프로젝트에 패키지 넣기

1. 쓰려는 Unity 프로젝트를 엽니다.
2. **Window > Package Manager > + > Add package from disk…** 를 누릅니다.
3. `C:\work\PlayLink\unity\io.playlink\package.json` 을 고릅니다.
4. 컴파일이 끝나면 메뉴에 **Tools > PlayLink** 가 생깁니다.

`Packages/manifest.json` 에 직접 적어도 됩니다.

```json
"io.playlink": "file:C:/work/PlayLink/unity/io.playlink"
```

git 주소로도 넣을 수 있습니다(비공개인 동안에는 그 PC 의 git 이 이 저장소에 접근할 수 있어야 합니다).

```json
"io.playlink": "git@github.com:yugd1211/PlayLink.git?path=/unity/io.playlink"
```

패키지를 넣지 않고 먼저 보려면 저장소의 `unity/TestProject` 를 Unity 2022.3 으로 열면 됩니다. 버튼 3개와 움직이는 상자가 있는 시험용 장면이 들어 있습니다.

프로젝트에 예전 Game View Remote 스크립트(`GameViewRemote*.cs`)가 남아 있어도 이름이 달라 함께 컴파일됩니다.

### 5. 처음 연결하기 (페어링)

1. **Tools > PlayLink > Window** 로 창을 엽니다.
2. "화질과 고급 설정"의 **ffmpeg 경로**에 2단계의 `ffmpeg.exe` 를 넣습니다(PATH 에 있으면 생략).
3. 맨 위의 **Start** 를 누릅니다.
4. 폰에서 PlayLink 앱을 엽니다. 폰은 PC 와 같은 Wi-Fi 에 있어야 합니다.
5. Unity 창의 목록에 폰이 **새 폰**으로 나타납니다. **[페어링]** 을 누릅니다.
6. Unity 창과 폰에 **같은 6자리 숫자**가 뜹니다. 숫자가 같으면 Unity 창에서 **[확인]**, 폰에서 **[허용]** 을 누릅니다.
7. 연결되면 폰에 "Unity 에서 Play 를 누르면 화면이 나옵니다"가 보입니다. Unity 에서 **Play** 를 누르면 게임 화면이 나옵니다.

QR 로 페어링할 수도 있습니다. Unity 창의 **"새 폰 페어링: QR 찍기"** 를 펼치고 앱의 **[QR 스캔]** 으로 찍으면 숫자 확인 없이 끝납니다.

### 6. 다음부터

- 페어링은 양쪽이 기억합니다. PC 쪽은 Windows 사용자별로 저장해서, 그 PC 의 모든 Unity 프로젝트에서 같은 폰이 보입니다.
- Unity 는 스스로 붙지 않습니다. Start 를 누른 뒤 **목록에서 폰을 고르거나**, 폰의 앱에서 **PC 를 고르면** 연결됩니다.
- 한 번에 폰 한 대만 연결됩니다. 다른 폰을 고르면 지금 폰은 끊깁니다.
- 폰이 다른 PC 화면을 보는 중에 접속하면 폰에서 바꿀지 묻습니다.
- 스크립트를 고쳐 다시 컴파일되거나 Play 를 켜고 꺼도 연결은 이어집니다.
- 폰에서 **[연결 끊기]** 를 누르면 Unity 가 다시 붙지 않습니다. 다시 보려면 어느 쪽에서든 다시 고릅니다.
- 창에서 폰의 이름(라벨)을 바꾸거나 페어링을 지울 수 있고, 앱의 설정에서도 PC 이름을 바꾸거나 지울 수 있습니다.

## 안 될 때

| 증상 | 할 일 |
|---|---|
| 폰이 목록에 안 뜸 | 폰 앱이 켜져 있고 화면에 떠 있는지, 같은 공유기인지 확인합니다. 그래도 안 뜨면 앱 대기 화면에 크게 보이는 **폰 주소**를 Unity 창의 **"폰이 목록에 안 보일 때: 폰 주소 직접 입력"** 에 넣습니다 |
| 주소를 넣어도 안 붙음 | 그 Wi-Fi 가 기기끼리의 통신을 막는 망일 수 있습니다(손님용 Wi-Fi 등). **USB** 로 연결합니다 |
| USB 로 연결 | 폰의 USB 디버깅을 켜고 케이블로 꽂은 뒤, 앱을 열어 둔 채 창의 **"USB 케이블로 연결"** 에서 [USB 로 연결]. Wi-Fi 없이 됩니다 |
| "폰에서 PlayLink 앱의 대기 화면을 열어 두세요" | 숫자 확인 페어링은 앱이 대기 화면일 때만 받습니다. 다른 창이나 설정 화면을 닫습니다 |
| 폰이 "페어링되어 있지 않습니다"로 거절 | 폰에서 페어링을 지웠거나 앱을 다시 설치했습니다. 창의 [다시 페어링]을 누릅니다 |
| 앱에 "PC가 H.264 를 보내지 않습니다" | ffmpeg 경로가 맞는지 확인합니다. 창의 "상태와 통계"에 고른 인코더와 실패 이유가 보입니다 |
| "Driver does not support the required nvenc API version" | ffmpeg 8.1 로 바꾸거나 그래픽 드라이버를 올립니다 |
| 화면이 안 나옴 | Unity 가 Play 중인지, Game 뷰가 보이는 상태인지(Unity 창 최소화 금지) 확인합니다 |

실제 폰에서 처음 돌려 보는 것이라 문제가 나오면 다음을 알려 주세요: 어느 단계에서 멈췄는지, Unity 창의 "상태와 통계" 내용, 앱 영상 화면 오른쪽 위 메뉴(≡)의 디버그 상태.

## 보안

- 페어링한 폰만 연결됩니다. 연결마다 새 키를 만들어(P-256 ECDH) 영상과 터치를 암호화합니다(AES-256-CTR + HMAC-SHA256).
- 숫자 확인 페어링은 양쪽에서 숫자를 확인해야 끝납니다. 숫자가 다르면 누르지 마세요.
- QR 에는 일회용 비밀이 들어 있습니다. 한 번 쓰면 바뀝니다.
- 페어링 키는 PC 에서는 `%LOCALAPPDATA%\PlayLink\identity.dat`(Windows DPAPI 로 보호), 폰에서는 Android Keystore 로 암호화해 둡니다.
- Unity 쪽의 P-256 계산은 직접 구현한 것입니다(Unity 의 Mono 에 없음). 알려진 한계는 [docs/PROTOCOL.md](docs/PROTOCOL.md) 6장에 있습니다.

## 폴더

| 폴더 | 내용 |
|---|---|
| `unity/io.playlink/` | Unity 에디터 패키지(에디터 전용) |
| `unity/TestProject/` | 패키지 시험용 Unity 2022.3 프로젝트(시험용 장면과 자동 시험) |
| `android/` | 폰 앱(Kotlin) |
| `tools/` | 프로토콜 기준 구현, 폰/PC 흉내 스크립트, 가상 기기 스크립트 |
| `docs/` | 설계 결정, 프로토콜, 확인 화면(`evidence/`), 예전 구조의 문서(`legacy/`) |

## 개발

```powershell
# 앱: 단위 시험 + 빌드 + dist 로 복사
cd android
.\build.ps1
```

- Unity Hub 로 설치한 에디터 중 Android Build Support(OpenJDK, SDK 포함)와 Gradle 8.13 이상이 든 버전(예: Unity 6000.4)을 찾아 씁니다. 못 찾으면 환경 변수 `PLAYLINK_JDK`, `PLAYLINK_GRADLE_JAR`, `PLAYLINK_SDK` 로 알려 줍니다.
- 서명 키와 비밀번호는 저장소에 넣지 않습니다. `android\keystore.properties`(`.gitignore`)가 있으면 그 키로, 없으면 그 PC 의 안드로이드 디버그 키로 서명합니다. 서명이 다른 APK 는 덮어 설치되지 않으니 폰에서 지우고 다시 설치합니다.

```properties
storeFile=<jks 파일 경로>
storePassword=<비밀번호>
keyAlias=<별칭>
keyPassword=<비밀번호>
```

```powershell
# 프로토콜 기준 구현의 자체 시험
node tools\playlink-proto.js selftest

# Unity 패키지: 컴파일, 암호 자체 시험
$U = "C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Unity.exe"
& $U -batchmode -nographics -quit -projectPath unity\TestProject -logFile unity\TestProject\compile.log
& $U -batchmode -nographics -projectPath unity\TestProject -executeMethod PlayLinkSelfTest.RunBatch -logFile unity\TestProject\selftest.log
```

그 밖의 자동 시험(`unity/TestProject/Assets/Editor`): `PlayLinkInteropTest`(Node 폰 흉내와 탐색·페어링·연결), `PlayLinkStreamTest`(Play 에 들어가 영상과 터치), `PlayLinkDeviceTest`(가상 기기나 USB 폰의 실제 앱과 연결). 각 파일 맨 위에 실행 방법이 있습니다.
폰 없이 Unity 창을 시험하려면 `node tools\playlink-proto.js phone` 이 폰 역할을 합니다(같은 PC 에서 탐색 응답과 페어링까지).

## 라이선스

MIT. 포함된 QR 생성 코드(`unity/io.playlink/Editor/ThirdParty/QrCodeGenerator`)도 MIT 이며 그 폴더에 고지가 있습니다.
