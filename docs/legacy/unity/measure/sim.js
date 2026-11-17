// Offline check of the page's presentation scheduler: runs the real schedEnqueue/schedTake code from the page
// against synthetic arrivals, for a 60 Hz and a 120 Hz display, and compares with the old "one per rAF in arrival order" path.
const fs = require('fs');
const html = fs.readFileSync(process.argv[2], 'utf8');
const src = html.slice(html.indexOf('const SCHED_START_DELAY_MS'), html.indexOf('function queuePresent('));
function rng(seed) { let s = seed; return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; }; }
function gauss(r) { return Math.sqrt(-2 * Math.log(r() + 1e-12)) * Math.cos(2 * Math.PI * r()); }
function run(vsync, useSched, srcFps, seed) {
  let now = 0;
  const gvr = { late: 0, superseded: 0 }, presentQueue = [], performance = { now: () => now }, schedOn = true;
  const api = new Function('gvr', 'presentQueue', 'performance', 'schedOn', src + '; return { schedEnqueue, schedTake, sched };')(gvr, presentQueue, performance, schedOn);
  const r = rng(seed);
  const frames = [];
  let cap = 1000;
  for (let i = 0; i < 3600; i++) {
    cap = 1000 + (i + 1) * 1000 / srcFps + gauss(r) * 0.6;     // editor frame time jitter (does not accumulate: the editor holds its target rate)
    let delay = 9 + Math.abs(gauss(r)) * 2.5;                  // transit + decode jitter
    if (r() < 0.01) { delay += 15 + r() * 20; }                // occasional late frame
    frames.push({ cap, decodedAt: cap + 5000 + delay });       // 5000 = clock offset between PC and phone
  }
  for (let i = 1; i < frames.length; i++) { frames[i].decodedAt = Math.max(frames[i].decodedAt, frames[i - 1].decodedAt + 0.2); }
  const shown = [];
  let fi = 0;
  for (let t = 6000.3; t < frames[frames.length - 1].decodedAt + 100; t += vsync) {
    while (fi < frames.length && frames[fi].decodedAt <= t) {
      now = frames[fi].decodedAt;
      const f = { cap: frames[fi].cap, i: fi };
      if (useSched) { api.schedEnqueue({ close() {} }, f); }
      else { presentQueue.push({ bmp: { close() {} }, f }); if (presentQueue.length > 2) { while (presentQueue.length > 1) { presentQueue.shift(); gvr.superseded++; } } }
      fi++;
    }
    now = t;
    const item = useSched ? api.schedTake(t) : presentQueue.shift();
    if (item) { shown.push({ t, cap: item.f.cap, i: item.f.i }); if (process.env.DBG && useSched && shown.length > 600 && shown.length < 660) { console.log(`t ${t.toFixed(1)} i ${item.f.i} r ${(t - item.f.due - api.sched.phase).toFixed(1)} phase ${api.sched.phase.toFixed(2)} vs ${api.sched.vsync.toFixed(2)} q ${presentQueue.length} lateBy ${(frames[item.f.i].decodedAt - item.f.due).toFixed(1)}`); } }
  }
  const s = shown.slice(300);   // skip warm-up
  const iv = [], err = [], lat = [];
  for (let i = 1; i < s.length; i++) {
    iv.push(s[i].t - s[i - 1].t);
    err.push(Math.abs((s[i].t - s[i - 1].t) - (s[i].cap - s[i - 1].cap)));   // display step vs. source step
    lat.push(s[i].t - s[i].cap - 5000);
  }
  const share = (p) => (100 * iv.filter(p).length / iv.length).toFixed(1);
  const mean = (a) => (a.reduce((x, y) => x + y, 0) / a.length).toFixed(1);
  const slots = {}; for (const g of iv) { const k = Math.round(g / vsync); slots[k] = (slots[k] || 0) + 1; }
  console.log(`${(1000 / vsync).toFixed(0)}Hz src ${srcFps}fps ${useSched ? 'sched' : 'asap '}: shown ${s.length} dropped ${gvr.superseded} | interval in vsync slots ${JSON.stringify(slots)} | >25ms ${share((g) => g > 25)}% <12ms ${share((g) => g < 12)}% | mean |step error| ${mean(err)} ms | capture->display ${mean(lat)} ms | delay ${api.sched.delay.toFixed(1)} slips ${api.sched.slips} late ${gvr.late}`);
}
if (process.env.DBG) { run(1000 / 60, true, 60.0, 7); } else { for (const fps of [60.0, 60.24]) { for (const vs of [1000 / 60, 1000 / 120]) { run(vs, false, fps, 7); run(vs, true, fps, 7); } } }
