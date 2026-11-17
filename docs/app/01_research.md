# 01. 조사 (Game View Remote 안드로이드 앱 시제품)

- 작성: 2026-11-13 23:20경 (1단계 워커). 이 단계에서 쓴 파일은 `docs/01_research.md`, `docs/02_PRD.md` 두 개뿐입니다.
- 입력: `docs/00_BRIEF.md`, `docs/00_current_system_and_history.md`(이하 "00 문서"), 조사 보고서 `gvr-streaming-research.md`.
- 이 문서는 브리프 3장의 질문에 근거와 함께 답합니다. 결정 자체는 `docs/02_PRD.md` 에 요구사항으로 옮겼습니다.

## 읽기 전에: 표시 규칙

| 표시 | 뜻 |
|---|---|
| **[실측]** | 이 문서를 쓰며 이 PC/폰에서 직접 실행해 본 것. 실행한 것의 목록은 [11장](#11-이-단계에서-실행한-것)에 있습니다 |
| **[문서]** | 1차 자료를 내려받아 읽은 것. URL을 붙였습니다. 따옴표 안은 원문입니다 |
| **[검색]** | 검색 결과 요약으로만 본 것. 본문을 직접 읽지 않았으므로 약한 근거입니다 |
| **[00]** | 00 문서에 적힌 것(그 문서의 근거 종류를 따릅니다) |
| **[추론]** | 위 근거에서 끌어낸 판단. 틀릴 수 있고, 어디서 확인할지를 같이 적었습니다 |
| **[미확인]** | 확인하지 못한 것 |

## 0. 한눈에 보는 결론

| # | 질문 | 결론 | 근거 수준 |
|---|---|---|---|
| 1 | 방화벽 | 이 PC에서는 폰 → Unity.exe 로 들어오는 연결이 **지금 막혀 있습니다**(직접 시도해 시간 초과). 원인은 네트워크가 공용 프로필이고 `Unity 2022.3.62f2 Editor` 차단 규칙이 있기 때문입니다. 차단 규칙은 허용 규칙보다 우선하므로 **관리자 권한으로 한 번 고치는 것 외에는 Unity.exe 가 직접 받을 방법이 없습니다.** 이 PC의 계정은 관리자 그룹이라 UAC "예" 한 번이면 됩니다(비밀번호 불필요) | 실측 + 문서 |
| 2 | 방화벽을 안 건드리는 대안 | 연결 방향을 뒤집으면(앱이 듣고 PC가 건다) 관리자 권한이 필요 없습니다. PC → 폰 방향 TCP는 이 LAN에서 이미 됩니다(무선 adb 가 그 방향). 다만 **앱 설치를 QR로 하는 것은 폰 → PC 접속이 반드시 필요**하므로 대안이 설치까지 대신하지는 못합니다 | 실측 + 추론 |
| 3 | QR 하나로 설치와 연결 | 됩니다. QR에는 `http://PC주소:포트/...` 주소 하나를 담고, 서버의 착륙 페이지가 "앱이 있으면 앱 열기, 없으면 APK 받기"를 처리합니다. 앱 안 스캐너는 같은 QR을 브라우저 없이 바로 해석합니다 | 문서 + 추론(삼성 인터넷 동작은 미확인) |
| 4 | 서명 안 된 사내 앱의 딥링크 | App Links(검증된 https)는 LAN 주소에서 불가능. **커스텀 스킴 + `intent://` 주소**가 동작하는 방식이고, 브라우저에서 사용자 탭 1번이 필요합니다 | 문서 |
| 5 | QR 생성 | MIT 라이선스 인코더 소스를 도구 폴더에 넣어 쓰는 것을 권장(ZXing.Net DLL은 다른 프로젝트의 같은 DLL과 충돌 위험, 직접 구현은 검증 부담) | 문서 + 추론 |
| 6 | 앱 안 스캐너 | CameraX + ML Kit 번들 모델 권장(오프라인, 약 2.4MB). 기본 카메라 경로도 같이 지원 | 문서 |
| 7 | 빌드 | 됩니다. Unity 6000.2.10f1 에 들어 있는 JDK 17 + Gradle 8.13 + SDK(36)와 AGP 8.13.0 으로 빈 앱을 실제로 빌드했습니다(61초, v2 서명). AGP는 처음 한 번 내려받아야 합니다 | 실측 |
| 8 | 전송 | 시제품은 지금 프로토콜(WebSocket/TCP + H.264 AVCC)을 그대로 쓰고 앱에서 Annex B로 바꿉니다. UDP 경로는 LAN 직접 실측을 본 뒤 정합니다 | 문서 + 00 + 추론 |
| 9 | 폰 | Android 16(API 36), One UI 8.5, Snapdragon 8 Gen 2. `c2.qti.avc.decoder.low_latency` 디코더가 있습니다. Wi-Fi는 지금 2.4GHz(채널 1) | 실측 |

---

## 1. 이 PC와 폰의 사실 (실측)

### 1.1 폰

| 항목 | 값 | 근거 |
|---|---|---|
| 모델 / Android / API | SM-F731N / 16 / 36 | [실측] `getprop` |
| One UI / 보안 패치 / 빌드 | 8.5 (`ro.build.version.oneui=80500`) / 2026-09-05 / BP4A.251205.006.F731NKSS6GZI1 | [실측] |
| SoC / ABI | SM8550(kalama) / arm64-v8a, armeabi-v7a | [실측] |
| H.264 디코더 | `c2.qti.avc.decoder`, **`c2.qti.avc.decoder.low_latency`**, `.secure` | [실측] `dumpsys media.player` |
| 디스플레이 모드 | 10 / 24 / 30 / 48 / 60 / 96 / 120fps 모드가 있음. `refresh_rate_mode=0` | [실측]. 0이 "60Hz 고정"을 뜻하는지는 [미확인] |
| Wi-Fi | SSID가 PC 유선망과 같은 공유기, **2412MHz(2.4GHz 채널 1), 802.11n**, RSSI −45, 링크 속도 송신 104 / 수신 58Mbps, `wlan0` 192.168.25.60/24 | [실측] `dumpsys wifi` |
| 기본 브라우저 | **삼성 인터넷**(`com.sec.android.app.sbrowser` 31.0.0.81). Chrome 157.0.8103.78 도 설치됨 | [실측] `resolve-activity`, `dumpsys package` |
| QR 관련 앱 | 삼성 카메라(`com.sec.android.app.camera`), `com.sec.android.QRreader`, Google Play 서비스 26.43.30 | [실측] `pm list packages` |
| "출처를 알 수 없는 앱 설치" | 삼성 인터넷, Chrome, 내 파일 모두 `REQUEST_INSTALL_PACKAGES: allow`(이미 허용돼 있음) | [실측] `appops get` |
| Play 프로텍트 | `package_verifier_user_consent=1`, `upload_apk_enable=1`(켜져 있는 것으로 보임) | [실측], 해석은 [추론] |
| 삼성 "보안 위험 자동 차단"(Auto Blocker) | `rampart_strict_protection_switch_enabled=0` 까지만 읽힘. **켜져 있는지 여부는 설정 키로 판별하지 못했습니다** | [미확인] |
| adb 연결 | 같은 폰이 USB(`R3CW70LJ2VP`)와 무선(`192.168.25.60:5555`)으로 함께 잡힘. 포트 7300 reverse 는 `UsbFfs`(USB) | [실측] |

- 이 폰에서는 브라우저의 "출처 허용"이 이미 켜져 있어 설치 단계 수가 다른 폰보다 적게 나옵니다. **새 폰에서의 단계 수는 4.3절의 "처음" 열을 봐야 합니다.** [추론]

### 1.2 PC 네트워크

| 항목 | 값 | 근거 |
|---|---|---|
| OS | Windows 11 Home 10.0.26200 | [실측] |
| 이더넷(Realtek GbE) | **Up, 100Mbps, 192.168.25.5/24, 게이트웨이 192.168.25.1**(인터넷 경로) | [실측] `Get-NetAdapter`, `Find-NetRoute` |
| Wi-Fi(Realtek RTL8852BE) | 끊김(소프트웨어 라디오 꺼짐). 169.254.x 주소만 | [실측] |
| vEthernet (WSL (Hyper-V firewall)) | Up, 가상, 172.30.224.1/20, 게이트웨이 없음 | [실측] |
| 그 밖 | Bluetooth PAN(없음), "로컬 영역 연결* 8/11"(169.254.x, Tentative) | [실측] |
| 네트워크 프로필 | 이더넷이 **Public(공용)** | [실측] `Get-NetConnectionProfile` |

- PC는 유선(100Mbps), 폰만 무선입니다. 무선 구간은 폰 ↔ 공유기 한 홉입니다. [실측]
- 지금 도구 창은 Up 상태인 모든 어댑터의 IPv4를 나열합니다 [00]. 이 PC에서는 `192.168.25.5` 와 `172.30.224.1`(WSL 가상) 두 개가 나오고, 뒤의 것은 폰에서 닿지 않습니다. [실측 + 추론]
- PC → 폰 방향 TCP는 이 LAN에서 됩니다: `adb devices` 에 `192.168.25.60:5555 device`(PC가 폰의 5555로 건 연결). 공유기가 유선 ↔ 무선 사이를 막지 않는다는 뜻입니다. [실측]

### 1.3 Windows 방화벽

| 항목 | 값 | 근거 |
|---|---|---|
| 프로필 상태 | Domain / Private / Public 모두 켜짐. 현재 프로필 Public, 기본 정책 `BlockInbound,AllowOutbound`, 알림 켜짐 | [실측] `netsh advfirewall show currentprofile`, `HNetCfg.FwPolicy2` |
| `Unity 2022.3.62f2 Editor` 규칙 | 인바운드 **Block / Public** / 모든 프로토콜·포트, 인바운드 Allow / Domain. **Private 용 규칙은 없음** | [실측] |
| 다른 Unity 버전 | 2022.3.50f1, 6000.2.10f1, 6000.4.5f1 모두 같은 모양(Domain 허용 + Public 차단) | [실측] |
| 이미 Public 인바운드가 허용된 프로그램 | `C:\Program Files\nodejs\node.exe`(TCP, UDP), 여러 `adb.exe`, MariaDB | [실측] |
| OS의 mDNS 수신 | `mDNS(Udp-in)` Allow, Public, UDP 5353, 원격 LocalSubnet, `svchost.exe` | [실측] |
| 브로드캐스트에 대한 유니캐스트 응답 | 꺼져 있지 않음(`UnicastResponsesToMulticastBroadcastDisabled=False`) | [실측] |
| 이 프로세스의 권한 | 관리자 아님(중간 무결성). 계정은 `BUILTIN\Administrators` 소속이지만 "거부 전용"(= UAC로 가려진 관리자 계정) | [실측] `whoami /groups` |
| UAC | 켜짐, `ConsentPromptBehaviorAdmin=5`(관리자 계정은 "예/아니요" 동의 창, 비밀번호 없음) | [실측] 레지스트리 |
| **폰 → PC 실제 도달** | 폰 셸에서 `nc -w 3 192.168.25.5 55504`(Unity.exe 가 `0.0.0.0:55504` 에서 듣는 포트) → **`nc: Timeout`**. 폰 → PC `ping` 3회 100% 손실 | [실측] |

- Unity.exe 는 지금 `0.0.0.0` 의 34999 / 38000 / 38443 / 55504 와 `127.0.0.1:7300` 에서 듣고 있습니다. 7300은 루프백 전용이라 직접 시험할 수 없어 55504 로 한 번 시도했습니다(연결이 성립하지 않았으므로 Unity 쪽에는 아무것도 도달하지 않았습니다). [실측]
- 관리자가 아닌 프로세스가 **읽을 수 있는 것**: 프로필, 기본 정책, 프로그램별 규칙(COM `HNetCfg.FwPolicy2`, 186ms). `Get-NetFirewallRule` + 필터 조회는 같은 내용을 주지만 2분 넘게 걸렸습니다. 즉 **도구가 "지금 막혀 있다"를 스스로 판정해 창에 표시할 수 있습니다.** [실측]
- 관리자가 아닌 프로세스가 **할 수 없는 것**: 규칙 추가/삭제/끄기, 네트워크 프로필 변경. 이것은 이 PC에서 시도하지 않았습니다(성공하면 시스템을 바꾸게 되므로). 근거는 아래 Microsoft 문서입니다. [문서], 시도는 [미확인]
- 옛 API `HNetCfg.FwMgr.IsPortAllowed` 는 이 PC에서 "Value does not fall within the expected range" 로 실패했습니다. 판정에는 `FwPolicy2.Rules` 를 직접 읽는 쪽을 써야 합니다. [실측]

### 1.4 빌드 도구

| 위치 | JDK | Gradle | build-tools | platforms | 그 밖 | 근거 |
|---|---|---|---|---|---|---|
| Unity **2022.3.62f2** `…\PlaybackEngines\AndroidPlayer` | Temurin 11.0.14.1 (`OpenJDK`) | 7.5.1 (`Tools\gradle\lib`) | 34.0.0 | 34, 35, 36 | NDK 23.1.7779620, platform-tools 32.0.0(adb 1.0.41), cmdline-tools 6.0, bundletool 1.17.2. SDK에 `licenses` 폴더 없음 | [실측] |
| Unity 2022.3.50f1 | 11.0.14.1 | 7.5.1 | 34.0.0 | 33, 34, 35 | | [실측] |
| Unity **6000.2.10f1** | **17.0.9** | **8.13** | **36.0.0** | 34, 35, 36 | `Tools\gradle` 에는 `lib` 만 있고 `bin\gradle.bat` 이 없음 | [실측] |
| Unity 6000.4.5f1 | 17.0.9 | 9.1.0 | 36.0.0 | 34, 35, 36 | | [실측] |
| Android Studio `C:\Program Files\Android\Android Studio` | JBR 21.0.3 | (없음, 래퍼가 받음) | | | 빌드 번호 242.23339.11.2421.12700392 | [실측] |
| 사용자 SDK `%LOCALAPPDATA%\Android\Sdk` | | | 34.0.0, 35.0.1 | 34 | platform-tools 35.0.2, `licenses\android-sdk-license` 있음, 에뮬레이터, system-images android-35. NDK/cmdline-tools 없음 | [실측] |
| `%USERPROFILE%\.gradle` | | 래퍼 배포본 gradle-7.5.1-bin | | | 캐시에 AGP 7.1.2 만 있었음(2025-08 마지막 기록). Kotlin 플러그인, CameraX, ML Kit, ZXing 없음 | [실측] |
| `%USERPROFILE%\.android\debug.keystore` | | | | | 있음(2025-02-27 생성, 2105바이트) | [실측] |
| 환경 변수 | `JAVA_HOME` = Unity **2022.3.50f1** 의 OpenJDK. `ANDROID_HOME`, `ANDROID_SDK_ROOT` 없음. PATH에 java/gradle/adb 없음 | | | | | [실측] |

**실제로 빌드해 본 결과** (빈 Activity 하나, Java, 의존성 없음, 스크래치 폴더):

| 시도 | 구성 | 결과 | 근거 |
|---|---|---|---|
| A. 오프라인, 2022.3.62f2 묶음 | JDK 11 + Gradle 7.5.1 + AGP 7.1.2(캐시) + compileSdk 34, `--offline` | **실패**: `No cached version of com.android.tools.build:gradle:7.1.2 available for offline mode` (13초) | [실측] |
| B. 온라인, 6000.2.10f1 묶음 | JDK 17.0.9 + Gradle 8.13(런처 jar 직접 실행) + **AGP 8.13.0**(내려받음) + compileSdk 36 + build-tools 36.0.0 + minSdk 26 + targetSdk 36 | **성공**: `BUILD SUCCESSFUL in 1m`, `app-debug.apk` 6,269바이트. `apksigner verify`: v2 서명 true, 서명자 `CN=Android Debug`(= `~\.android\debug.keystore`) | [실측] |
| C. B를 지운 뒤 `--offline` 재빌드 | 같은 구성 | **성공**(18초, APK 생성) | [실측] |

- 결론: **이 PC의 도구만으로 APK를 빌드할 수 있습니다. 단, AGP와 그 의존성(이번에 약 100MB)을 처음 한 번 내려받아야 합니다.** 한 번 받은 뒤에는 오프라인으로 다시 빌드됩니다. Android Studio 는 필요 없습니다(있긴 합니다). [실측]
- B의 실행 방법(Unity 6000.2.10f1 에는 `gradle.bat` 이 없음): `JAVA_HOME=<…6000.2.10f1…\OpenJDK>` 로 두고 `java -cp <…\Tools\gradle\lib\gradle-launcher-8.13.jar> org.gradle.launcher.GradleMain -p <프로젝트> assembleDebug`. `local.properties` 의 `sdk.dir` 은 같은 Unity의 `SDK`. 앱 프로젝트에 Gradle 래퍼를 넣으면 래퍼가 Gradle 8.13 배포본(별도 내려받기)을 받아 같은 일을 합니다. [실측], 래퍼는 [추론]
- SDK가 `Program Files` 아래라 쓰기 불가여서 `Exception while marshalling …\package.xml. Probably the SDK is read-only` 경고가 나오지만 빌드는 됩니다. [실측]
- Kotlin, AndroidX, CameraX, ML Kit 를 쓰면 그만큼 더 내려받습니다(이번 시험은 Java만). [추론]
- Unity 2022.3.38f1 이상은 기본으로 Gradle 7.5.1 + AGP 7.4.2 를 씁니다 [문서: https://docs.unity3d.com/2022.3/Documentation/Manual/android-gradle-overview.html ]. AGP 7.4 는 JDK 11, Gradle 7.5 이상 [문서: https://developer.android.com/build/releases/past-releases/agp-7-4-0-release-notes ]. 2022.3.62f2 묶음(JDK 11)으로는 AGP 8.x 를 못 씁니다(AGP 8 계열의 JDK 17 요구는 이번 B 구성이 JDK 17에서 돈 것으로만 확인했고, 공식 표의 8.13 행은 읽지 못했습니다 [미확인]). 그래서 **앱 빌드는 6000.2.10f1 묶음(또는 Android Studio JBR 21)을 권장**합니다. [실측 + 추론]

---

## 2. Windows 방화벽: 어떻게 넘는가

### 2.1 왜 막혀 있는가

- Microsoft 문서의 규칙 우선순위: "Explicitly defined allow rules take precedence over the default block setting." / "**Explicit block rules take precedence over any conflicting allow rules.**" [문서: https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules ]
- 규칙이 없는 프로그램이 처음 들을 때: "a dialog box prompts the user to either allow or block an application's packets the first time the app is launched or tries to communicate in the network." / "If the user has admin permissions, they're prompted. If they respond *No* or cancel the prompt, block rules are created." / "If the user isn't a local admin and they are prompted, block rules are created. It doesn't matter what option is selected." / "once the rules are added, they must be deleted to generate the prompt again." [같은 문서]
- 이 PC의 `Unity … Editor` Public 차단 규칙은 위 동작(예전에 뜬 창을 취소했거나 공용 칸을 체크하지 않음)으로 생긴 것으로 보입니다. [추론]
- 공용 프로필: "It's the default profile for unidentified networks." 개인 프로필: "It can be set manually on a network interface by an administrator." [문서: https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/ ]

따라서:

1. 허용 규칙만 추가해서는 안 됩니다. **차단 규칙을 끄거나 지워야** 합니다(차단이 우선).
2. 차단 규칙이 이미 있으므로 Windows 알림 창은 다시 뜨지 않습니다.
3. 네트워크를 개인 프로필로 바꾸는 것도 관리자 작업이고, 바꿔도 개인 프로필용 허용 규칙이 없어 기본 차단입니다(1.3절 표).

### 2.2 가능한 방법 비교

| 방법 | 관리자 권한 | 사용자가 하는 일 | 설치 QR 가능 | 평가 |
|---|---|---|---|---|
| **A. 도구의 "방화벽 허용" 버튼** → 승격된 PowerShell 한 번 실행: 이 Unity.exe 의 인바운드 차단 규칙을 끄고, `프로그램 = 이 Unity.exe, TCP, 로컬 포트 = 도구 포트, 원격 = LocalSubnet, 프로필 = 전체` 허용 규칙을 추가 | **한 번**(UAC "예" 1번. 표준 사용자 계정이면 관리자 비밀번호) | 버튼 1번 + UAC 1번 | 예 | **권장.** Unity 버전(실행 파일 경로)마다 한 번. 명령을 직접 치지 않음 |
| B. 연결 방향 뒤집기: 앱이 듣고 Unity가 폰으로 건다(아웃바운드는 기본 허용) | **없음** | 없음 | **아니요**(앱이 설치돼 있어야 함) | 대안. PC가 폰 주소를 알아내는 방법이 필요(2.4절). 이 단계에서 끝까지 확인하지 못함 |
| C. 무선 adb 터널(지금 있는 방식) | 없음 | USB 한 번 또는 무선 디버깅 페어링 | 아니요 | 지금도 됨. 그러나 실측 8.3Mbps, 100~290ms 정체 [00]. LAN 직접이 아님 → 요구사항 11의 목표 경로가 아님 |
| D. USB adb reverse | 없음 | 케이블 | 아니요(대신 `adb install`) | 대체 수단으로 유지 |
| E. 다른 실행 파일(중계 프로세스)이 대신 듣기 | 그 실행 파일에 규칙이 없으면 똑같이 한 번 필요 | — | — | 이득 없음. 단, 이 PC에는 `node.exe` 가 이미 Public 허용이라 **시험용**으로는 쓸 수 있음(2.5절) |
| F. 네트워크를 개인 프로필로 변경 | 필요 | 설정 변경 | — | 이것만으로는 안 됨(2.1의 3) |
| G. 외부 중계 서버 / 인터넷 | 없음 | — | 가능 | 범위 밖(인터넷 필요, 지연 증가, 화면이 밖으로 나감) |

- A의 명령 형태(문서의 예): `New-NetFirewallRule -DisplayName "…" -Direction Inbound -Program "…" -RemoteAddress LocalSubnet -Action Allow` [문서: https://learn.microsoft.com/en-us/powershell/module/netsecurity/new-netfirewallrule , https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/configure-with-command-line ]. 이 문서들은 "관리자 권한이 필요하다"를 명시하지 않았고, 근거는 위 rules 문서의 "require user interaction and administrative privilege" 와 일반 동작입니다. **이 PC에서 승격 없이 규칙 추가를 시도해 보지는 않았습니다.** [문서 + 미확인]
- Unity 자신도 승격이 필요할 때 같은 방식을 씁니다: `…\AndroidPlayer\Tools\RunElevatedCommand.ps1` 이 `Start-Process PowerShell.exe -Verb RunAs` 로 다시 실행합니다. [실측: 파일 내용]
- **워커는 A를 끝까지 시험할 수 없습니다.** UAC 동의 창은 사람이 눌러야 합니다. 소유자가 한 번 눌러 줘야 합니다(PRD 맨 위 "소유자가 정할 것" 1번).
- A를 적용한 뒤의 상태는 도구가 `FwPolicy2` 로 다시 읽어 확인할 수 있고, USB로 폰이 붙어 있으면 `adb shell nc` 로 폰에서 실제 도달까지 확인할 수 있습니다(1.3절에서 쓴 방법). [실측한 방법의 재사용]

### 2.3 정직한 한계

- **Unity.exe 가 LAN에서 직접 받게 하려면 이 PC에서는 관리자 동의 1번이 불가피합니다.** 관리자 권한 없이, 클릭 없이 넘는 방법은 찾지 못했습니다. [실측 + 문서]
- 그리고 **QR로 앱을 설치하는 흐름은 폰이 PC에서 APK를 받아야 하므로 A가 전제**입니다. A 없이 설치하려면 USB(`adb install`, 도구 버튼 1번)를 써야 합니다. [추론]
- 표준 사용자 계정(관리자 비밀번호를 모르는 사내 PC)에서는 A가 막힙니다. 그 경우의 길은 B 또는 USB입니다. [추론]

### 2.4 B(방향 뒤집기)를 쓰려면 무엇이 더 필요한가

앱이 TCP 포트를 열어 듣고, Unity가 그 주소로 겁니다. 문제는 **PC가 폰의 주소를 아는 방법**입니다(폰이 PC에게 먼저 말을 걸 수 없으므로).

| 폰 주소를 아는 방법 | 관리자 | 상태 |
|---|---|---|
| OS의 DNS-SD 탐색 API `DnsServiceBrowse`(dnsapi.dll, Windows 10 이상, "Used to initiate a DNS-SD discovery for services running on the local network.") + 앱이 `NsdManager.registerService` 로 서비스 등록 | 없음 | 수신은 Unity.exe 가 아니라 `svchost.exe` 가 하고, 이 PC에는 그 허용 규칙(Public, UDP 5353, LocalSubnet)이 있습니다 [실측]. Unity(Mono)에서 P/Invoke 로 부르는 것은 **시험하지 않았습니다** [미확인]. [문서: https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsservicebrowse , https://developer.android.com/develop/connectivity/wifi/use-nsd ] |
| Unity가 UDP 브로드캐스트로 "여기 있음"을 보내고 앱이 그 출발 포트로 유니캐스트 응답 | 없음 | "If a computer sends a broadcast packet, a unicast response is allowed for three seconds." [문서: https://learn.microsoft.com/en-us/windows/win32/api/netfw/nf-netfw-inetfwpolicy2-get_unicastresponsestomulticastbroadcastdisabled ]. 이 PC에서 그 기능은 켜져 있습니다 [실측]. 그러나 **Unity.exe 의 명시적 차단 규칙이 이 응답까지 막는지는 모릅니다** [미확인] |
| 같은 /24 대역의 주소 전부에 앱 포트로 TCP 연결 시도(아웃바운드) | 없음 | 구현은 가장 단순. 큰 사내망에서는 포트 스캔으로 보일 수 있음 [추론] |
| USB로 한 번 붙었을 때 adb로 폰의 `wlan0` 주소를 읽어 기억(지금 도구가 무선 전환 때 이미 하는 일 [00]) | 없음 | "USB 한 번 → 이후 무선". QR은 아님 |

- B에서 QR의 역할: 주소가 아니라 **토큰**을 옮깁니다. 앱이 QR을 읽어 토큰을 얻고 → 서비스 등록에 토큰의 해시(세션 표시)를 싣고 → Unity가 그 표시를 가진 폰에 걸고 → 토큰으로 서로 확인합니다. 사용자 눈에는 "QR 한 번 찍으면 연결"로 같습니다. [추론]
- B의 Android 쪽 조건: 대상 SDK 36 이하에서는 로컬 네트워크 접근(들어오는 TCP 수락 포함)이 `INTERNET` 권한으로 암묵 허용됩니다(5.4절). [문서]
- **이 단계의 결론**: B는 "될 가능성이 높지만 이 PC에서 Unity.exe 로 확인한 부분이 없는" 방식입니다. PRD에서는 프로토콜을 **어느 쪽이 걸든 같은 핸드셰이크**가 되게 요구하고, B의 가능성 확인(작은 실험)을 시제품에 넣고, 본 구현은 그 결과를 보고 정하도록 했습니다.

### 2.5 시험에만 쓸 수 있는 지름길 (이 PC 한정)

`node.exe` 는 이 PC에서 Public 인바운드 TCP/UDP 가 이미 허용돼 있습니다 [실측]. Node 로 만든 **TCP 중계**(예: `0.0.0.0:7310` → `127.0.0.1:<Unity 시험 포트>`)나 **재생 서버**(녹화한 H.264 스트림을 프로토콜 그대로 보냄)를 띄우면 방화벽을 건드리지 않고, Unity 코드를 고치지 않고도 **LAN 직접 경로의 수치를 잴 수 있습니다.** 소유자의 UAC 동의를 기다리는 동안 5단계가 무선 측정을 시작할 수 있는 길입니다. 제품 기능은 아닙니다(다른 PC에는 이 규칙이 없음). 이 단계에서는 띄워 보지 않았습니다. [실측(규칙) + 추론(쓰임), 동작은 미확인]

- `python.exe` 는 규칙이 없습니다. Python 으로 `0.0.0.0` 에서 들으면 Windows 알림 창이 소유자 화면에 뜨고, 취소되면 차단 규칙이 생깁니다. **시험 서버를 Python 으로 LAN에 열지 마십시오.** [실측(규칙 없음) + 문서]

---

## 3. QR 연결

### 3.1 딥링크 방식: 서명되지 않은(검증 도메인이 없는) 사내 앱에서 무엇이 동작하는가

| 방식 | 동작 여부 | 근거 |
|---|---|---|
| **Android App Links**(검증된 http(s) 링크가 앱을 바로 엶) | **불가.** 검증은 "Android queries the corresponding websites for the Digital Asset Links file at `https://<hostname>/.well-known/assetlinks.json`" 로 이뤄집니다. LAN의 `http://192.168.x.x:포트` 는 https 호스트가 아니므로 검증할 수 없습니다(문서는 https만 설명) | [문서: https://developer.android.com/training/app-links/verify-android-applinks ], 불가 판단은 [추론] |
| **커스텀 스킴**(`gvr://…`) | 동작. "Deep linking is a general system capability of Android, supported on all versions, on all devices." 인텐트 필터에 `ACTION_VIEW`, `BROWSABLE`("It is required in order for the intent filter to be accessible from a web browser."), `DEFAULT` 가 필요. 다른 앱이 같은 스킴을 등록하면 선택 창이 뜰 수 있음 | [문서: https://developer.android.com/training/app-links/deep-linking ] |
| **`intent://` 주소**(브라우저 → 앱) | 동작(Chrome). 형식 `intent://HOST/PATH#Intent;scheme=…;package=…;S.browser_fallback_url=<인코딩된 주소>;end`. "When an intent isn't resolved or an external application doesn't launch, the user can be redirected to the fallback URL." | [문서: https://developer.chrome.com/docs/android/intents ] |
| 페이지가 열리자마자 자동으로 앱 실행 | **불가.** Chrome은 "The Intent URI is initiated without user gesture." 인 경우와 "A JavaScript timer tried to open an application without a user gesture" 인 경우 실행하지 않습니다. → 착륙 페이지에 **버튼 1개(탭 1번)** 가 필요 | [같은 문서] |
| 삼성 인터넷(이 폰의 기본 브라우저)에서 `intent://` + 대체 주소 | Chromium 기반이라 같을 것으로 예상하지만 **1차 자료를 찾지 못했습니다.** 구현 단계에서 `adb shell am start -a android.intent.action.VIEW -d <주소>` 로 열고 버튼을 눌러 확인해야 합니다 | [미확인] |
| QR에 커스텀 스킴을 직접 담아 기본 카메라로 찍기 | 삼성 카메라가 http(s) 가 아닌 주소를 링크로 다루는지 **확인하지 못했습니다.** 검색으로는 결론이 없었고, 주소에 공백이 있으면 링크로 안 열고 글자로 보여 준다는 사용자 보고가 있습니다 | [검색: https://eu.community.samsung.com/t5/mobile-apps-services/samsung-camera-qr-code-scanner-doesn-t-open-links-with-spaces/m-p/11870193 ], [미확인] |

결론: **QR에는 평범한 `http://` 주소를 담습니다.** 어떤 카메라 앱이든 http 주소는 링크로 엽니다. 앱으로 넘어가는 일은 착륙 페이지의 버튼(`intent://` + 대체 주소)이 하고, 앱 안 스캐너는 같은 주소를 직접 해석합니다. [추론]

### 3.2 QR 하나로 설치와 연결을 합칠 수 있는가

됩니다.

```
QR = http://<PC 주소>:<포트>/c?<버전, 서버 표시, 토큰, 다른 주소 후보>

기본 카메라로 찍음 → 브라우저가 착륙 페이지를 엶
   └ 버튼 [앱으로 열기]  = intent://c?...#Intent;scheme=gvr;package=<앱>;
                            S.browser_fallback_url=<같은 서버의 설치 안내 주소>;end
        ├ 앱이 있으면: 앱이 열리고 주소의 값으로 바로 연결
        └ 앱이 없으면: 브라우저가 대체 주소(설치 안내)로 이동 → [APK 받기]
앱 안 스캐너로 찍음 → 앱이 주소를 직접 해석 → 바로 연결 (브라우저 없음)
```

- 서버(Unity 도구)가 새로 내줘야 하는 것: 착륙 페이지, APK 파일(`Content-Type: application/vnd.android.package-archive`), (선택) 앱 버전 정보. 지금 서버는 `GET /` 의 페이지 외에 파일을 내주는 기능이 없습니다 [00 1.6]. [추론]
- 서버는 지금 **Play 중에만** 돕니다 [00 1.1]. 설치는 Play 전에 하고 싶은 일이므로, Start 를 누르면 Play 여부와 무관하게 HTTP(착륙 페이지, APK, 연결 대기)가 열려야 요구사항 5("Start 를 누르면 서버가 열리고 QR 이 생성")에 맞습니다. [추론, PRD 요구사항으로 옮김]

### 3.3 QR에 무엇을 담는가

| 값 | 왜 | 비고 |
|---|---|---|
| 형식 버전 | 앱과 도구의 버전이 어긋났을 때 알 수 있게 | QR 형식 버전과 전송 프로토콜 버전은 별개. 전송 프로토콜 버전은 접속 후 `hello` 로 확인 [00 1.5] |
| 주 주소 + 포트 | 주소 자체가 QR의 호스트 부분 | 3.5절의 규칙으로 고른 1개 |
| 다른 주소 후보 | 어댑터가 여러 개일 때 주 주소가 틀렸을 경우 앱이 병렬로 시도 | 브라우저는 주 주소만 쓸 수 있으므로 주 주소 선택이 중요 |
| 서버 표시(임의 값) | 앱이 "기억해 둔 그 서버"인지 알아보고 자동 재접속, mDNS 광고와 대조 | PC/프로젝트별로 한 번 생성해 저장 |
| 토큰 | 인증(6장) | 128비트 이상 무작위, base64url 22자 |
| (선택) 표시 이름 | 앱 화면에 "어느 PC/프로젝트"인지 | QR 길이가 늘어나므로 접속 후 받아도 됨 |

- 길이: `http://192.168.25.5:7300/c?v=1&s=8자&t=22자&a=172.30.224.1` 형태로 약 75자입니다. QR 버전 6(41×41 모듈), 오류 정정 M 이면 바이트 모드 106자까지 담습니다. 버전 5(37×37)는 84자입니다. **이 용량 수치는 표준 표의 값을 기억으로 적은 것이고, 이번에 출처 페이지에서 표를 읽어 오지 못했습니다**(https://www.qrcode.com/en/about/version.html 는 "Version 1 (21 × 21 modules) up to Version 40 (177 × 177 modules)" 까지만 읽힘). 구현 단계에서 인코더가 고른 버전으로 확인됩니다. [문서(일부) + 미확인]
- 주소에 공백이나 특수 문자를 넣지 않습니다(위 삼성 카메라 보고). [검색]

### 3.4 앱 안 스캐너 대 기본 카메라

| | 기본 카메라 → 브라우저 → 앱 | 앱 안 스캐너 |
|---|---|---|
| 앱이 없어도 됨 | 예(설치 경로) | 아니요 |
| 연결까지 탭 수(앱 설치 후) | 카메라 링크 1 + 페이지 버튼 1 = **2** (브라우저의 "앱에서 열기" 확인이 있으면 +1, 미확인) | 앱 아이콘 1 = **1** (처음 한 번 카메라 권한 +1) |
| 방화벽 A 필요 | **예**(페이지를 PC에서 받아야 함) | A 또는 B |
| 브라우저 탭이 남음 | 예 | 아니요 |
| 워커가 adb로 검증 가능 | 예(같은 주소를 `am start` 로 열기) | 스캔 자체는 사람 손이 필요. 해석 → 연결 경로는 같은 주소를 인텐트로 넣어 검증 |

권장: **둘 다 지원하고, 평소 연결의 기본은 앱 안 스캐너 + 자동 재접속**입니다. 기본 카메라 경로는 설치와 "앱이 있는지 모를 때"의 입구입니다. 같은 QR을 씁니다. [추론]

앱 안 스캐너 구현 후보:

| 후보 | 크기 | 카메라 권한 | 오프라인 | 라이선스/상태 | 근거 |
|---|---|---|---|---|---|
| **CameraX + ML Kit 번들 모델** `com.google.mlkit:barcode-scanning:17.3.0` | "About 2.4 MB size increase." (+ CameraX) | 필요(런타임 창 1번) | 예(모델이 앱 안) | ML Kit 약관(무료). API 23 이상 | [문서: https://developers.google.com/ml-kit/vision/barcode-scanning/android ] |
| ML Kit 비번들 `play-services-mlkit-barcode-scanning:18.3.1` | "About 200 KB" | 필요 | 모델을 Play 서비스가 내려받음 | 같음 | [같은 문서] |
| Google 코드 스캐너 `play-services-code-scanner:16.1.0` | 작음 | **불필요**("without requiring your app to request camera permission") | "This API uses an unbundled library that must be downloaded before use." 오프라인 동작은 문서에 없음 | UI가 Play 서비스 것이라 화면을 못 바꿈 | [문서: https://developers.google.com/ml-kit/vision/barcode-scanning/code-scanner ] |
| `com.journeyapps:zxing-android-embedded:4.3.0` | (미확인) | 필요 | 예 | Apache 2.0. 바탕인 ZXing 은 "The project is in maintenance mode" | [문서: https://github.com/journeyapps/zxing-android-embedded , https://github.com/zxing/zxing ] |

- **권장: CameraX + ML Kit 번들 모델.** 이유: 인터넷이 없어도 되고(사내망, 첫 실행), Play 서비스의 모듈 내려받기에 기대지 않고, Google이 유지합니다. 대가: 카메라 권한 창 1번, APK가 수 MB 커짐(LAN 내려받기라 영향 작음). [추론]
- 차선: `zxing-android-embedded`(전부 오픈소스로 두고 싶을 때). [추론]

### 3.5 어댑터가 여러 개일 때 어느 주소를 넣는가

이 PC의 예: `192.168.25.5`(이더넷, 게이트웨이 있음)와 `172.30.224.1`(WSL 가상, 게이트웨이 없음) [실측].

권장 순서(위에서부터 먼저 해당하는 것을 주 주소로):

1. USB/무선 adb 로 폰이 보이면: 폰 `wlan0` 주소와 **같은 서브넷**인 PC 주소. (이 PC: 폰 192.168.25.60/24 → 192.168.25.5) [실측한 값으로 성립]
2. 기본 경로(인터넷 방향)를 가진 어댑터의 주소. (이 PC: `Find-NetRoute` 결과 이더넷 192.168.25.5) [실측]
3. 게이트웨이가 있는 물리 어댑터(가상, 루프백, 169.254.x, 터널/VPN 설명을 가진 것은 뒤로) 중 사설 대역 주소.
4. 나머지는 "다른 주소 후보"로만 넣습니다.

- 틀릴 수 있으므로 창에서 주소를 바꿀 수 있어야 하고(목록에서 고르기, 입력 아님), 바꾸면 QR이 다시 그려져야 합니다. [추론]
- VPN이 켜진 PC는 이번에 시험할 수 없었습니다(이 PC에 VPN 어댑터 없음). [미확인]

### 3.6 재접속

| 방법 | 내용 | 근거 |
|---|---|---|
| 마지막 서버 기억 | 앱이 서버 표시, 토큰, 마지막으로 성공한 주소들을 저장. 앱을 열면 먼저 그 주소로 시도 | [추론] |
| 주소가 바뀐 경우(DHCP) | A 방식: Unity가 DNS-SD 로 서비스(`_<이름>._tcp`, TXT에 서버 표시)를 등록하고 앱이 `NsdManager.discoverServices` 로 찾음. 등록은 `DnsServiceRegister`("Used to register a discoverable service on this device.", "The registration is tied to the lifetime of the calling process.") | [문서: https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsserviceregister , https://developer.android.com/develop/connectivity/wifi/use-nsd ]. Unity에서의 호출은 [미확인] |
| 그래도 안 되면 | QR 다시 찍기(주소만 새로 얻음, 토큰은 그대로) | [추론] |

- 00 문서의 실제 사례: 저장된 무선 주소는 `.42` 였는데 실제는 `192.168.25.60` 이었습니다 [00 L10]. 주소 기억만으로는 부족하다는 근거입니다.
- 토큰을 Start 마다 새로 만들면 Play 할 때마다 QR을 다시 찍어야 합니다(서버가 Play 중에만 돌던 지금 구조에서는 특히). **PC에 저장하는 오래 가는 짝 토큰 + "새로 발급" 버튼**이 "QR 한 번" 요구에 맞습니다. [추론]

---

## 4. QR 설치

### 4.1 무엇이 필요한가

| 항목 | 내용 | 근거 |
|---|---|---|
| APK 제공 | Unity 도구의 HTTP 서버가 APK를 내줌. 방화벽 A가 전제(2.3절) | [추론] |
| 서명 | 대상 SDK 30 이상은 v2 이상 서명 필수: "Apps that target Android 11 (API level 30) that are currently only signed using APK Signature Scheme v1 must now also be signed using APK Signature Scheme v2 or higher." 이번 시험 빌드는 v2 서명이 기본으로 됨 | [문서: https://developer.android.com/about/versions/11/behavior-changes-11 ], [실측] |
| 디버그 키 | `$HOME/.android/debug.keystore`, 30년 유효. 스토어 배포에는 못 씀(사내 직접 설치는 무관) | [문서: https://developer.android.com/studio/publish/app-signing ] |
| 갱신(재설치) | "the system allows the update if the certificates match." → **PC마다 다른 디버그 키로 빌드하면 덮어 설치가 안 되고 지웠다 깔아야 합니다.** 앱 프로젝트에 전용 키 저장소 하나를 두고 어느 PC에서 빌드하든 그것으로 서명해야 합니다 | [같은 문서], 대책은 [추론] |
| 최소 대상 SDK | Android 15: "apps with a targetSdkVersion lower than 24 can't be installed." Android 16 문서에서는 해당 절을 찾지 못했습니다. 대상 SDK 36이면 무관 | [문서: https://developer.android.com/about/versions/15/behavior-changes-all ], Android 16은 [미확인] |
| 출처 허용 | 설치를 시작하는 앱(브라우저, 파일 앱)별로 "이 출처 허용"이 필요. 이 폰은 이미 허용 상태 | [실측] |
| Play 프로텍트 | "Google Play Protect checks apps when you install them." / 처음 보는 앱이면 "This app is unknown to Play Protect." 창("Play Protect hasn't seen this app before.", "Appeals are not relevant and won't remove this message."). 대상 SDK가 현재보다 2단계 넘게 낮으면 경고("the app's targetSdkVersion is more than 2 versions lower than the current Android API level"). `RECEIVE_SMS`, `READ_SMS`, 알림 접근, 접근성 권한을 선언한 직접 설치 앱은 자동 차단 | [문서: https://support.google.com/googleplay/answer/2812853 , https://developers.google.com/android/play-protect/warning-dev-guidance ] |
| 삼성 자동 차단(Auto Blocker) | 켜져 있으면 Galaxy Store / Play 스토어 밖의 설치를 막고, 끄려면 설정 > 보안 및 개인정보 보호 > 자동 차단에서 인증 후 꺼야 함. One UI 6.1.1 로 출시된 기기는 기본 켜짐, 이전 버전에서 올라온 기기는 기존 설정 유지 | [검색: https://www.samsung.com/uk/support/mobile-devices/protect-your-galaxy-device-with-the-new-auto-blocker-feature/ , https://www.androidauthority.com/enable-sideloading-one-ui-6-1-1-3463446 ]. 본문 직접 인용 아님. 이 폰의 상태는 [미확인] |
| 브라우저의 내려받기 경고 | Chrome은 APK에 "File might be harmful" 류의 확인을 띄움. 평문 http 내려받기에 추가 경고가 붙는지는 확인하지 못함 | [검색], [미확인] |
| 개발자 확인 제도 | "September 30, 2026: Protections begin for all users who install apps from participating stores in Brazil, Indonesia, Singapore, and Thailand on certified devices running Android 7+." / "Verification capability will be expanded globally for all apps on certified Android devices in 2027." / "Apps installed using ADB won't require verification." / 학생·취미 개발자용 제한 배포 계정이 있음(문서 두 곳이 기기 수를 20대와 50대로 다르게 적음) | [문서: https://developer.android.com/developer-verification , https://developer.android.com/developer-verification/guides/faq , https://developer.android.com/developer-verification/guides/limited-distribution ] |

- 개발자 확인 제도는 지금(2026-11, 한국) 이 폰에는 적용 전으로 보이지만, **2027년 전 세계 확대 뒤에는 브라우저로 받은 미등록 앱 설치가 막히거나 절차가 늘 수 있습니다.** adb 설치는 예외라고 명시돼 있습니다. 시제품에는 영향이 없고, 사내 배포 단계에서 다시 봐야 합니다. [문서 + 추론]

### 4.2 설치 흐름 비교

| 흐름 | 폰에서 탭 수 | 전제 | 평가 |
|---|---|---|---|
| **QR → 착륙 페이지 → APK 받기 → 설치** | 4.3절 | 방화벽 A | 요구사항 3. 케이블 없음 |
| **도구의 "USB로 설치" 버튼**(`adb install -r`) | **0** | USB 디버깅이 켜진 폰 + 케이블 | 가장 빠르고 경고가 없음(개발자 확인 제도 예외). 개발자 옵션을 모르는 사람에게는 오히려 어려움 |
| 메신저/드라이브로 APK 전달 | 비슷 | 인터넷 | 도구 밖. 버전이 어긋나기 쉬움 |

권장: QR 설치를 기본으로 만들고, USB 설치 버튼을 대체 수단으로 둡니다(요구사항 9의 워커 시험도 이 경로를 씁니다). [추론]

### 4.3 QR 설치 때 사람이 보게 되는 화면과 탭 수

"처음" = 그 폰에서 직접 설치를 해 본 적이 없는 경우, "이 폰" = SM-F731N 의 지금 상태(브라우저 출처 허용이 이미 켜져 있음 [실측]). **아래는 문서와 폰 설정값으로 구성한 예상이고, 실제 화면을 끝까지 눌러 보지는 않았습니다**(이 단계는 폰을 건드리지 않음). 구현 단계에서 adb 로 같은 주소를 열어 한 번 끝까지 가며 화면을 기록해야 합니다.

| # | 화면 | 누르는 것 | 처음 | 이 폰 | 근거 |
|---|---|---|---|---|---|
| 1 | 카메라가 QR을 인식해 주소 표시 | 주소(링크) | 1 | 1 | [추론] |
| 2 | 착륙 페이지 | [앱으로 열기] → 앱이 없으므로 설치 안내로 이동 | 1 | 1 | [문서: Chrome intents], 삼성 인터넷은 [미확인] |
| 3 | 설치 안내 | [APK 받기] | 1 | 1 | [추론] |
| 4 | 브라우저 내려받기 경고("유해할 수 있는 파일") | [그래도 받기] | 1 | 1 | [검색], 삼성 인터넷 문구 [미확인] |
| 5 | 내려받기 완료 | [열기] | 1 | 1 | [추론] |
| 6 | "이 출처의 앱 설치 허용 안 됨" → 설정 → 허용 토글 → 뒤로 | 3 | 3 | **0** | [실측: 이미 allow] |
| 7 | 삼성 자동 차단이 켜져 있으면 설치가 막힘 → 설정에서 끄기(인증 포함) | 약 5 이상 | 0 또는 5+ | [미확인] | [검색] |
| 8 | 패키지 설치 확인 | [설치] | 1 | 1 | [추론] |
| 9 | Play 프로텍트 "알 수 없는 앱" | [검사 없이 설치] 또는 [검사] | 1~2 | 1~2 | [문서] |
| 10 | 설치 완료 | [열기] | 1 | 1 | [추론] |
| | **합계** | | **약 11~12** (자동 차단이 켜져 있으면 +5 이상) | **약 8~9** | |

- 2번과 3번은 착륙 페이지를 "앱이 없을 때는 처음부터 [APK 받기]를 크게 보여 주는" 식으로 만들어도 브라우저는 앱 설치 여부를 알 수 없으므로 줄일 수 없습니다(페이지는 앱이 있는지 미리 알 방법이 없음). 버튼 두 개를 한 화면에 같이 두면 2번을 건너뛸 수 있습니다: **[앱으로 열기]와 [앱 설치]를 한 페이지에 나란히** → 처음 설치는 약 10~11탭. [추론]
- 이 탭들은 Android 가 직접 설치에 요구하는 것이라 도구가 없앨 수 없습니다. 줄이는 유일한 길은 USB 설치(0탭)입니다. [추론]
- 갱신 때는 6번이 이미 돼 있고 확인 창 문구가 "업데이트"로 바뀝니다. 서명이 같아야 합니다(4.1). [문서 + 추론]
- 평문 `http://` 사설 주소를 브라우저가 열 때 "안전하지 않은 사이트" 경고가 한 번 끼는지(HTTPS 우선 모드)는 확인하지 못했습니다. [미확인]

### 4.4 연결 때 사람이 보게 되는 화면 (앱 설치 후)

| 흐름 | 화면 | 탭 수 |
|---|---|---|
| 앱 안 스캐너(처음) | 앱 아이콘 → 카메라 권한 창 [앱 사용 중에만 허용] → QR 비춤 → 연결 | 2 |
| 앱 안 스캐너(그 뒤) | 앱 아이콘 → QR 비춤 → 연결 | 1 |
| **자동 재접속**(서버를 기억) | 앱 아이콘 → 연결 | **1, QR 없음** |
| 기본 카메라 | 카메라 링크 → 착륙 페이지 [앱으로 열기] → 연결 | 2 (+ 브라우저 확인 창이 있으면 1, 미확인) |

- 로컬 네트워크 권한 창: **대상 SDK 36 이하에서는 뜨지 않습니다**(5.4절). [문서]
- 알림 권한, 위치 권한은 쓰지 않습니다. [추론]

---

## 5. 앱 (디코딩, 표시, 입력)

### 5.1 H.264 형식: 변환을 어디서 하는가

- MediaCodec 문서의 코덱별 데이터 표: "H.264 AVC | SPS (Sequence Parameter Sets*) | PPS (Picture Parameter Sets*)" / "Each parameter set and the codec-specific-data sections marked with (*) must start with a start code of "\x00\x00\x00\x01"." / "In case of H.264 AVC, you can also concatenate all codec-specific data and submit it as a single codec-config buffer." 길이 접두(AVCC) 입력을 받는다는 말은 읽은 범위에 없습니다. [문서: https://developer.android.com/reference/android/media/MediaCodec ]
- 서버는 avcC 설정 1개 + AVCC(길이 4바이트 + NAL) 프레임을 보냅니다. [00 1.5.3]
- 변환은 단순합니다: avcC 에서 SPS/PPS 를 꺼내 시작 코드를 붙여 `csd-0` / `csd-1` 로, 프레임은 NAL마다 길이 4바이트를 `00 00 00 01` 로 **같은 자리에서** 덮어씀(길이가 같아 복사 없음). [추론]
- **권장: 앱에서 변환.** 서버가 형식을 두 가지 내면 Chrome 페이지(avcC 를 `description` 으로 씀 [00])와 갈라지고 ffmpeg 출력 경로를 건드려야 합니다. 앱 쪽 변환은 프레임당 수십 바이트 쓰기입니다. [추론]
- 인코더가 재시작되면 설정이 새로 옵니다 [00 1.6]. 문서: "you must package the entire new codec-specific configuration data together with the key frame into a single buffer (including any start codes)". 해상도가 같으면 이 방식, 다르면 디코더를 다시 만드는 쪽이 안전합니다. [문서 + 추론]

### 5.2 저지연 설정

| 설정 | 내용 | 근거 |
|---|---|---|
| `MediaFormat.KEY_LOW_LATENCY = 1` (API 30) | "An optional key describing the low latency decoding mode." | [문서: https://developer.android.com/reference/android/media/MediaFormat ] |
| `KEY_PRIORITY = 0`, `KEY_OPERATING_RATE` (API 23) | 실시간 우선순위 / 원하는 동작 속도 | [같은 문서] |
| 제조사 키(Moonlight 가 쓰는 것) | Qualcomm(`omx.qcom`, `c2.qti`): `vendor.qti-ext-dec-low-latency.enable = 1`, `vendor.qti-ext-dec-picture-order.enable = 1`, `KEY_OPERATING_RATE = Short.MAX_VALUE`. Exynos: `vendor.rtc-ext-dec-low-latency.enable`. Kirin, Amlogic 키도 있음. `FEATURE_LowLatency` 지원 여부를 먼저 확인 | [문서: https://raw.githubusercontent.com/moonlight-stream/moonlight-android/master/app/src/main/java/com/limelight/binding/video/MediaCodecHelper.java ] |
| 이 폰 | `c2.qti.avc.decoder.low_latency` 가 따로 있음 → 이름으로 직접 고르거나 `KEY_LOW_LATENCY` 로 선택되게 함 | [실측], 선택 방식은 [추론] |

- Moonlight 는 GPL v3 입니다(https://raw.githubusercontent.com/moonlight-stream/moonlight-android/master/LICENSE.txt). **코드를 가져오지 말고** 키 이름과 방식만 참고합니다. [문서]
- 직접 MediaCodec 을 쓸 때 이 폰의 디코더가 프레임을 붙잡는지는 모릅니다 [00 7.2]. 앱의 첫 측정 항목입니다(입력 수 − 출력 수).

### 5.3 표시: Surface, 주사율, 표시 시각

| 항목 | 내용 | 근거 |
|---|---|---|
| SurfaceView 대 TextureView | SurfaceView 는 디코더 출력이 별도 레이어로 합성기에 직접 가고 UI 스레드를 거치지 않음(브리프가 앱을 만드는 이유). TextureView 는 앱의 UI 렌더링에 묶임 → **SurfaceView** | [추론](브리프 1장의 전제). 이번에 1차 자료 인용 없음 |
| `releaseOutputBuffer(index, renderTimestampNs)` | 표시 시각을 지정해 내보냄. "If you are done with a buffer, use this call to update its surface timestamp and return it to the codec to render it on the output surface." 세부 동작(지정 시각 이후 첫 VSYNC 에 표시, 같은 VSYNC 에 여러 장이면 마지막 것)은 요약으로만 읽음 | [문서: MediaCodec], 세부는 [문서 요약] |
| `setOnFrameRenderedListener` | "Listener to be called when an output frame has rendered on the output surface" → 페이지에서는 불가능했던 **실제 표시 시각**을 얻음(00 문서 3.4.4의 "그리기 호출 시각 ≠ 표시 시각" 문제) | [문서: MediaCodec] |
| `Surface.setFrameRate(rate, compatibility, strategy)` | API 30(2인자), 31(3인자). 영상은 `FRAME_RATE_COMPATIBILITY_FIXED_SOURCE`("This value should be used for video content."). 단 "there is no guarantee", "The platform will only switch display modes … if the mode switch is lightweight and unlikely to be noticeable to the user." | [문서: https://developer.android.com/reference/android/view/Surface , https://developer.android.com/media/optimize/performance/frame-rate ] |
| 주의 | Surface 문서: "If this surface is consumed by something other than the system compositor, e.g. a media codec, this call has no effect." → 디코더 **출력**용 SurfaceView 의 Surface 는 합성기가 소비하므로 해당하지 않는 것으로 읽지만, 실기기에서 주사율이 실제로 바뀌는지 확인이 필요 | [문서], 해석은 [추론] |
| 프레임 페이싱 참고 | Moonlight: `Choreographer.FrameCallback` 구현, 출력 대기 한도 `OUTPUT_BUFFER_QUEUE_LIMIT = 2`, 균형 모드에서 `releaseOutputBuffer(buffer, frameTimeNanos)` | [문서: MediaCodecDecoderRenderer.java, 뒤쪽 절반은 요약] |

- 탭 순간 60 ↔ 120Hz 전환이 끊김의 원인인지는 확정되지 않았습니다 [00 3.4.4]. 앱은 **주사율 정책을 바꿔 가며(요청 안 함 / 60 / 120) 같은 조건에서 비교할 수 있게** 만들어야 그 질문에 답할 수 있습니다. [추론]

### 5.4 로컬 네트워크 권한

- "Starting in Android 17 (API level 37), local network protections are enforced for apps targeting Android 17 or higher." / "If your app targets SDK 36 or lower, local network access is implicitly granted using the INTERNET permission." / 대상: "Making an outgoing TCP connection", "Accepting an incoming TCP connection", UDP 송수신, mDNS, `NsdManager`. 새 권한 이름은 `ACCESS_LOCAL_NETWORK`. [문서: https://developer.android.com/privacy-and-security/local-network-permission ]
- 이 폰은 Android 16 이고 앱의 대상 SDK를 36으로 두면 **권한 창이 없습니다.** 대상 SDK를 37로 올리는 날에는 권한 창 1번이 생깁니다. [문서 + 추론]

### 5.5 그 밖 (앱 기능 조사 요약)

| 항목 | 방법 | 근거 |
|---|---|---|
| Wi-Fi 저지연 | `WifiManager.WifiLock(WIFI_MODE_FULL_LOW_LATENCY)`(API 29): "In this Wi-Fi lock mode, Wi-Fi will operate with a priority to achieve low latency." 화면이 켜져 있고 앱이 앞에 있을 때만 유효(요약으로 읽음). 대가로 배터리와 처리량이 줄 수 있음 | [문서: https://developer.android.com/reference/android/net/wifi/WifiManager ], 조건은 [문서 요약] |
| 터치 | `MotionEvent` 의 포인터별 좌표를 **그려진 영상 영역 기준 0~1** 로 바꿔 지금 형식(`d/m/u/c`, `id`, `x`, `y`, `s`)으로 전송. 손가락 키는 포인터 ID. move 는 화면 갱신당 포인터별 1개 | [00 1.5.6, 1.5.7] |
| 수신 확인 | 바이너리 1개당 `a` 1개를 **받자마자**(디코딩 전), 설정 메시지에도. 영상 소켓에 다른 텍스트 금지 | [00 L3, L4] |
| `caps` | `{"t":"caps","h264":1}` 를 공백 없이 글자 그대로 | [00 L9] |
| 전체 화면, 화면 켜짐 유지, 방향 | 몰입 모드, `FLAG_KEEP_SCREEN_ON`, 영상 가로세로에 맞춘 방향 고정. Z Flip5 는 디스플레이가 2개 | [추론], 디스플레이는 [00 L10] |
| 수명 주기 | 백그라운드로 가면 연결을 닫고(서버가 "읽지 않는 클라이언트"를 과부하로 오인한 전례 [00 3.3 #5]), 돌아오면 재접속 + 키프레임 | [00], [추론] |
| 진단 HUD | 카운터만 표시(받은 수 / 디코더 출력 수 / 표시한 수, 도착 간격, 표시 간격, 터치 → 표시, 지금 경로와 주소). 프레임 내용은 읽지 않음 | [00 L0, L2, L7, 6.10] |

---

## 6. 보안

지금 서버는 인증, Origin 검사, TLS 가 없고 WebSocket 경로의 쿼리 문자열을 무시합니다. [00 1.5.1]

| 위협(LAN에 연 뒤) | 막는 방법 | 한계 |
|---|---|---|
| 같은 망의 누구나 화면을 보고 터치를 넣음 | **토큰 없이는 영상/입력 연결을 받지 않음.** 토큰은 QR로만 전달 | QR을 본(찍은) 사람은 들어올 수 있음. 그래서 "새로 발급" 버튼과 접속 중인 기기 표시가 필요 |
| 토큰을 망에서 엿들음(평문 TCP) | 토큰 자체를 보내지 않고 **서버가 준 임의 값에 대한 HMAC** 으로 증명 | 기본 카메라 경로에서는 토큰이 http 주소에 실려 PC로 한 번 평문으로 감(착륙 페이지 요청). 주소의 `#` 뒤에 실으면 서버로 전송되지 않지만 페이지의 스크립트가 다뤄야 함 → 설계에서 정함 |
| 화면 내용을 망에서 엿봄 | (시제품) 하지 않음. 영상은 평문 | 같은 Wi-Fi 의 공격자가 패킷을 잡으면 화면을 복원할 수 있음. 공용 Wi-Fi 에서 쓰지 말라고 안내. 암호화는 다음 범위 |
| 다른 PC가 서버인 척함 | 앱도 서버에게 같은 HMAC 증명을 요구(상호 확인) | — |
| 악성 웹 페이지가 `ws://PC:포트` 로 붙음 | 토큰이 없으면 거절. 추가로 `Origin` 헤더가 있는 요청은 자기 페이지가 아닐 때 거절 | — |
| APK 바꿔치기 | (시제품) 하지 않음. 평문 http | 갱신은 서명이 같아야 하므로 이미 설치된 앱은 바꿔치기가 안 됨. 처음 설치는 무방비 |
| 터치 주입으로 에디터 밖을 조작 | 주입은 게임의 터치 입력으로만 들어감 | — |

- 토큰을 주소 쿼리에 싣는 지금 구조와의 호환: 서버는 쿼리 문자열을 지금 무시하므로 [00 1.5.1], 토큰 검사를 넣어도 Chrome 페이지(루프백 `127.0.0.1` 접속)는 **루프백에서 온 연결은 토큰 없이 허용**으로 그대로 둘 수 있습니다. adb reverse 도 PC에서는 루프백으로 들어옵니다 [00 2.1]. [추론]

---

## 7. 전송 (무선 1순위)

### 7.1 지금 아는 것과 모르는 것

- 무선 수치는 전부 무선 adb 터널에서 잰 것이고 LAN 직접 수치는 없습니다: 처리량 8.3Mbps, 왕복 7~22ms, 정체 100~290ms, 터치 → 표시 80.6ms, 탭 시 고르지 않은 간격 3.8%. [00 3.6]
- 폰은 지금 **2.4GHz 채널 1, 802.11n, 링크 104/58Mbps** 입니다 [실측]. 터널의 8.3Mbps 가 Wi-Fi 한계인지 터널 한계인지는 아직 모릅니다 [00 3.6.4].
- **5GHz 는 워커가 잴 수 없습니다.** 폰 설정을 바꿀 수 없고(브리프 4장), 공유기가 5GHz SSID 를 내는지도 확인하지 못했습니다. [미확인]

### 7.2 TCP 대 UDP

| | 지금의 WebSocket(TCP) | UDP 계열(RTP + FEC, 또는 자체) |
|---|---|---|
| 손실 때 | 재전송될 때까지 뒤 프레임이 모두 밀림(00 문서의 100~290ms 정체가 이것인지는 미측정) | 잃은 프레임만 깨지고 FEC / 참조 무효화 / IDR 로 복구 |
| 유사 제품 | scrcpy(adb 터널 위 TCP, 앱 수준 확인 없음), Orca | Moonlight/Sunshine, Parsec("TCP는 손실 때 지연이 크게 늘어 쓰지 않음") [조사 보고서] |
| 구현량 | 있음 | 큼: 패킷화, 순서/손실 처리, 혼잡 제어, **키프레임을 즉시 만들 수 있는 인코더**(지금은 ffmpeg 재시작 0.43초 [00]) |
| 방화벽 | A에서 TCP 1포트 | A에서 UDP 포트 추가(같은 규칙에 넣을 수 있음). B(방향 뒤집기)와도 조합 가능 |

- **권장: 시제품은 TCP 그대로, 앱 전용 경로를 만들지 않습니다.** 이유: ① LAN 직접 TCP 가 실제로 얼마나 밀리는지 수치가 없습니다(없는 문제를 풀 수 있음). ② 지금 서버는 손실 복구에 필요한 "즉시 키프레임"이 없어 UDP 의 이점을 살리지 못합니다. ③ 앱이 풀려는 1차 문제는 표시 단계입니다. [추론]
- **UDP 로 가는 조건을 미리 정해 둡니다**(PRD): LAN 직접 TCP 에서 정체(도착 지연 40ms 이상)의 비율과 최대 길이를 재고, 잠정 기준을 넘으면 다음 범위에서 UDP 경로를 설계합니다.

### 7.3 TCP 위에서 할 수 있는 것 (시제품 범위)

| 항목 | 내용 | 근거 |
|---|---|---|
| 소켓 | 양쪽 `TCP_NODELAY`. **메시지 1개 = 쓰기 1번**(앱이 보내는 확인과 입력도). 나눠 쓰면 40ms 묶임이 생긴 전례 | [00 L1] |
| 지터 버퍼 | 헤더의 캡처 시각으로 표시 예정 시각을 정하고(페이지의 "표시 일정"과 같은 원리), 지연 목표는 최근 도착 흔들림에 맞춰 0~2프레임으로 자동 조절. `releaseOutputBuffer(…, 시각)` 로 VSYNC 에 맞춤 | [00 3.4.3], [문서], Moonlight 균형 모드 [조사 보고서] |
| 몰려 온 프레임 | 디코딩은 전부 하고(P 프레임은 앞 프레임을 참조하므로 건너뛸 수 없음) **표시만 최신 1장** | [00 3.2], [추론] |
| 프레임 버림 | 서버의 시간 기준 역압(가장 오래된 미확인 150ms)과 인코딩 전 건너뜀이 이미 있음. 앱은 확인을 받자마자 보내 이 판정을 흐리지 않음 | [00 1.5.5, L3] |
| 키프레임 복구 | 디코더 오류나 재접속 때만 `{"t":"key"}`. 한 번에 0.43초 공백이라 남발하지 않음 | [00 1.5.4] |
| 비트레이트 | 지금은 고정 CBR 6Mbps, 바꾸면 인코더 재시작. 자동 적응은 서버 일이고 다음 범위. 시제품은 **2.4GHz 에서 6Mbps 가 유지되는지를 측정**만 함 | [00], [추론] |
| Wi-Fi 절전 | `WIFI_MODE_FULL_LOW_LATENCY` 잠금 | [문서] |
| 경로 표시 | HUD 에 지금 주소(127.0.0.1 이면 adb 터널)와 Wi-Fi 주파수/링크 속도. 말없는 경로 전환 금지 | [00 L0] |

### 7.4 영상 클라이언트 2개 문제와 공존

- 서버는 영상 클라이언트가 정확히 1개일 때만 H.264 를 씁니다. 2개면 둘 다 MJPEG 입니다. 입력 번호는 입력 연결이 0 → 1개가 될 때만 초기화됩니다. [00 1.6, L8]
- 다른 워커의 Chrome 세션(포트 7300)을 건드리면 안 됩니다. [브리프 4장]
- 시제품 시험 방법(권장 순서): ① 5단계(앱 구현)는 **재생 서버**(녹화한 프로토콜 2 스트림을 다른 포트에서 그대로 보냄. `node.exe` 면 LAN 직접도 가능, 2.5절)로 개발하고, ② 조정자가 허락한 시간대에 실제 Unity 서버로 확인합니다. ③ 6단계(Unity 쪽)에서 "앱과 페이지가 같이 붙었을 때"의 규칙을 서버에 넣습니다. [추론]
- 공존 규칙 권장: **나중에 붙은 영상 클라이언트가 H.264 를 받고, 먼저 있던 쪽은 "다른 기기가 보는 중" 상태로 둠**(둘 다 MJPEG 로 떨어뜨리지 않음). 자세한 것은 설계에서. [추론]

---

## 8. QR 생성 (Unity 에디터, 외부 서비스 없음)

| 후보 | 라이선스 | 형태 | 장점 | 단점 | 근거 |
|---|---|---|---|---|---|
| **직접 구현**(프로젝트에 순수 C# 으로 작성) | 없음 | 파일 1개(수백 줄: 바이트 모드, 리드-솔로몬, 마스크 8종 평가, 형식/버전 정보) | 의존성 0, 라이선스 표기 0, 필요한 범위만 | **미묘한 오류가 "어떤 폰에서는 안 읽힘"으로 나타남.** 검증을 전부 우리가 해야 함 | [추론] |
| **MIT 인코더 소스를 넣기**: `manuelbl/QrCodeGenerator`(Net.Codecrete.QrCodeGenerator) | MIT | .NET Standard 2.0 용 소스 여러 파일. Nayuki 의 Java 판을 옮긴 것에서 시작("It started as a C# port of" …, "Version 3 is a complete rewrite") | 검증된 알고리즘(40개 버전, 4개 오류 정정 수준), 핵심 라이브러리에 의존성 없음, 소스로 넣으면 DLL 충돌이 없음 | 라이선스 문구를 같이 넣어야 함. 파일 수와 Unity 호환성은 **확인하지 못함**(Unity 언급 없음) | [문서: https://github.com/manuelbl/QrCodeGenerator , https://github.com/nayuki/QR-Code-generator ] |
| MIT: `QRCoder` | MIT | "Zero dependencies", .NET Standard 1.3+. `PngByteQRCode`, `BitmapByteQRCode` 는 System.Drawing 불필요. 모듈 행렬(`ModuleMatrix`, `List<BitArray>`)을 직접 읽을 수 있음 | 널리 쓰임, 렌더러 포함 | 파일이 많음(렌더러, 페이로드 생성기 등 우리가 안 쓰는 것) | [문서: https://github.com/codebude/QRCoder ] |
| **ZXing.Net** | Apache 2.0 | DLL. "Unity3D (.Net 2.0 built without System.Drawing reference)" 빌드 제공. 인코딩과 **디코딩** 모두 | 에디터 안에서 "만든 QR을 다시 읽어 확인"까지 가능 | Unity 프로젝트에 `zxing.unity.dll` 이 이미 들어 있는 경우가 흔해 **같은 어셈블리 중복 충돌** 위험(도구를 다른 프로젝트에 설치하려는 목표와 상충). 크기 큼. 최근 릴리스 날짜는 확인 못 함 | [문서: https://github.com/micjahn/ZXing.Net ], 충돌 위험은 [추론] |

- catjump 프로젝트에는 지금 ZXing/QRCoder 계열 파일이 없습니다(`Assets`, `Packages` 검색 결과 0건). API 호환 수준은 `apiCompatibilityLevel: 6`(.NET Standard 2.1)입니다. [실측]
- Nayuki 본가는 C# 판이 없습니다("Java, TypeScript/JavaScript, Python, Rust, C++, C"). [문서]

**권장: MIT 인코더 소스(`manuelbl/QrCodeGenerator` 의 핵심 인코더 파일)를 도구 폴더에 자체 네임스페이스로 넣고, 모듈 행렬을 `Texture2D` 로 직접 그립니다.**

- 이유: ① 인코딩 알고리즘을 새로 쓰지 않으므로 "읽히지 않는 QR" 위험이 가장 작습니다. ② 소스로 넣으면 DLL 중복 충돌이 없고 나중에 UPM 패키지로 묶기 쉽습니다. ③ 인터넷이 필요 없습니다(런타임에 아무것도 받지 않음). ④ MIT 라 문구만 같이 두면 됩니다. [추론]
- 차선: 핵심 파일을 Unity 에서 컴파일하는 데 문제가 있으면 `QRCoder` 의 `QRCodeGenerator` 부분 소스. 그것도 안 되면 직접 구현. [추론]
- **검증(요구사항 10)**: 만든 텍스처를 PNG 로 저장해 PC의 Python OpenCV(`cv2.QRCodeDetector`)로 다시 읽어 원문과 비교합니다. 이 PC의 Python 3.13 에 `cv2`, `PIL`, `numpy` 가 설치돼 있습니다(`pyzbar`, `qrcode`, `zxingcpp` 는 없음). [실측]
- 그릴 때 지켜야 할 것: 둘레 여백 4모듈, 모듈당 정수 픽셀(필터 Point), 어두운 테마에서도 흰 바탕에 검은 모듈, 창 크기에 따라 모듈당 4픽셀 이상. [추론]

---

## 9. 배포 형태 (다른 Unity 프로젝트에 "설치")

- Unity 패키지 구조: 편집기 코드는 `Editor` 폴더, 어셈블리 정의 파일 필요, 폴더 이름은 패키지 이름(`com.example.mypackage`). [문서: https://docs.unity3d.com/2022.3/Documentation/Manual/CustomPackages.html ]
- Git 주소로 설치: `https://…/repo.git?path=/subfolder#revision`, "the path query parameter always precedes the revision anchor", Git 2.14.0 이상 필요. [문서: https://docs.unity3d.com/2022.3/Documentation/Manual/upm-git.html ]
- 지금 상태: `Assets/04_Scripts/Editor/GameViewRemote/` 는 git 에 추적되지 않고 [00], 다른 워커가 지금도 고치고 있습니다(이 단계 중에도 `GameViewRemote.cs`, `GameViewRemoteAdb.cs`, `GameViewRemoteWindow.cs`, `GameViewRemotePage.html` 이 23:11 로 바뀜 [실측]). 어셈블리 정의 파일은 폴더에 없습니다 [실측].
- 권장 형태(다음 범위): `com.<조직>.gameviewremote/` = `package.json` + `Editor/`(asmdef, Editor 전용) + `Editor/ThirdParty/`(QR 인코더 + 라이선스) + `App~/`(APK. `~` 로 끝나는 폴더는 Unity 가 가져오지 않음 — 이 규칙은 이번에 문서로 확인하지 않음 [미확인]) + `Documentation~/`.
- **권장: 패키지화는 시제품 범위에 넣지 않습니다.** 폴더가 지금도 바뀌는 중이라 옮기면 다른 워커의 작업과 충돌합니다. 대신 새로 쓰는 코드(QR, 착륙 페이지, 토큰, 방화벽 점검)는 **게임 코드에 의존하지 않게**, 나중에 폴더째 옮길 수 있게 씁니다. [추론]

---

## 10. 확인하지 못한 것 (모음)

| # | 항목 | 왜 중요한가 | 어디서 확인 |
|---|---|---|---|
| 1 | 삼성 인터넷에서 `intent://` + `S.browser_fallback_url` 동작, "앱에서 열기" 확인 창 유무 | 기본 카메라 경로의 탭 수와 성립 여부 | 5/6단계: adb 로 주소 열고 화면 기록 |
| 2 | 삼성 카메라가 QR의 http 주소를 여는 실제 화면, 커스텀 스킴 QR 처리 | 요구사항 10에 따라 소유자 시험 | 소유자 |
| 3 | 평문 http 사설 주소에서 APK 를 받을 때의 브라우저 경고(Chrome, 삼성 인터넷) | 설치 탭 수 | 5/6단계 |
| 4 | 이 폰의 삼성 자동 차단 상태 | 켜져 있으면 QR 설치가 막힘 | 소유자(설정 화면) 또는 설치 시도 결과 |
| 5 | 승격 없이 방화벽 규칙을 바꿀 수 없다는 것(시도 안 함), A 적용 뒤 실제 도달 | 방화벽 설계의 전제 | 6단계 + 소유자 UAC 1번 |
| 6 | B(방향 뒤집기): Unity(Mono)에서 `DnsServiceBrowse` 호출, 브로드캐스트 응답이 Unity.exe 차단 규칙을 통과하는지 | 관리자 없는 경로의 성립 여부 | 시제품의 작은 실험 |
| 7 | LAN 직접 TCP 의 처리량, 왕복, 정체 분포(2.4GHz), 5GHz 전부 | 무선 합격 기준 | 5단계 첫 측정. 5GHz 는 소유자가 폰을 옮겨 줘야 함 |
| 8 | 이 폰의 MediaCodec 직접 사용 시 프레임 붙잡음, `setFrameRate` 가 실제로 주사율을 바꾸는지, `refresh_rate_mode=0` 의 뜻 | 앱의 핵심 기대 | 5단계 |
| 9 | QR 용량 표(버전별 바이트 수), `manuelbl/QrCodeGenerator` 의 파일 수와 Unity 컴파일 | QR 생성 | 6단계 |
| 10 | AGP 공식 호환 표의 8.13 행(Gradle/JDK/최대 API). 읽힌 페이지는 최신 9.5 만 다룸 | 빌드 구성의 근거. 실제 빌드로는 확인됨 | 필요 시 |
| 11 | Android 16 의 최소 설치 가능 대상 SDK | 대상 36이면 무관 | — |
| 12 | VPN 이 켜진 PC에서의 주소 선택 | 3.5절 규칙 | 다른 PC |
| 13 | 삼성 자동 차단, Chrome 내려받기 경고는 검색 요약으로만 확인 | 4장 | 필요 시 본문 확인 |
| 14 | SurfaceView 와 TextureView 의 차이는 1차 자료 인용 없이 적음 | 5.3절 | 설계 단계 |

---

## 11. 이 단계에서 실행한 것

폰과 PC의 상태를 바꾸는 명령은 실행하지 않았습니다. 폰 화면을 깨우거나 앱을 띄우지 않았습니다.

| 대상 | 실행한 것 | 비고 |
|---|---|---|
| 폰(adb, 전부 `-s R3CW70LJ2VP`) | `devices -l`, `getprop`(모델, 버전, SoC, ABI), `ip addr show wlan0`, `dumpsys wifi`/`display`/`media.player`/`package`(읽기), `settings get/list`(읽기), `pm list packages`, `cmd package resolve-activity`, `appops get`, `reverse --list` | 읽기 전용 |
| 폰 → PC 도달 | `ping -c 3 192.168.25.5`(100% 손실), `toybox nc -w 3 192.168.25.5 55504` **1회**(시간 초과) | 폰 셸에서 나가는 연결 시도 2건. 연결이 성립하지 않아 Unity 에 도달한 것은 없음 |
| PC 읽기 | 어댑터/주소/경로/프로필, 방화벽 프로필과 규칙(PowerShell, `netsh … show`, COM `FwPolicy2`), `whoami /groups`, UAC 레지스트리 값, Unity.exe 의 듣는 포트 목록, 설치된 SDK/JDK/Gradle 목록, catjump 폴더 목록과 `ProjectSettings.asset` 3줄 | 읽기 전용. catjump 저장소는 고치지 않음 |
| 시험 빌드 | 스크래치 폴더(`…\scratchpad\probe`, `…\scratchpad\probe2`)에 빈 앱을 만들어 Gradle 빌드 3회 | **부수 효과: `%USERPROFILE%\.gradle\caches` 에 AGP 8.13.0 과 의존성이 내려받아졌습니다**(캐시 합계 약 106MB). Google Maven / Maven Central 로의 요청에 개인 정보는 없습니다. APK 는 설치하지 않았습니다 |
| 외부 열람 | 위에 URL 로 적은 문서들, 검색 3건 | 요청에 소유자/회사/프로젝트 이름과 내부 주소를 넣지 않음 |
| 하지 않은 것 | 방화벽 변경 시도, 포트 열기, 폰에 설치/실행, Unity 조작, 7300 포트 접속, catjump 파일 수정 | |
