#!/usr/bin/env node
// 가짜 PC(영상 재생): 실제 Unity 없이 앱의 새 연결 방식을 시험합니다. replay.js(예전: 앱이 걸어 옴)의 재생 부분을 가져와
// 방향만 뒤집었습니다: 이 스크립트가 폰으로 걸고(playlink-proto.js 의 initiate, mode paired), 그 암호화 흐름 안에서
// 예전 서버 역할의 WebSocket 프레임(hello, seat, 28바이트 헤더 + H.264, 수신 확인 역압, 입력 채널의 ping 답과 터치 기록)을 보냅니다.
// 페어링은 먼저 playlink-proto.js 로 해 둡니다(같은 --state 파일):
//   node tools/playlink-proto.js pc --host 127.0.0.1 --port 7400 --state pc.json --mode sas
// 사용: node tools/app-fake-pc.js --host 127.0.0.1 --port 7400 --state pc.json [--name N] [--take 0] [--wait-want] [--seconds 20]
//        [--size 720x1280] [--fps 60] [--kbps 4000] [--codec x264|nvenc] [--clip 10] [--video-only] [--input-only] [--close 1000] [--ffmpeg <경로>]
//   --wait-want : 폰이 탐색 응답(TCP who)에 want 를 실을 때까지(폰에서 이 PC 를 고를 때까지) 1초마다 묻다가 겁니다
//   --take 0    : 자동 재접속인 척(hello 의 take=false)
//   --seconds N : N 초 뒤에 끝냄(--close <코드> 가 있으면 그 닫기 코드를 보내고, 없으면 그냥 끊음). 없으면 폰이 끊을 때까지
// 가상 기기는 adb forward tcp:7400 tcp:7300 뒤에 127.0.0.1:7400 으로 겁니다. 키는 출력하지 않습니다.
'use strict';
const net = require('net'), crypto = require('crypto'), fs = require('fs'), { spawn } = require('child_process');
const P = require('./playlink-proto.js');

const a = {};
for (let i = 2; i < process.argv.length; i++) { const k = process.argv[i].replace(/^--/, ''); if (i + 1 < process.argv.length && !process.argv[i + 1].startsWith('--')) a[k] = process.argv[++i]; else a[k] = true; }
const HOST = a.host || '127.0.0.1', PORT = parseInt(a.port || P.DEFAULT_PORT, 10), TAKE = a.take !== '0';
const [W, H] = String(a.size || '720x1280').split('x').map(Number), FPS = Number(a.fps || 60), KBPS = Number(a.kbps || 4000), CLIP_S = Number(a.clip || 10);
const CODEC = a.codec || 'x264', SECONDS = Number(a.seconds || 0);
const FF_DEFAULT = 'C:\\Users\\dbrud\\Tools\\ffmpeg-gvr\\ffmpeg-n8.1-latest-win64-gpl-8.1\\bin\\ffmpeg.exe';
const FFMPEG = a.ffmpeg || (fs.existsSync(FF_DEFAULT) ? FF_DEFAULT : 'ffmpeg');
const t0 = process.hrtime.bigint();
const nowMs = () => Number(process.hrtime.bigint() - t0) / 1e6;
const log = (...x) => console.log(nowMs().toFixed(0).padStart(7), '[pc]', ...x);

let state = null;
try { state = JSON.parse(fs.readFileSync(a.state, 'utf8')); } catch (e) { }
if (!state || !state.id || !state.peers) { console.error('오류: --state 파일이 없습니다. 먼저 playlink-proto.js pc --mode sas|qr 로 페어링하세요.'); process.exit(1); }
if (a.name) state.name = a.name;
const phoneId = a.peer || Object.keys(state.peers)[0];
const pairKey = state.peers[phoneId] ? P.unb64u(state.peers[phoneId].key) : null;
if (!pairKey) { console.error('오류: 페어링된 폰이 없습니다(--state).'); process.exit(1); }
const self = { id: state.id, name: state.name };

const stats = { sent: 0, acks: 0, ackMsSum: 0, ackMsMax: 0, restarts: 0, keyRequests: 0, pointer: 0, lastPointer: '', pings: 0, quality: '' };
let video = null, input = null, lastInputSeq = 0, frameId = 0, done = false;

// ---------------------------------------------------------------- WebSocket 프레임(서버 역할: 마스크 없이 보내고 마스크된 것을 받음)
function frame(opcode, payload) {
  const n = payload.length; let h;
  if (n < 126) { h = Buffer.from([0x80 | opcode, n]); }
  else if (n < 65536) { h = Buffer.alloc(4); h[0] = 0x80 | opcode; h[1] = 126; h.writeUInt16BE(n, 2); }
  else { h = Buffer.alloc(10); h[0] = 0x80 | opcode; h[1] = 127; h.writeBigUInt64BE(BigInt(n), 2); }
  return Buffer.concat([h, payload]);
}
const sendText = (ch, obj) => ch.write(frame(1, Buffer.from(typeof obj === 'string' ? obj : JSON.stringify(obj))));

// 채널의 레코드를 읽어 프레임으로 나눕니다. 끝나면(끊김, 닫기 프레임) 이유를 돌려줍니다.
async function readFrames(ch, onText) {
  let rx = Buffer.alloc(0);
  for (;;) {
    let rec;
    try { rec = await ch.readRecord(0); } catch (e) { return '끊김(' + e.message + ')'; }
    rx = rx.length ? Buffer.concat([rx, rec]) : rec;
    while (rx.length >= 2) {
      const op = rx[0] & 0x0F, masked = (rx[1] & 0x80) !== 0, len = rx[1] & 0x7F;
      if (!masked || len > 125) return '폰이 보낸 프레임이 규칙과 다릅니다(마스크 ' + masked + ', 길이 ' + len + ')';
      if (rx.length < 6 + len) break;
      const mask = rx.subarray(2, 6), data = Buffer.from(rx.subarray(6, 6 + len));
      for (let i = 0; i < len; i++) data[i] ^= mask[i & 3];
      rx = rx.subarray(6 + len);
      if (op === 8) return '폰이 닫음(' + (len >= 2 ? data.readUInt16BE(0) : 1005) + ')';
      if (op === 9) { ch.write(frame(10, data)); continue; }
      if (op === 1) onText(data.toString('utf8'));
    }
  }
}

// ---------------------------------------------------------------- 녹화해 둔 H.264 를 정밀한 간격으로 재생 (replay.js 와 같음)
const INTERVAL = 1000 / FPS;
let clip = [], playing = false, playIndex = 0, playDue = 0, queue = [], unacked = [];
function recordClip() {
  return new Promise((resolve, reject) => {
    const enc = CODEC === 'nvenc'
      ? ['-pix_fmt', 'yuv420p', '-c:v', 'h264_nvenc', '-preset', 'p1', '-tune', 'ull', '-zerolatency', '1', '-delay', '0', '-bf', '0', '-g', '600', '-rc', 'cbr', '-b:v', KBPS + 'k', '-maxrate', KBPS + 'k', '-bufsize', Math.max(100, KBPS / 30 | 0) + 'k', '-profile:v', 'high']
      : ['-c:v', 'libx264', '-preset', 'veryfast', '-tune', 'zerolatency', '-bf', '0', '-g', '600', '-b:v', KBPS + 'k', '-maxrate', KBPS + 'k', '-bufsize', Math.max(100, KBPS / 30 | 0) + 'k', '-profile:v', 'high', '-pix_fmt', 'yuv420p'];
    const p = spawn(FFMPEG, ['-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', `testsrc2=size=${W}x${H}:rate=${FPS}`, '-t', String(CLIP_S), ...enc, '-flush_packets', '1', '-flvflags', 'no_duration_filesize', '-f', 'flv', 'pipe:1'], { stdio: ['ignore', 'pipe', 'pipe'] });
    p.on('error', reject);
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
      const bytes = clip.reduce((s, c) => s + c.payload.length, 0);
      log(`clip: ${clip.length} messages, ${(bytes / 1024 / 1024).toFixed(1)} MB, ${(bytes * 8 / CLIP_S / 1000).toFixed(0)} kbps, ffmpeg exit ${code}`);
      if (clip.length > 10 && clip[0].config) resolve(); else reject(new Error('H.264 녹화 실패(ffmpeg)'));
    });
  });
}
function startStream(why) {          // 설정 + 키프레임부터 다시 내보냄(실제 서버가 ffmpeg 를 다시 시작하는 것에 해당)
  queue = []; playIndex = 0; playDue = nowMs(); stats.restarts++;
  if (!playing) { playing = true; tick(); }
  log('stream restart:', why);
}
function tick() {
  if (!playing || done) return;
  const now = nowMs();
  if (now >= playDue) {
    const m = clip[playIndex];
    const h = Buffer.alloc(28);
    h.writeUInt32LE(frameId++ >>> 0, 0); h.writeUInt16LE(W, 4); h.writeUInt16LE(H, 6);
    h.writeInt32LE(m.config ? 0 : lastInputSeq, 8); h.writeUInt32LE(3000, 12); h.writeUInt32LE(8000, 16);
    h.writeUInt32LE(2 | (m.key ? 4 : 0) | (m.config ? 8 : 0), 20); h.writeUInt32LE(Math.round((m.config ? now : playDue) * 10) >>> 0, 24);
    queue.push(frame(2, Buffer.concat([h, m.payload])));
    pump();
    playIndex++;
    if (m.config) { playDue = Math.max(playDue, now); } else { playDue += INTERVAL; if (now - playDue > 100) playDue = now; }
    if (playIndex >= clip.length) playIndex = 0;
  }
  setImmediate(tick);      // 돌면서 기다림(코어 하나를 씀): Windows 의 node 타이머는 15.6ms 단위라 setTimeout 으로는 간격이 흔들립니다
}
function pump() {
  if (!video || done) { queue = []; return; }
  // 역압: 가장 오래된 미확인이 150ms 이상이거나 미확인 32장이면 더 안 보냄(실제 서버의 H.264 규칙)
  while (queue.length) {
    const oldest = unacked.length ? nowMs() - unacked[0] : 0;
    if (oldest >= 150 || unacked.length >= 32) break;
    unacked.push(nowMs()); video.write(queue.shift()); stats.sent++;
  }
  if (queue.length > 120) startStream('backlog');
  if (unacked.length && nowMs() - unacked[0] > 1000) unacked = [];      // 확인 시간 초과: 미확인을 비우고 계속
}

// ---------------------------------------------------------------- 폰으로 걸기
async function dial(ch) {
  const socket = net.connect(PORT, HOST);
  socket.setNoDelay(true);
  await new Promise((res, rej) => { socket.once('connect', res); socket.once('error', rej); });
  socket.on('error', () => { });
  const r = await P.initiate(socket, { self, ch, mode: 'paired', secret: pairKey, take: TAKE });
  log(`${ch} 연결됨: ${r.peer.name} app=${r.fin.app} caps=${r.fin.caps}`);
  return r.channel;
}

async function who() {
  const socket = net.connect(PORT, HOST);
  await new Promise((res, rej) => { socket.once('connect', res); socket.once('error', rej); });
  const n = P.b64u(crypto.randomBytes(8));
  const body = Buffer.from(JSON.stringify({ t: 'who', v: P.VERSION, pc: self.id, name: self.name, n }), 'utf8');
  socket.write(Buffer.concat([Buffer.from([body.length >> 8, body.length & 255]), body]));
  const reader = new P.Reader(socket);
  const len = (await reader.read(2, 5000)).readUInt16BE(0);
  const r = JSON.parse((await reader.read(len, 5000)).toString('utf8'));
  socket.destroy();
  return { r, proof: P.checkReply(r, self.id, n, pairKey, null) };
}

function finish(why, code) {
  if (done) return;
  done = true; playing = false;
  const s = stats;
  log(`끝: ${why}`);
  log(`결과: 보낸 프레임 ${s.sent}, 수신 확인 ${s.acks} (평균 ${s.acks ? (s.ackMsSum / s.acks).toFixed(1) : '-'}ms, 최대 ${s.ackMsMax.toFixed(0)}ms), 키 요청 ${s.keyRequests}, 터치 메시지 ${s.pointer}, ping ${s.pings}, 마지막 터치 ${s.lastPointer || '-'}${s.quality ? ', 품질 ' + s.quality : ''}`);
  if (video) video.close(); if (input) input.close();
  setTimeout(() => process.exit(code), 100);
}

(async () => {
  if (!a['input-only']) await recordClip();
  if (a['wait-want']) {
    log('폰이 이 PC 를 고르기를 기다립니다(who 를 1초마다)');
    for (;;) {
      let w = null; try { w = await who(); } catch (e) { }
      if (w && w.r.want && w.proof === 'paired') { log(`폰이 연결을 바람: ${w.r.name} busy=${w.r.busy || '-'}`); break; }
      await new Promise((res) => setTimeout(res, 1000));
    }
  }
  if (!a['input-only']) {
    video = await dial('video');
    sendText(video, '{"t":"hello","v":2}');
    sendText(video, { t: 'seat', s: 'active' });
    startStream('video open');
    readFrames(video, (text) => {
      if (text[0] !== '{') { const t = unacked.shift(); if (t !== undefined) { const ms = nowMs() - t; stats.acks++; stats.ackMsSum += ms; if (ms > stats.ackMsMax) stats.ackMsMax = ms; } pump(); return; }
      if (text.includes('"t":"caps"')) { log('caps', text); return; }
      if (text.includes('"t":"key"')) { stats.keyRequests++; startStream('key request'); return; }
      if (text.includes('"t":"take"')) { log('take'); sendText(video, { t: 'seat', s: 'active' }); return; }
    }).then((why) => finish('영상 채널 ' + why, 0));
    setInterval(pump, 20);
  }
  if (!a['video-only']) {
    input = await dial('input');
    readFrames(input, (text) => {
      let m; try { m = JSON.parse(text); } catch (e) { return; }
      if (typeof m.t !== 'string' || m.t.length !== 1) return;
      if (m.t === 'p') { stats.pings++; return sendText(input, `{"t":"P","ts":${m.ts}}`); }
      if (m.t === 'q') { stats.quality = text; return; }
      if ('dmuc'.includes(m.t)) { stats.pointer++; stats.lastPointer = text; if (m.s > lastInputSeq) lastInputSeq = m.s; if (m.t !== 'm') log('touch', text); }
    }).then((why) => finish('입력 채널 ' + why, 0));
  }
  setInterval(() => log(`보낸 프레임 ${stats.sent}, 확인 ${stats.acks}, 터치 ${stats.pointer}, ping ${stats.pings}`), 2000);
  if (SECONDS > 0) setTimeout(() => {
    if (a.close) { const b = Buffer.alloc(2); b.writeUInt16BE(Number(a.close)); for (const ch of [video, input]) if (ch) ch.write(frame(8, b)); setTimeout(() => finish(`${SECONDS}초 지남, 닫기 코드 ${a.close} 를 보냄`, 0), 300); }
    else finish(`${SECONDS}초 지남`, 0);
  }, SECONDS * 1000);
})().catch((e) => { log(`실패: ${e.code || ''} ${e.message}`); process.exit(2); });
