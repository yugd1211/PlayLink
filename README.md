# PlayLink

Unity 에디터의 Game View 를 같은 Wi-Fi 의 안드로이드 폰에서 보고 터치로 조작하는 도구입니다. 빌드 없이 에디터의 Play 화면을 폰으로 확인합니다. 에디터 전용이라 게임 빌드에는 들어가지 않습니다.

예전 이름은 Game View Remote 입니다.

## 지금 상태 (먼저 읽어 주세요)

구조를 바꾸는 중입니다. 이 저장소의 코드는 **이름만 PlayLink 로 바꾼 예전 방식**입니다.

- 예전 방식: 폰이 PC 의 Unity(TCP 7300)로 접속합니다. 그래서 **PC 마다 Windows 방화벽 허용을 한 번 눌러야 합니다**(관리자 권한 필요).
- 새 방식(작업 예정): 앱이 기다리고 Unity 가 폰으로 접속합니다. PC 쪽 설정이 없어집니다.

정한 내용과 진행 순서는 [docs/DESIGN_DECISIONS.md](docs/DESIGN_DECISIONS.md) 에 있습니다. 아래 설치 방법은 지금 코드(예전 방식) 기준이고, 새 방식이 들어가면 다시 씁니다.

확인된 범위:

- Unity 2022.3.62f2 에서 패키지가 컴파일됩니다(`unity/TestProject`).
- 앱이 빌드되고 단위 시험이 통과합니다.
- 이름을 바꾼 뒤의 연결(QR → 설치 → 영상)은 아직 다시 돌려 보지 않았습니다. 이름을 바꾸기 전 코드는 가상 기기에서 끝까지 동작했습니다.

## 필요한 것

| 항목 | 내용 |
|---|---|
| PC | Windows, Unity 2022.3 이상 |
| 그래픽카드 | **NVIDIA** (지금은 ffmpeg 의 `h264_nvenc` 만 씁니다. AMD / Intel / CPU 자동 선택은 작업 예정) |
| ffmpeg | 8.1 권장. 아래 2단계 |
| 폰 | Android 8.0(API 26) 이상, PC 와 같은 Wi-Fi(같은 공유기) |
| 권한 | Windows 방화벽 허용을 누를 때 PC 관리자 권한 |

## 다른 PC 에 설치하기

### 1. 저장소 받기

비공개 저장소라서 GitHub 계정에 권한이 있어야 합니다.

```powershell
git clone git@github.com:yugd1211/PlayLink.git
```

아래에서는 `C:\work\PlayLink` 에 받았다고 가정합니다. 다른 곳이면 경로만 바꿔 읽으세요.

### 2. ffmpeg 준비

1. <https://github.com/BtbN/FFmpeg-Builds/releases> 에서 `ffmpeg-n8.1-latest-win64-gpl-8.1.zip` 을 받아 압축을 풉니다.
2. `bin\ffmpeg.exe` 의 경로를 기억해 둡니다(4단계에서 Unity 창에 넣습니다). PATH 에 넣어 두면 입력하지 않아도 됩니다.

그래픽 드라이버보다 새 ffmpeg 는 "Driver does not support the required nvenc API version" 오류가 납니다. 그럴 때는 8.1 을 쓰거나 드라이버를 올립니다.

### 3. 앱 파일(APK) 넣기

Unity 창이 폰에 내주는 APK 는 저장소에 들어 있지 않습니다(`.gitignore`). 둘 중 한 가지로 넣습니다.

**방법 A. Releases 에서 받기 (쉬움)**

1. 저장소의 Releases 에서 `PlayLink.apk` 와 `app.json` 을 받습니다.
2. `C:\work\PlayLink\unity\io.playlink\Editor\App~\` 폴더를 만들고 두 파일을 넣습니다. 폴더 이름 끝의 `~` 까지 그대로 써야 합니다.

**방법 B. 직접 빌드하기**

Unity Hub 로 설치한 에디터 중에 Android Build Support(OpenJDK, SDK 포함)가 들어 있고 Gradle 8.13 이상이 든 버전(예: Unity 6000.4)이 하나 있어야 합니다. 처음 빌드는 인터넷에서 라이브러리를 받습니다.

```powershell
cd C:\work\PlayLink\android
.\build.ps1 -Publish
```

- 결과: `dist\PlayLink-<버전>.apk`, 그리고 `unity\io.playlink\Editor\App~\` 로 복사.
- 도구를 못 찾으면 환경 변수로 알려 줍니다: `PLAYLINK_JDK`(JDK 17 폴더), `PLAYLINK_GRADLE_JAR`(gradle-launcher jar), `PLAYLINK_SDK`(Android SDK 폴더).
- `android\keystore.properties` 가 없으면 그 PC 의 안드로이드 디버그 키로 서명합니다. **PC 마다 서명이 달라서**, 다른 PC 에서 만든 앱이 폰에 이미 있으면 덮어 설치되지 않습니다. 폰에서 PlayLink 를 지우고 다시 설치하세요.

### 4. Unity 프로젝트에 패키지 넣기

1. 쓰려는 Unity 프로젝트를 엽니다.
2. **Window > Package Manager > + > Add package from disk…** 를 누릅니다.
3. `C:\work\PlayLink\unity\io.playlink\package.json` 을 고릅니다.
4. 컴파일이 끝나면 메뉴에 **Tools > PlayLink** 가 생깁니다.

`Packages/manifest.json` 에 직접 적어도 됩니다.

```json
"io.playlink": "file:C:/work/PlayLink/unity/io.playlink"
```

git 주소로 넣는 방법(`…PlayLink.git?path=/unity/io.playlink`)은 지금은 권하지 않습니다. 패키지가 읽기 전용 캐시에 들어가서 3단계의 `App~` 폴더를 넣을 자리가 없습니다.

프로젝트에 예전 Game View Remote 스크립트(`GameViewRemote*.cs`)가 남아 있어도 클래스 이름이 달라 컴파일은 됩니다. 다만 둘 다 포트 7300 을 쓰므로 한쪽만 Start 하세요.

### 5. 연결하기

1. **Tools > PlayLink > Window** 로 창을 엽니다.
2. "화질과 고급 설정"의 **ffmpeg 경로**에 2단계의 `ffmpeg.exe` 를 넣습니다(PATH 에 있으면 생략).
3. 맨 위의 **Start** 를 누릅니다. QR 이 나타납니다. QR 옆에 "설치될 앱: 0.1.2 (3)" 처럼 버전이 보이면 3단계가 제대로 된 것입니다.
4. 창의 **Windows 방화벽** 구역에서 **[방화벽 허용…]** 을 누릅니다. 무엇을 바꾸는지 먼저 보여 주고, Windows 가 한 번 묻습니다. [예]를 누릅니다.
   - 여는 범위: 이 Unity.exe, 들어오는 TCP 7300, 지금 붙어 있는 네트워크 대역만.
   - **[되돌리기]** 로 원래대로 돌립니다. PlayLink 를 그만 쓸 때는 되돌리기를 누르고 지우세요.
5. 폰을 PC 와 같은 Wi-Fi 에 붙이고, 폰의 기본 카메라로 QR 을 찍어 뜨는 링크를 엽니다.
6. 폰 브라우저의 안내 페이지에서 **[앱 설치(APK 받기)]** 로 설치한 뒤 **[여기를 눌러 연결]** 을 누릅니다.
   - Chrome 이 "안전하지 않은 다운로드", "유해한 파일일 수 있음" 경고를 띄우면 [계속], 설치할 때 "출처를 알 수 없는 앱"을 묻으면 허용합니다. Play 프로텍트가 물으면 [설치]를 고릅니다.
7. Unity 에서 **Play** 를 누르면 폰에 게임 화면이 나옵니다. 다음부터는 폰에서 앱만 열면 다시 연결됩니다.

### 안 될 때

| 증상 | 확인할 것 |
|---|---|
| QR 을 찍었는데 페이지가 안 열림 | 4번의 방화벽 허용을 했는지, 폰과 PC 가 같은 공유기인지. 회사 Wi-Fi 가 기기끼리의 통신을 막는 망이면 이 방식으로는 안 됩니다 |
| 창에 주소가 이상함 | PC 에 네트워크가 여러 개면 창의 "이 PC 주소"에서 폰과 같은 공유기 쪽 주소를 고릅니다 |
| 앱에 "PC가 H.264 를 보내지 않습니다" | ffmpeg 경로가 맞는지, NVIDIA 그래픽카드인지 |
| "Driver does not support the required nvenc API version" | ffmpeg 8.1 로 바꾸거나 그래픽 드라이버를 올립니다 |
| QR 옆에 앱 버전이 안 보임 | `App~` 폴더 위치와 이름, 안에 `PlayLink.apk` 와 `app.json` 이 있는지 |
| 앱 설치가 "패키지 충돌"로 실패 | 다른 PC 에서 만든 앱이 깔려 있습니다. 폰에서 PlayLink 를 지우고 다시 설치 |
| 화면이 안 나옴 | Unity 가 Play 중인지. Play 전에는 폰에 "Unity 에서 Play 를 누르면 화면이 나옵니다"가 보입니다 |

창의 **[USB 로 앱 설치] [USB 로 연결]** 버튼은 지금 가상 기기(emulator)에만 동작하게 막혀 있습니다. 실제 폰은 QR 로 연결하세요.

예전 방식의 자세한 사용법(창의 각 항목, 폰에 나오는 글, 방화벽이 여는 범위)은 [docs/unity/README_tool.md](docs/unity/README_tool.md) 에 있습니다. 그 문서는 예전 이름(Game View Remote)과 경로로 적혀 있습니다.

## 주의

- QR 을 찍은 사람은 누구나 이 PC 의 게임 화면을 보고 조작할 수 있습니다. 회사나 집 Wi-Fi 에서만 쓰고, QR 이 다른 사람에게 알려졌으면 창의 **[새 QR 발급]** 을 누르세요.
- 지금 방식은 영상과 터치를 암호화하지 않습니다(새 방식에서 암호화합니다).

## 폴더

| 폴더 | 내용 |
|---|---|
| `unity/io.playlink/` | Unity 에디터 패키지(에디터 전용) |
| `unity/TestProject/` | 패키지 컴파일 확인용 Unity 2022.3 프로젝트 |
| `android/` | 폰 앱(Kotlin) |
| `tools/` | 가상 기기와 측정 스크립트 |
| `docs/` | 설계 결정, 예전 구조의 개발 문서 |

## 개발

```powershell
# 앱: 단위 시험 + 빌드 + dist 로 복사 (-Publish 를 붙이면 Unity 패키지의 App~ 로도 복사)
cd android
.\build.ps1

# Unity 패키지 컴파일 확인
& "C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Unity.exe" -batchmode -nographics -quit -projectPath unity\TestProject -logFile unity\TestProject\compile.log
```

서명 키와 비밀번호는 저장소에 넣지 않습니다. 고정된 키로 서명하려면 `android\keystore.properties` 를 만듭니다(`.gitignore` 에 들어 있습니다).

```properties
storeFile=<jks 파일 경로>
storePassword=<비밀번호>
keyAlias=<별칭>
keyPassword=<비밀번호>
```

## 라이선스

MIT. 포함된 QR 생성 코드(`unity/io.playlink/Editor/ThirdParty/QrCodeGenerator`)도 MIT 이며 그 폴더에 고지가 있습니다.
