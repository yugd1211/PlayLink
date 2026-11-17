// 앱 기록 파일(rec-*.json)의 표시 수준 지표. 정의는 측정 스크립트 analyze.js / measure.js 와 같습니다(설계 8.2).
//   holds(멈춘 갱신)  : 연속 표시 두 장에서 (표시 간격 − 캡처 간격) > +8ms, 원본이 움직이던 구간(캡처 간격 < 25ms)만
//   skipped           : 받았는데 표시하지 않은 H.264 프레임
//   longest hold      : 움직이던 구간의 표시 간격 최댓값
//   touch → display   : 포인터 메시지를 큐에 넣은 때 → 그 입력 번호를 실은 첫 프레임의 표시 시각(down/up 만)
// 표시 시각은 두 가지로 냅니다(리뷰 B1):
//   release  = 앱이 버퍼를 Surface 로 넘긴 때. 페이지의 그리기 호출 시각(drawnAt)과 같은 자리 → 페이지와 견주는 값
//   rendered = MediaCodec.OnFrameRenderedListener 가 알려 준 시각 → 앱 단독 값
// 사용: node app-report.js <rec.json> [--json] [--skip=<초>](앞부분 버림)
'use strict';
const fs = require('fs');
const rec = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const asJson = process.argv.includes('--json');
const skipS = Number((process.argv.find((a) => a.startsWith('--skip=')) || '--skip=0').slice(7));
const f1 = (v) => Number.isFinite(v) ? v.toFixed(1) : '-';
const q = (a, p) => a.length ? a[Math.min(a.length - 1, Math.floor(a.length * p))] : NaN;
const sorted = (v) => v.filter(Number.isFinite).slice().sort((x, y) => x - y);
const dist = (v) => { const a = sorted(v); return { n: a.length, p50: q(a, 0.5), p90: q(a, 0.9), p99: q(a, 0.99), max: a.length ? a[a.length - 1] : NaN, avg: a.length ? a.reduce((x, y) => x + y, 0) / a.length : NaN }; };
const ds = (d) => `n=${d.n} avg=${f1(d.avg)} p50=${f1(d.p50)} p90=${f1(d.p90)} p99=${f1(d.p99)} max=${f1(d.max)}`;

// app.frames: [gen,id,flags,bytes,capMs,seq,recv,queued,decoded,due,release,target,rendered]
let all = rec.app.frames.map((f) => ({ gen: f[0], id: f[1], flags: f[2], bytes: f[3], cap: f[4], seq: f[5], recv: f[6], queued: f[7], decoded: f[8], due: f[9], release: f[10], target: f[11], rendered: f[12] }));
const lastGen = all.length ? all[all.length - 1].gen : 0;
all = all.filter((f) => f.gen === lastGen && f.recv >= skipS * 1000);
if (all.length < 20) { console.log('too few frames: ' + all.length); process.exit(0); }
const t0 = all[0].recv, t1 = all[all.length - 1].recv, span = (t1 - t0) / 1000;
const ptr = rec.ptr.map((e) => ({ kind: e[0], send: e[1], evt: e[2], x: e[3], y: e[4], s: e[5] })).filter((e) => e.send >= t0);
const raf = rec.raf.filter((t) => t >= t0 && t <= t1);
const rafGaps = []; for (let i = 1; i < raf.length; i++) rafGaps.push(raf[i] - raf[i - 1]);

function presentation(key) {
  const shown = all.filter((f) => f[key] > 0);
  const steps = [];
  for (let i = 1; i < shown.length; i++) { const a = shown[i - 1], b = shown[i]; steps.push({ at: b[key], gap: b[key] - a[key], src: b.cap - a.cap, d: (b[key] - a[key]) - (b.cap - a.cap) }); }
  const moving = steps.filter((s) => s.src < 25);
  const movingSec = moving.reduce((a, s) => a + s.src, 0) / 1000;
  const holds = moving.filter((s) => s.d > 8), jumps = moving.filter((s) => s.d < -8);
  const inner = all.filter((f) => f.id > shown[0].id && f.id < shown[shown.length - 1].id);
  const skipped = inner.filter((f) => !(f[key] > 0));
  const off = Math.min(...shown.map((f) => f[key] - f.cap));
  const t2d = [], e2d = [];
  for (const p of ptr.filter((e) => e.kind === 'd' || e.kind === 'u')) {
    const f = shown.find((x) => x[key] > p.send && x.seq >= p.s);
    if (f && f[key] - p.send < 1000) { t2d.push(f[key] - p.send); e2d.push(f[key] - p.evt); }
  }
  return {
    shown: shown.length, fps: shown.length / span, movingSec, holdsPerS: holds.length / Math.max(0.001, movingSec), jumpsPerS: jumps.length / Math.max(0.001, movingSec),
    skippedPerS: skipped.length / Math.max(0.001, movingSec), skipped: skipped.length, holds: holds.length, longest: moving.length ? Math.max(...moving.map((s) => s.gap)) : NaN,
    interval: dist(moving.map((s) => s.gap)), unevenPct: moving.length ? 100 * moving.filter((s) => s.gap < 9 || s.gap > 25).length / moving.length : NaN,
    capToDisplay: dist(shown.map((f) => f[key] - f.cap - off)), touch: dist(t2d), touchEvent: dist(e2d), steps,
    over50: moving.filter((s) => s.gap > 50).length, over100: moving.filter((s) => s.gap > 100).length,
  };
}
const rel = presentation('release'), ren = presentation('rendered');
const recvGaps = []; for (let i = 1; i < all.length; i++) recvGaps.push(all[i].recv - all[i - 1].recv);
const offRecv = Math.min(...all.map((f) => f.recv - f.cap));
const transit = all.map((f) => f.recv - f.cap - offRecv);
const decode = all.filter((f) => f.decoded > 0 && f.queued > 0).map((f) => f.decoded - f.queued);
const renderLag = all.filter((f) => f.rendered > 0 && f.release > 0).map((f) => f.rendered - f.release);
const targetErr = all.filter((f) => f.rendered > 0 && f.target > 0).map((f) => f.rendered - f.target);
const rendered = all.filter((f) => f.rendered > 0).length, released = all.filter((f) => f.release > 0).length;

// 누름 구간(NF-10): 누르는 동안 프레임이 계속 전달됐는가
const presses = [];
const downs = ptr.filter((e) => e.kind === 'd');
for (const d of downs) {
  const u = ptr.find((e) => (e.kind === 'u' || e.kind === 'c') && e.send >= d.send);
  if (!u || u.send - d.send < 30) continue;
  const during = all.filter((f) => f.recv >= d.send && f.recv <= u.send);
  const around = all.filter((f) => f.recv >= d.send - 20 && f.recv <= u.send + 20);
  let gap = 0; for (let i = 1; i < around.length; i++) gap = Math.max(gap, around[i].recv - around[i - 1].recv);
  const capDuring = all.filter((f) => f.cap + offRecv >= d.send && f.cap + offRecv <= u.send).length;
  const after = all.filter((f) => f.recv > u.send && f.recv <= u.send + 6).length;
  const holdMax = Math.max(0, ...rel.steps.filter((s) => s.at >= d.send && s.at <= u.send + 200 && s.src < 25).map((s) => s.gap));
  presses.push({ ms: u.send - d.send, delivered: during.length, captured: capDuring, gapMax: gap, burstAfter: after, holdMax });
}
const pressSummary = presses.length ? {
  n: presses.length, pressMs: dist(presses.map((p) => p.ms)), deliveryGapMax: dist(presses.map((p) => p.gapMax)), holdMax: dist(presses.map((p) => p.holdMax)),
  zeroDelivered: presses.filter((p) => p.delivered === 0).length, burst3: presses.filter((p) => p.burstAfter >= 3).length,
  delivered: presses.reduce((a, p) => a + p.delivered, 0), captured: presses.reduce((a, p) => a + p.captured, 0),
  stalled: presses.filter((p) => p.gapMax > 50 || p.holdMax > 50).length,
} : null;

const out = {
  file: process.argv[2].replace(/^.*[\\/]/, ''), info: rec.info, seconds: span, frames: all.length, recvFps: all.length / span,
  release: { ...rel, steps: undefined }, rendered: { ...ren, steps: undefined },
  arrival: dist(recvGaps), arrivalOver40: recvGaps.filter((g) => g >= 40).length, bursts3ms: recvGaps.filter((g) => g < 3).length,
  transitExcess: dist(transit), transitOver25Pct: 100 * transit.filter((t) => t >= 25).length / transit.length, transitOver40Pct: 100 * transit.filter((t) => t >= 40).length / transit.length,
  decode: dist(decode), renderedOfReleased: `${rendered}/${released}`, renderLag: dist(renderLag), renderedMinusTarget: dist(targetErr),
  raf: dist(rafGaps.filter((g) => g < 500)), rafUnder12Pct: rafGaps.length ? 100 * rafGaps.filter((g) => g < 12).length / rafGaps.length : NaN, rr: rec.app.rr.filter((r) => r[0] >= t0),
  ptr: { downs: downs.length, moves: ptr.filter((e) => e.kind === 'm').length, deliveryDelay: dist(ptr.map((e) => e.send - e.evt)) }, press: pressSummary, counters: rec.app.counters,
};
if (asJson) { console.log(JSON.stringify(out)); process.exit(0); }
const row = (name, p) => `${name}: holds ${f1(p.holdsPerS)}/s (${p.holds}), jumps ${f1(p.jumpsPerS)}/s, skipped ${f1(p.skippedPerS)}/s (${p.skipped}), longest ${f1(p.longest)} ms, >50ms ${p.over50}, >100ms ${p.over100}, shown ${f1(p.fps)} fps, uneven ${f1(p.unevenPct)}%, interval ${ds(p.interval)}\n    capture->display ${ds(p.capToDisplay)} | touch->display ${ds(p.touch)} | event->display ${ds(p.touchEvent)}`;
console.log(`${out.file}  ${JSON.stringify(rec.info)}`);
console.log(`recorded ${f1(span)} s, H.264 frames ${all.length} (${f1(out.recvFps)}/s), moving ${f1(rel.movingSec)} s`);
console.log(row('RELEASE (page-comparable)', rel));
console.log(row('RENDERED (app only)', ren));
console.log(`rendered callbacks ${out.renderedOfReleased} | rendered - release ${ds(out.renderLag)} | rendered - target ${ds(out.renderedMinusTarget)}`);
console.log(`arrival gap ${ds(out.arrival)} | >=40ms ${out.arrivalOver40} | <3ms ${out.bursts3ms} | transit excess ${ds(out.transitExcess)} (>=25ms ${f1(out.transitOver25Pct)}%, >=40ms ${f1(out.transitOver40Pct)}%)`);
console.log(`decode ms ${ds(out.decode)} | vsync interval ${ds(out.raf)} | vsync<12ms ${f1(out.rafUnder12Pct)}% | display mode changes ${JSON.stringify(out.rr)}`);
console.log(`pointer: downs ${out.ptr.downs}, moves ${out.ptr.moves}, delivery delay (send - event) ${ds(out.ptr.deliveryDelay)}`);
if (pressSummary) console.log(`presses ${pressSummary.n}: press ms ${ds(pressSummary.pressMs)} | delivery gap max while pressed ${ds(pressSummary.deliveryGapMax)} | hold max (down..up+200ms) ${ds(pressSummary.holdMax)} | delivered ${pressSummary.delivered}/${pressSummary.captured} captured | zero-delivery presses ${pressSummary.zeroDelivered} | >=3 frames within 6ms after up ${pressSummary.burst3} | stalled(>50ms) ${pressSummary.stalled}`);
console.log(`counters ${JSON.stringify(rec.app.counters)}`);
