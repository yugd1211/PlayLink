// Analyze a passive recording (record.js): presentation-level evenness overall and around each real finger tap.
// usage: node analyze.js <record.json> [--timeline=N]
const fs = require('fs');
const rec = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const timelineN = Number((process.argv.find((a) => a.startsWith('--timeline=')) || '--timeline=2').slice(11));
const f1 = (v) => Number.isFinite(v) ? v.toFixed(1) : '-';
const pct = (a, p) => a.length ? a[Math.min(a.length - 1, Math.floor(a.length * p))] : NaN;
const dist = (v) => { const a = v.filter(Number.isFinite).slice().sort((x, y) => x - y); return `n=${a.length} p50=${f1(pct(a, 0.5))} p90=${f1(pct(a, 0.9))} p99=${f1(pct(a, 0.99))} max=${f1(a[a.length - 1])}`; };
const max = (a) => a.length ? Math.max(...a) : 0;

const frames = rec.frames.map((f) => ({ id: f[0], cap: f[1], recvAt: f[2], drawnAt: f[3], dec: f[4], seq: f[5], due: f[6] })).sort((a, b) => a.id - b.id);
if (frames.length < 10) { console.log('too few frames recorded: ' + frames.length); process.exit(0); }
const off = Math.min(...frames.map((f) => f.recvAt - f.cap));   // page clock - server clock (+ minimum transit)
const drawn = new Set(frames.map((f) => f.id));
const arrOff = (() => { const m = new Map(rec.arrivals); const d = frames.filter((f) => m.has(f.id)).map((f) => f.recvAt - m.get(f.id)); d.sort((a, b) => a - b); return d.length ? d[d.length >> 1] : 0; })();
const skipped = rec.arrivals.filter((a) => a[0] > frames[0].id && a[0] < frames[frames.length - 1].id && !drawn.has(a[0])).map((a) => ({ id: a[0], at: a[1] + arrOff }));
const steps = [];
for (let i = 1; i < frames.length; i++) {
  const a = frames[i - 1], b = frames[i];
  steps.push({ at: b.drawnAt, gap: b.drawnAt - a.drawnAt, src: b.cap - a.cap, d: (b.drawnAt - a.drawnAt) - (b.cap - a.cap), id: b.id });
}
const unity = rec.unity.map((f) => ({ frame: f[0], t: f[1], dt: f[2], cap: f[3], renders: f[4], screen: f[5], fingers: f[6], ptr: f[7], capMs: f[8], injMs: f[9], waitMs: f[10] }));
const enc = new Map(rec.encodes.map((e) => [e[0], e[2]]));
const raf = rec.raf;
const span = (frames[frames.length - 1].drawnAt - frames[0].drawnAt) / 1000;
const sch = rec.samples.length ? rec.samples[rec.samples.length - 1].c.sched : null;
console.log(`page ${JSON.stringify(rec.info)} | recorded ${f1(span)} s, frames drawn ${frames.length}, arrived-not-drawn ${skipped.length}, unity frames ${unity.length}`);
console.log(`link/server samples: ack avg ${f1(Math.min(...rec.samples.map((s) => s.snap.ack)))}..${f1(max(rec.samples.map((s) => s.snap.ack)))} ms, ack max ${f1(max(rec.samples.map((s) => s.snap.ackMax)))} ms, stride ${[...new Set(rec.samples.map((s) => s.snap.stride))].join('/')}, send drops max ${max(rec.samples.map((s) => s.snap.drops))}/s, codec ${rec.samples.length ? rec.samples[0].snap.codec : '-'}, sched ${sch ? `delay ${f1(sch.delay)} slips ${sch.slips} vsync ${f1(sch.vsync)}` : 'n/a'}`);

// segments where the picture was changing every frame (the game was moving): source step < 25 ms
const moving = steps.filter((s) => s.src < 25);
const movingSec = moving.reduce((a, s) => a + s.src, 0) / 1000;
const holds = moving.filter((s) => s.d > 8), jumps = moving.filter((s) => s.d < -8);
const rafGaps = []; for (let i = 1; i < raf.length; i++) { rafGaps.push(raf[i] - raf[i - 1]); }
console.log(`moving picture time ${f1(movingSec)} s (${f1(100 * movingSec / span)}% of the recording)`);
console.log(`PRESENTATION while moving: holds (repeated vsync) ${f1(holds.length / movingSec)}/s, early jumps ${f1(jumps.length / movingSec)}/s, skipped frames ${f1(skipped.length / Math.max(1, movingSec))}/s, longest hold ${f1(max(moving.map((s) => s.gap)))} ms, display interval ${dist(moving.map((s) => s.gap))}`);
console.log(`hold sizes (extra ms beyond the source step, rounded to 8 ms): ${JSON.stringify(holds.reduce((m, x) => { const k = Math.round(x.d / 8.33) * 8; m[k] = (m[k] || 0) + 1; return m; }, {}))}`);
console.log(`transit excess (arrival - capture - min) ${dist(frames.map((f) => f.recvAt - f.cap - off))} | decode ms ${dist(frames.map((f) => f.dec))} | capture -> display ${dist(frames.map((f) => f.drawnAt - f.cap - off))}`);
console.log(`rAF interval ${dist(rafGaps.filter((g) => g < 500))} | rAF > 25 ms: ${rafGaps.filter((g) => g > 25 && g < 500).length} | rAF < 12 ms: ${f1(100 * rafGaps.filter((g) => g < 12).length / Math.max(1, rafGaps.length))}%`);
console.log(`unity dt ${dist(unity.map((u) => u.dt))} | > 25 ms: ${unity.filter((u) => u.dt > 25).length} | capture requested without a screen-camera render: ${unity.filter((u) => u.cap === 1 && u.screen === 0).length} | capture skipped (stride/inflight/no texture): ${unity.filter((u) => u.cap >= 2).length}`);
const t2f = rec.t2f.map((t) => t.ms);
console.log(`touch -> display (page measurement, first frame carrying the input) ${dist(t2f)}`);

// ---- real finger taps ----
const downs = rec.ptr.filter((e) => e[0] === 'd').map((e) => ({ at: e[1], evt: e[2], x: e[3], y: e[4] }));
const ups = rec.ptr.filter((e) => e[0] === 'u' || e[0] === 'c');
console.log(`finger: ${downs.length} downs, ${ups.length} ups, ${rec.ptr.filter((e) => e[0] === 'm').length} moves; pointer event delivery delay (handler time - event time) ${dist(rec.ptr.map((e) => e[1] - e[2]))}`);
const W = 500;
const rows = [];
for (const d of downs) {
  if (d.at - W < frames[0].drawnAt || d.at + W > frames[frames.length - 1].drawnAt) { continue; }
  const up = ups.find((u) => u[1] >= d.at);
  const st = d.at - off;   // server time of the touch-down (error: minimum transit, a few ms)
  const win = (a, b) => {
    const s = steps.filter((x) => x.at >= a && x.at < b);
    const mv = s.filter((x) => x.src < 25);
    const sk = skipped.filter((x) => x.at >= a && x.at < b).length;
    const r = raf.filter((t) => t >= a && t < b); let rm = 0; for (let i = 1; i < r.length; i++) { rm = Math.max(rm, r[i] - r[i - 1]); }
    const fr = frames.filter((f) => f.recvAt >= a && f.recvAt < b);
    const uf = unity.filter((u) => u.t >= a - off && u.t < b - off);
    return { uneven: mv.filter((x) => Math.abs(x.d) > 8).length + sk, holds: mv.filter((x) => x.d > 8).length, hold: max(mv.map((x) => x.gap)), frames: fr.length, raf: rm, rafN: r.length, dec: max(fr.map((f) => f.dec)), transit: max(fr.map((f) => f.recvAt - f.cap - off)), udt: max(uf.map((u) => u.dt)), lat: fr.length ? fr.reduce((x, f) => x + f.drawnAt - f.cap - off, 0) / fr.length : NaN };
  };
  const began = unity.find((u) => u.t >= st - 5 && u.ptr > 0);
  const next = began ? unity.find((u) => u.frame === began.frame + 1) : null;
  const next2 = began ? unity.find((u) => u.frame === began.frame + 2) : null;
  rows.push({ d, st, press: up ? up[1] - d.at : NaN, pre: win(d.at - W, d.at), post: win(d.at, d.at + W), began, next, next2 });
}
if (rows.length) {
  const sum = (sel) => rows.reduce((a, r) => a + sel(r), 0);
  const mx = (sel) => max(rows.map(sel).filter(Number.isFinite));
  console.log(`--- ${rows.length} taps with a full ${W} ms window on each side: before -> after the touch-down`);
  console.log(`uneven presentations (hold/jump/skip while moving): ${sum((r) => r.pre.uneven)} -> ${sum((r) => r.post.uneven)} | taps with any: ${rows.filter((r) => r.pre.uneven).length} -> ${rows.filter((r) => r.post.uneven).length}`);
  console.log(`frames arriving per window (source activity): ${f1(sum((r) => r.pre.frames) / rows.length)} -> ${f1(sum((r) => r.post.frames) / rows.length)}`);
  console.log(`longest hold: ${f1(mx((r) => r.pre.hold))} -> ${f1(mx((r) => r.post.hold))} ms | rAF interval max: ${f1(mx((r) => r.pre.raf))} -> ${f1(mx((r) => r.post.raf))} ms | rAF count per window ${f1(sum((r) => r.pre.rafN) / rows.length)} -> ${f1(sum((r) => r.post.rafN) / rows.length)}`);
  console.log(`decode ms max: ${f1(mx((r) => r.pre.dec))} -> ${f1(mx((r) => r.post.dec))} | transit excess max: ${f1(mx((r) => r.pre.transit))} -> ${f1(mx((r) => r.post.transit))} | capture->display avg: ${f1(sum((r) => r.pre.lat || 0) / rows.length)} -> ${f1(sum((r) => r.post.lat || 0) / rows.length)}`);
  console.log(`unity dt max in window: ${dist(rows.map((r) => r.pre.udt))} -> ${dist(rows.map((r) => r.post.udt))}`);
  console.log(`unity frame that applied the touch (Began): dt ${dist(rows.filter((r) => r.began).map((r) => r.began.dt))} | next frame dt ${dist(rows.filter((r) => r.next).map((r) => r.next.dt))} | frame after dt ${dist(rows.filter((r) => r.next2).map((r) => r.next2.dt))} | inject ms ${dist(rows.filter((r) => r.began).map((r) => r.began.injMs))} | readback wait ms ${dist(rows.filter((r) => r.next).map((r) => r.next.waitMs))}`);
  console.log(`press duration ms ${dist(rows.map((r) => r.press))} | taps where the frame after Began took > 25 ms: ${rows.filter((r) => (r.next && r.next.dt > 25) || (r.next2 && r.next2.dt > 25) || (r.began && r.began.dt > 25)).length} of ${rows.length}`);
  const worst = rows.slice().sort((a, b) => (b.post.uneven - a.post.uneven) || (b.post.hold - a.post.hold)).slice(0, timelineN);
  for (const r of worst) {
    console.log(`--- timeline, tap at page t=${f1(r.d.at)} (press ${f1(r.press)} ms): ms relative to touch-down`);
    const ev = [];
    for (const u of unity.filter((x) => x.t >= r.st - 120 && x.t < r.st + 420)) {
      const e = enc.get(u.frame);
      ev.push([u.t - r.st, `U ${u.frame} dt ${f1(u.dt)}${u.ptr ? ' PTR' + u.ptr : ''}${u.fingers ? ' finger' : ''} cap${u.cap}${u.screen ? '' : ' NO-RENDER'} inj ${u.injMs.toFixed(2)} wait ${f1(u.waitMs)} ${e === undefined ? '' : ['h264', 'ident', 'BACKLOG', 'jpeg', 'GUARD'][e]}`]);
    }
    for (const f of frames.filter((x) => x.recvAt >= r.d.at - 120 && x.recvAt < r.d.at + 420)) {
      ev.push([f.recvAt - r.d.at, `   P recv ${f.id} cap@${f1(f.cap - r.st)} transit+${f1(f.recvAt - f.cap - off)} dec ${f1(f.dec)}`]);
      const s = steps.find((x) => x.id === f.id);
      ev.push([f.drawnAt - r.d.at, `      P DRAW ${f.id} step ${s ? f1(s.gap) : '-'} (src ${s ? f1(s.src) : '-'})${s && s.src < 25 && Math.abs(s.d) > 8 ? (s.d > 0 ? '  <== HOLD' : '  <== JUMP') : ''}${f.due ? ` due${f1(f.due - f.drawnAt)}` : ''}`]);
    }
    for (const k of skipped.filter((x) => x.at >= r.d.at - 120 && x.at < r.d.at + 420)) { ev.push([k.at - r.d.at, `   P recv ${k.id} NEVER DRAWN`]); }
    for (const e of rec.ptr.filter((x) => x[0] !== 'm' && x[1] >= r.d.at - 120 && x[1] < r.d.at + 420)) { ev.push([e[1] - r.d.at, `>>> finger ${e[0] === 'd' ? 'DOWN' : 'UP'}`]); }
    const rr = raf.filter((t) => t >= r.d.at - 120 && t < r.d.at + 420);
    for (let i = 1; i < rr.length; i++) { if (rr[i] - rr[i - 1] > 21 || rr[i] - rr[i - 1] < 12) { ev.push([rr[i] - r.d.at, `         rAF interval ${f1(rr[i] - rr[i - 1])}`]); } }
    ev.sort((a, b) => a[0] - b[0]);
    for (const [t, text] of ev) { console.log(`${t.toFixed(1).padStart(7)}  ${text}`); }
  }
}
