#!/usr/bin/env node
// Unity 쪽 구현을 시험하는 폰 흉내(Node, 외부 모듈 없음). 기준 구현 tools/playlink-proto.js 의 respond/discoveryReply 를 그대로 쓰고,
// 키 교환 뒤의 WebSocket 프레임(PROTOCOL.md 4장)까지 앱처럼 주고받습니다: 영상 연결의 hello/seat/프레임을 받아 세고 "a" 로 확인하고,
// 입력 연결로 ping 과 터치를 보냅니다. 상태는 --status 파일(JSON)에 적고, 명령은 표준 입력으로 한 줄씩 받습니다.
//   node tools/unity-peer-test.js --port 7411 --state <폰 상태.json> --status <상태.json> [--name N] [--bind 127.0.0.1]
//        [--want] [--reject] [--busy] [--h264] [--no-udp] [--cmd <명령 파일>]
//   환경 변수 PLAYLINK_QR=<playlink://pair?…>   폰이 그 QR 을 찍은 것처럼 동작(명령줄에 비밀이 보이지 않게 환경 변수로 받음)
//   표준 입력 명령: close(닫기 프레임 1000 을 보내고 끊음) | drop(그냥 끊음) | touch(화면 가운데를 눌렀다 뗌) | key(키프레임 요청) | quit
//   --cmd 를 주면 표준 입력 대신 그 파일을 0.1초마다 읽어 명령을 실행하고 지웁니다(띄운 쪽이 사라져도 계속 돎: Unity 도메인 리로드 시험용).
// 키와 비밀 값은 출력하지 않습니다. 6자리 확인 숫자는 화면에 보이는 값이라 상태 파일에 적습니다(비교용).
'use strict';
const net = require('net');
const dgram = require('dgram');
const fs = require('fs');
const crypto = require('crypto');
const proto = require('./playlink-proto.js');

function args(argv) {
  const o = {};
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith('--')) continue;
    const k = argv[i].slice(2);
    if (i + 1 < argv.length && !argv[i + 1].startsWith('--')) o[k] = argv[++i]; else o[k] = true;
  }
  return o;
}
const a = args(process.argv.slice(2));
const port = parseInt(a.port || proto.DEFAULT_PORT, 10);

let state = null;
try { state = JSON.parse(fs.readFileSync(a.state, 'utf8')); } catch (e) { }
if (!state || !state.id) state = { id: proto.newId(), name: a.name || 'TestPhone', peers: {} };
if (a.name) state.name = a.name;
const saveState = () => { if (a.state) fs.writeFileSync(a.state, JSON.stringify(state)); };
saveState();

const status = {
  id: state.id, ready: false, queries: 0, whos: 0, pairs: 0, pairMode: '', digits: '', refused: '', failed: '',
  videoOpen: false, inputOpen: false, videoOpens: 0, inputOpens: 0, hello: 0, seats: '', statusTexts: 0,
  frames: 0, frameBytes: 0, jpegFrames: 0, h264Frames: 0, h264Configs: 0, h264Keys: 0, badFrames: 0, width: 0, height: 0, lastInputSeq: -1,
  pongs: 0, closeCode: 0, closeCodes: '', finCaps: '', finProto: 0, pcName: '', take: false,
};
let statusDirty = true;
const note = () => { statusDirty = true; };
setInterval(() => {
  if (!statusDirty || !a.status) return;
  statusDirty = false;
  try { fs.writeFileSync(a.status + '.tmp', JSON.stringify(status)); fs.renameSync(a.status + '.tmp', a.status); } catch (e) { statusDirty = true; }
}, 50);

const qr = process.env.PLAYLINK_QR ? proto.parseQr(process.env.PLAYLINK_QR) : null;
if (process.env.PLAYLINK_QR && !qr) { console.error('PLAYLINK_QR 형식이 올바르지 않습니다'); process.exit(2); }
let qrUsed = false;
const peerKey = (id) => (state.peers[id] ? proto.unb64u(state.peers[id].key) : null);
const links = { video: null, input: null };
const opts = {
  self: { id: state.id, name: state.name }, app: 100, caps: 'h264,seat',
  get qr() { return qr && !qrUsed ? qr : null; },
  lookup: peerKey,
  want: (pc) => !!a.want || !!(qr && !qrUsed && qr.pc === pc),
  busy: () => (a.busy ? 'CCCCCCCCCCCCCCCC' : ''),
  confirm: async (sas) => { status.digits = sas; note(); if (a.reject) return false; await new Promise((r) => setTimeout(r, 300)); return true; },
  admit: async (h) => { status.take = !!h.take; note(); return a.busy && h.mode === 'paired' ? { code: 'busy', msg: '' } : null; },
};

// ---- WebSocket 프레임(폰 = 클라이언트 역할: 마스크해서 보내고, 마스크 없는 프레임을 받음)
function wsFrame(opcode, payload) {
  const body = Buffer.isBuffer(payload) ? payload : Buffer.from(payload, 'utf8');
  const mask = crypto.randomBytes(4);
  let head;
  if (body.length < 126) head = Buffer.from([0x80 | opcode, 0x80 | body.length]);
  else if (body.length < 65536) { head = Buffer.alloc(4); head[0] = 0x80 | opcode; head[1] = 0x80 | 126; head.writeUInt16BE(body.length, 2); }
  else { head = Buffer.alloc(10); head[0] = 0x80 | opcode; head[1] = 0x80 | 127; head.writeBigUInt64BE(BigInt(body.length), 2); }
  const masked = Buffer.alloc(body.length);
  for (let i = 0; i < body.length; i++) masked[i] = body[i] ^ mask[i & 3];
  return Buffer.concat([head, mask, masked]);
}
class WsParser {
  constructor(onMessage) { this.buf = Buffer.alloc(0); this.onMessage = onMessage; }
  push(chunk) {
    this.buf = this.buf.length ? Buffer.concat([this.buf, chunk]) : chunk;
    for (;;) {
      if (this.buf.length < 2) return;
      const opcode = this.buf[0] & 0x0f;
      if (this.buf[1] & 0x80) throw new Error('PC 가 마스크한 프레임을 보냄');
      if (!(this.buf[0] & 0x80)) throw new Error('PC 가 나눈 프레임을 보냄');
      let len = this.buf[1] & 0x7f, off = 2;
      if (len === 126) { if (this.buf.length < 4) return; len = this.buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (this.buf.length < 10) return; len = Number(this.buf.readBigUInt64BE(2)); off = 10; }
      if (this.buf.length < off + len) return;
      const payload = this.buf.subarray(off, off + len);
      this.buf = this.buf.subarray(off + len);
      this.onMessage(opcode, payload);
    }
  }
}

function onVideo(channel, opcode, payload) {
  if (opcode === 8) { status.closeCode = payload.length >= 2 ? payload.readUInt16BE(0) : 1005; status.closeCodes += (status.closeCodes ? ',' : '') + status.closeCode; note(); return; }
  if (opcode === 1) {
    const text = payload.toString('utf8');
    let m = null; try { m = JSON.parse(text); } catch (e) { }
    if (!m) { status.badFrames++; note(); return; }
    if (m.t === 'hello') {
      status.hello = m.v | 0;
      if (status.seats.length || status.frames) status.badFrames++;      // hello 가 첫 메시지여야 함
      if (a.h264) channel.write(wsFrame(1, '{"t":"caps","h264":1}'));
    } else if (m.t === 'seat') status.seats += (status.seats ? ',' : '') + m.s + (m.s === 'lobby' && m.msg ? '*' : '');
    else if (m.t === 's') status.statusTexts++;
    note();
    return;
  }
  if (opcode !== 2) return;
  if (payload.length < 28) { status.badFrames++; note(); return; }
  const flags = payload.readUInt32LE(20);
  status.frames++;
  status.frameBytes += payload.length;
  status.width = payload.readUInt16LE(4);
  status.height = payload.readUInt16LE(6);
  status.lastInputSeq = payload.readInt32LE(8);
  if (flags & 8) status.h264Configs++;
  else if (flags & 2) {
    status.h264Frames++;
    if (flags & 4) status.h264Keys++;
    // AVCC: 4바이트 길이 + NAL. 길이들이 내용과 맞는지 봅니다.
    let off = 28, ok = payload.length > 32;
    while (ok && off < payload.length) { if (off + 4 > payload.length) { ok = false; break; } off += 4 + payload.readUInt32BE(off); }
    if (!ok || off !== payload.length) status.badFrames++;
  } else {
    status.jpegFrames++;
    if (payload[28] !== 0xff || payload[29] !== 0xd8 || payload[payload.length - 2] !== 0xff || payload[payload.length - 1] !== 0xd9) status.badFrames++;
  }
  // 설정(avcC)도 프레임처럼 확인을 보냅니다(앱과 같게: 받은 바이너리 메시지마다 "a").
  channel.write(wsFrame(1, 'a'));
  note();
}

function onInput(channel, opcode, payload) {
  if (opcode === 8) { status.closeCode = payload.length >= 2 ? payload.readUInt16BE(0) : 1005; note(); return; }
  if (opcode !== 1) return;
  let m = null; try { m = JSON.parse(payload.toString('utf8')); } catch (e) { }
  if (m && m.t === 'P') { status.pongs++; note(); }
}

async function serve(r) {
  const ch = r.hello.ch;
  const old = links[ch];
  if (old) old.close();
  links[ch] = r.channel;
  status[ch + 'Open'] = true; status[ch + 'Opens']++;
  status.finCaps = String(r.fin.caps || ''); status.finProto = r.fin.proto | 0; status.pcName = r.peer.name;
  if (ch === 'video') { status.closeCode = 0; status.seats = ''; status.hello = 0; }
  note();
  const parser = new WsParser((opcode, payload) => (ch === 'video' ? onVideo : onInput)(r.channel, opcode, payload));
  let seq = 0;
  const pinger = ch === 'input' ? setInterval(() => r.channel.write(wsFrame(1, JSON.stringify({ t: 'p', ts: ++seq }))), 250) : null;
  try { for (;;) parser.push(await r.channel.readRecord(0)); }
  catch (e) { if (e.message !== 'closed' && e.message !== 'eof') { status.failed += (status.failed ? ',' : '') + ch + ':' + e.message; } }
  finally {
    if (pinger) clearInterval(pinger);
    if (links[ch] === r.channel) { links[ch] = null; status[ch + 'Open'] = false; }
    r.channel.close();
    note();
  }
}

const server = net.createServer(async (socket) => {
  socket.setNoDelay(true);
  socket.on('error', () => { });
  try {
    const r = await proto.respond(socket, opts);
    if (r.who) { status.whos++; note(); return; }
    if (r.hello.ch === 'pair') {
      state.peers[r.peer.id] = { key: proto.b64u(r.keys.pair), name: r.peer.name };
      saveState();
      if (r.hello.mode === 'qr') qrUsed = true;
      status.pairs++; status.pairMode = r.hello.mode; status.pcName = r.peer.name; note();
      setTimeout(() => r.channel.close(), 100);
      return;
    }
    serve(r);
  } catch (e) {
    status.refused += (status.refused ? ',' : '') + (e.code || 'error');
    note();
    socket.destroy();
  }
});
server.listen(port, a.bind || '0.0.0.0', () => { status.ready = true; note(); });

if (!a['no-udp']) {
  const udp = dgram.createSocket({ type: 'udp4', reuseAddr: true });
  udp.on('message', (msg, from) => {
    let q; try { q = JSON.parse(msg.toString('utf8')); } catch (e) { return; }
    if (!q || q.t !== 'q' || q.v !== proto.VERSION) return;
    udp.send(Buffer.from(JSON.stringify(proto.discoveryReply(opts, q, port)), 'utf8'), from.port, from.address);
    status.queries++; note();
  });
  udp.on('error', (e) => { status.failed += 'udp:' + e.code; note(); });
  udp.bind(port);
}

function command(line) {
  const cmd = line.trim();
  if (cmd === 'quit') process.exit(0);
  if (cmd === 'close') {
    for (const ch of ['video', 'input']) { const c = links[ch]; if (c) { c.write(wsFrame(8, Buffer.from([0x03, 0xe8]))); c.socket.end(); } }
  } else if (cmd === 'drop') {
    for (const ch of ['video', 'input']) { const c = links[ch]; if (c) c.socket.destroy(); }
  } else if (cmd === 'touch') {
    const c = links.input;
    if (c) {
      c.write(wsFrame(1, JSON.stringify({ t: 'd', id: 1, x: 0.5, y: 0.5, s: 101 })));
      setTimeout(() => { if (links.input === c) c.write(wsFrame(1, JSON.stringify({ t: 'u', id: 1, x: 0.5, y: 0.5, s: 102 }))); }, 150);
    }
  } else if (cmd === 'key') {
    const c = links.video;
    if (c) c.write(wsFrame(1, '{"t":"key"}'));
  }
}
if (a.cmd) {
  setInterval(() => {
    let text = null;
    try { text = fs.readFileSync(a.cmd, 'utf8'); fs.unlinkSync(a.cmd); } catch (e) { return; }
    for (const line of text.split(/\r?\n/)) if (line.trim()) command(line);
  }, 100);
} else {
  let pendingInput = '';
  process.stdin.on('data', (d) => { pendingInput += d.toString('utf8'); let i; while ((i = pendingInput.indexOf('\n')) >= 0) { command(pendingInput.slice(0, i)); pendingInput = pendingInput.slice(i + 1); } });
  process.stdin.on('end', () => process.exit(0));
}
