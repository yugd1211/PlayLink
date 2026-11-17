// For every real finger press: did video frames keep arriving on the page while the finger was down?
const rec = JSON.parse(require('fs').readFileSync(process.argv[2], 'utf8'));
const frames = rec.frames.map((f) => ({ id: f[0], cap: f[1], recvAt: f[2], drawnAt: f[3] })).sort((a, b) => a.id - b.id);
const m = new Map(rec.arrivals); const d = frames.filter((f) => m.has(f.id)).map((f) => f.recvAt - m.get(f.id)).sort((a, b) => a - b); const arrOff = d[d.length >> 1];
const arr = (Number.isFinite(arrOff) ? rec.arrivals.map((a) => a[1] + arrOff) : []).sort((a, b) => a - b);   // empty in worker mode: DevTools does not see the worker's socket from the page session          // network arrival times (DevTools) on the page clock
const recv = frames.map((f) => f.recvAt);                                           // onmessage times of drawn frames
const off = Math.min(...frames.map((f) => f.recvAt - f.cap));
const caps = frames.map((f) => f.cap + off);                                        // when each drawn frame was captured (page clock)
const downs = rec.ptr.filter((e) => e[0] === 'd'), ups = rec.ptr.filter((e) => e[0] === 'u' || e[0] === 'c');
const f1 = (v) => v.toFixed(1);
const rows = [];
for (const dn of downs) {
  const up = ups.find((u) => u[1] >= dn[1]); if (!up) { continue; }
  const a = dn[1], b = up[1], press = b - a;
  const sourceDuring = caps.filter((t) => t >= a && t < b).length;              // frames the PC captured (and that were later drawn) while the finger was down
  if (press < 30 || sourceDuring < 2) { continue; }                             // need a press long enough and a moving picture
  const netDuring = arr.filter((t) => t >= a + 8 && t < b).length;              // frames reaching the phone's network stack during the press
  const jsDuring = recv.filter((t) => t >= a + 8 && t < b).length;              // frames delivered to the page's onmessage during the press
  const after = recv.filter((t) => t >= b && t < b + 12).length;                // frames delivered within 12 ms after the finger left
  // longest gap between message deliveries that overlaps the press
  let gap = 0; for (let i = 1; i < recv.length; i++) { if (recv[i] >= a && recv[i - 1] <= b) { gap = Math.max(gap, recv[i] - recv[i - 1]); } }
  rows.push({ press, sourceDuring, netDuring, jsDuring, after, gap });
}
rows.sort((x, y) => x.press - y.press);
console.log(`presses analysed (>= 30 ms, picture moving): ${rows.length}`);
console.log('press ms | frames captured during press | reached phone network during press | delivered to page JS during press | delivered within 12 ms after release | longest delivery gap ms');
for (const r of rows) { console.log(`${f1(r.press).padStart(7)} | ${String(r.sourceDuring).padStart(3)} | ${String(r.netDuring).padStart(3)} | ${String(r.jsDuring).padStart(3)} | ${String(r.after).padStart(3)} | ${f1(r.gap)}`); }
const sum = (k) => rows.reduce((a, r) => a + r[k], 0);
console.log(`TOTAL: captured ${sum('sourceDuring')}, reached network ${sum('netDuring')}, delivered to JS ${sum('jsDuring')} (${f1(100 * sum('jsDuring') / Math.max(1, sum('sourceDuring')))}% of captured), released right after finger-up ${sum('after')}`);
const xs = rows.map((r) => r.press), ys = rows.map((r) => r.gap); const mx = xs.reduce((a, b) => a + b, 0) / xs.length, my = ys.reduce((a, b) => a + b, 0) / ys.length;
const cov = xs.reduce((a, x, i) => a + (x - mx) * (ys[i] - my), 0), vx = xs.reduce((a, x) => a + (x - mx) ** 2, 0), vy = ys.reduce((a, y) => a + (y - my) ** 2, 0);
console.log(`correlation(press duration, longest delivery gap) = ${(cov / Math.sqrt(vx * vy)).toFixed(2)}; mean press ${f1(mx)} ms, mean gap ${f1(my)} ms`);
