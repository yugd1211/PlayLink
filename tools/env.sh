# 시험 스크립트 공용: adb 경로와 일련번호(항상 -s). source tools/env.sh
export ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f2/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export S=${PLAYLINK_SERIAL:-emulator-5554}   # 기본은 가상 기기입니다(브리프 3.4: 실제 폰 사용 금지). 실제 폰은 소유자가 허락한 뒤에만 PLAYLINK_SERIAL 로 지정합니다.
export PKG=io.playlink.app
# 진단 입구(adb 만 통과: DUMP 권한이 걸린 별칭). 진단 값(pace, hud, rec …)과 cmd 는 이 입구로 온 인텐트만 받습니다.
export DIAG=$PKG/io.playlink.app.Diag
# 가상 기기가 아닌 일련번호는 거부합니다(코드 리뷰 M9). 실제 폰은 소유자가 허락한 그 자리에서만 PLAYLINK_ALLOW_REAL_DEVICE=1 을 함께 줍니다.
case "$S" in emulator-*) ;; *) if [ "$PLAYLINK_ALLOW_REAL_DEVICE" != 1 ]; then echo "env.sh: PLAYLINK_SERIAL=$S 는 가상 기기가 아닙니다. 실제 폰 사용은 금지입니다(브리프 3.4)." >&2; export S=emulator-5554; return 1 2>/dev/null || exit 1; fi ;; esac
a() { "$ADB" -s $S "$@"; }
# 화면을 PC 파일로. 사용: shot <PC 경로.png> [display id]   (가상 기기는 id 없이. 화면이 둘인 기기만 id 를 넘깁니다)
shot() { if [ -n "$2" ]; then MSYS_NO_PATHCONV=1 "$ADB" -s $S exec-out screencap -d "$2" -p > "$1"; else MSYS_NO_PATHCONV=1 "$ADB" -s $S exec-out screencap -p > "$1"; fi; }
# 빌드 + 설치
bi() { (cd "$(dirname "${BASH_SOURCE[0]}")/../android" && ./build.sh assembleProto > ../tools/build.log 2>&1; grep -E "^e: |BUILD" ../tools/build.log | head -20) && a install -r "$(dirname "${BASH_SOURCE[0]}")/../android/app/build/outputs/apk/proto/app-proto.apk" | tail -1; }
# 딥링크로 열기: open <port> [추가 쿼리]   (재생 서버 기본 sid/토큰. 실제 서버(인증 없음)에도 같은 값을 씁니다: 루프백이라 무시됨)
open() { a shell "am start -W -a android.intent.action.VIEW -d 'playlink://127.0.0.1:$1/c?v=1&s=PLKTEST2&t=AAECAwQFBgcICQoLDA0ODw&via=usb&take=1$2' -n $DIAG" | grep -E "Status|TotalTime"; }
cmd() { a shell am start -n $DIAG --es cmd "$@" > /dev/null; }
# 기록 파일 가져오기: pullrec  (tools/rec/ 로)
pullrec() { mkdir -p "$(dirname "${BASH_SOURCE[0]}")/rec"; (cd "$(dirname "${BASH_SOURCE[0]}")/rec" && MSYS_NO_PATHCONV=1 "$ADB" -s $S pull /sdcard/Android/data/$PKG/files/rec/. . | tail -1); }
# 한 구간 재기: seg <이름> <초> <idle|tap|key> [탭 주기 초=1.5]
#   앱이 이미 연결돼 PLAYING 인 상태에서 기록을 시작/끝내고 파일을 가져와 보고서를 찍습니다.
#   tap = 제자리 120ms 누름(adb shell input swipe x y x y 120): 주입한 것이며 손가락이 아닙니다.
#   key = 터치 없이 같은 주기로 input keyevent 0 (프로세스 기동만의 영향을 보는 대조, 리뷰 m10)
seg() {
  local name=$1 sec=$2 mode=$3 every=${4:-1.5} T="$(dirname "${BASH_SOURCE[0]}")"
  cmd rec_start --es name "$name"; sleep 0.5
  local n=$(python -c "print(int(($sec-1)/$every))")
  if [ "$mode" = tap ]; then a shell "i=0; while [ \$i -lt $n ]; do input swipe 540 1000 540 1000 120; sleep $(python -c "print(max(0.1,$every-0.75))"); i=\$((i+1)); done"
  elif [ "$mode" = key ]; then a shell "i=0; while [ \$i -lt $n ]; do input keyevent 0; sleep $(python -c "print(max(0.1,$every-0.6))"); i=\$((i+1)); done"
  else sleep $sec; fi
  cmd rec_stop; sleep 1.5; pullrec > /dev/null
  local f=$(ls -t "$T"/rec/rec-*-"$name".json | head -1); node "$T/app-report.js" "$f" --skip=0.7
}
# 포트에서 듣는 프로세스를 끝냄(시험 서버 전용 포트에만 씁니다. 7300 은 쓰지 않습니다)
killport() { [ "$1" = 7300 ] && return; for p in $(netstat -ano | grep -E "[:.]$1 .*LISTENING" | awk '{print $NF}' | sort -u); do taskkill //F //PID $p > /dev/null 2>&1; done; }
