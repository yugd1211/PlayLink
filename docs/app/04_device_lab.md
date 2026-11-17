STATUS: NOT READY (최종 단계 작업자가 02:5x 에 stop-avd.ps1 로 종료함. 다시 쓰려면 tools/device-lab/start-avd.ps1 로 띄우고 이 줄을 READY 로)

# 04. 장치 랩: PC 의 안드로이드 가상 기기

작성: 장치 랩 워커, 2026-11-14 01:02~01:30. 맨 윗줄 STATUS 는 다른 워커가 읽는 값입니다. 기기를 내리거나 다시 띄우는 워커는 그 줄을 먼저 `STATUS: NOT READY (이유)` 로 바꾸고, 끝나면 되돌립니다.

실제 폰(SM-F731N)은 이 작업에서 한 번도 쓰지 않았습니다(adb 명령 없음, 연결 시도 없음).

## 1. 요약

| 항목 | 결과 |
|---|---|
| 소유자 지시 (1) 기존 가상 기기를 고쳐서 쓴다 | **됨.** 기존 AVD `Medium_Phone_API_35` 를 그대로 씁니다. 데이터를 지우지 않았고 새 AVD 나 대체 에뮬레이터는 만들지도 설치하지도 않았습니다 |
| 고친 방법 | 창 없이(`-no-window`) 띄웁니다. 창이 있는 실행 파일은 이 PC 에서 멈춥니다(6절) |
| 시리얼 | `emulator-5554` (콘솔 5554, adb 5555) |
| 시작 명령 | `powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\dbrud\Developemnt\SeePlay\GameViewRemoteApp\tools\device-lab\start-avd.ps1` |
| 콜드 스타트 검증 | 완전히 내린 상태에서 같은 명령으로 2번 연속 성공: 01:19:48 → READY 44초, 01:20:39 → READY 40초. 두 번 다 `sys.boot_completed=1`, adb 상태 `device`, 앱 설치 유지, reverse 2개 설정됨 |
| 앱이 영상을 보여 주는가 | **보여 줌.** 실제 Unity 서버(7300)의 H.264 720x1280 을 `c2.goldfish.h264.decoder` 로 디코딩해 표시. 스크린샷 `docs/04_device_lab_app.png` (01:23, 두 번째 콜드 스타트 뒤) |
| Orca 에뮬레이터 패널 | **됨.** `orca emulator attach emulator-5554 --focus --json` → `attached: true`, `streamUrl: scrcpy://emulator-5554` (3절) |
| 이 PC 에 설치한 것 | 없음. 만든 파일과 바꾼 것은 8절 |

## 2. 시작, 종료, 확인

```powershell
# 시작(이미 떠 있으면 다시 띄우지 않고 reverse 만 다시 겁니다). 끝에 "READY serial=emulator-5554 ..." 가 나오고 종료 코드 0.
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\dbrud\Developemnt\SeePlay\GameViewRemoteApp\tools\device-lab\start-avd.ps1
#   -KillHung : qemu 가 떠 있는데 adb 가 붙지 않을 때, 그 qemu 를 끝내고 다시 띄움
#   -Gpu swiftshader_indirect : 호스트 GPU 대신 소프트웨어 렌더링(기본은 host)

# 종료(콘솔 kill 로 정상 종료, 20초 안에 안 끝나면 강제 종료)
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\dbrud\Developemnt\SeePlay\GameViewRemoteApp\tools\device-lab\stop-avd.ps1
```

스크립트가 실제로 실행하는 것:

```
작업 폴더 C:\Users\dbrud\AppData\Local\Android\Sdk\emulator
emulator.exe -avd Medium_Phone_API_35 -port 5554 -no-window -no-snapshot -no-boot-anim -no-audio -gpu host
```

- 시작 전에 죽은 인스턴스가 남긴 잠금 `Medium_Phone.avd\hardware-qemu.ini.lock`, `multiinstance.lock` 을 지웁니다(qemu 프로세스가 없을 때만). 이 잠금은 정상 종료 뒤에도 남는 것을 확인했고, 남아 있으면 `Running multiple emulators with the same AVD` 로 실패합니다.
- `-no-snapshot`: 매번 콜드 부팅(40~45초)이고 종료할 때 스냅샷을 쓰지 않습니다. 기존 스냅샷 폴더는 건드리지 않습니다. `/data` 에 쓴 것(설치한 앱, adb 허용)은 유지됩니다.
- 로그: `%TEMP%\gvr-device-lab\emulator-<시각>.out.log`
- 창 없는 실행 파일의 프로세스 이름은 `qemu-system-x86_64-headless.exe` 입니다(`qemu-system-x86_64` 로만 찾으면 안 보입니다). 메모리 약 3.1GB 를 씁니다(측정 시 PC 여유 메모리 4.9GB).
- **부팅 직후 2분쯤은 기기 부하가 큽니다**(load average 22, `com.google.android.gms` 가 CPU 70%). 이때 재면 수치가 크게 나빠집니다(5절). 측정은 READY 뒤 2분 이상 지나서 하세요.

### adb 규칙 (모든 워커)

- **쓰는 adb: Unity 것** `C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe` (32.0.0). 지금 떠 있는 adb 서버(pid 73232, 11-13 20:42 시작)가 이 파일이고 `tools/env.sh` 도 이 경로입니다.
- SDK 의 adb(`…\Android\Sdk\platform-tools\adb.exe`, 35.0.2)는 Orca 패널과 에뮬레이터가 내부에서 씁니다. 두 adb 의 서버 프로토콜 버전이 같아서(둘 다 1.0.41) 섞여도 서버가 다시 뜨지 않는 것을 확인했습니다(Orca attach 전후로 서버 pid 73232 그대로, reverse 유지). 그래도 워커는 한 가지(Unity 것)만 씁니다.
- 항상 `-s emulator-5554`. `adb kill-server`, `adb start-server`, `adb connect`, `adb disconnect` 금지.
- `tools/env.sh` 는 `export GVR_SERIAL=emulator-5554` 를 먼저 하고 `source` 하면 그대로 쓸 수 있습니다(기본값은 실제 폰 시리얼이므로 **반드시 먼저 설정**). `shot()` 함수는 폰의 화면 id 를 쓰므로 가상 기기에서는 쓰지 말고 아래 명령을 씁니다.

```bash
"$ADB" -s emulator-5554 exec-out screencap -p > shot.png      # Git Bash. PowerShell 은 cmd /c "adb ... > 파일" 로(PowerShell 의 > 는 PNG 를 깨뜨립니다)
```

## 3. 앱이 PC 의 서버에 닿는 방법

기기 안에서 `nc -z` 로 확인(01:25, Unity 가 Play 중일 때):

| 기기에서 본 주소 | 닿는 곳 | 결과 |
|---|---|---|
| `127.0.0.1:7300` | `adb reverse tcp:7300 tcp:7300` → PC `127.0.0.1:7300` | 열림. **앱 시험은 이것으로 했습니다**(앱은 `via=usb` 경로로 취급) |
| `10.0.2.2:7300` | 에뮬레이터 NAT → PC 루프백 | 열림(포트만 확인, 앱으로는 시험하지 않음) |
| `192.168.25.5:7300` | 에뮬레이터 NAT → PC 의 LAN 주소 | 열림(포트만 확인). 서버에는 PC 자신에서 온 연결로 보이고 실제 Wi-Fi 를 거치지 않습니다 |

- reverse 는 `start-avd.ps1` 이 부팅마다 `tcp:7300`, `tcp:7311` 을 다시 겁니다(재부팅하면 없어지기 때문). 다른 포트는 각자 `adb -s emulator-5554 reverse tcp:<포트> tcp:<포트>`.
- 7311(재생 서버 `tools/replay.js`)은 제가 확인할 때 떠 있지 않았습니다. reverse 만 걸어 두었고 앱으로 시험하지 않았습니다.
- **실제 Unity 서버(7300)는 이제 인증을 합니다.** `tools/env.sh` 의 `open` 이 쓰는 시험 값(`s=GVRTEST2`)으로 열면 앱이 `state FAILED_SERVER 이 QR 은 다른 PC의 것입니다` 를 냅니다(01:13 확인). 실제 값은 `catjump\Library\GameViewRemote\secret.json` 의 `serverId`, `token` 입니다:

```powershell
$adb = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
$j = Get-Content 'C:\Users\dbrud\Developemnt\SeePlay\catjump\Library\GameViewRemote\secret.json' -Raw | ConvertFrom-Json
& $adb -s emulator-5554 shell "am start -W -a android.intent.action.VIEW -d 'seeplay-gvr://127.0.0.1:7300/c?v=1&s=$($j.serverId)&t=$($j.token)&via=usb&take=1' kr.co.seeplay.gvr"
```

  주의: `adb shell "…'URL'…"` 안에서 `&` 앞에 역슬래시를 붙이면 안 됩니다(작은따옴표 안이라 역슬래시가 그대로 들어가 앱이 링크를 못 읽습니다. 처음에 이렇게 해서 "QR 을 찍으세요" 화면만 나왔습니다).

### Orca 에뮬레이터 패널

```
orca emulator devices --json                          # emulator-5554, state booted 로 나옴
orca emulator attach emulator-5554 --focus --json     # attached: true, scrcpy 스트림
orca emulator kill --json                             # 보기 도우미만 내림(기기는 그대로)
```

- 가상 기기를 `start-avd.ps1` 로 먼저 띄운 뒤 **시리얼로** attach 합니다. AVD 이름(`orca emulator attach Medium_Phone_API_35`)으로 하면 Orca 가 창 있는 에뮬레이터를 직접 띄우려다 멈춥니다(조정자가 본 "stopped before it finished booting").
- attach 는 명령 결과와 기기 쪽 흔적(`scrcpy-server` 프로세스, 576x1280 가상 디스플레이 생성 로그)으로 확인했습니다. 패널 화면 자체는 제가 볼 수 없어 눈으로 확인하지 못했습니다.
- 패널이 붙어 있으면 기기 안에서 scrcpy 가 화면을 H.264 로 인코딩하므로 부하가 늘어납니다. **수치를 잴 때는 `orca emulator kill` 로 떼고 재세요.** 재부팅하면 attach 는 끊기므로 다시 해야 합니다. 지금은 붙여 둔 상태입니다(01:21).

## 4. adb 인증(한 번 해 둠)과 다시 필요할 때

- 처음 부팅했을 때 adb 가 `unauthorized` 였습니다. 에뮬레이터가 PC 의 adb 공개 키를 넘겨도(로그 `Sending adb public key`) 이 Play 스토어 이미지(user 빌드, `qemu.adb.secure=1`)는 자동 허용하지 않고 화면에 "Allow USB debugging?" 창을 띄웠습니다. 창에 나온 지문 `97:36:EB:…:FB:FC` 가 `C:\Users\dbrud\.android\adbkey.pub` 의 MD5 와 같은 것을 확인하고, 에뮬레이터 콘솔로 [Always allow from this computer] 와 [Allow] 를 눌렀습니다. 이후 부팅 3번은 자동으로 `device` 가 됐습니다.
- 다시 `unauthorized` 가 되면(AVD 데이터를 지웠거나 PC 의 adb 키가 바뀐 경우): `tools\device-lab\authorize-adb.ps1`. adb 입력은 인증 전이라 못 쓰고, 콘솔의 `event mouse` 는 이 이미지에서 먹지 않아서 `event send EV_ABS:ABS_MT_…` 멀티터치 이벤트(좌표 0..32767)를 보냅니다. `-ShotDir <폴더> -ShotOnly` 로 콘솔 스크린샷만 찍어 화면을 먼저 볼 수 있습니다. (이 스크립트는 제가 손으로 보낸 것과 같은 이벤트를 묶은 것이고, 스크립트 파일 자체를 실행해 보지는 않았습니다.)

## 5. 기기 사양, 코덱, 수치

| 항목 | 값 |
|---|---|
| 이미지 | `google/sdk_gphone64_x86_64/emu64xa:15/AE3A.240806.036` (Android 15, API 35, Play 스토어, user 빌드: root 없음) |
| ABI | `x86_64,arm64-v8a` (arm64 는 번역기 Berberis 로 실행) |
| 화면 | 1080x2400, 420dpi, **60Hz 한 가지**(다른 주사율 모드 없음) |
| RAM / CPU | 2GB / 6코어(설정은 8, 에뮬레이터가 6으로 제한), WHPX 가속 동작 |
| 그래픽 | `-gpu host`: 호스트 NVIDIA RTX 4060 의 OpenGL 로 변환 |
| APK | 네이티브 라이브러리가 `arm64-v8a`, `armeabi-v7a` 뿐(CameraX 의 `libimage_processing_util_jni.so`, `libsurface_util_jni.so`). x86_64 용이 없어 설치 후 `primaryCpuAbi=arm64-v8a` 가 되고 번역으로 돕니다. 설치와 실행, 영상 표시에는 문제가 없었습니다. 카메라(QR 스캔) 화면은 열어 보지 않았습니다 |

비디오 디코더(`dumpsys media.player`):

| 형식 | 가상 하드웨어 디코더 | 소프트웨어 디코더 |
|---|---|---|
| H.264 | `c2.goldfish.h264.decoder` (vendor, hw-accelerated 로 표시, Baseline/Main/High 5.2, 96x96~4096x4096) | `c2.android.avc.decoder` |
| HEVC | `c2.goldfish.hevc.decoder` | `c2.android.hevc.decoder` |
| VP9 | `c2.goldfish.vp9.decoder` | `c2.android.vp9.decoder` |
| AV1 | 없음 | `c2.android.av1-dav1d.decoder`, `c2.android.av1.decoder` |

앱은 `c2.goldfish.h264.decoder` 를 골랐고(`decoder c2.goldfish.h264.decoder 720x1280 keys=[ll=1 ]`) 코덱 오류는 없었습니다(`codecErrors 0`). goldfish 디코더는 에뮬레이터가 호스트 쪽에서 디코딩해 주는 가상 장치입니다(호스트에서 어떤 방식으로 디코딩하는지는 확인하지 않았습니다). 삼성 폰의 디코더와는 다른 구현입니다.

### 가상 기기에서 잰 수치 (실제 폰을 대표하지 않습니다. 기능 확인용)

실제 Unity 서버 7300, `adb reverse`, H.264 720x1280, 앱 기본 설정(`pace=sched buf=auto rr=match`), `tools/app-report.js --skip=0.7` 의 RELEASE 줄. 기록 파일은 8절의 scratchpad `rec\`.

| 구간 | 조건 | 받은 fps | 표시 fps | 디코딩 ms 평균 / p50 / p90 / p99 / 최대 | 표시 간격 p50 / p99 / 최대 ms | 캡처→표시 p50 ms |
|---|---|---|---|---|---|---|
| `avd_idle` 01:14, 12.8초 | `-gpu swiftshader_indirect`, 부팅 6분 뒤, 패널 안 붙임 | 59.7 | 48.3 | 11.6 / 8.6 / 24.0 / 55.3 / 98.7 | 16.8 / 46.0 / 57.3 | 22.4 |
| `avd_host_idle` 01:17, 16.4초 | `-gpu host`, **부팅 1분 뒤(부하 중)**, 패널 안 붙임 | 57.4 | 22.2 | 40.3 / 37.3 / 76.2 / 121.1 / 185.7 | 25.3 / 79.5 / 81.6 | 74.2 |
| `avd_host_idle2` 01:19, 15.8초 | `-gpu host`, 부팅 2.5분 뒤, 패널 안 붙임 | 59.0 | 55.9 | 8.1 / 7.2 / 11.0 / 38.2 / 60.5 | 16.1 / 27.5 / 33.2 | (출력에서 빼고 봄) |
| `avd_h_tap` 01:23, 7.2초 | `-gpu host`, 부팅 2.5분 뒤, **패널 붙임**, 제자리 120ms 누름 7회(`input swipe 540 1700 540 1700 120`) | 60.3 | 52.0 | 11.9 / 7.9 / 22.4 / 85.3 / 94.6 | 16.5 / 33.2 / 40.7 | 26.3 |

- 서버가 60fps 로 보내는 것은 다 받습니다. 표시가 48~56fps 인 것은 가상 기기의 디코딩과 합성이 가끔 한 주기를 넘기기 때문으로 보입니다(디코딩 p99 가 38~85ms). 실제 폰의 근거로 쓸 수 없습니다.
- 탭 구간: 누름 7회가 모두 서버로 전달됐고(`pointerSent 14`, 전달 지연 p50 2.6ms), 누르는 동안에도 프레임은 계속 왔습니다(60.3fps). 터치→표시는 n=14, p50 97.8ms, 최대 309ms 로 폰 수치와 비교할 수 없습니다(표본 적음, scrcpy 부하 있음, `adb shell input` 주입).
- 구간마다 조건(부팅 후 경과, 패널 여부, GPU 모드)이 달라서 GPU 모드끼리의 엄밀한 비교는 아닙니다. 정착 뒤 `-gpu host` 가 더 나아 보여 기본값으로 했습니다.

### 실제 폰과 다른 점 (가상 기기로는 확인할 수 없는 것)

- 디코더가 다릅니다(위). 디코딩 시간, 저지연 키(`low-latency`) 동작, 디코더 재생성 시간은 폰에서 다시 봐야 합니다.
- 60Hz 고정입니다. 120Hz, 가변 주사율 전환, `Surface.setFrameRate` 효과(`rr=match`)는 확인할 수 없습니다.
- 접이식 화면, 삼성 One UI, 삼성 인터넷, 제조사 전원/열 관리가 없습니다.
- 무선이 없습니다. 기기의 Wi-Fi 는 가상이고(앱 기록에 `Wi-Fi 2447MHz 1Mbps -50dBm` 로 찍히지만 가짜 값) 모든 통신은 PC 안의 NAT 나 adb 터널입니다. **무선(LAN 직접) 수치, 2.4/5GHz 차이, 방화벽 동작은 잴 수 없습니다.** PC 가 폰으로 거는 방식(앱이 7301 에서 듣기)은 `adb -s emulator-5554 forward tcp:7301 tcp:7301` 로 흉내는 낼 수 있겠지만 방화벽을 거치지 않습니다(시험하지 않음).
- 터치는 `adb shell input` 주입(또는 Orca 패널의 클릭)입니다. 손가락 탭의 표본화, 멀티터치, 누르는 동안의 시스템 동작은 폰과 다릅니다.
- 카메라는 가상 장면(`hw.camera.back = virtualscene`)입니다. 카메라로 QR 찍기는 확인하지 않았습니다. 딥링크(`am start … seeplay-gvr://…`)는 동작합니다.
- 번역(arm64→x86_64)과 2GB RAM, 부팅 직후 Play 서비스 부하 때문에 시간 수치가 흔들립니다.

## 6. 실패한 것과 원인

| 시도 | 결과 | 원인 |
|---|---|---|
| 창 있는 에뮬레이터(`emulator.exe -avd … -gpu swiftshader_indirect`, `-no-window` 없음) | 멈춤: `qemu-system-x86_64.exe` CPU 약 0.9초, 137MB 에서 정지, adb 에 안 나타남. 01:04 에 재현 | 아래 |
| 같은 것을 WMI(`Win32_Process.Create`)로, Orca 환경 변수가 없는 프로세스에서 실행 | 똑같이 멈춤. 로그도 같음 | 셸 환경 문제가 아님 |
| `-no-window` (`qemu-system-x86_64-headless.exe`) | **부팅됨.** swiftshader 와 host 둘 다 | (해결) |
| `orca emulator attach Medium_Phone_API_35` (조정자, 이전) | stopped before it finished booting | Orca 가 창 있는 에뮬레이터를 띄우므로 같은 멈춤으로 보임 |
| 콘솔 `event mouse x y 0 1` 로 인증 창 누르기 | 반응 없음 | 이 이미지는 멀티터치 장치로 입력을 받음. `event send EV_ABS:ABS_MT_…` 는 동작 |
| `tools/env.sh` 의 시험 링크(`s=GVRTEST2`)로 7300 열기 | `FAILED_SERVER 이 QR 은 다른 PC의 것입니다` | 서버가 인증을 하게 됨. 앱의 오동작이 아님(3절) |
| `start-avd.ps1` 첫 판 | 띄우자마자 "qemu 프로세스가 끝났습니다"(종료 코드 4)로 잘못 끝남. 에뮬레이터는 정상 부팅 중이었음 | 스크립트가 qemu 가 뜨기 전에 검사했고, 프로세스 이름도 headless 판을 못 찾았음. 고친 뒤 콜드 스타트 2회 통과 |

창 있는 에뮬레이터가 멈추는 것에 대해 확인한 사실:

- 멈출 때 로그에 `SharedLibrary::open succeeded for [libGLESv2.dll]. File name: [C:\Users\dbrud\AppData\Local\Programs\orca\libGLESv2.dll]` 와 같은 폴더의 `libEGL.dll` 이 찍히고, `-gpu swiftshader_indirect` 인데도 그래픽 어댑터가 `ANGLE (NVIDIA … Direct3D11)` 로 나옵니다. 즉 에뮬레이터의 `lib64\gles_swiftshader\` 가 아니라 Orca(Electron)의 ANGLE 이 들어간 것으로 찍힙니다. 창 없는 실행에서는 같은 이름을 `…\emulator\lib64\gles_swiftshader\libGLESv2.dll` 에서 제대로 불러옵니다.
- **Orca 폴더가 DLL 검색에 들어가는 경로는 밝히지 못했습니다.** 확인해서 배제한 것: 이 셸의 `PATH`(Orca 는 `…\orca\resources\bin` 만 있고 루트는 없음), 사용자/시스템 레지스트리 `PATH`(Orca 없음), 부모에게서 물려받는 `SetDllDirectory` 값(이 셸에서 `GetDllDirectory` 가 빈 값), 작업 폴더(에뮬레이터 폴더로 지정), Orca 환경 변수(WMI 로 띄워도 같음). 확인하지 않은 것: 창을 가진 프로세스에만 끼어드는 전역 훅이나 오버레이, 창 있는 실행 파일의 매니페스트, Orca 를 끈 상태에서의 동작. 멈춘 프로세스에 대해 `tasklist /m libGLESv2.dll` 을 했을 때 qemu 가 목록에 없었으므로, 로그에 찍힌 경로의 모듈이 그 시점에 실제로 올라가 있었는지도 확정하지 못했습니다.
- 따라서 "Orca 의 ANGLE 이 들어가서 멈춘다"는 로그가 가리키는 추정이고, 확정된 것은 "창 있는 실행은 멈추고 창 없는 실행은 된다"입니다. 화면은 Orca 패널(scrcpy)로 볼 수 있어 더 파지 않았습니다.

대체 에뮬레이터(소유자 지시 2)는 필요 없어서 평가하거나 설치하지 않았습니다.

## 7. 앱 워커에게

- 가상 기기에서 앱이 실패한 것은 없습니다. 설치, 딥링크, 인증(기록의 `auth: ok`), H.264 디코딩과 표시, 터치 전달, 기록(`rec_start`/`rec_stop`)이 동작했습니다.
- 처음 실행 때 시스템의 "Viewing full screen" 안내가 화면 위쪽을 덮습니다(안드로이드 기본 동작, 한 번 [Got it] 을 눌러 없앴습니다. AVD 데이터를 지우면 다시 나옵니다).
- 01:14 로그에 `state STANDBY 다른 기기가 보고 있습니다.` 가 한 번 찍혔고 직후 기록에서는 다시 영상이 왔습니다. 원인은 보지 않았습니다(다른 클라이언트가 자리를 가져갔다 돌려준 것일 수 있음). 01:23 기록의 `reconnects 9` 도 원인을 보지 않았습니다.
- 앱 로그 태그는 `GVR` 입니다: `adb -s emulator-5554 logcat -d --pid=<앱 pid> | grep "GVR  "`.

## 8. 이 PC 에서 만들고 바꾼 것 (전부)

설치한 프로그램: **없음.** 내려받은 것: 없음. adb 서버: 다시 띄우지 않음(pid 73232 그대로).

만든 파일:

| 경로 | 내용 |
|---|---|
| `GameViewRemoteApp\docs\04_device_lab.md`, `04_device_lab_app.png` | 이 문서와 스크린샷 |
| `GameViewRemoteApp\tools\device-lab\start-avd.ps1`, `stop-avd.ps1`, `authorize-adb.ps1` | 시작, 종료, 인증 창 누르기 |
| `%TEMP%\gvr-device-lab\emulator-*.log` | 에뮬레이터 로그(지워도 됨) |
| `C:\Users\dbrud\AppData\Local\Temp\claude\9054e148b7aa1b30\f0e73cd6-69a0-460f-bddf-655267cd451e\scratchpad\` | 시험 스크립트, 부팅 로그 `boot_*.log`, 화면 `shots\*.png`, 앱 기록 `rec\rec-*.json` (지워도 됨) |

AVD `Medium_Phone_API_35` 에 생긴 변화(데이터를 지우지 않았고 `config.ini` 도 고치지 않음):

- 잠금 `hardware-qemu.ini.lock`, `multiinstance.lock` 삭제(죽은 인스턴스의 것, 실행할 때마다 다시 생김).
- 기기 안: 이 PC 의 adb 키를 "항상 허용"으로 등록. 앱 `kr.co.seeplay.gvr` 0.1.0(versionCode 1) 설치. "Viewing full screen" 안내 닫음. 앱 기록 파일 `/sdcard/Android/data/kr.co.seeplay.gvr/files/rec/` 4개. Orca 가 올린 `/data/local/tmp/scrcpy-server.jar`.
- `emu-launch-params.txt` 등 에뮬레이터가 실행할 때 스스로 쓰는 파일은 갱신됨.

끝낸 프로세스: 제가 시험하다 멈춘 qemu/emulator(01:04~01:06, 2번)와 검증용 재시작 때의 것. 작업을 시작했을 때 떠 있던 qemu 는 없었습니다.

실제 Unity 서버(7300)에 한 일: 앱을 `take=1` 로 4번 짧게 연결(01:13~01:23, 그때 보고 있던 클라이언트가 있었다면 자리를 가져옴), 게임 화면(게임 오버 화면)에 제자리 누름 7회 주입(기기 좌표 540,1700). Unity 설정, 코드, `secret.json` 은 읽기만 했고 토큰 값은 어디에도 적지 않았습니다. **앱은 지금도 7300 에 붙어 재생 중입니다**(떼려면 `adb -s emulator-5554 shell am force-stop kr.co.seeplay.gvr`).

지금 상태(01:30): 가상 기기 실행 중(`-gpu host`), adb `device`, reverse `tcp:7300`, `tcp:7311`, Orca 패널 attach 됨, 앱 설치되어 있고 앞에 떠 있음.
