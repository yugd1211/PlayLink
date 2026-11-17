// 가상 기기 시험용 중계: PC 의 127.0.0.1:<듣는 포트> 로 온 연결을 Unity 의 LAN 주소(예: 192.168.25.5:7300)로 그대로 넘깁니다.
// 왜: 에뮬레이터의 NAT 경로(기기 → 192.168.25.5 / 10.0.2.2)는 TCP 바이트를 드문드문 잃어서(docs/06_unity_integration.md 5장)
//     영상과 APK 내려받기가 깨집니다. adb reverse 는 손실이 없지만 PC 의 127.0.0.1 로만 갑니다. 이 중계를 사이에 두면
//     가상 기기가 손실 없이 "LAN 쪽 서버(인증하는 쪽)"에 닿습니다.
// 사용: node lan-relay.js <LAN 주소> [LAN 포트=7300] [듣는 포트=7320] [분=60]
//       adb -s emulator-5554 reverse tcp:7320 tcp:7320
// 127.0.0.1 에서만 듣고(외부에서 못 붙음), 정한 시간이 지나면 스스로 끝납니다. 내용은 기록하지 않습니다.
const net = require('net');
const target = process.argv[2], tport = +(process.argv[3] || 7300), lport = +(process.argv[4] || 7320), minutes = +(process.argv[5] || 60);
if (!target) { console.error('사용: node lan-relay.js <LAN 주소> [LAN 포트] [듣는 포트] [분]'); process.exit(2); }
let n = 0;
net.createServer(c => {
  const u = net.connect(tport, target); n++;
  c.setNoDelay(true); u.setNoDelay(true);
  c.pipe(u); u.pipe(c);
  const done = () => { c.destroy(); u.destroy(); };
  c.on('error', done); u.on('error', done); c.on('close', () => u.end()); u.on('close', () => c.end());
}).listen(lport, '127.0.0.1', () => console.log(`relay 127.0.0.1:${lport} -> ${target}:${tport} (${minutes}분 뒤 종료)`));
setTimeout(() => { console.log('연결 수', n); process.exit(0); }, minutes * 60000);
