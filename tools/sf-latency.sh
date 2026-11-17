#!/bin/bash
# 앱 SurfaceView 레이어의 실제 표시 시각(SurfaceFlinger --latency)을 읽어 표시 간격 분포를 냅니다(리뷰 B1 의 대조).
# 사용: tools/sf-latency.sh   (앱이 PLAYING 일 때. 최근 127장)
source "$(dirname "$0")/env.sh"
L=$(MSYS_NO_PATHCONV=1 "$ADB" -s $S shell "dumpsys SurfaceFlinger --list" | grep -o "SurfaceView\[$PKG[^}]*(BLAST)#[0-9]*" | head -1)
MSYS_NO_PATHCONV=1 "$ADB" -s $S shell "dumpsys SurfaceFlinger --latency '$L'" | python -c "
import sys
rows=[l.split() for l in sys.stdin.read().strip().splitlines()]
period=int(rows[0][0])/1e6
t=[int(r[1])/1e6 for r in rows[1:] if len(r)==3 and 0<int(r[1])<9e18]
d=[int(r[0])/1e6 for r in rows[1:] if len(r)==3 and 0<int(r[1])<9e18]
g=sorted(b-a for a,b in zip(t,t[1:]))
import collections
c=collections.Counter(round(x/ (period/2))*0.5 for x in g)
print('layer refresh period %.2f ms, %d presented frames, actual present interval p50 %.1f p90 %.1f max %.1f ms' % (period,len(t),g[len(g)//2],g[int(len(g)*0.9)],g[-1]))
print('intervals in refresh periods:', dict(sorted(c.items())))
late=[a-b for a,b in zip(t,d)]
print('actualPresent - desiredPresent: min %.1f p50 %.1f max %.1f ms' % (min(late), sorted(late)[len(late)//2], max(late)))
"
