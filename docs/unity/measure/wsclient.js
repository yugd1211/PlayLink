// Scripted video client on this PC (no phone): checks the MJPEG fallback, the H.264 path, the keyframe request and the ack backpressure.
// usage: node wsclient.js <mjpeg|h264|h264-key|h264-noack> [seconds]
const mode = process.argv[2] || 'mjpeg';
const seconds = Number(process.argv[3] || 6);
const HEADER = 28;
const ws = new WebSocket('ws://127.0.0.1:7300/video');
ws.binaryType = 'arraybuffer';
const frames = [];
const texts = [];
let ackEnabled = true, keySentAt = 0, configs = 0;
const pct = (a, p) => a.length ? a[Math.min(a.length - 1, Math.floor(a.length * p))] : NaN;
ws.onopen = () => {
  if (mode !== 'mjpeg') { ws.send(JSON.stringify({ t: 'caps', h264: 1 })); }
  if (mode === 'h264-key') { setTimeout(() => { keySentAt = performance.now(); ws.send(JSON.stringify({ t: 'key' })); }, 2500); }
  if (mode === 'h264-noack') { setTimeout(() => { ackEnabled = false; }, 2000); setTimeout(() => { ackEnabled = true; for (const f of frames.filter((x) => x.unacked)) { ws.send('a'); f.unacked = false; } }, 3500); }
  setTimeout(finish, seconds * 1000);
};
ws.onmessage = (ev) => {
  if (typeof ev.data === 'string') { texts.push(ev.data); return; }
  const now = performance.now();
  const dv = new DataView(ev.data);
  const f = { at: now, id: dv.getUint32(0, true), w: dv.getUint16(4, true), h: dv.getUint16(6, true), flags: dv.getUint32(20, true), cap: dv.getUint32(24, true) / 10, bytes: ev.data.byteLength, unacked: false };
  const body = new Uint8Array(ev.data, HEADER);
  f.jpegOk = body.length > 4 && body[0] === 0xFF && body[1] === 0xD8 && body[body.length - 2] === 0xFF && body[body.length - 1] === 0xD9;
  if (f.flags & 8) { configs++; f.avcC = body[0] === 1; }
  frames.push(f);
  if (ackEnabled) { ws.send('a'); } else { f.unacked = true; }
};
ws.onerror = (e) => { console.log('ws error'); process.exit(1); };
function finish() {
  const video = frames.filter((f) => !(f.flags & 8));
  const h264 = video.filter((f) => f.flags & 2), jpeg = video.filter((f) => !(f.flags & 2));
  const gaps = []; for (let i = 1; i < video.length; i++) { gaps.push(video[i].at - video[i - 1].at); }
  const capGaps = []; for (let i = 1; i < video.length; i++) { capGaps.push(video[i].cap - video[i - 1].cap); }
  const s = gaps.slice().sort((a, b) => a - b);
  const lat = video.map((f) => f.at - f.cap); const base = Math.min(...lat);
  console.log(`mode ${mode}: texts ${JSON.stringify(texts.slice(0, 2))}`);
  console.log(`frames ${video.length} in ${seconds}s (${(video.length / seconds).toFixed(1)}/s): h264 ${h264.length} (key ${h264.filter((f) => f.flags & 4).length}), jpeg ${jpeg.length} (valid SOI/EOI ${jpeg.filter((f) => f.jpegOk).length}), config ${configs}, size ${video.length ? video[0].w + 'x' + video[0].h : '-'}, avg bytes ${Math.round(video.reduce((a, f) => a + f.bytes, 0) / Math.max(1, video.length))}`);
  console.log(`arrival gap ms p50 ${pct(s, 0.5).toFixed(1)} p99 ${pct(s, 0.99).toFixed(1)} max ${s[s.length - 1].toFixed(1)} | capture-time step ms p50 ${pct(capGaps.slice().sort((a, b) => a - b), 0.5).toFixed(1)} | capture->arrival excess max ${(Math.max(...lat) - base).toFixed(1)} | ids monotonic ${video.every((f, i) => i === 0 || f.id > video[i - 1].id)}`);
  if (mode === 'h264-key') {
    const cfg = frames.find((f) => (f.flags & 8) && f.at > keySentAt);
    const key = frames.find((f) => (f.flags & 4) && !(f.flags & 8) && f.at > keySentAt);
    const before = video.filter((f) => f.at <= keySentAt).pop();
    console.log(`keyframe request: config after ${cfg ? (cfg.at - keySentAt).toFixed(0) : 'none'} ms, keyframe after ${key ? (key.at - keySentAt).toFixed(0) : 'none'} ms, gap in video around the request ${key && before ? (key.at - before.at).toFixed(0) : '-'} ms`);
  }
  if (mode === 'h264-noack') {
    const during = video.filter((f) => f.at > t0 + 2000 && f.at <= t0 + 3500);
    const last = during.length ? during[during.length - 1].at - (t0 + 2000) : 0;
    console.log(`no acks for 1500 ms: ${during.length} frames still arrived, the last one ${last.toFixed(0)} ms after acks stopped (expected: sending stops about 150 ms + one ack timeout window), frames in the 1 s after acks resumed: ${video.filter((f) => f.at > t0 + 3500 && f.at <= t0 + 4500).length}`);
  }
  ws.close();
  setTimeout(() => process.exit(0), 200);
}
const t0 = performance.now();
