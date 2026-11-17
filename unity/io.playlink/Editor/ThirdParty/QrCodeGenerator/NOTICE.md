# QrCodeGenerator (제3자 소스)

- 출처: https://github.com/manuelbl/QrCodeGenerator , 태그 `v2.0.6` 의 `QrCodeGenerator/` 폴더
- 저작권: Copyright (c) Manuel Bleichenbacher, Copyright (c) Project Nayuki
- 라이선스: MIT (`LICENSE` 파일, 각 소스 파일 머리말에도 전문이 있습니다)
- 가져온 파일: `QrCode.cs`, `QrSegment.cs`, `BitArrayExtensions.cs`, `ReedSolomonGenerator.cs`, `Objects.cs`, `DataTooLongException.cs`, `Graphics.cs`
- 가져오지 않은 파일: `QrSegmentAdvanced.cs`(한자 모드와 최적 분할. 쓰지 않음)
- 바꾼 것: 네임스페이스 한 줄뿐입니다. `Net.Codecrete.QrCodeGenerator` → `PlayLinkThirdParty.QrCodeGenerator`
  (다른 프로젝트에 같은 라이브러리가 DLL 이나 소스로 들어 있어도 이름이 겹치지 않게). 그 밖의 코드는 원본 그대로입니다.
- 쓰는 곳: `PlayLinkQr.cs` (`QrCode.EncodeBinary`, `GetModule`). 인터넷이나 외부 서비스를 쓰지 않습니다.
- 새 버전으로 바꿀 때: 같은 파일을 받아 네임스페이스만 바꾸고, 메뉴 `Tools > PlayLink > QR Self Test` 를 돌립니다.
