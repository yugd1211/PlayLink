# Game View Remote 측정 스크립트

개발 문서 12.7~12.9의 수치를 낸 스크립트입니다. Node 22 이상(내장 `fetch`, `WebSocket`)에서 그대로 실행합니다. 도구 본체는 이 폴더에 의존하지 않습니다.

준비: 폰을 USB로 연결하고 `adb forward tcp:9333 localabstract:chrome_devtools_remote`로 폰 Chrome의 DevTools 포트를 엽니다(끝나면 `adb forward --remove tcp:9333`). 스크립트 안의 adb 경로는 Unity 2022.3.62f2 기준이고, 기기가 여러 대면 `ANDROID_SERIAL`을 지정합니다. 서버는 `http://127.0.0.1:7300`으로 가정합니다.

| 파일 | 하는 일 |
|---|---|
| `measure.js <idle\|tap\|start\|drag> <초> [x y] [--press=120 --still]` | 폰 페이지와 서버 `/stats`를 함께 읽어 표시 수준 지표(멈춘 갱신, 건너뛴 프레임, 정지 최대, 터치 → 표시)를 냅니다. `tap`은 `adb shell input`으로 터치를 넣고, `--press=120 --still`은 움직임 없이 120ms 누르기(손가락 탭의 정지 현상을 재현)입니다. Chrome 창이 앞에 있을 때만 넣습니다. 주사율(rAF)을 터치 직후/무터치로 나눠 내고, 60 ↔ 120Hz 전환 시각과 그 주변 표시 간격, 표시 일정의 여유 · 칸 넘김도 냅니다. `--every=5000`은 누르는 주기(ms, 가변 주사율 폰이 60Hz로 돌아갈 틈을 줌), `--series`는 250ms 단위 시간순 기록. 페이지가 프레임을 600장(10초)만 기억하므로 한 번에 10초 이하로 잽니다 |
| `record.js <초> <out.json>` | 아무것도 넣지 않고 기록만 합니다(프레임, rAF, 실제 포인터 이벤트, Unity 프레임). 소유자가 직접 플레이할 때 씁니다 |
| `analyze.js <out.json> [--timeline=N]` | 기록에서 손가락 누르기 전후 500ms를 비교하고, 가장 나쁜 탭의 시간순 기록을 찍습니다 |
| `touchstall.js <out.json>` | 누르는 동안 프레임이 페이지에 전달됐는지를 누르기마다 셉니다 |
| `linktest.js <PC IP> <초> <반복>` | 게임 없이 링크만 비교합니다: 무선 adb reverse(7312), Wi-Fi 직접, USB adb reverse(7313). 실행 전에 `adb -s <무선> reverse tcp:7312 tcp:7312`, `adb -s <USB> reverse tcp:7313 tcp:7312`를 걸고 폰에서 `http://localhost:7313/`을 엽니다 |
| `wsclient.js <mjpeg\|h264\|h264-key\|h264-noack>` | PC에서 영상 클라이언트 흉내: MJPEG 대체 경로, 키프레임 요청, 수신 확인 역압 확인 |
| `nav.js <탭 id> <url>` | DevTools로 폰 탭을 주소로 이동 |
| `sim.js <GameViewRemotePage.html>` | 표시 일정 알고리즘을 60Hz/120Hz 가상 화면에서 돌려 보는 시뮬레이션 |

테스트 패턴은 `http://127.0.0.1:7300/debug?pattern=1`(끄기 `pattern=0`)입니다.
