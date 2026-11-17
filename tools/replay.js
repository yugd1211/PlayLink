// 프로토콜 2 재생 서버(설계 2.3): 실제 Unity 서버 없이 앱을 시험합니다.
//  - 시작할 때 ffmpeg 를 실제 서버와 같은 인코더 인자로 한 번 돌려(입력만 테스트 패턴) H.264 를 녹화해 두고,
//    FLV 태그 → 28바이트 헤더 + avcC/AVCC 로 정밀한 간격(--interval ms, 기본 1000/fps)에 맞춰 반복 재생합니다.
//  - 수신 확인 역압, caps/key/take, 입력 소켓의 ping 답과 inputSeq 반영(터치 에코), 인증 1(설계 3.2/3.3), 닫기 코드, 자리/대기실.
// 사용: node replay.js [--port=7311] [--bind=127.0.0.1] [--auth] [--sid=GVRTEST2] [--token=<22자>] [--codec=nvenc|x264]
//        [--kbps=6000] [--size=720x1280] [--fps=60] [--interval=16.61] [--clip=20] [--lobby=<초>] [--standby] [--ffmpeg=<경로>] [--app=<versionCode>]
// 제어(루프백 전용): GET /ctl?close=<코드> | standby=1 | active=1 | stale=<문구> | stale=  ,  GET /stats
// LAN 에 열 때는 node.exe 로만(이 PC의 방화벽에서 이미 허용). 토큰은 로그에 찍지 않습니다.
'use strict';
const net = require('net'), crypto = require('crypto'), fs = require('fs'), path = require('path'), { spawn } = require('child_process');
const arg = (k, d) => { const a = process.argv.find((x) => x === '--' + k || x.startsWith('--' + k + '=')); return a ? (a.includes('=') ? a.slice(k.length + 3) : true) : d; };
const PORT = Number(arg('port', 7311)), BIND = arg('bind', '127.0.0.1'), AUTH = !!arg('auth', false);
const SID = arg('sid', 'GVRTEST2'), TOKEN = arg('token', 'AAECAwQFBgcICQoLDA0ODw'), KEY = Buffer.from(TOKEN, 'base64url');
const [W, H] = String(arg('size', '720x1280')).split('x').map(Number), FPS = Number(arg('fps', 60)), KBPS = Number(arg('kbps', 6000));
const CODEC = arg('codec', 'nvenc'), APPVER = Number(arg('app', 1)), LOBBY_S = Number(arg('lobby', 0)), START_STANDBY = !!arg('standby', false);
const FF_DEFAULT = 'C:\\Users\\dbrud\\Tools\\ffmpeg-gvr\\ffmpeg-n8.1-latest-win64-gpl-8.1\\bin\\ffmpeg.exe';
const FFMPEG = arg('ffmpeg', fs.existsSync(FF_DEFAULT) ? FF_DEFAULT : 'ffmpeg');
const t0 = process.hrtime.bigint();
const nowMs = () => Number(process.hrtime.bigint() - t0) / 1e6;
const log = (...a) => console.log(nowMs().toFixed(0).padStart(8), ...a);
const b64u = (b) => b.toString('base64url');
const proof = (role, ch, cn, sn) => b64u(crypto.createHmac('sha256', KEY).update(`GVR-AUTH-1|${role}|${ch}|${SID}|${cn}|${sn}`).digest());
const parseAuth = (v) => Object.fromEntries(String(v).split(';').map((p) => p.trim()).filter((p) => p.includes('=')).map((p) => [p.slice(0, p.indexOf('=')).trim(), p.slice(p.indexOf('=') + 1).trim()]));

let clients = [], nextId = 1, frameId = 0, lastInputSeq = 0, inputCount = 0, lobbyOver = LOBBY_S <= 0, preAuth = 0;
const stats = { sent: 0, acks: 0, ackMsSum: 0, ackMsMax: 0, encoderRestarts: 0, backlogSkips: 0, pointer: 0, lastPointer: '' };

// ---------------------------------------------------------------- WebSocket 쓰기(메시지 1개 = write 1번)
function frame(opcode, payload) {
  const n = payload.length; let h;
  if (n < 126) { h = Buffer.from([0x80 | opcode, n]); }
  else if (n < 65536) { h = Buffer.alloc(4); h[0] = 0x80 | opcode; h[1] = 126; h.writeUInt16BE(n, 2); }
  else { h = Buffer.alloc(10); h[0] = 0x80 | opcode; h[1] = 127; h.writeBigUInt64BE(BigInt(n), 2); }
  return Buffer.concat([h, payload]);
}
const sendText = (c, obj) => { if (!c.sock.destroyed) c.sock.write(frame(1, Buffer.from(typeof obj === 'string' ? obj : JSON.stringify(obj)))); };
function closeWith(c, code) { if (c.sock.destroyed) return; const b = Buffer.alloc(2); b.writeUInt16BE(code); c.sock.write(frame(8, b)); c.sock.end(); }
function httpReply(sock, status, type, body, extra) {
  const b = Buffer.isBuffer(body) ? body : Buffer.from(body);
  sock.end(Buffer.concat([Buffer.from(`HTTP/1.1 ${status}\r\nContent-Type: ${type}\r\nContent-Length: ${b.length}\r\n${extra || ''}Cache-Control: no-store\r\nConnection: close\r\n\r\n`), b]));
}
const httpError = (sock, status, code, msg) => httpReply(sock, status, 'application/json; charset=utf-8', JSON.stringify({ t: 'err', code, msg }), `X-GVR-Error: ${code}\r\n`);

// ---------------------------------------------------------------- 녹화해 둔 H.264 를 정밀한 간격으로 재생
// 시작할 때 ffmpeg 로 CLIP_S 초를 한 번 인코딩해(실제 서버와 같은 인코더 인자, 입력만 테스트 패턴) 메모리에 두고,
// 프레임마다 INTERVAL ms 간격으로 내보냅니다. 끝에 닿으면 처음(설정 + 키프레임)으로 돌아갑니다(GOP 600 이라 키프레임은 맨 앞 1장).
// 처음에는 ffmpeg -re 로 실시간 인코딩했는데, Windows 타이머 때문에 15.8ms 간격 + 가끔 32ms 로 몰려 나와 기준 소스로 쓸 수 없었습니다.
const CLIP_S = Number(arg('clip', 20)), INTERVAL = Number(arg('interval', 1000 / FPS));
let clip = [], clipReady = false, playing = false, playIndex = 0, playDue = 0, queue = [];           // queue: 아직 못 보낸 메시지(순서 큐)
function recordClip() {
  const enc = CODEC === 'x264'
    ? ['-c:v', 'libx264', '-preset', 'veryfast', '-tune', 'zerolatency', '-bf', '0', '-g', '600', '-b:v', KBPS + 'k', '-maxrate', KBPS + 'k', '-bufsize', Math.max(100, KBPS / 30 | 0) + 'k', '-profile:v', 'high', '-pix_fmt', 'yuv420p']
    : ['-pix_fmt', 'yuv420p', '-c:v', 'h264_nvenc', '-preset', 'p1', '-tune', 'ull', '-zerolatency', '1', '-delay', '0', '-bf', '0', '-g', '600', '-rc', 'cbr', '-b:v', KBPS + 'k', '-maxrate', KBPS + 'k', '-bufsize', Math.max(100, KBPS / 30 | 0) + 'k', '-profile:v', 'high'];
  const p = spawn(FFMPEG, ['-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', `testsrc2=size=${W}x${H}:rate=${FPS}`, '-t', String(CLIP_S), ...enc, '-flush_packets', '1', '-flvflags', 'no_duration_filesize', '-f', 'flv', 'pipe:1'], { stdio: ['ignore', 'pipe', 'pipe'] });
  p.stderr.on('data', (d) => log('ffmpeg:', String(d).trim().slice(0, 300)));
  let buf = Buffer.alloc(0), headerDone = false;
  p.stdout.on('data', (d) => {
    buf = buf.length ? Buffer.concat([buf, d]) : d;
    if (!headerDone) { if (buf.length < 13) return; buf = buf.subarray(13); headerDone = true; }     // FLV 헤더 9 + 이전 태그 크기 4
    while (buf.length >= 11) {
      const size = buf.readUIntBE(1, 3);
      if (buf.length < 11 + size + 4) break;
      const type = buf[0], data = buf.subarray(11, 11 + size);
      buf = buf.subarray(11 + size + 4);
      if (type !== 9 || size < 5 || (data[0] & 0x0F) !== 7 || data[1] > 1) continue;                    // AVC 영상 태그만
      clip.push({ payload: Buffer.from(data.subarray(5)), key: (data[0] >> 4) === 1, config: data[1] === 0 });   // 앞 5바이트를 뗀 것 = avcC 또는 AVCC
    }
  });
  p.on('exit', (code) => {
    clipReady = clip.length > 10 && clip[0].config;
    const bytes = clip.reduce((a, c) => a + c.payload.length, 0);
    log(`clip: ${clip.length} messages, ${(bytes / 1024 / 1024).toFixed(1)} MB, ${(bytes * 8 / CLIP_S / 1000).toFixed(0)} kbps, ffmpeg exit ${code}, ready=${clipReady}`);
    if (!clipReady) process.exit(1);
    refreshSeats.last = 0; refreshSeats('clip ready');
  });
}
function startEncoder(why) {          // 이름은 실제 서버를 따름: 설정 + 키프레임부터 다시 내보냄
  if (!clipReady) return;
  queue = []; playIndex = 0; playDue = nowMs(); stats.encoderRestarts++;
  if (!playing) { playing = true; tick(); }
  log('stream restart:', why);
}
function stopEncoder() { playing = false; queue = []; }
function tick() {
  if (!playing) return;
  const now = nowMs();
  if (now >= playDue) {
    const m = clip[playIndex];
    onEncoded(m.payload, m.key, m.config, playDue);
    playIndex++;
    if (m.config) { playDue = Math.max(playDue, now); } else { playDue += INTERVAL; if (now - playDue > 100) playDue = now; }
    if (playIndex >= clip.length) playIndex = 0;
    return setImmediate(tick);
  }
  setImmediate(tick);      // 돌면서 기다림(코어 하나를 씀). Windows 의 node 타이머는 15.6ms 단위라 setTimeout 으로는 간격이 15.9/26ms 로 흔들렸습니다. 보는 클라이언트가 있을 때만 돕니다.
}

function onEncoded(payload, key, config, capMs) {
  const h = Buffer.alloc(28);
  h.writeUInt32LE(frameId++ >>> 0, 0); h.writeUInt16LE(W, 4); h.writeUInt16LE(H, 6);
  h.writeInt32LE(config ? 0 : lastInputSeq, 8); h.writeUInt32LE(3000, 12); h.writeUInt32LE(8000, 16);
  h.writeUInt32LE(2 | (key ? 4 : 0) | (config ? 8 : 0), 20); h.writeUInt32LE(Math.round((config ? nowMs() : capMs) * 10) >>> 0, 24);
  queue.push({ msg: frame(2, Buffer.concat([h, payload])), at: nowMs() });
  pump();
}

const activeVideo = () => clients.find((c) => c.kind === 'video' && c.ready && !c.standby && !c.lobby);
function pump() {
  const c = activeVideo();
  if (!c) { queue = []; return; }
  // 역압: 가장 오래된 미확인이 150ms 이상이거나 미확인 32장이면 더 안 보냄(실제 서버의 H.264 규칙)
  while (queue.length) {
    const oldest = c.unacked.length ? nowMs() - c.unacked[0] : 0;
    if (oldest >= 150 || c.unacked.length >= 32) break;
    const m = queue.shift();
    c.unacked.push(nowMs()); c.sock.write(m.msg); stats.sent++;
  }
  if (queue.length > 120) { stats.backlogSkips += queue.length; startEncoder('backlog'); }
  if (c.unacked.length && nowMs() - c.unacked[0] > 1000) { c.unacked = []; }      // 확인 시간 초과: 미확인을 비우고 계속
}
setInterval(pump, 20);

// ---------------------------------------------------------------- 자리
function refreshSeats(reason) {
  const v = activeVideo();
  if (v && v.id !== refreshSeats.last) { refreshSeats.last = v.id; startEncoder('client ' + v.id + ' (' + reason + ')'); }
  if (!v) { refreshSeats.last = 0; stopEncoder(); }
}
function setStandby(group, standby, msg) {
  for (const c of clients.filter((x) => x.did === group)) {
    c.standby = standby;
    if (c.kind === 'video' && c.seatCap) sendText(c, standby ? { t: 'seat', s: 'standby', who: 'page', msg: msg || '다른 기기가 보고 있습니다.' } : { t: 'seat', s: 'active' });
  }
  refreshSeats('seat');
}

// ---------------------------------------------------------------- 연결
const server = net.createServer((sock) => {
  sock.setNoDelay(true);
  let head = Buffer.alloc(0), upgraded = false, c = null, rx = Buffer.alloc(0);
  const loopback = sock.remoteAddress === '127.0.0.1' || sock.remoteAddress === '::ffff:127.0.0.1' || sock.remoteAddress === '::1';
  sock.on('error', () => { });
  sock.on('close', () => { if (c) { clients = clients.filter((x) => x !== c); if (c.authPending) preAuth--; log('close', c.kind, c.id); refreshSeats('leave'); } });
  sock.on('data', (d) => {
    if (upgraded) { rx = rx.length ? Buffer.concat([rx, d]) : d; readFrames(); return; }
    head = Buffer.concat([head, d]);
    const end = head.indexOf('\r\n\r\n');
    if (end < 0) { if (head.length > 16384) sock.destroy(); return; }
    const lines = head.subarray(0, end).toString('latin1').split('\r\n');
    const [method, target] = lines[0].split(' ');
    const headers = {}; for (const l of lines.slice(1)) { const i = l.indexOf(':'); if (i > 0) headers[l.slice(0, i).trim().toLowerCase()] = l.slice(i + 1).trim(); }
    const url = new URL(target, 'http://x'); const p = url.pathname;
    rx = head.subarray(end + 4);
    if ((p === '/video' || p === '/input') && /websocket/i.test(headers['upgrade'] || '')) return doUpgrade(p.slice(1), headers);
    if (method !== 'GET' && method !== 'HEAD') return httpReply(sock, '405 Method Not Allowed', 'text/plain', '');
    if (p === '/c') {      // 착륙 페이지 파일이 없으면 죽지 않고 404 (코드 리뷰 m17)
      const landing = path.join(__dirname, 'landing.html');
      if (!fs.existsSync(landing)) return httpReply(sock, '404 Not Found', 'text/plain', 'no landing.html');
      return httpReply(sock, '200 OK', 'text/html; charset=utf-8', fs.readFileSync(landing));
    }
    if (p === '/app.json') { const f = path.join(__dirname, '..', 'dist', 'app.json'); return fs.existsSync(f) ? httpReply(sock, '200 OK', 'application/json', fs.readFileSync(f)) : httpError(sock, '404 Not Found', 'not_found', ''); }
    if (p === '/apk') {
      const dist = path.join(__dirname, '..', 'dist'); const f = fs.existsSync(dist) ? fs.readdirSync(dist).find((x) => x.endsWith('.apk')) : null;
      if (!f) return httpError(sock, '404 Not Found', 'not_found', 'APK 가 없습니다');
      return httpReply(sock, '200 OK', 'application/vnd.android.package-archive', fs.readFileSync(path.join(dist, f)), `Content-Disposition: attachment; filename="${f}"\r\n`);
    }
    if (loopback && p === '/stats') return httpReply(sock, '200 OK', 'application/json', JSON.stringify({ nowMs: nowMs(), clients: clients.map((x) => ({ id: x.id, kind: x.kind, did: x.did, auth: x.auth, standby: x.standby, lobby: x.lobby, unacked: x.unacked.length })), stats, ackMs: stats.acks ? stats.ackMsSum / stats.acks : 0, lastInputSeq, queue: queue.length }));
    if (loopback && p === '/ctl') {
      const q = url.searchParams;
      if (q.has('close')) for (const x of clients.slice()) closeWith(x, Number(q.get('close')));
      if (q.has('standby')) for (const g of new Set(clients.map((x) => x.did))) setStandby(g, true);
      if (q.has('active')) for (const g of new Set(clients.map((x) => x.did))) setStandby(g, false);
      if (q.has('stale')) for (const x of clients.filter((y) => y.kind === 'video')) sendText(x, q.get('stale') ? { t: 's', stale: true, msg: q.get('stale') } : { t: 's', stale: false });
      return httpReply(sock, '200 OK', 'text/plain', 'ok');
    }
    return httpError(sock, '404 Not Found', 'not_found', '');
  });

  function doUpgrade(kind, headers) {
    const accept = crypto.createHash('sha1').update(headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    let resp = `HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n`;
    c = { id: nextId++, kind, sock, unacked: [], ready: false, standby: false, lobby: false, auth: 'none', did: 'page', seatCap: false, authPending: false, cn: '', sn: '' };
    const a = headers['x-gvr-auth'];
    if (AUTH && a) {
      const f = parseAuth(a);
      if (f.v !== '1') { c = null; return httpError(sock, '426 Upgrade Required', 'auth_version', '인증 절차 버전이 맞지 않습니다'); }
      if (f.proto !== '2') { c = null; return httpError(sock, '426 Upgrade Required', 'proto_version', '프로토콜 버전이 맞지 않습니다(서버 2)'); }
      if (f.sid !== SID) { c = null; return httpError(sock, '403 Forbidden', 'unknown_server', '이 서버의 QR 이 아닙니다'); }
      if (!/^[A-Za-z0-9_-]{22}$/.test(f.cn || '') || !/^[a-z0-9]{12}$/.test(f.did || '')) { c = null; return httpError(sock, '400 Bad Request', 'bad_request', 'cn/did 형식'); }
      if (preAuth >= 8) { c = null; return httpError(sock, '503 Service Unavailable', 'busy', ''); }
      c.cn = f.cn; c.sn = b64u(crypto.randomBytes(16)); c.did = 'app:' + f.did; c.take = f.take === '1'; c.seatCap = (f.caps || '').split(',').includes('seat');
      c.authPending = true; preAuth++;
      resp += `X-GVR-Auth: v=1; sn=${c.sn}; mac=${proof('srv', kind, c.cn, c.sn)}; proto=2; app=${APPVER}; caps=h264,seat,take; name=Replay\r\n`;
      c.authTimer = setTimeout(() => { if (c.authPending) { log('auth timeout', c.id); closeWith(c, 4401); } }, 3000);
    } else if (AUTH && !loopback) { c = null; return httpError(sock, '401 Unauthorized', 'auth_required', 'QR 연결이 필요합니다'); }
    upgraded = true;
    sock.write(resp + '\r\n');
    clients.push(c);
    log('open', kind, c.id, sock.remoteAddress, a ? 'auth-header' : 'no-auth-header');
    if (!c.authPending) admit();
    if (rx.length) readFrames();
  }

  function admit() {
    c.ready = true;
    c.lobby = !lobbyOver && c.auth === 'ok';
    c.standby = START_STANDBY && c.auth === 'ok' && !setStandby.taken;
    if (c.kind === 'video') {
      sendText(c, '{"t":"hello","v":2}');
      if (c.seatCap) sendText(c, c.lobby ? { t: 'seat', s: 'lobby', msg: 'Unity 에서 Play 를 누르면 화면이 나옵니다.' } : c.standby ? { t: 'seat', s: 'standby', who: 'page', msg: '다른 기기가 보고 있습니다.' } : { t: 'seat', s: 'active' });
      refreshSeats('join');
    } else { inputCount++; if (clients.filter((x) => x.kind === 'input').length === 1) lastInputSeq = 0; }
  }

  function readFrames() {
    while (rx.length >= 2) {
      const op = rx[0] & 0x0F, masked = (rx[1] & 0x80) !== 0; let len = rx[1] & 0x7F, off = 2;
      if (len === 126) { if (rx.length < 4) return; len = rx.readUInt16BE(2); off = 4; } else if (len === 127) { if (rx.length < 10) return; len = Number(rx.readBigUInt64BE(2)); off = 10; }
      if (!masked || len > 65536) { sock.destroy(); return; }
      if (rx.length < off + 4 + len) return;
      const mask = rx.subarray(off, off + 4), data = Buffer.from(rx.subarray(off + 4, off + 4 + len));
      for (let i = 0; i < len; i++) data[i] ^= mask[i & 3];
      rx = rx.subarray(off + 4 + len);
      if (op === 8) { sock.end(); return; }
      if (op === 9) { sock.write(frame(10, data)); continue; }
      if (op === 1) onText(data.toString('utf8'));
    }
  }

  function onText(text) {
    if (c.authPending) {
      let mac = ''; try { mac = JSON.parse(text).mac || ''; } catch (e) { }
      const want = proof('cli', c.kind, c.cn, c.sn);
      c.authPending = false; preAuth--; clearTimeout(c.authTimer);
      if (mac.length !== want.length || !crypto.timingSafeEqual(Buffer.from(mac), Buffer.from(want))) { log('auth failed', c.id); return closeWith(c, 4401); }
      c.auth = 'ok'; log('auth ok', c.kind, c.id, c.did, 'take=' + (c.take ? 1 : 0));
      return admit();
    }
    if (c.kind === 'video') {
      if (text[0] !== '{') { const t = c.unacked.shift(); if (t !== undefined) { const ms = nowMs() - t; stats.acks++; stats.ackMsSum += ms; if (ms > stats.ackMsMax) stats.ackMsMax = ms; } pump(); return; }
      if (text.includes('"t":"caps"')) { c.h264 = text.includes('"h264":1'); return; }
      if (text.includes('"t":"key"')) { log('key request from', c.id); if (activeVideo() === c) startEncoder('key request'); return; }
      if (text.includes('"t":"take"')) { log('take from', c.id); setStandby.taken = true; setStandby(c.did, false); return; }
      return;                                    // 모르는 메시지는 버림(R2)
    }
    let m; try { m = JSON.parse(text); } catch (e) { return; }
    if (typeof m.t !== 'string' || m.t.length !== 1) return;
    if (m.t === 'p') return sendText(c, `{"t":"P","ts":${m.ts}}`);
    if (c.standby || c.lobby) return;
    if ('dmuc'.includes(m.t)) { stats.pointer++; stats.lastPointer = text; if (m.s > lastInputSeq) lastInputSeq = m.s; if (m.t !== 'm') log('touch', text); }
  }
});

if (LOBBY_S > 0) setTimeout(() => { lobbyOver = true; log('lobby over: closing with 4410'); for (const x of clients.slice()) closeWith(x, 4410); }, LOBBY_S * 1000);
recordClip();
server.listen(PORT, BIND, () => log(`replay server ${BIND}:${PORT} auth=${AUTH ? 'on sid=' + SID : 'off'} lobby=${LOBBY_S}s standby=${START_STANDBY} ffmpeg=${path.basename(FFMPEG)}`));
process.on('SIGINT', () => process.exit(0));
