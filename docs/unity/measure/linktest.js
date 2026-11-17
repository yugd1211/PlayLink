// Link comparison without the game: a node server streams video-sized messages (12.5 KB every 16.7 ms) to a page in the phone's Chrome
// over three routes and measures one-way transit jitter and ack round trip:
//   adb-wifi  : ws://localhost:7312  (adb reverse on the wireless adb transport)
//   direct    : ws://<PC LAN IP>:7312 (plain Wi-Fi TCP, no adb)
//   adb-usb   : ws://localhost:7313  (adb reverse on the USB transport)
// usage: node linktest.js <pcLanIp> <secondsPerPhase> <rounds> [bytes]
const http = require('http');
const crypto = require('crypto');
const pcIp = process.argv[2];
const seconds = Number(process.argv[3] || 20);
const rounds = Number(process.argv[4] || 2);
const BYTES = Number(process.argv[5] || 12528);
const INTERVAL = 1000 / 60;
const routes = [['adb-wifi', 'ws://localhost:7312/s'], ['direct', `ws://${pcIp}:7312/s`], ['adb-usb', 'ws://localhost:7313/s']];
const results = [];

const PAGE = `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width"><body style="background:#000;color:#8f8;font:14px monospace"><pre id="o">link test...</pre><script>
const routes = ${JSON.stringify(routes)}; const rounds = ${rounds}; const o = document.getElementById('o');
const log = (t) => { o.textContent += '\\n' + t; };
async function phase(name, url) {
  return new Promise((res) => {
    let ws; try { ws = new WebSocket(url); } catch (e) { res({ name, error: String(e) }); return; }
    ws.binaryType = 'arraybuffer';
    const arr = [];
    const timer = setTimeout(() => { if (ws.readyState !== 1) { try { ws.close(); } catch (e) {} res({ name, error: 'no connection in 5 s' }); } }, 5000);
    ws.onmessage = (ev) => {
      if (typeof ev.data === 'string') { clearTimeout(timer); ws.send(JSON.stringify({ done: 1, name, arr })); setTimeout(() => { ws.close(); res({ name, n: arr.length }); }, 300); return; }
      const dv = new DataView(ev.data);
      const seq = dv.getUint32(0, true);
      arr.push([seq, dv.getFloat64(4, true), performance.now()]);
      ws.send('a' + seq);
    };
    ws.onerror = () => { clearTimeout(timer); res({ name, error: 'websocket error' }); };
  });
}
(async () => { for (let r = 0; r < rounds; r++) { for (const [name, url] of routes) { log(name + ' ...'); const x = await phase(name, url); log(JSON.stringify(x)); } } log('finished'); fetch('/finished'); })();
</script>`;

function wsFrame(payload, opcode) {
  const n = payload.length;
  const head = n < 126 ? Buffer.from([0x80 | opcode, n]) : n < 65536 ? Buffer.from([0x80 | opcode, 126, n >> 8, n & 255]) : null;
  return Buffer.concat([head, payload]);   // one buffer -> one socket write
}
const pct = (a, p) => a.length ? a[Math.min(a.length - 1, Math.floor(a.length * p))] : NaN;
const f1 = (v) => Number.isFinite(v) ? v.toFixed(1) : '-';

function serve(sock, remote) {
  sock.setNoDelay(true);
  const sent = new Map();
  const ack = [];
  let seq = 0, buf = Buffer.alloc(0), text = '', closed = false;
  const t0 = performance.now();
  const body = Buffer.alloc(BYTES, 7);
  // precise pacing: sleep most of the interval, spin the rest (Windows timers are coarse)
  let nextAt = performance.now();
  const tick = () => {
    if (closed) { return; }
    const now = performance.now();
    if (now >= nextAt) {
      if (now - t0 > seconds * 1000) { sock.write(wsFrame(Buffer.from('end'), 1)); return; }
      body.writeUInt32LE(seq, 0); body.writeDoubleLE(now, 4);
      sent.set(seq, now); seq++;
      sock.write(wsFrame(body, 2));
      nextAt += INTERVAL; if (nextAt < now - 100) { nextAt = now; }
    }
    if (nextAt - performance.now() > 2) { setTimeout(tick, 1); } else { setImmediate(tick); }
  };
  tick();
  sock.on('data', (d) => {
    buf = Buffer.concat([buf, d]);
    for (;;) {
      if (buf.length < 2) { return; }
      let len = buf[1] & 127, off = 2;
      if (len === 126) { if (buf.length < 4) { return; } len = buf.readUInt16BE(2); off = 4; } else if (len === 127) { if (buf.length < 10) { return; } len = Number(buf.readBigUInt64BE(2)); off = 10; }
      const masked = (buf[1] & 128) !== 0; const need = off + (masked ? 4 : 0) + len;
      if (buf.length < need) { return; }
      const op = buf[0] & 15, fin = (buf[0] & 128) !== 0;
      const payload = Buffer.from(buf.subarray(off + (masked ? 4 : 0), need));
      if (masked) { const m = buf.subarray(off, off + 4); for (let i = 0; i < payload.length; i++) { payload[i] ^= m[i & 3]; } }
      buf = buf.subarray(need);
      if (op === 8) { closed = true; sock.end(); return; }
      if (op === 1 || op === 0) {
        text += payload.toString('utf8');
        if (!fin) { continue; }
        const msg = text; text = '';
        if (msg[0] === 'a') { const s = Number(msg.slice(1)); if (sent.has(s)) { ack.push(performance.now() - sent.get(s)); } continue; }
        const r = JSON.parse(msg);
        const tr = r.arr.map((x) => x[2] - x[1]); const base = Math.min(...tr);
        const ex = tr.map((x) => x - base).sort((a, b) => a - b);
        const gaps = []; for (let i = 1; i < r.arr.length; i++) { gaps.push(r.arr[i][2] - r.arr[i - 1][2]); }
        const stalls = gaps.filter((g) => g >= 40);
        const a = ack.slice().sort((x, y) => x - y);
        const line = { route: r.name, remote, n: r.arr.length, sent: seq, transitExcess: { p50: pct(ex, 0.5), p90: pct(ex, 0.9), p99: pct(ex, 0.99), max: ex[ex.length - 1] }, lateShare25: 100 * ex.filter((x) => x >= 25).length / ex.length, lateShare40: 100 * ex.filter((x) => x >= 40).length / ex.length, stalls40: stalls.length, stallMax: Math.max(0, ...gaps), ack: { p50: pct(a, 0.5), p90: pct(a, 0.9), p99: pct(a, 0.99), max: a[a.length - 1] } };
        results.push(line);
        console.log(`${r.name.padEnd(8)} n=${line.n}/${seq} | transit excess ms p50 ${f1(line.transitExcess.p50)} p90 ${f1(line.transitExcess.p90)} p99 ${f1(line.transitExcess.p99)} max ${f1(line.transitExcess.max)} | >=25ms ${f1(line.lateShare25)}% >=40ms ${f1(line.lateShare40)}% | arrival gaps >=40ms: ${line.stalls40} (max ${f1(line.stallMax)}) | ack round trip ms p50 ${f1(line.ack.p50)} p90 ${f1(line.ack.p90)} p99 ${f1(line.ack.p99)} max ${f1(line.ack.max)} | from ${remote}`);
      }
    }
  });
  sock.on('close', () => { closed = true; });
  sock.on('error', () => { closed = true; });
}

const server = http.createServer((req, res) => {
  if (req.url === '/finished') { res.end('ok'); console.log('page finished'); setTimeout(() => process.exit(0), 500); return; }
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(PAGE);
});
server.on('upgrade', (req, sock) => {
  const key = req.headers['sec-websocket-key'];
  const accept = crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  sock.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  serve(sock, `${sock.remoteAddress}`);
});
server.listen(7312, '0.0.0.0', () => console.log(`link test server on 0.0.0.0:7312, ${BYTES} bytes every ${INTERVAL.toFixed(1)} ms, ${seconds} s per phase, ${rounds} rounds`));
setTimeout(() => { console.log('timeout'); process.exit(1); }, (seconds * 3 * rounds + 60) * 1000);
