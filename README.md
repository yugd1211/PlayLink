# PlayLink

Unity 에디터의 Game View 를 같은 Wi-Fi 의 안드로이드 폰에서 보고 터치로 조작하는 도구입니다. 빌드 없이 에디터의 Play 화면을 폰으로 확인합니다.

지금은 구조를 바꾸는 중입니다. 정한 내용과 진행 순서는 [docs/DESIGN_DECISIONS.md](docs/DESIGN_DECISIONS.md) 에 있습니다.

## 폴더

| 폴더 | 내용 |
|---|---|
| `unity/io.playlink/` | Unity 에디터 패키지(에디터 전용) |
| `android/` | 폰 앱(Kotlin) |
| `tools/` | 가상 기기와 측정 스크립트 |
| `docs/` | 설계 결정, 예전 구조의 개발 문서 |

## 요구 사항

- Unity 2022.3 이상, Windows 에디터
- Android 8.0(API 26) 이상
- H.264 전송에는 ffmpeg 가 필요합니다

## 라이선스

MIT. 포함된 QR 생성 코드(`unity/io.playlink/Editor/ThirdParty/QrCodeGenerator`)도 MIT 이며 그 폴더에 고지가 있습니다.
