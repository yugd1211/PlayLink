#!/usr/bin/env node
// PlayLink 연결 프로토콜의 기준 구현(Node, 외부 모듈 없음). 문서: docs/PROTOCOL.md
// Unity 쪽과 앱 쪽 구현을 이 스크립트를 상대로 시험합니다.
//   node tools/playlink-proto.js selftest                       한 프로세스 안에서 PC ↔ 폰 역할을 세 방식 모두 돌려 봄
//   node tools/playlink-proto.js vectors                        고정 입력의 시험 벡터(JSON)를 출력
//   node tools/playlink-proto.js phone [--port 7300] [--state f.json] [--name N] [--want] [--qr <playlink://pair?…>] [--reject]
//                                                               폰 흉내: TCP 대기 + UDP 탐색 응답. 숫자 확인은 자동으로 [허용]
//   node tools/playlink-proto.js pc --host H [--port 7300] [--state f.json] [--name N] --mode who|sas|qr|paired [--ch video|input] [--secret <b64url>]
//                                                               PC 흉내: 폰으로 접속
//   node tools/playlink-proto.js discover [--state f.json] [--port 7300] [--to 255.255.255.255]
//                                                               UDP 로 묻고 2초 동안 받은 답을 출력
// 키와 비밀 값은 출력하지 않습니다(vectors 의 고정 시험 값 제외).
'use strict';
const crypto = require('crypto');
const net = require('net');
const dgram = require('dgram');
const fs = require('fs');

const VERSION = 1;
const DEFAULT_PORT = 7300;
const MAX_HANDSHAKE_MESSAGE = 2048;
const MAX_RECORD = 65535;
const WRITE_CHUNK = 16384;
const TAG_BYTES = 16;

const b64u = (b) => Buffer.from(b).toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const unb64u = (s) => Buffer.from(String(s || '').replace(/-/g, '+').replace(/_/g, '/'), 'base64');
const sha256 = (...parts) => { const h = crypto.createHash('sha256'); for (const p of parts) h.update(p); return h.digest(); };
const hmac = (key, ...parts) => { const h = crypto.createHmac('sha256', key); for (const p of parts) h.update(p); return h.digest(); };
const u16 = (n) => Buffer.from([(n >> 8) & 255, n & 255]);
const u64 = (n) => { const b = Buffer.alloc(8); b.writeBigUInt64BE(BigInt(n)); return b; };
const BASE32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
const newId = () => Array.from(crypto.randomBytes(16), (x) => BASE32[x & 31]).join('');
const cleanName = (s) => String(s || '').replace(/[^\p{L}\p{N} _\-.()]/gu, '').slice(0, 32);

// ---------------------------------------------------------------- 키 계산

// psk: paired = 페어링 키(32B), qr = SHA256(QR 비밀 16B), sas = 0 32개
function pskFor(mode, secret) {
  if (mode === 'paired') return Buffer.from(secret);
  if (mode === 'qr') return sha256(Buffer.from(secret));
  return Buffer.alloc(32);
}

function transcriptHash(m1, m2, m3) {
  return sha256(Buffer.from('PLK1'), u16(m1.length), m1, u16(m2.length), m2, u16(m3.length), m3);
}

function deriveKeys(th, z, psk) {
  const prk = hmac(th, z, psk);
  const k = (label) => hmac(prk, Buffer.from(label, 'ascii'), Buffer.from([1]));
  const sasKey = k('sas');
  return {
    encI2R: k('enc i2r'), macI2R: k('mac i2r'), encR2I: k('enc r2i'), macR2I: k('mac r2i'), pair: k('pair'),
    sas: String(sasKey.readUInt32BE(0) % 1000000).padStart(6, '0'),
  };
}

// 탐색 응답과 who 응답의 증명(앞 16바이트)
function discoveryMac(key, pcId, phoneId, name, port, want, nonce) {
  return b64u(hmac(key, Buffer.from(`PLK1-DISC|${pcId}|${phoneId}|${name}|${port}|${want ? 1 : 0}|${nonce}`, 'utf8')).subarray(0, 16));
}

function newEphemeral() {
  const e = crypto.createECDH('prime256v1');
  e.generateKeys();
  return e;
}

// ---------------------------------------------------------------- 레코드

function sealRecord(encKey, macKey, seq, plain) {
  const iv = Buffer.concat([u64(seq), Buffer.alloc(8)]);
  const c = crypto.createCipheriv('aes-256-ctr', encKey, iv);
  const ct = Buffer.concat([c.update(plain), c.final()]);
  const len = u16(ct.length);
  const tag = hmac(macKey, u64(seq), len, ct).subarray(0, TAG_BYTES);
  return Buffer.concat([len, ct, tag]);
}

function openRecord(encKey, macKey, seq, len, ct, tag) {
  const want = hmac(macKey, u64(seq), u16(len), ct).subarray(0, TAG_BYTES);
  if (tag.length !== TAG_BYTES || !crypto.timingSafeEqual(want, tag)) throw new Error('bad record tag');
  const iv = Buffer.concat([u64(seq), Buffer.alloc(8)]);
  const d = crypto.createDecipheriv('aes-256-ctr', encKey, iv);
  return Buffer.concat([d.update(ct), d.final()]);
}

// 소켓에서 정해진 바이트 수를 읽는 작은 읽개(Promise)
class Reader {
  constructor(socket) {
    this.chunks = []; this.size = 0; this.waiter = null; this.error = null;
    socket.on('data', (d) => { this.chunks.push(d); this.size += d.length; this.pump(); });
    socket.on('end', () => { this.error = this.error || new Error('eof'); this.pump(); });
    socket.on('close', () => { this.error = this.error || new Error('closed'); this.pump(); });
    socket.on('error', (e) => { this.error = this.error || e; this.pump(); });
  }
  pump() {
    const w = this.waiter;
    if (!w) return;
    if (this.size >= w.n) {
      this.waiter = null;
      const all = Buffer.concat(this.chunks);
      this.chunks = all.length > w.n ? [all.subarray(w.n)] : [];
      this.size = all.length - w.n;
      clearTimeout(w.timer);
      w.resolve(all.subarray(0, w.n));
    } else if (this.error) { this.waiter = null; clearTimeout(w.timer); w.reject(this.error); }
  }
  read(n, timeoutMs) {
    return new Promise((resolve, reject) => {
      const w = { n, resolve, reject, timer: null };
      if (timeoutMs) w.timer = setTimeout(() => { if (this.waiter === w) { this.waiter = null; reject(new Error('timeout')); } }, timeoutMs);
      this.waiter = w;
      this.pump();
    });
  }
}

// 핸드셰이크가 끝난 연결. write/readRecord 가 레코드 단위로 암호화합니다.
class SecureChannel {
  constructor(socket, reader, encOut, macOut, encIn, macIn) {
    Object.assign(this, { socket, reader, encOut, macOut, encIn, macIn, seqOut: 0, seqIn: 0 });
  }
  write(data) {
    const buf = Buffer.isBuffer(data) ? data : Buffer.from(data);
    for (let off = 0; off < buf.length; off += WRITE_CHUNK) {
      this.socket.write(sealRecord(this.encOut, this.macOut, this.seqOut++, buf.subarray(off, Math.min(buf.length, off + WRITE_CHUNK))));
    }
  }
  writeJson(obj) { this.socket.write(sealRecord(this.encOut, this.macOut, this.seqOut++, Buffer.from(JSON.stringify(obj), 'utf8'))); }
  async readRecord(timeoutMs) {
    const head = await this.reader.read(2, timeoutMs);
    const len = head.readUInt16BE(0);
    if (len === 0) throw new Error('empty record');
    const body = await this.reader.read(len + TAG_BYTES, timeoutMs);
    return openRecord(this.encIn, this.macIn, this.seqIn++, len, body.subarray(0, len), body.subarray(len));
  }
  async readJson(timeoutMs) { return JSON.parse((await this.readRecord(timeoutMs)).toString('utf8')); }
  close() { this.socket.destroy(); }
}

// ---------------------------------------------------------------- 핸드셰이크

function sendPlain(socket, obj) {
  const body = Buffer.from(JSON.stringify(obj), 'utf8');
  if (body.length > MAX_HANDSHAKE_MESSAGE) throw new Error('handshake message too long');
  socket.write(Buffer.concat([u16(body.length), body]));
  return body;
}
async function readPlain(reader, timeoutMs) {
  const len = (await reader.read(2, timeoutMs)).readUInt16BE(0);
  if (len === 0 || len > MAX_HANDSHAKE_MESSAGE) throw new Error('bad handshake length');
  const body = await reader.read(len, timeoutMs);
  return { body, msg: JSON.parse(body.toString('utf8')) };
}
class ProtoError extends Error { constructor(code, msg) { super(msg || code); this.code = code; } }

// PC 쪽(거는 쪽). opts: { self:{id,name}, ch, mode, secret, take, caps, proto, confirm(sas)->Promise<bool>, fixed:{ni,eph} }
// 돌려주는 값: { channel, peer:{id,name,app}, keys, fin }
async function initiate(socket, opts) {
  const reader = new Reader(socket);
  const eph = (opts.fixed && opts.fixed.eph) || newEphemeral();
  const epk = eph.getPublicKey();
  const ni = (opts.fixed && opts.fixed.ni) || crypto.randomBytes(16);
  const m1 = sendPlain(socket, { t: 'hello', v: VERSION, pc: opts.self.id, name: opts.self.name, ch: opts.ch, mode: opts.mode,
    take: !!opts.take, ni: b64u(ni), commit: b64u(sha256(epk, ni)) });
  const r2 = await readPlain(reader, opts.helloTimeoutMs || 40000);          // 폰이 "바꿀까요?"를 묻는 동안 기다릴 수 있음
  if (r2.msg.t === 'err') throw new ProtoError(r2.msg.code, r2.msg.msg);
  if (r2.msg.t !== 'hello' || r2.msg.v !== VERSION) throw new ProtoError('version');
  const peerPub = unb64u(r2.msg.epk);
  const m3 = sendPlain(socket, { t: 'key', epk: b64u(epk) });
  const z = eph.computeSecret(peerPub);
  const keys = deriveKeys(transcriptHash(m1, r2.body, m3), z, pskFor(opts.mode, opts.secret));
  const channel = new SecureChannel(socket, reader, keys.encI2R, keys.macI2R, keys.encR2I, keys.macR2I);
  const peer = { id: r2.msg.ph, name: cleanName(r2.msg.name), app: r2.msg.app | 0 };
  try {
    if (opts.mode === 'sas') {
      const [mine, theirs] = await Promise.all([Promise.resolve(opts.confirm ? opts.confirm(keys.sas, peer) : true), channel.readJson(70000)]);
      if (theirs.t !== 'ok') throw new ProtoError(theirs.code || 'rejected', theirs.msg);
      if (!mine) { channel.writeJson({ t: 'err', code: 'rejected' }); throw new ProtoError('rejected'); }
      channel.writeJson({ t: 'ok' });
    }
    channel.writeJson({ t: 'fin', proto: opts.proto || 2, caps: opts.caps || 'h264,seat,take' });
    const fin = await channel.readJson(5000);
    if (fin.t === 'err') throw new ProtoError(fin.code, fin.msg);
    if (fin.t !== 'fin') throw new ProtoError('bad_message');
    return { channel, peer, keys, fin };
  } catch (e) {
    channel.close();
    // 폰이 fin 을 확인하지 못하고 끊으면(키가 서로 다름) 인증 실패로 봅니다.
    throw e instanceof ProtoError ? e : new ProtoError('auth_failed', e.message);
  }
}

// 폰 쪽(받는 쪽). opts: { self:{id,name}, app, lookup(pcId)->key|null, qr:{pc,secret}|null, confirm(sas, peer)->Promise<bool>,
//                         admit(hello)->Promise<null|{code,msg}>, caps, proto, fixed:{nr,eph} }
async function respond(socket, opts) {
  const reader = new Reader(socket);
  const r1 = await readPlain(reader, 5000);
  const h = r1.msg;
  const refuse = (code, msg) => { sendPlain(socket, { t: 'err', code, msg: msg || '' }); socket.end(); throw new ProtoError(code, msg); };
  if (h.t === 'who') {
    sendPlain(socket, discoveryReply(opts, h, socket.localPort));
    socket.end();
    return { who: true };
  }
  if (h.t !== 'hello') refuse('bad_message');
  if (h.v !== VERSION) refuse('version', '앱과 Unity 도구의 버전이 다릅니다.');
  let secret = null;
  if (h.mode === 'paired') {
    if (h.ch !== 'video' && h.ch !== 'input') refuse('bad_message');
    secret = opts.lookup(h.pc);
    if (!secret) refuse('unknown_pc', '이 폰은 이 PC 와 페어링되어 있지 않습니다.');
  } else if (h.mode === 'qr') {
    if (h.ch !== 'pair') refuse('bad_message');
    if (!opts.qr || opts.qr.pc !== h.pc) refuse('unknown_pc', '이 폰이 찍은 QR 이 아닙니다.');
    secret = opts.qr.secret;
  } else if (h.mode === 'sas') {
    if (h.ch !== 'pair') refuse('bad_message');
  } else refuse('bad_message');
  if (opts.admit) { const no = await opts.admit(h); if (no) refuse(no.code, no.msg); }
  const eph = (opts.fixed && opts.fixed.eph) || newEphemeral();
  const nr = (opts.fixed && opts.fixed.nr) || crypto.randomBytes(16);
  const m2 = sendPlain(socket, { t: 'hello', v: VERSION, ph: opts.self.id, name: opts.self.name, app: opts.app | 0, epk: b64u(eph.getPublicKey()), nr: b64u(nr) });
  const r3 = await readPlain(reader, 5000);
  if (r3.msg.t !== 'key') throw new ProtoError('bad_message');
  const peerPub = unb64u(r3.msg.epk);
  if (!crypto.timingSafeEqual(sha256(peerPub, unb64u(h.ni)), Buffer.concat([unb64u(h.commit), Buffer.alloc(32)]).subarray(0, 32))) { socket.destroy(); throw new ProtoError('bad_commit'); }
  const z = eph.computeSecret(peerPub);
  const keys = deriveKeys(transcriptHash(r1.body, m2, r3.body), z, pskFor(h.mode, secret));
  const channel = new SecureChannel(socket, reader, keys.encR2I, keys.macR2I, keys.encI2R, keys.macI2R);
  const peer = { id: h.pc, name: cleanName(h.name) };
  try {
    if (h.mode === 'sas') {
      const allowed = await (opts.confirm ? opts.confirm(keys.sas, peer) : true);
      if (!allowed) { channel.writeJson({ t: 'err', code: 'rejected' }); throw new ProtoError('rejected'); }
      channel.writeJson({ t: 'ok' });
      const theirs = await channel.readJson(70000);
      if (theirs.t !== 'ok') throw new ProtoError(theirs.code || 'rejected');
    }
    const fin = await channel.readJson(5000);              // 여기서 태그가 틀리면 상대의 키가 다른 것입니다
    if (fin.t !== 'fin') throw new ProtoError('bad_message');
    if ((fin.proto | 0) !== (opts.proto || 2)) { channel.writeJson({ t: 'err', code: 'proto', msg: '앱과 Unity 도구의 버전이 다릅니다.' }); throw new ProtoError('proto'); }
    channel.writeJson({ t: 'fin', proto: opts.proto || 2, app: opts.app | 0, caps: opts.caps || 'h264,seat' });
    return { channel, peer, keys, fin, hello: h };
  } catch (e) {
    channel.close();
    throw e instanceof ProtoError ? e : new ProtoError('auth_failed', e.message);
  }
}

// 탐색 응답(UDP 와 TCP who 가 같은 내용). opts 는 respond 와 같고 want(pcId)->bool, busy()->pcId|'' 를 더 봅니다.
function discoveryReply(opts, q, port) {
  const want = !!(opts.want && opts.want(q.pc));
  const r = { t: 'r', v: VERSION, ph: opts.self.id, name: opts.self.name, port, busy: (opts.busy && opts.busy()) || '', want, n: String(q.n || '') };
  const key = opts.lookup && opts.lookup(q.pc);
  if (key) r.mac = discoveryMac(key, q.pc, r.ph, r.name, port, want, r.n);
  else if (opts.qr && opts.qr.pc === q.pc) { r.qr = true; r.mac = discoveryMac(sha256(opts.qr.secret), q.pc, r.ph, r.name, port, want, r.n); }
  return r;
}

// PC 쪽: 응답의 증명을 확인합니다. 'paired' | 'qr' | 'none'(증명 없음) | 'bad'
function checkReply(r, pcId, nonce, pairKey, qrSecret) {
  if (!r || r.t !== 'r' || r.v !== VERSION || r.n !== nonce) return 'bad';
  if (!r.mac) return 'none';
  const eq = (key) => { const a = Buffer.from(String(r.mac)); const b = Buffer.from(discoveryMac(key, pcId, r.ph, r.name, r.port, r.want, r.n)); return a.length === b.length && crypto.timingSafeEqual(a, b); };
  if (r.qr) return qrSecret && eq(sha256(qrSecret)) ? 'qr' : 'bad';
  return pairKey && eq(pairKey) ? 'paired' : 'bad';
}

// QR 내용: playlink://pair?v=1&pc=<pcId>&n=<이름>&s=<비밀 16B base64url>
const qrText = (pcId, name, secret) => `playlink://pair?v=1&pc=${pcId}&n=${encodeURIComponent(name)}&s=${b64u(secret)}`;
function parseQr(text) {
  const m = /^playlink:\/\/pair\?(.*)$/.exec(String(text || ''));
  if (!m) return null;
  const p = new URLSearchParams(m[1]);
  const secret = unb64u(p.get('s'));
  if (p.get('v') !== '1' || !/^[A-Z2-7]{16}$/.test(p.get('pc') || '') || secret.length !== 16) return null;
  return { pc: p.get('pc'), name: cleanName(p.get('n')), secret };
}

module.exports = { VERSION, DEFAULT_PORT, b64u, unb64u, sha256, hmac, newId, cleanName, pskFor, transcriptHash, deriveKeys, discoveryMac,
  sealRecord, openRecord, Reader, SecureChannel, initiate, respond, discoveryReply, checkReply, qrText, parseQr, ProtoError };

// ---------------------------------------------------------------- 상태 파일과 명령

function loadState(file, name) {
  let s = null;
  try { s = JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { }
  if (!s || !s.id) s = { id: newId(), name, peers: {} };
  if (name) s.name = name;
  return s;
}
const saveState = (file, s) => { if (file) fs.writeFileSync(file, JSON.stringify(s, null, 1)); };
const peerKey = (s, id) => (s.peers[id] ? unb64u(s.peers[id].key) : null);

function args(argv) {
  const o = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].startsWith('--')) { const k = argv[i].slice(2); if (i + 1 < argv.length && !argv[i + 1].startsWith('--')) o[k] = argv[++i]; else o[k] = true; } else o._.push(argv[i]);
  }
  return o;
}

async function runPhone(a) {
  const state = loadState(a.state, a.name || 'NodePhone');
  const port = parseInt(a.port || DEFAULT_PORT, 10);
  const qr = a.qr ? parseQr(a.qr) : null;
  let active = '';
  const opts = {
    self: { id: state.id, name: state.name }, app: 100, qr,
    lookup: (pc) => peerKey(state, pc), want: (pc) => !!a.want || (qr && qr.pc === pc), busy: () => active,
    confirm: async (sas, peer) => { console.log(`[phone] 숫자 확인 ${sas} (PC ${peer.name}) → ${a.reject ? '거절' : '허용'}`); return !a.reject; },
  };
  const server = net.createServer(async (socket) => {
    socket.setNoDelay(true);
    try {
      const r = await respond(socket, opts);
      if (r.who) { console.log('[phone] who 에 답함'); return; }
      if (r.hello.ch === 'pair') {
        state.peers[r.peer.id] = { key: b64u(r.keys.pair), name: r.peer.name };
        saveState(a.state, state);
        console.log(`[phone] 페어링 완료: ${r.peer.name} (${r.hello.mode})`);
        r.channel.close();
        return;
      }
      active = r.peer.id;
      console.log(`[phone] ${r.hello.ch} 연결됨: ${r.peer.name} caps=${r.fin.caps}`);
      let bytes = 0, records = 0;
      const tick = setInterval(() => console.log(`[phone] ${r.hello.ch}: 레코드 ${records}개, ${bytes} 바이트`), 2000);
      try { for (;;) { const rec = await r.channel.readRecord(0); records++; bytes += rec.length; if (a.echo) r.channel.write(rec); } }
      catch (e) { console.log(`[phone] ${r.hello.ch} 끝: ${e.message}`); } finally { clearInterval(tick); }
    } catch (e) { console.log(`[phone] 거절/실패: ${e.code || ''} ${e.message}`); socket.destroy(); }
  });
  server.listen(port, a.bind || '0.0.0.0', () => console.log(`[phone] TCP ${port} 에서 대기 (id ${state.id})`));
  const udp = dgram.createSocket({ type: 'udp4', reuseAddr: true });
  udp.on('message', (msg, from) => {
    let q; try { q = JSON.parse(msg.toString('utf8')); } catch (e) { return; }
    if (!q || q.t !== 'q' || q.v !== VERSION) return;
    const reply = Buffer.from(JSON.stringify(discoveryReply(opts, q, port)), 'utf8');
    udp.send(reply, from.port, from.address);
    console.log(`[phone] 탐색 질문 ${from.address}:${from.port} (PC ${cleanName(q.name)}) → 답함`);
  });
  udp.bind(port, () => console.log(`[phone] UDP ${port} 에서 탐색 응답`));
}

async function runPc(a) {
  const state = loadState(a.state, a.name || 'NodePC');
  const port = parseInt(a.port || DEFAULT_PORT, 10);
  const socket = net.connect(port, a.host || '127.0.0.1');
  socket.setNoDelay(true);
  await new Promise((res, rej) => { socket.once('connect', res); socket.once('error', rej); });
  if (a.mode === 'who') {
    const n = b64u(crypto.randomBytes(8));
    sendPlain(socket, { t: 'who', v: VERSION, pc: state.id, name: state.name, n });
    const r = (await readPlain(new Reader(socket), 5000)).msg;
    console.log(`[pc] who: ${r.name} (${r.ph}) port=${r.port} busy=${r.busy || '-'} want=${r.want} 증명=${checkReply(r, state.id, n, peerKey(state, r.ph), a.secret ? unb64u(a.secret) : null)}`);
    socket.destroy();
    return;
  }
  let secret = null;
  if (a.mode === 'qr') secret = unb64u(a.secret);
  if (a.mode === 'paired') { secret = peerKey(state, a.peer || Object.keys(state.peers)[0]); if (!secret) throw new Error('페어링된 폰이 없습니다(--state)'); }
  const ch = a.mode === 'paired' ? (a.ch || 'video') : 'pair';
  const r = await initiate(socket, { self: { id: state.id, name: state.name }, ch, mode: a.mode, secret, take: true,
    confirm: async (sas, peer) => { console.log(`[pc] 숫자 확인 ${sas} (폰 ${peer.name}) → 같음`); return true; } });
  if (ch === 'pair') {
    state.peers[r.peer.id] = { key: b64u(r.keys.pair), name: r.peer.name };
    saveState(a.state, state);
    console.log(`[pc] 페어링 완료: ${r.peer.name}`);
    r.channel.close();
    return;
  }
  console.log(`[pc] ${ch} 연결됨: ${r.peer.name} app=${r.fin.app} caps=${r.fin.caps}`);
  if (a.send) { const n = parseInt(a.send, 10); r.channel.write(crypto.randomBytes(n)); console.log(`[pc] ${n} 바이트 보냄`); }
  try { for (;;) { const rec = await r.channel.readRecord(a.send ? 3000 : 0); console.log(`[pc] 받음 ${rec.length} 바이트`); } } catch (e) { console.log(`[pc] 끝: ${e.message}`); }
  r.channel.close();
}

async function runDiscover(a) {
  const state = loadState(a.state, a.name || 'NodePC');
  const port = parseInt(a.port || DEFAULT_PORT, 10);
  const udp = dgram.createSocket('udp4');
  const n = b64u(crypto.randomBytes(8));
  udp.on('message', (msg, from) => {
    let r; try { r = JSON.parse(msg.toString('utf8')); } catch (e) { return; }
    console.log(`[discover] ${from.address}: ${cleanName(r.name)} (${r.ph}) port=${r.port} want=${r.want} 증명=${checkReply(r, state.id, n, peerKey(state, r.ph), a.secret ? unb64u(a.secret) : null)}`);
  });
  await new Promise((res) => udp.bind(0, res));
  udp.setBroadcast(true);
  const q = Buffer.from(JSON.stringify({ t: 'q', v: VERSION, pc: state.id, name: state.name, n }), 'utf8');
  udp.send(q, port, a.to || '255.255.255.255');
  setTimeout(() => udp.close(), 2000);
}

function fixedVectors() {
  const priv = (fill) => Buffer.alloc(32, fill);
  const ei = crypto.createECDH('prime256v1'); ei.setPrivateKey(priv(0x11));
  const er = crypto.createECDH('prime256v1'); er.setPrivateKey(priv(0x22));
  const z = ei.computeSecret(er.getPublicKey());
  const m1 = Buffer.from('{"t":"hello","v":1,"pc":"AAAAAAAAAAAAAAAA"}'), m2 = Buffer.from('{"t":"hello","v":1,"ph":"BBBBBBBBBBBBBBBB"}'), m3 = Buffer.from('{"t":"key"}');
  const th = transcriptHash(m1, m2, m3);
  const out = { ecdh: { privI: priv(0x11).toString('hex'), privR: priv(0x22).toString('hex'), pubI: ei.getPublicKey().toString('hex'), pubR: er.getPublicKey().toString('hex'), z: z.toString('hex') },
    transcript: { m1: m1.toString(), m2: m2.toString(), m3: m3.toString(), th: th.toString('hex') }, modes: {} };
  const pairKey = Buffer.alloc(32, 0x33), qrSecret = Buffer.alloc(16, 0x44);
  for (const [mode, secret] of [['sas', null], ['qr', qrSecret], ['paired', pairKey]]) {
    const k = deriveKeys(th, z, pskFor(mode, secret));
    out.modes[mode] = { secret: secret ? secret.toString('hex') : '', encI2R: k.encI2R.toString('hex'), macI2R: k.macI2R.toString('hex'), encR2I: k.encR2I.toString('hex'), macR2I: k.macR2I.toString('hex'), pair: k.pair.toString('hex'), sas: k.sas };
  }
  const k = deriveKeys(th, z, pskFor('paired', pairKey));
  const plain = Buffer.from('PlayLink record test vector, 40 bytes..!');
  out.record = { plain: plain.toString('hex'), seq0: sealRecord(k.encI2R, k.macI2R, 0, plain).toString('hex'), seq5: sealRecord(k.encI2R, k.macI2R, 5, plain).toString('hex') };
  out.discovery = { key: pairKey.toString('hex'), pc: 'AAAAAAAAAAAAAAAA', ph: 'BBBBBBBBBBBBBBBB', name: 'Pixel 8', port: 7300, want: true, n: 'AAECAwQFBgc', mac: discoveryMac(pairKey, 'AAAAAAAAAAAAAAAA', 'BBBBBBBBBBBBBBBB', 'Pixel 8', 7300, true, 'AAECAwQFBgc') };
  return out;
}

async function selftest() {
  let failed = 0;
  const check = (name, ok, detail) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); if (!ok) failed++; };
  const pc = { id: newId(), name: 'TEST-PC', peers: {} }, phone = { id: newId(), name: 'Test Phone', peers: {} };
  const qrSecret = crypto.randomBytes(16);
  let phoneOpts = { self: phone, app: 7, lookup: (id) => peerKey(phone, id), qr: { pc: pc.id, secret: qrSecret }, confirm: async () => true };
  const server = net.createServer(); await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;
  const once = (run) => new Promise((resolve) => server.once('connection', (s) => resolve(run(s).catch((e) => ({ error: e })))));
  const dial = async () => { const s = net.connect(port, '127.0.0.1'); await new Promise((r) => s.once('connect', r)); return s; };
  const pair = async (mode, secret, o) => { const side = once((s) => respond(s, Object.assign({}, phoneOpts, o))); const i = await initiate(await dial(), { self: pc, ch: mode === 'paired' ? 'video' : 'pair', mode, secret, confirm: async () => true }).catch((e) => ({ error: e })); return [i, await side]; };

  let [i, r] = await pair('sas', null);
  check('sas: 양쪽 숫자 같음', !i.error && !r.error && i.keys.sas === r.keys.sas && /^\d{6}$/.test(i.keys.sas));
  check('sas: 페어링 키 같음', !i.error && i.keys.pair.equals(r.keys.pair));
  [i, r] = await pair('sas', null, { confirm: async () => false });
  check('sas: 폰에서 거절 → rejected', i.error && i.error.code === 'rejected', i.error && i.error.code);
  [i, r] = await pair('qr', qrSecret);
  check('qr: 완료', !i.error && !r.error && i.keys.pair.equals(r.keys.pair));
  pc.peers[phone.id] = { key: b64u(i.keys.pair) }; phone.peers[pc.id] = { key: b64u(i.keys.pair) };
  [i, r] = await pair('qr', crypto.randomBytes(16));
  check('qr: 비밀이 다르면 auth_failed', i.error && i.error.code === 'auth_failed', i.error && i.error.code);
  [i, r] = await pair('paired', peerKey(pc, phone.id));
  check('paired: 완료', !i.error && !r.error);
  if (!i.error) {
    const big = crypto.randomBytes(100000);
    i.channel.write(big);
    let got = Buffer.alloc(0);
    while (got.length < big.length) got = Buffer.concat([got, await r.channel.readRecord(2000)]);
    check('paired: 100000 바이트 왕복 전송', got.equals(big));
    r.channel.writeJson({ x: 1 });
    check('paired: 반대 방향', (await i.channel.readJson(2000)).x === 1);
    // 변조: 태그 한 바이트를 바꾼 레코드는 거절
    const rec = sealRecord(i.keys.encI2R, i.keys.macI2R, i.channel.seqOut, Buffer.from('x')); rec[rec.length - 1] ^= 1; i.channel.socket.write(rec);
    check('변조된 레코드 거절', await r.channel.readRecord(2000).then(() => false, (e) => /tag/.test(e.message)));
    i.channel.close(); r.channel.close();
  }
  [i, r] = await pair('paired', crypto.randomBytes(32));
  check('paired: 키가 다르면 auth_failed', i.error && i.error.code === 'auth_failed', i.error && i.error.code);
  [i, r] = await pair('paired', peerKey(pc, phone.id), { lookup: () => null });
  check('paired: 폰이 모르는 PC → unknown_pc', i.error && i.error.code === 'unknown_pc', i.error && i.error.code);
  const n = b64u(crypto.randomBytes(8));
  const reply = discoveryReply(Object.assign({}, phoneOpts, { qr: null, want: () => true }), { pc: pc.id, n }, 7300);
  check('탐색 응답 증명(paired)', checkReply(reply, pc.id, n, peerKey(pc, phone.id), null) === 'paired');
  check('탐색 응답: 이름을 바꾸면 bad', checkReply(Object.assign({}, reply, { name: 'Evil' }), pc.id, n, peerKey(pc, phone.id), null) === 'bad');
  check('탐색 응답: 다른 질문의 답이면 bad', checkReply(reply, pc.id, 'other', peerKey(pc, phone.id), null) === 'bad');
  const q = parseQr(qrText(pc.id, 'TEST-PC', qrSecret));
  check('QR 왕복', q && q.pc === pc.id && q.secret.equals(qrSecret) && q.name === 'TEST-PC');
  const v = fixedVectors();
  check('벡터: ECDH 양쪽 같음', (() => { const e = crypto.createECDH('prime256v1'); e.setPrivateKey(Buffer.from(v.ecdh.privR, 'hex')); return e.computeSecret(Buffer.from(v.ecdh.pubI, 'hex')).toString('hex') === v.ecdh.z; })());
  server.close();
  console.log(failed ? `실패 ${failed}개` : '모두 통과');
  process.exit(failed ? 1 : 0);
}

if (require.main === module) {
  const a = args(process.argv.slice(2));
  const cmd = a._[0];
  const run = cmd === 'selftest' ? selftest() : cmd === 'vectors' ? Promise.resolve(console.log(JSON.stringify(fixedVectors(), null, 1)))
    : cmd === 'phone' ? runPhone(a) : cmd === 'pc' ? runPc(a) : cmd === 'discover' ? runDiscover(a)
    : Promise.reject(new Error('사용법은 파일 맨 위 주석을 보세요'));
  run.catch((e) => { console.error(`오류: ${e.code || ''} ${e.message}`); process.exit(1); });
}
