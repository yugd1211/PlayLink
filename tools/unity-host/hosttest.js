// Unity 쪽 앞단(PlayLinkHost)과 입장 심사(PlayLinkGate), 자리 배정(PlayLinkSeats)을 이 PC에서 시험하는 스크립트 클라이언트.
// Unity 코드와 독립으로 쓴 구현입니다(Node 의 net + crypto 만 사용, WebSocket 을 직접 다룸). 폰이 필요 없습니다.
//
// 사용: node hosttest.js <시험 이름|all> --secret <프로젝트>/Library/PlayLink/secret.json [--host 192.168.25.5] [--port 7300] [--seconds 4]
//   시험 이름: vectors reject paths auth abuse limits legacy seats lobby
//   - vectors : 설계 3.2 의 시험 벡터 4개를 이 스크립트의 계산과 대조(서버 불필요)
//   - reject  : 헤더 없음 401, sid 다름 403, 버전 426, 형식 400, Origin 403 (앞단)
//   - paths   : 앞단의 /c /app.json /apk 와 /, /stats, /debug 404, POST 405
//   - auth    : 인증 성공(영상 + 입력), hello → seat, 프레임 수신(Play 중 + 테스트 패턴일 때), 입력 ping
//   - abuse   : 틀린 토큰, 반사(서버 증명을 되보냄), 다시 보내기(지난 연결의 auth 메시지), 침묵 → 전부 4401
//   - limits  : 1바이트씩 천천히 보내는 연결이 3초에 닫힘, 17번째 인증 전 연결이 바로 닫힘, 9번째 인증 대기가 503
//   - legacy  : 루프백(127.0.0.1)의 인증 없는 연결(Chrome 페이지 경로)이 그대로 되는지, 다른 Origin 은 403, 루프백에서도 앱 인증이 되는지
//   - seats   : 앱 2개와 페이지 1개로 자리 규칙(한쪽만 영상, 자동 재접속은 뺏지 않음, take 로 넘김, 떠나면 이어받음)
//   - lobby   : (Unity 서버가 꺼져 있을 때) hello + seat lobby, 입력 ping 응답. 켜지면 4410 을 받는지는 --wait-kick 초 동안 기다려 확인
// 토큰은 화면에 찍지 않습니다.
'use strict';
const net = require('net');
const crypto = require('crypto');
const fs = require('fs');

const args = process.argv.slice(2);
const testName = args[0] || 'all';
const opt = (name, fallback) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : fallback; };
const HOST = opt('host', '192.168.25.5');
const PORT = Number(opt('port', 7300));
const SECONDS = Number(opt('seconds', 4));
const WAIT_KICK = Number(opt('wait-kick', 0));
const SECRET_PATH = opt('secret', null);
const HEADER_BYTES = 28;

const b64url = (buf) => buf.toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
const fromB64url = (text) => Buffer.from(text.replace(/-/g, '+').replace(/_/g, '/'), 'base64');
const proof = (key, role, channel, sid, cn, sn) => b64url(crypto.createHmac('sha256', key).update(`PLAYLINK-AUTH-1|${role}|${channel}|${sid}|${cn}|${sn}`, 'ascii').digest());
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const randomDid = () => crypto.randomBytes(6).toString('hex');

let secret = null;
function loadSecret() {
  if (secret) { return secret; }
  if (!SECRET_PATH) { throw new Error('--secret <secret.json> 이 필요합니다'); }
  const data = JSON.parse(fs.readFileSync(SECRET_PATH, 'utf8').replace(/^﻿/, ''));
  secret = { sid: data.serverId, key: fromB64url(data.token) };
  return secret;
}

let passed = 0, failed = 0;
function check(name, ok, detail) {
  if (ok) { passed++; } else { failed++; }
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${name}${detail !== undefined ? '  → ' + detail : ''}`);
}

// ---- 가장 낮은 수준: TCP 로 붙어 HTTP 요청을 보내고 응답 헤더까지 읽는다 ----
function request({ host = HOST, port = PORT, method = 'GET', path = '/', headers = {}, raw = null }) {
  return new Promise((resolve) => {
    const sock = net.connect({ host, port });
    const startedAt = Date.now();
    let buf = Buffer.alloc(0), done = false;
    const finish = (result) => { if (!done) { done = true; resolve(Object.assign({ sock, ms: Date.now() - startedAt }, result)); } };
    sock.setNoDelay(true);
    sock.on('connect', () => {
      const lines = [`${method} ${path} HTTP/1.1`, `Host: ${host}:${port}`];
      for (const name of Object.keys(headers)) { lines.push(`${name}: ${headers[name]}`); }
      sock.write(raw !== null ? raw : lines.join('\r\n') + '\r\n\r\n');
    });
    sock.on('data', (chunk) => {
      if (done) { return; }
      buf = Buffer.concat([buf, chunk]);
      const end = buf.indexOf('\r\n\r\n');
      if (end < 0) { return; }
      const head = buf.subarray(0, end).toString('latin1').split('\r\n');
      const status = Number(head[0].split(' ')[1]);
      const responseHeaders = {};
      for (const line of head.slice(1)) { const c = line.indexOf(':'); if (c > 0) { responseHeaders[line.slice(0, c).trim().toLowerCase()] = line.slice(c + 1).trim(); } }
      const rest = buf.subarray(end + 4);
      if (status === 101) { sock.removeAllListeners('data'); finish({ status, headers: responseHeaders, rest }); return; }
      // 일반 응답: 본문을 Content-Length 만큼(또는 닫힐 때까지) 모은다
      const want = Number(responseHeaders['content-length'] || -1);
      const body = [rest]; let got = rest.length;
      sock.removeAllListeners('data');
      const complete = () => finish({ status, headers: responseHeaders, body: Buffer.concat(body) });
      if (method === 'HEAD' || (want >= 0 && got >= want)) { complete(); return; }
      sock.on('data', (more) => { body.push(more); got += more.length; if (want >= 0 && got >= want) { complete(); } });
      sock.on('close', complete);
    });
    sock.on('error', (e) => finish({ status: 0, error: e.code || String(e) }));
    sock.on('close', () => finish({ status: 0, error: 'closed', closedAfterMs: Date.now() - startedAt }));
  });
}

// ---- WebSocket(클라이언트 쪽): 마스크해서 보내고, 서버 프레임을 메시지로 나눈다 ----
class Ws {
  constructor(sock, rest) {
    this.sock = sock; this.buf = rest || Buffer.alloc(0);
    this.texts = []; this.frames = []; this.closeCode = null; this.closed = false; this.closedAt = 0;
    this.onText = null; this.autoAck = true;
    sock.on('data', (chunk) => { this.buf = Buffer.concat([this.buf, chunk]); this.parse(); });
    sock.on('close', () => { this.closed = true; this.closedAt = this.closedAt || Date.now(); });
    sock.on('error', () => { this.closed = true; });
    this.parse();
  }
  parse() {
    for (;;) {
      if (this.buf.length < 2) { return; }
      const opcode = this.buf[0] & 0x0F;
      let len = this.buf[1] & 0x7F, off = 2;
      if (len === 126) { if (this.buf.length < 4) { return; } len = this.buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (this.buf.length < 10) { return; } len = Number(this.buf.readBigUInt64BE(2)); off = 10; }
      if (this.buf.length < off + len) { return; }
      const payload = this.buf.subarray(off, off + len);
      this.buf = this.buf.subarray(off + len);
      if (opcode === 1) { const text = payload.toString('utf8'); this.texts.push(text); if (this.onText) { this.onText(text); } }
      else if (opcode === 2) {
        const f = { at: Date.now(), id: payload.readUInt32LE(0), w: payload.readUInt16LE(4), h: payload.readUInt16LE(6), flags: payload.readUInt32LE(20), bytes: payload.length, order: this.texts.length };
        this.frames.push(f);
        if (this.autoAck) { this.send('a'); }
      } else if (opcode === 8) { this.closeCode = payload.length >= 2 ? payload.readUInt16BE(0) : 1005; this.closedAt = Date.now(); }
    }
  }
  send(text) {
    if (this.closed) { return; }
    const data = Buffer.from(text, 'utf8'), mask = crypto.randomBytes(4);
    const head = data.length < 126 ? Buffer.from([0x81, 0x80 | data.length]) : Buffer.from([0x81, 0x80 | 126, data.length >> 8, data.length & 255]);
    const body = Buffer.alloc(data.length);
    for (let i = 0; i < data.length; i++) { body[i] = data[i] ^ mask[i & 3]; }
    this.sock.write(Buffer.concat([head, mask, body]));   // 메시지 1개 = 쓰기 1번
  }
  close() { if (!this.closed) { this.sock.destroy(); this.closed = true; } }
  h264() { return this.frames.filter((f) => (f.flags & 2) && !(f.flags & 8)); }
  since(t) { return this.frames.filter((f) => f.at >= t && !(f.flags & 8)); }
  has(pattern) { return this.texts.some((t) => t.includes(pattern)); }
}

const upgradeHeaders = (extra) => Object.assign({ Upgrade: 'websocket', Connection: 'Upgrade', 'Sec-WebSocket-Key': crypto.randomBytes(16).toString('base64'), 'Sec-WebSocket-Version': '13' }, extra || {});
const authHeader = (f) => Object.keys(f).map((k) => `${k}=${f[k]}`).join('; ');

// 앱처럼 붙는다. tamper 로 일부러 틀리게 만들 수 있다.
async function openApp({ host = HOST, port = PORT, channel = 'video', did = randomDid(), take = 0, caps = 'h264,seat', key = null, sid = null, fields = {}, clientMac = null, cn = null, sendAuth = true, sendCaps = true }) {
  const s = loadSecret();
  const useKey = key || s.key;
  const nonce = cn || b64url(crypto.randomBytes(16));
  const header = Object.assign({ v: 1, sid: sid || s.sid, cn: nonce, proto: 2, app: 1, did, dev: 'node-test', take, caps }, fields);
  const res = await request({ host, port, path: '/' + channel, headers: upgradeHeaders({ 'X-PlayLink-Auth': authHeader(header) }) });
  const out = { status: res.status, errorCode: res.headers ? res.headers['x-playlink-error'] : undefined, res, did, cn: nonce };
  if (res.status !== 101) { if (res.sock) { res.sock.destroy(); } return out; }
  const reply = {};
  for (const part of (res.headers['x-playlink-auth'] || '').split(';')) { const i = part.indexOf('='); if (i > 0) { reply[part.slice(0, i).trim()] = part.slice(i + 1).trim(); } }
  out.reply = reply;
  out.serverMacOk = reply.mac === proof(useKey, 'srv', channel, header.sid, nonce, reply.sn || '');
  const ws = new Ws(res.sock, res.rest);
  out.ws = ws;
  out.authMessage = JSON.stringify({ t: 'auth', mac: clientMac ? clientMac(reply) : proof(useKey, 'cli', channel, header.sid, nonce, reply.sn || '') });
  if (sendAuth) { ws.send(out.authMessage); }
  if (sendAuth && sendCaps && channel === 'video') { ws.onText = (text) => { if (text.includes('"hello"')) { ws.send('{"t":"caps","h264":1}'); } }; if (ws.has('"hello"')) { ws.send('{"t":"caps","h264":1}'); } }
  return out;
}

async function waitFor(predicate, ms) {
  const end = Date.now() + ms;
  while (Date.now() < end) { if (predicate()) { return true; } await sleep(20); }
  return predicate();
}

// ============================================================ 시험들
const tests = {};

tests.vectors = async () => {
  const key = Buffer.from('000102030405060708090a0b0c0d0e0f', 'hex');
  const cn = b64url(Buffer.from('101112131415161718191a1b1c1d1e1f', 'hex')), sn = b64url(Buffer.from('202122232425262728292a2b2c2d2e2f', 'hex'));
  check('토큰 t', b64url(key) === 'AAECAwQFBgcICQoLDA0ODw');
  check('cn / sn', cn === 'EBESExQVFhcYGRobHB0eHw' && sn === 'ICEiIyQlJicoKSorLC0uLw');
  check('video/srv', proof(key, 'srv', 'video', 'PLKTEST1', cn, sn) === 'rASdAm0R4E9eOg8WrTdhWEl_tYFZPYQaTVDJQFcq7MI');
  check('video/cli', proof(key, 'cli', 'video', 'PLKTEST1', cn, sn) === 'Kum7Uc2NTxLHeflx39gkJOZfTH0T9JUHS--WvYy_kR8');
  check('input/srv', proof(key, 'srv', 'input', 'PLKTEST1', cn, sn) === 'L04dFts1PdX9hTbKhFnitvOCwRrHVJlvmdKtLpSdFms');
  check('input/cli', proof(key, 'cli', 'input', 'PLKTEST1', cn, sn) === '5GsiBVEovxdUf6qehtU9FF2vtYkyHPwfOdV_X5P_Ijg');
};

tests.reject = async () => {
  let r = await request({ path: '/video', headers: upgradeHeaders() });
  check('헤더 없음 → 401 auth_required', r.status === 401 && r.headers['x-playlink-error'] === 'auth_required', `${r.status} ${r.headers && r.headers['x-playlink-error']} body=${r.body}`);
  if (r.sock) { r.sock.destroy(); }
  r = await request({ path: '/input', headers: upgradeHeaders() });
  check('입력 채널도 헤더 없음 → 401', r.status === 401, r.status);
  let a = await openApp({ sid: 'ABCDEFGH' });
  check('sid 다름 → 403 unknown_server', a.status === 403 && a.errorCode === 'unknown_server', `${a.status} ${a.errorCode}`);
  a = await openApp({ fields: { v: 2 } });
  check('v=2 → 426 auth_version', a.status === 426 && a.errorCode === 'auth_version', `${a.status} ${a.errorCode}`);
  a = await openApp({ fields: { proto: 3 } });
  check('proto=3 → 426 proto_version', a.status === 426 && a.errorCode === 'proto_version', `${a.status} ${a.errorCode} ${a.res.body}`);
  a = await openApp({ cn: 'short' });
  check('cn 형식 오류 → 400 bad_request', a.status === 400 && a.errorCode === 'bad_request', `${a.status} ${a.errorCode}`);
  a = await openApp({ did: 'BAD' });
  check('did 형식 오류 → 400 bad_request', a.status === 400 && a.errorCode === 'bad_request', `${a.status} ${a.errorCode}`);
  const s = loadSecret();
  r = await request({ path: '/video', headers: upgradeHeaders({ Origin: 'http://localhost:' + PORT, 'X-PlayLink-Auth': authHeader({ v: 1, sid: s.sid, cn: b64url(crypto.randomBytes(16)), proto: 2, app: 1, did: randomDid() }) }) });
  check('앞단에 Origin 이 있는 요청 → 403 bad_origin', r.status === 403 && r.headers['x-playlink-error'] === 'bad_origin', `${r.status} ${r.headers && r.headers['x-playlink-error']}`);
  r = await request({ path: '/x/audio', headers: upgradeHeaders() });
  check('등록되지 않은 채널 /x/audio → 404', r.status === 404, r.status);
};

tests.paths = async () => {
  for (const path of ['/', '/index.html', '/stats', '/debug?pattern=1', '/favicon.ico']) {
    const r = await request({ path });
    check(`GET ${path} → 404 not_found`, r.status === 404 && r.headers['x-playlink-error'] === 'not_found', r.status);
  }
  let r = await request({ path: '/c' });
  const html = r.body ? r.body.toString('utf8') : '';
  check('GET /c → 200 html', r.status === 200 && /text\/html/.test(r.headers['content-type']) && html.includes('intent://'), `${r.status} ${r.headers && r.headers['content-type']} ${html.length}B`);
  check('착륙 페이지에 토큰이 없음', !html.includes(b64url(loadSecret().key)));
  check('착륙 페이지 8KB 이하(설계 9.5)', html.length > 0 && r.body.length <= 8192, r.body ? r.body.length + 'B' : '-');
  r = await request({ path: '/c?noapp=1' });
  check('GET /c?noapp=1 → 200', r.status === 200, r.status);
  r = await request({ path: '/c', method: 'HEAD' });
  check('HEAD /c → 200, 본문 없음', r.status === 200 && Number(r.headers['content-length']) > 0, `${r.status} len=${r.headers && r.headers['content-length']}`);
  if (r.sock) { r.sock.destroy(); }
  r = await request({ path: '/app.json' });
  check('GET /app.json', r.status === 200 || r.status === 404, `${r.status} ${r.body ? r.body.toString('utf8').slice(0, 120) : ''}`);
  r = await request({ path: '/apk' });
  if (r.status === 200) {
    check('GET /apk → 200, 형식과 길이', r.headers['content-type'] === 'application/vnd.android.package-archive' && Number(r.headers['content-length']) === r.body.length && /attachment; filename="PlayLink.*\.apk"/.test(r.headers['content-disposition']),
      `${r.body.length}B ${r.headers['content-disposition']} sha256=${crypto.createHash('sha256').update(r.body).digest('hex').slice(0, 16)}…`);
  } else {
    check('GET /apk → APK 없음이면 404', r.status === 404, r.status);
  }
  r = await request({ path: '/c', method: 'POST' });
  check('POST /c → 405', r.status === 405, r.status);
};

tests.auth = async () => {
  const did = randomDid();
  const t0 = Date.now();
  const v = await openApp({ did, take: 1 });
  check('영상: 101 + 서버 증명 일치', v.status === 101 && v.serverMacOk, `status ${v.status}, reply ${v.reply ? Object.keys(v.reply).join(',') : '-'}`);
  if (v.status !== 101) { return; }
  check('응답 헤더: proto=2, caps, name, app', v.reply.proto === '2' && v.reply.caps === 'h264,seat,take' && v.reply.name !== undefined && /^\d+$/.test(v.reply.app), `proto=${v.reply.proto} caps=${v.reply.caps} name=${v.reply.name} app=${v.reply.app}`);
  await waitFor(() => v.ws.texts.length >= 2 || v.ws.closed, 2000);
  check('첫 메시지가 hello v2', v.ws.texts[0] === '{"t":"hello","v":2}', v.ws.texts[0]);
  const lobby = v.ws.has('"s":"lobby"');
  check('hello 다음에 seat (active 또는 lobby)', v.ws.texts.length >= 2 && /"t":"seat","s":"(active|lobby)"/.test(v.ws.texts[1]), v.ws.texts[1]);
  const i = await openApp({ did, take: 1, channel: 'input' });
  check('입력: 101 + 서버 증명 일치', i.status === 101 && i.serverMacOk, i.status);
  i.ws.send('{"t":"p","ts":1234.5}');
  await waitFor(() => i.ws.texts.length > 0, 1500);
  check('입력 ping → {"t":"P","ts":1234.5}', i.ws.texts[0] === '{"t":"P","ts":1234.5}', i.ws.texts[0]);
  if (lobby) {
    console.log('  (Unity 서버가 꺼져 있어 대기실입니다. 프레임 수신은 Play 중에 다시 돌려 확인하세요)');
  } else {
    await sleep(SECONDS * 1000);
    const h264 = v.ws.h264(), config = v.ws.frames.filter((f) => f.flags & 8), jpeg = v.ws.frames.filter((f) => !(f.flags & 2));
    check(`프레임 수신(${SECONDS}초)`, v.ws.frames.length > 0, `H.264 ${h264.length}장(키 ${h264.filter((f) => f.flags & 4).length}), 설정 ${config.length}, JPEG ${jpeg.length}, ${h264.length ? h264[0].w + 'x' + h264[0].h : '-'}, 첫 프레임까지 ${v.ws.frames.length ? v.ws.frames[0].at - t0 : '-'}ms`);
    check('H.264 로 옴(설정 → 키프레임)', config.length >= 1 && h264.length > 0, `texts=${JSON.stringify(v.ws.texts)}`);
  }
  check('연결 유지(닫히지 않음)', !v.ws.closed && !i.ws.closed);
  v.ws.close(); i.ws.close();
};

tests.abuse = async () => {
  const wrong = crypto.randomBytes(16);
  let a = await openApp({ key: wrong });
  check('틀린 토큰: 서버 증명이 내 키로는 검증되지 않음(앱은 여기서 닫음)', a.status === 101 && a.serverMacOk === false, a.status);
  await waitFor(() => a.ws.closed, 2000);
  check('틀린 토큰으로 만든 클라이언트 증명 → 닫기 4401, hello 없음', a.ws.closeCode === 4401 && !a.ws.has('hello'), `close ${a.ws.closeCode} texts ${a.ws.texts.length}`);
  a = await openApp({ clientMac: (reply) => reply.mac });
  await waitFor(() => a.ws.closed, 2000);
  check('반사(서버 증명을 그대로 되보냄) → 4401', a.ws.closeCode === 4401 && !a.ws.has('hello'), `close ${a.ws.closeCode}`);
  // 다시 보내기: 정상 연결 하나에서 쓴 cn 과 auth 메시지를 새 연결에 그대로 쓴다(서버 임의 값이 달라 통과하면 안 됨)
  const good = await openApp({});
  await waitFor(() => good.ws.has('hello'), 2000);
  check('(준비) 정상 연결 성공', good.ws.has('hello'));
  const captured = good.authMessage;
  a = await openApp({ cn: good.cn, did: good.did, clientMac: () => JSON.parse(captured).mac });
  await waitFor(() => a.ws.closed, 2000);
  check('다시 보내기(같은 cn, 지난 연결의 증명) → 4401', a.ws.closeCode === 4401 && !a.ws.has('hello'), `close ${a.ws.closeCode}, sn 이 달랐나 ${a.reply.sn !== good.reply.sn}`);
  good.ws.close();
  // 영상 채널의 증명을 입력 채널에 쓴다
  a = await openApp({ channel: 'input', clientMac: (reply) => proof(loadSecret().key, 'cli', 'video', loadSecret().sid, a ? a.cn : '', reply.sn) });
  await waitFor(() => a.ws.closed, 2000);
  check('다른 채널의 증명 → 4401', a.ws.closeCode === 4401, `close ${a.ws.closeCode}`);
  const t0 = Date.now();
  a = await openApp({ sendAuth: false });
  await waitFor(() => a.ws.closed, 5000);
  const took = (a.ws.closedAt || Date.now()) - t0;
  check('101 뒤 침묵 → 약 3초 뒤 4401', a.ws.closeCode === 4401 && took > 2500 && took < 4000, `close ${a.ws.closeCode} after ${took}ms`);
  a = await openApp({ clientMac: () => 'x'.repeat(400) });
  await waitFor(() => a.ws.closed, 2000);
  check('너무 긴 auth 메시지 → 4401', a.ws.closeCode === 4401, `close ${a.ws.closeCode}`);
};

tests.limits = async () => {
  // 1바이트씩 천천히 보내는 연결: 읽기마다의 제한이 아니라 연결을 받은 때부터의 절대 기한(3초)이어야 한다
  await new Promise((resolve) => {
    const sock = net.connect({ host: HOST, port: PORT });
    const text = 'GET /video HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\n';
    let i = 0; const t0 = Date.now();
    const timer = setInterval(() => { if (i < text.length && !sock.destroyed) { sock.write(text[i++]); } }, 400);
    sock.on('error', () => {});
    sock.on('close', () => { clearInterval(timer); const ms = Date.now() - t0; check('0.4초마다 1바이트 → 약 3초 뒤 서버가 닫음', ms > 2500 && ms < 4500, ms + 'ms'); resolve(); });
    setTimeout(() => { if (!sock.destroyed) { clearInterval(timer); check('0.4초마다 1바이트 → 약 3초 뒤 서버가 닫음', false, '8초가 지나도 열려 있음'); sock.destroy(); resolve(); } }, 8000);
  });
  // 한 주소가 차지할 수 있는 인증 전 연결은 6개(코드 리뷰 m2: 한 기기가 전체 한도 16개를 혼자 채우지 못함). 이 PC 에서는 주소가 하나라 전체 한도 16 은 시험하지 못한다.
  const PER_ADDRESS = 6;
  const idle = [];
  for (let n = 0; n < PER_ADDRESS; n++) { const sock = net.connect({ host: HOST, port: PORT }); sock.on('error', () => {}); idle.push({ sock, closedAt: 0 }); idle[n].sock.on('close', () => { idle[n].closedAt = Date.now(); }); }
  await sleep(300);
  const t0 = Date.now();
  const r = await request({ path: '/c' });
  check(`같은 주소의 인증 전 연결 ${PER_ADDRESS}개가 차 있을 때 다음 연결 → 응답 없이 바로 닫힘`, r.status === 0 && (Date.now() - t0) < 1000, `status ${r.status} ${r.error} ${Date.now() - t0}ms`);
  await sleep(3600);
  check(`말 없는 ${PER_ADDRESS}개도 3초 뒤 전부 닫힘`, idle.every((c) => c.closedAt > 0), idle.filter((c) => c.closedAt > 0).length + '/' + PER_ADDRESS);
  for (const c of idle) { c.sock.destroy(); }
  const again = await request({ path: '/c' });
  check('그 뒤에는 다시 받음', again.status === 200, again.status);
  // 인증 대기(101 을 받고 증명을 안 보냄) 8개 + 9번째. 앞단은 주소별 한도 6 이 먼저 걸리므로 루프백 리스너(주소별 한도 없음)에서 본다.
  const lo = '127.0.0.1';
  const probe = await request({ host: lo, path: '/stats' });
  if (probe.status !== 200) { console.log('  (루프백 서버가 꺼져 있어 인증 대기 한도 시험은 건너뜁니다. Play 중에 다시 돌리세요)'); return; }
  const pending = [];
  for (let n = 0; n < 8; n++) { pending.push(await openApp({ host: lo, sendAuth: false })); }
  const ninth = await openApp({ host: lo, sendAuth: false });
  check('루프백: 인증 대기 8개가 차 있을 때 9번째 → 503 busy', ninth.status === 503 && ninth.errorCode === 'busy', `${ninth.status} ${ninth.errorCode}; 앞의 8개 상태 ${pending.map((p) => p.status).join(',')}`);
  // 인증 대기 수는 리스너별(코드 리뷰 m2): 루프백이 가득 차 있어도 앞단(QR 주소)으로는 인증이 된다
  const front = await openApp({});
  await waitFor(() => front.ws && front.ws.has('hello'), 2000);
  check('루프백의 인증 대기가 가득 차 있어도 앞단으로는 인증됨', front.status === 101 && front.ws && front.ws.has('hello'), front.status + ' ' + front.errorCode);
  if (front.ws) { front.ws.close(); }
  if (ninth.ws) { ninth.ws.close(); }
  for (const p of pending) { if (p.ws) { p.ws.close(); } }
  await sleep(300);
  const after = await openApp({});
  await waitFor(() => after.ws && after.ws.has('hello'), 2000);
  check('비운 뒤에는 정상 인증', after.status === 101 && after.ws.has('hello'), after.status);
  if (after.ws) { after.ws.close(); }
};

tests.legacy = async () => {
  const lo = '127.0.0.1';
  let r = await request({ host: lo, path: '/stats' });
  if (r.status === 0) { console.log('  (127.0.0.1:' + PORT + ' 이 닫혀 있습니다: Unity 가 Play 중이 아니거나 서버가 꺼져 있음. 이 시험은 Play 중에 돌리세요)'); check('루프백 서버가 열려 있음', false, r.error); return; }
  check('루프백 GET /stats → 200 (지금과 같음)', r.status === 200, r.status);
  r = await request({ host: lo, path: '/' });
  check('루프백 GET / → 페이지', r.status === 200 && r.body.length > 10000, `${r.status} ${r.body ? r.body.length : 0}B`);
  r = await request({ host: lo, path: '/c' });
  check('루프백 GET /c → 착륙 페이지', r.status === 200 && r.body.toString('utf8').includes('intent://'), r.status);
  r = await request({ host: lo, path: '/video', headers: upgradeHeaders({ Origin: 'http://localhost:' + PORT }) });
  check('루프백, 헤더 없음, Origin = 자기 페이지 → 101 (Chrome 페이지 경로)', r.status === 101 && !r.headers['x-playlink-auth'], r.status);
  if (r.status === 101) {
    const ws = new Ws(r.sock, r.rest);
    await waitFor(() => ws.texts.length > 0, 1500);
    check('페이지 경로의 첫 메시지 hello v2', ws.texts[0] === '{"t":"hello","v":2}', ws.texts[0]);
    ws.send('{"t":"caps","h264":1}');
    await sleep(SECONDS * 1000);
    const h264 = ws.h264();
    check(`페이지 경로로 H.264 수신(${SECONDS}초)`, h264.length > 0 && ws.frames.some((f) => f.flags & 8), `H.264 ${h264.length}장, 설정 ${ws.frames.filter((f) => f.flags & 8).length}, JPEG ${ws.frames.filter((f) => !(f.flags & 2)).length}, seat 메시지 ${ws.texts.filter((t) => t.includes('seat')).length}개(0 이어야 함)`);
    check('페이지 경로에는 seat 메시지가 가지 않음', !ws.has('"seat"'));
    ws.close();
  }
  r = await request({ host: lo, path: '/video', headers: upgradeHeaders() });
  check('루프백, 헤더 없음, Origin 없음 → 101 (측정 스크립트 경로)', r.status === 101, r.status);
  if (r.sock) { r.sock.destroy(); }
  r = await request({ host: lo, path: '/video', headers: upgradeHeaders({ Origin: 'http://evil.example' }) });
  check('루프백, 다른 Origin → 403 bad_origin', r.status === 403 && r.headers['x-playlink-error'] === 'bad_origin', r.status);
  r = await request({ host: lo, path: '/input', headers: upgradeHeaders({ Origin: 'http://127.0.0.1:' + PORT }) });
  check('루프백 입력 소켓 → 101', r.status === 101, r.status);
  if (r.status === 101) {
    const ws = new Ws(r.sock, r.rest);
    ws.send('{"t":"p","ts":77.25}');
    await waitFor(() => ws.texts.length > 0, 1500);
    check('입력 ping 응답', ws.texts[0] === '{"t":"P","ts":77.25}', ws.texts[0]);
    ws.send('{"t":"mouse","id":0,"x":0.5,"y":0.5,"s":1}');   // 두 글자 이상의 t 는 버려져야 한다(서버 쪽 /stats 의 pointerMessagesReceived 로 확인)
    await sleep(200);
    ws.close();
  }
  await sleep(300);
  const a = await openApp({ host: lo, take: 0 });
  check('루프백에서 앱 인증(USB 경로) → 101 + 서버 증명', a.status === 101 && a.serverMacOk, a.status);
  if (a.ws) { await waitFor(() => a.ws.has('seat'), 1500); check('USB 앱: hello → seat', a.ws.texts[0] === '{"t":"hello","v":2}' && a.ws.has('"t":"seat"'), JSON.stringify(a.ws.texts.slice(0, 2))); a.ws.close(); }
  const bad = await openApp({ host: lo, key: crypto.randomBytes(16) });
  if (bad.ws) { await waitFor(() => bad.ws.closed, 2000); }
  check('루프백에서도 틀린 토큰은 4401', bad.ws && bad.ws.closeCode === 4401, bad.ws ? bad.ws.closeCode : bad.status);
};

tests.seats = async () => {
  const lo = '127.0.0.1';
  const seat = (c) => { const all = c.ws.texts.filter((t) => t.includes('"t":"seat"')); return all.length ? JSON.parse(all[all.length - 1]).s : null; };
  const flowing = async (c, ms = 1200) => { const t = Date.now(); await sleep(ms); return (c.ws || c).since(t).length; };
  const didA = randomDid(), didB = randomDid();
  // 1. 앱 A 가 사람 손으로 붙음
  const a = await openApp({ did: didA, take: 1 });
  const aInput = await openApp({ did: didA, take: 1, channel: 'input' });
  await waitFor(() => seat(a) !== null, 2000);
  if (seat(a) === 'lobby') { check('Unity 서버가 돌고 있어야 하는 시험입니다(Play 중 + 테스트 패턴)', false, 'lobby'); a.ws.close(); aInput.ws.close(); return; }
  check('A(take=1) → active', seat(a) === 'active', seat(a));
  check('A 가 프레임을 받음', (await flowing(a)) > 0, a.ws.frames.length + '장 (0 이면 화면이 멈춰 있음: 테스트 패턴을 켜고 다시)');
  // 2. 앱 B 가 자동 재접속처럼(take=0) 붙음 → 뺏지 않음
  const b = await openApp({ did: didB, take: 0 });
  const bInput = await openApp({ did: didB, take: 0, channel: 'input' });
  await waitFor(() => seat(b) !== null, 2000);
  check('B(take=0) → standby, who=app', seat(b) === 'standby' && b.ws.has('"who":"app"'), b.ws.texts.join(' '));
  check('B 에는 프레임이 가지 않음', (await flowing(b)) === 0, b.ws.frames.length + '장');
  check('A 는 계속 받고 seat 변화 없음', (await flowing(a)) > 0 && seat(a) === 'active');
  check('A 는 H.264 를 유지(둘이 붙어도 MJPEG 로 떨어지지 않음)', a.ws.since(Date.now() - 1000).every((f) => f.flags & 2), `최근 1초 ${a.ws.since(Date.now() - 1000).length}장`);
  bInput.ws.send('{"t":"p","ts":5}');
  await waitFor(() => bInput.ws.texts.length > 0, 1000);
  check('대기 중인 B 의 입력 소켓도 ping 에는 답함', bInput.ws.texts[0] === '{"t":"P","ts":5}', bInput.ws.texts[0]);
  // 3. 페이지(인증 없는 루프백)가 붙음 → 앱이 보고 있으므로 대기 문구
  const pr = await request({ host: lo, path: '/video', headers: upgradeHeaders({ Origin: 'http://localhost:' + PORT }) });
  const page = pr.status === 101 ? new Ws(pr.sock, pr.rest) : null;
  if (page) {
    await waitFor(() => page.texts.length >= 2, 1500);
    page.send('{"t":"caps","h264":1}');
    check('페이지: hello 다음에 대기 문구(stale + code standby)', page.texts[0] === '{"t":"hello","v":2}' && page.has('"code":"standby"'), JSON.stringify(page.texts));
    check('페이지에는 프레임이 가지 않음', (await flowing(page)) === 0, page.frames.length + '장');
    // 페이지가 0.7초마다 다시 붙는 것처럼 5번 붙었다 떨어져도 A 는 그대로
    const before = a.ws.texts.length;
    for (let n = 0; n < 5; n++) { const r = await request({ host: lo, path: '/video', headers: upgradeHeaders({ Origin: 'http://localhost:' + PORT }) }); await sleep(120); if (r.sock) { r.sock.destroy(); } }
    await sleep(300);
    check('페이지 재접속 5번에도 A 의 자리 메시지 없음(뺏기지 않음)', a.ws.texts.length === before && (await flowing(a)) > 0, `A texts +${a.ws.texts.length - before}`);
  } else { check('페이지 경로 연결(루프백)', false, pr.status + ' ' + pr.error); }
  // 4. B 가 [여기서 보기]
  b.ws.send('{"t":"take"}');
  await waitFor(() => seat(b) === 'active' && seat(a) === 'standby', 2000);
  check('B 가 take → B active, A standby', seat(b) === 'active' && seat(a) === 'standby', `A=${seat(a)} B=${seat(b)}`);
  const tSwitch = Date.now();
  await sleep(1500);
  const bFrames = b.ws.frames.filter((f) => f.at >= tSwitch);
  check('B 가 설정 → 키프레임부터 새로 받음', bFrames.some((f) => f.flags & 8) && bFrames.some((f) => (f.flags & 4) && !(f.flags & 8)), `설정 ${bFrames.filter((f) => f.flags & 8).length}, 키 ${bFrames.filter((f) => (f.flags & 4) && !(f.flags & 8)).length}, 전체 ${bFrames.length}`);
  check('A 에는 이제 프레임이 가지 않음', (await flowing(a)) === 0);
  // 5. A 가 자동 재접속(take=0)으로 다시 붙어도 B 를 뺏지 않음
  a.ws.close(); aInput.ws.close();
  await sleep(300);
  const a2 = await openApp({ did: didA, take: 0 });
  await waitFor(() => seat(a2) !== null, 2000);
  check('A 가 take=0 으로 다시 붙음 → standby (B 유지)', seat(a2) === 'standby' && seat(b) === 'active', `A=${seat(a2)} B=${seat(b)}`);
  // 6. 페이지가 [여기서 보기]
  if (page) {
    page.send('{"t":"take"}');
    await waitFor(() => seat(b) === 'standby', 2000);
    check('페이지가 take → B standby(who=page), 페이지는 풀림', seat(b) === 'standby' && b.ws.texts[b.ws.texts.length - 1].includes('"who":"page"') && page.texts[page.texts.length - 1].includes('"stale":false'), `B=${b.ws.texts[b.ws.texts.length - 1]} page=${page.texts[page.texts.length - 1]}`);
    check('페이지가 프레임을 받음', (await flowing(page, 1500)) > 0, page.frames.length + '장');
    // 앱이 대기 중인 채로 10초 동안 스스로 뺏지 않는지는 앱 쪽 시험. 서버 쪽: take=0 재접속이 페이지를 밀어내지 않는지
    const a3 = await openApp({ did: randomDid(), take: 0 });
    await waitFor(() => seat(a3) !== null, 2000);
    check('페이지가 선호일 때 새 앱(take=0)이 붙어도 standby', seat(a3) === 'standby' && (await flowing(page)) > 0, seat(a3));
    a3.ws.close();
    // 7. 페이지가 떠나면 가장 먼저 붙은 앱이 이어받음
    page.close();
    await waitFor(() => seat(b) === 'active' || seat(a2) === 'active', 2000);
    check('페이지가 떠남 → 붙어 있는 앱 중 먼저 붙은 B 가 active', seat(b) === 'active' && seat(a2) === 'standby', `A=${seat(a2)} B=${seat(b)}`);
  }
  // 8. B 가 떠나면 A 가 이어받음
  b.ws.close(); bInput.ws.close();
  await waitFor(() => seat(a2) === 'active', 2000);
  check('B 가 떠남 → A active, 프레임 다시 옴', seat(a2) === 'active' && (await flowing(a2, 1500)) > 0, seat(a2));
  a2.ws.close();
};

// 실제 앱(가상 기기)이 보고 있는 상태에서 돌립니다: 스크립트 페이지 클라이언트가 붙고 → [여기서 보기] → 앱이 되찾기를 기다림.
// 사용: node hosttest.js withapp --secret … [--seconds 25]  (기다리는 동안 기기의 앱에서 [여기서 보기]를 누릅니다)
tests.withapp = async () => {
  const lo = '127.0.0.1';
  const flowing = async (c, ms = 1200) => { const t = Date.now(); await sleep(ms); return c.since(t).length; };
  const lastCode = (c) => { const all = c.texts.filter((t) => t.includes('"code"')); return all.length ? all[all.length - 1] : ''; };
  const pr = await request({ host: lo, path: '/video', headers: upgradeHeaders({ Origin: 'http://localhost:' + PORT }) });
  if (pr.status !== 101) { check('페이지 경로 연결(루프백)', false, pr.status + ' ' + pr.error); return; }
  const page = new Ws(pr.sock, pr.rest);
  await waitFor(() => page.texts.length >= 2, 1500);
  page.send('{"t":"caps","h264":1}');
  check('앱이 보는 중 → 페이지는 대기(code standby)', page.has('"code":"standby"'), JSON.stringify(page.texts).slice(0, 300));
  check('대기 중인 페이지에는 프레임이 가지 않음', (await flowing(page)) === 0, page.frames.length + '장');
  page.send('{"t":"take"}');
  const tTake = Date.now();
  await waitFor(() => page.since(tTake).length > 0, 3000);
  const got = page.frames.filter((f) => f.at >= tTake);
  check('페이지가 take → 프레임을 받기 시작', got.length > 0, got.length + '장');
  check('페이지가 받는 것은 H.264', got.length > 0 && got.every((f) => f.flags & 2), `H.264 ${got.filter((f) => f.flags & 2).length}/${got.length}`);
  console.log('  ... 이제 기기의 앱에 "다른 기기가 보고 있습니다"와 [여기서 보기]가 보여야 합니다. ' + SECONDS + '초 안에 누르세요.');
  const before = page.texts.length; const t0 = Date.now();
  await waitFor(() => page.texts.slice(before).some((t) => t.includes('"code":"standby"')) || page.closed, SECONDS * 1000);
  const back = page.texts.slice(before).some((t) => t.includes('"code":"standby"'));
  check('앱이 [여기서 보기] → 페이지가 대기로 돌아감', back, back ? `${Date.now() - t0}ms 뒤` : '시간 안에 오지 않음 ' + lastCode(page));
  if (back) { check('되찾긴 뒤 페이지에는 프레임이 가지 않음', (await flowing(page)) === 0); }
  page.close();
};

tests.lobby = async () => {
  const did = randomDid();
  const v = await openApp({ did, take: 1 });
  check('인증 성공', v.status === 101 && v.serverMacOk, v.status);
  if (v.status !== 101) { return; }
  await waitFor(() => v.ws.texts.length >= 2, 2000);
  check('hello → seat lobby', v.ws.texts[0] === '{"t":"hello","v":2}' && /"t":"seat","s":"lobby"/.test(v.ws.texts[1] || ''), JSON.stringify(v.ws.texts));
  const i = await openApp({ did, take: 1, channel: 'input' });
  i.ws.send('{"t":"p","ts":9.5}');
  await waitFor(() => i.ws.texts.length > 0, 1500);
  check('대기실의 입력 ping 응답', i.ws.texts[0] === '{"t":"P","ts":9.5}', i.ws.texts[0]);
  await sleep(500);
  check('대기실에서는 프레임이 오지 않고 연결 유지', v.ws.frames.length === 0 && !v.ws.closed && !i.ws.closed);
  if (WAIT_KICK > 0) {
    console.log(`  … Unity 서버가 켜지기를 ${WAIT_KICK}초까지 기다립니다`);
    await waitFor(() => v.ws.closed, WAIT_KICK * 1000);
    await waitFor(() => i.ws.closed, 1000);
    check('서버가 켜지면 영상/입력 소켓 모두 4410', v.ws.closeCode === 4410 && i.ws.closeCode === 4410, `video ${v.ws.closeCode} input ${i.ws.closeCode}`);
    const t0 = Date.now();
    const again = await openApp({ did, take: 1 });
    await waitFor(() => again.ws && again.ws.has('"s":"active"'), 3000);
    check('곧바로 다시 붙으면 active', again.ws && again.ws.has('"s":"active"'), again.ws ? JSON.stringify(again.ws.texts) + ` ${Date.now() - t0}ms` : again.status);
    if (again.ws) { again.ws.close(); }
  }
  v.ws.close(); i.ws.close();
};

// 닫기 코드를 기다리는 보조 시험: [새 QR 발급](4403), Stop/리로드(1000) 를 Unity 쪽에서 일으키는 동안 붙어 있는다
tests.hold = async () => {
  const v = await openApp({ take: 1 });
  check('인증 성공', v.status === 101 && v.serverMacOk, v.status);
  if (v.status !== 101) { return; }
  console.log(`  … ${WAIT_KICK || 20}초까지 닫히기를 기다립니다`);
  await waitFor(() => v.ws.closed, (WAIT_KICK || 20) * 1000);
  console.log(`  닫기 코드 ${v.ws.closeCode}, 받은 텍스트 ${JSON.stringify(v.ws.texts)}`);
  check('닫기 프레임을 받음', v.ws.closeCode !== null, v.ws.closeCode);
};

// ---- 코드 리뷰(07_code_review_unity.md) 수정 확인 ----
// M2: 같은 앱(did)이 영상 소켓을 하나 더 열면 옛 소켓이 4409 로 닫히고, 새 소켓은 H.264 를 받는다(예전에는 둘 다 MJPEG 로 떨어짐)
// 실제 앱(가상 기기)이 보고 있을 때 돌립니다: 그 앱의 did 로 영상 소켓을 하나 더 엽니다 → 서버가 앱의 옛 소켓을 4409 로 닫습니다.
// 사용: node hosttest.js dupapp --secret … --did <앱의 did> [--seconds 6]   (did 는 Unity 의 PlayLinkHost.Viewers 의 group "app:<did>")
// 그 뒤 기기에서 앱 화면을 봅니다(이 스크립트는 --seconds 동안 새 소켓을 쥐고 있다가 놓습니다).
tests.dupapp = async () => {
  const did = opt('did', null);
  if (!did) { check('--did 가 필요합니다', false); return; }
  const b = await openApp({ did, take: 0 });
  await waitFor(() => b.ws && (b.ws.has('"t":"seat"') || b.ws.closed), 2000);
  check('같은 did 의 두 번째 영상 소켓이 받아들여짐(seat 메시지)', b.ws && b.ws.has('"t":"seat"'), b.ws ? JSON.stringify(b.ws.texts).slice(0, 200) : 'no socket');
  await sleep(SECONDS * 1000);
  const got = b.ws.frames.filter((f) => !(f.flags & 8));
  check('두 번째 소켓이 자리를 이어받아 H.264 를 받음', got.length > 0 && got.slice(1).every((f) => f.flags & 2), `H.264 ${got.filter((f) => f.flags & 2).length}/${got.length}`);
  b.ws.close();
};

tests.dup = async () => {
  const did = randomDid();
  const a = await openApp({ did, take: 1 });
  const aIn = await openApp({ did, take: 1, channel: 'input' });
  await waitFor(() => a.ws && a.ws.h264().length > 5, 4000);
  if (a.ws && a.ws.has('"s":"lobby"')) { check('Unity 서버가 돌고 있어야 하는 시험입니다(Play 중)', false, 'lobby'); a.ws.close(); aIn.ws.close(); return; }
  check('(준비) 첫 영상 소켓이 H.264 를 받는 중', a.ws.h264().length > 5, a.ws.h264().length + '장');
  // 옛 소켓을 닫지 않은 채(반쯤 열린 소켓 흉내) 같은 did 로 다시 붙는다. 자동 재접속이므로 take=0
  const tNew = Date.now();
  const b = await openApp({ did, take: 0 });
  await waitFor(() => a.ws.closed || a.ws.closeCode !== null, 2000);
  check('옛 영상 소켓이 4409 로 닫힘', a.ws.closeCode === 4409, 'close ' + a.ws.closeCode);
  await sleep(2500);
  const got = b.ws.frames.filter((f) => !(f.flags & 8));
  check('새 영상 소켓이 seat active', b.ws.has('"s":"active"'), JSON.stringify(b.ws.texts));
  check('새 영상 소켓이 H.264 를 받음(설정 → 키프레임)', b.ws.frames.some((f) => f.flags & 8) && got.some((f) => f.flags & 4), `설정 ${b.ws.frames.filter((f) => f.flags & 8).length}, 전체 ${got.length}, 첫 프레임 ${b.ws.frames.length ? b.ws.frames[0].at - tNew : '-'}ms`);
  // 접속 직후의 첫 화면 1장은 JPEG 일 수 있다(서버가 caps 메시지를 받기 전에 보내는 seed, 예전부터 그랬음). 설정 프레임 뒤로는 전부 H.264 여야 한다.
  const cfgAt = b.ws.frames.findIndex((f) => f.flags & 8);
  const after = cfgAt >= 0 ? b.ws.frames.slice(cfgAt + 1) : [];
  check('설정 프레임 뒤로 JPEG 로 떨어진 프레임이 없음', after.length > 30 && after.every((f) => f.flags & 2), `H.264 ${after.filter((f) => f.flags & 2).length}/${after.length}, 설정 전 JPEG ${b.ws.frames.slice(0, Math.max(0, cfgAt)).filter((f) => !(f.flags & 2)).length}장`);
  check('입력 소켓은 그대로(영상만 바뀜)', !aIn.ws.closed);
  const bIn = await openApp({ did, take: 0, channel: 'input' });
  await waitFor(() => aIn.ws.closed || aIn.ws.closeCode !== null, 2000);
  check('입력 소켓도 다시 열면 옛 것이 4409 로 닫힘', aIn.ws.closeCode === 4409, 'close ' + aIn.ws.closeCode);
  bIn.ws.send('{"t":"p","ts":3}');
  await waitFor(() => bIn.ws.texts.length > 0, 1500);
  check('새 입력 소켓이 ping 에 답함', bIn.ws.texts[0] === '{"t":"P","ts":3}', bIn.ws.texts[0]);
  // 다른 앱(did 가 다름)은 닫히지 않는다
  const other = await openApp({ did: randomDid(), take: 0 });
  await sleep(500);
  check('다른 기기가 붙어도 보던 기기의 소켓은 닫히지 않음', !b.ws.closed && other.ws && other.ws.has('"s":"standby"'), other.ws ? JSON.stringify(other.ws.texts) : other.status);
  for (const c of [b, bIn, other]) { if (c.ws) { c.ws.close(); } }
};

// M1: 받는 쪽이 읽기를 멈춰도(Wi-Fi 상대가 사라진 것과 같은 상황) 서버가 그 연결을 스스로 끊고, 기다리던 다른 클라이언트가 이어받는다.
// 읽지 않는 상대는 TCP 가 스스로 포기하지 않으므로(영 창 탐침이 계속됨) 쓰기 제한(SendTimeout)이 없으면 끝나지 않는다.
// --hold <초>: 멈춘 채로 있는 시간(기본 25). 그동안 Unity 쪽에서 닫기(Stop, 새 QR)를 걸어 메인 스레드가 멈추지 않는지 따로 잰다.
tests.stall = async () => {
  const lo = '127.0.0.1';
  const hold = Number(opt('hold', 25));
  const a = await openApp({ take: 1 });
  await waitFor(() => a.ws && a.ws.h264().length > 30, 5000);
  if (!a.ws || a.ws.h264().length === 0) { check('(준비) 앱 연결이 H.264 를 받는 중(Play 중이어야 함)', false, a.status); return; }
  check('(준비) 앱 연결이 H.264 를 받는 중', true, a.ws.h264().length + '장');
  const pr = await request({ host: lo, path: '/video', headers: upgradeHeaders({ Origin: 'http://localhost:' + PORT }) });
  const page = new Ws(pr.sock, pr.rest);
  await waitFor(() => page.texts.length >= 2, 1500);
  page.send('{"t":"caps","h264":1}');
  check('(준비) 기다리는 페이지 클라이언트는 대기', page.has('"code":"standby"') && page.frames.length === 0, JSON.stringify(page.texts));
  // 여기서부터 앱 쪽은 아무것도 읽지 않는다(확인 메시지도 가지 않음). 소켓은 열어 둔다.
  // --pause-at <epoch ms>: 멈추는 시각을 밖에서 정한다(Unity 쪽에서 그 시각 + 몇 초에 닫기를 걸어 메인 스레드 시간을 재려는 용도)
  const pauseAt = Number(opt('pause-at', 0));
  if (pauseAt > Date.now()) { await sleep(pauseAt - Date.now()); }
  a.res.sock.pause();
  const t0 = Date.now();
  console.log(`  … 앱 소켓의 읽기를 멈췄습니다(${new Date(t0).toTimeString().slice(0, 8)}). ${hold}초 동안 둡니다`);
  await waitFor(() => page.frames.length > 0, hold * 1000);
  const tookOver = page.frames.length > 0 ? page.frames[0].at - t0 : -1;
  check('서버가 멈춘 연결을 스스로 끊고 기다리던 클라이언트가 이어받음', tookOver >= 0, tookOver >= 0 ? `${tookOver}ms 뒤 페이지에 첫 프레임` : `${hold}초 동안 이어받지 못함`);
  check('이어받기까지 15초 이내(쓰기 제한 3초 + 소켓 버퍼가 차는 시간)', tookOver >= 0 && tookOver < 15000, tookOver + 'ms');
  await sleep(1500);
  const got = page.frames.filter((f) => !(f.flags & 8));
  check('이어받은 클라이언트가 H.264 를 받음', got.length > 10 && got.every((f) => f.flags & 2), `H.264 ${got.filter((f) => f.flags & 2).length}/${got.length}`);
  a.res.sock.resume();
  await waitFor(() => a.ws.closed, 3000);
  check('멈췄던 소켓은 서버가 닫은 상태', a.ws.closed, `closed=${a.ws.closed} code=${a.ws.closeCode}`);
  page.close(); a.ws.close();
};

// m3, m4, m5/m19, m6: 루프백의 Origin 포트와 Host 헤더, 착륙 페이지, APK 주소별 한도
tests.fixes = async () => {
  const lo = '127.0.0.1';
  let r = await request({ host: lo, path: '/video', headers: upgradeHeaders({ Origin: 'http://localhost:9999' }) });
  check('루프백, Origin 의 포트가 다름(localhost:9999) → 403 bad_origin', r.status === 403 && r.headers['x-playlink-error'] === 'bad_origin', r.status);
  if (r.sock) { r.sock.destroy(); }
  r = await request({ host: lo, path: '/video', headers: upgradeHeaders({ Origin: 'http://localhost' }) });
  check('루프백, Origin 에 포트 없음(= 80) → 403', r.status === 403, r.status);
  if (r.sock) { r.sock.destroy(); }
  r = await request({ host: lo, path: '/video', headers: upgradeHeaders({ Origin: 'http://LOCALHOST:' + PORT }) });
  check('루프백, Origin 대소문자만 다름 → 101', r.status === 101, r.status);
  if (r.sock) { r.sock.destroy(); }
  r = await request({ host: lo, path: '/input', headers: upgradeHeaders({ Origin: 'http://127.0.0.1:' + PORT }) });
  check('루프백, Origin = 127.0.0.1:포트 → 101', r.status === 101, r.status);
  if (r.sock) { r.sock.destroy(); }
  for (const path of ['/stats', '/', '/debug']) {
    r = await request({ host: lo, path, headers: { Host: 'evil.example' } });
    check(`루프백 GET ${path} 에 Host: evil.example → 404`, r.status === 404, r.status);
  }
  r = await request({ host: lo, path: '/stats', headers: { Host: 'evil.example:7300' } });
  check('Host: evil.example:7300 → 404', r.status === 404, r.status);
  r = await request({ host: lo, path: '/stats', headers: { Host: 'localhost:' + PORT } });
  check('Host: localhost:포트 → 200 (adb reverse 의 Chrome 페이지가 쓰는 값)', r.status === 200, r.status);
  r = await request({ host: lo, path: '/stats', raw: 'GET /stats HTTP/1.1\r\n\r\n' });
  check('Host 헤더가 없는 요청 → 200 (측정 스크립트)', r.status === 200, r.status);
  r = await request({ path: '/c' });
  const html = r.body ? r.body.toString('utf8') : '';
  check('착륙 페이지: 패키지와 스킴이 서버 값으로 채워짐', html.includes("'io.playlink.app'") && html.includes("'playlink'") && !html.includes('%%PLAYLINK_'), html.length + '자');
  check('착륙 페이지: 연결 값을 형식 전체로 검사하고 다시 조립', html.includes('FORMAT.exec') && !html.includes("'/c?' + link"));
  // /apk: 같은 주소에서는 한 번에 하나만. 읽지 않는 연결 하나를 걸어 두고 두 번째
  const head = await request({ path: '/apk', method: 'HEAD' });
  if (head.status === 200) {
    const first = net.connect({ host: HOST, port: PORT });
    first.on('error', () => {});
    first.write(`GET /apk HTTP/1.1\r\nHost: ${HOST}:${PORT}\r\n\r\n`);
    first.pause();
    await sleep(400);
    r = await request({ path: '/apk' });
    check('/apk: 같은 주소에서 받는 중이면 두 번째는 503 busy', r.status === 503 && r.headers['x-playlink-error'] === 'busy', r.status);
    r = await request({ path: '/apk', method: 'HEAD' });
    check('/apk HEAD 는 받는 중에도 200', r.status === 200, r.status);
    if (r.sock) { r.sock.destroy(); }
    first.destroy();
    await sleep(500);
    r = await request({ path: '/apk' });
    check('/apk: 앞의 연결이 끊긴 뒤에는 끝까지 받음', r.status === 200 && Number(r.headers['content-length']) === r.body.length, `${r.status} ${r.body ? r.body.length : 0}B`);
  } else {
    console.log('  (APK 가 도구에 없어 /apk 한도 시험은 건너뜁니다)');
  }
};

// 선호(누가 보고 있었는지)가 서버 재시작과 도메인 리로드를 넘어 남는지: prefer1 을 띄워 둔 채 Unity 쪽에서 재시작/리컴파일 → 닫힌 뒤 prefer2
//   node hosttest.js prefer1 --did aaaaaaaaaaaa --wait-kick 120 …   그리고   node hosttest.js prefer2 --did aaaaaaaaaaaa …
tests.prefer1 = async () => {
  const a = await openApp({ did: opt('did', 'aaaaaaaaaaaa'), take: 1 });
  await waitFor(() => a.ws && a.ws.has('"s":"active"'), 2000);
  check('A(take=1) → active', a.ws && a.ws.has('"s":"active"'), a.status);
  console.log(`  … ${WAIT_KICK || 60}초까지 닫히기를 기다립니다`);
  await waitFor(() => a.ws.closed, (WAIT_KICK || 60) * 1000);
  check('서버가 닫음(닫기 코드 있음)', a.ws.closeCode !== null, 'close ' + a.ws.closeCode);
};
tests.prefer2 = async () => {
  const seat = (c) => { const all = c.ws.texts.filter((t) => t.includes('"t":"seat"')); return all.length ? JSON.parse(all[all.length - 1]).s : null; };
  const b = await openApp({ did: randomDid(), take: 0 });
  await waitFor(() => seat(b) !== null, 3000);
  check('보던 기기가 없는 동안 B(take=0)가 먼저 붙음 → active(이어받음)', seat(b) === 'active', seat(b));
  const a = await openApp({ did: opt('did', 'aaaaaaaaaaaa'), take: 0 });
  await waitFor(() => seat(a) === 'active' && seat(b) === 'standby', 3000);
  check('보던 기기 A 가 take=0 으로 돌아옴 → A active, B standby (선호가 재시작을 넘어 남음)', seat(a) === 'active' && seat(b) === 'standby', `A=${seat(a)} B=${seat(b)}`);
  const t = Date.now(); await sleep(1200);
  check('A 가 프레임을 받음', a.ws.since(t).length > 0, a.ws.since(t).length + '장');
  a.ws.close(); b.ws.close();
};

(async () => {
  const names = testName === 'all' ? ['vectors', 'reject', 'paths', 'auth', 'abuse', 'limits', 'legacy', 'seats', 'dup', 'fixes'] : testName.split(',');
  for (const name of names) {
    if (!tests[name]) { console.log('모르는 시험: ' + name); process.exit(2); }
    console.log(`[${name}]  ${name === 'vectors' ? '' : HOST + ':' + PORT}`);
    try { await tests[name](); } catch (e) { failed++; console.log('  FAIL 예외: ' + (e && e.stack || e)); }
  }
  console.log(`\n결과: 통과 ${passed}, 실패 ${failed}`);
  process.exit(failed ? 1 : 0);
})();
