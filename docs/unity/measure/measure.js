// Game View Remote measurement: phone page (Chrome DevTools) + server /stats, with optional injected touches.
// usage: node measure.js <mode: idle|tap|drag> <seconds> [x y] [--detail]
const { spawn } = require('child_process');
const ADB = 'C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.62f2\\Editor\\Data\\PlaybackEngines\\AndroidPlayer\\SDK\\platform-tools\\adb.exe';
const mode = process.argv[2] || 'idle';
const seconds = Number(process.argv[3] || 10);
const tx = Number(process.argv[4] || 540), ty = Number(process.argv[5] || 1000);
const detail = process.argv.includes('--detail');
const tapEvery = Number((process.argv.find((a) => a.startsWith('--every=')) || '--every=1500').slice(8));

const press = Number((process.argv.find((a) => a.startsWith('--press=')) || '--press=100').slice(8));
const still = process.argv.includes('--still');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const pct = (a, p) => a.length ? a[Math.min(a.length - 1, Math.floor(a.length * p))] : NaN;
const sorted = (v) => v.filter(Number.isFinite).slice().sort((x, y) => x - y);
const f1 = (v) => Number.isFinite(v) ? v.toFixed(1) : '-';
function summary(values) {
  const a = sorted(values);
  const mean = a.reduce((s, v) => s + v, 0) / (a.length || 1);
  return `n=${a.length} mean=${f1(mean)} p50=${f1(pct(a, 0.5))} p90=${f1(pct(a, 0.9))} p99=${f1(pct(a, 0.99))} max=${f1(a[a.length - 1])}`;
}
const share = (values, pred) => values.length ? (100 * values.filter(pred).length / values.length) : NaN;

async function stats() {
  const r = await fetch('http://127.0.0.1:7300/stats');
  return r.json();
}

(async () => {
  const waitSeconds = Number((process.argv.find((a) => a.startsWith('--wait=')) || '--wait=0').slice(7));
  const waitUntil = Date.now() + waitSeconds * 1000;
  let ws = null, next = 1;
  const waiting = new Map();
  const recv = [], sent = [];
  function call(method, params = {}) {
    return new Promise((res) => { const mid = next++; waiting.set(mid, res); ws.send(JSON.stringify({ id: mid, method, params })); });
  }
  const evalJs = async (expression, awaitPromise = false) => {
    const r = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise });
    return r && r.result ? r.result.value : undefined;
  };
  for (;;) {
  const pages = await (await fetch('http://127.0.0.1:9333/json')).json();
  const candidates = pages.filter((p) => p.url.startsWith('http://localhost:7300'));
  for (const p of candidates) {
    const sock = new WebSocket(p.webSocketDebuggerUrl);
    const ok = await new Promise((res) => { sock.onopen = () => res(true); sock.onerror = () => res(false); });
    if (!ok) { continue; }
    ws = sock;
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id && waiting.has(m.id)) { const r = waiting.get(m.id); waiting.delete(m.id); r(m.result); return; }
      if (m.method === 'Network.webSocketFrameReceived') {
        const r = m.params.response;
        if (r.opcode === 2) {
          const b = Buffer.from(r.payloadData.slice(0, 40), 'base64');
          recv.push({ t: m.params.timestamp * 1000, id: b.readUInt32LE(0), flags: b.readUInt32LE(20) });
        }
      } else if (m.method === 'Network.webSocketFrameSent') {
        sent.push({ t: m.params.timestamp * 1000, data: m.params.response.payloadData.slice(0, 16) });
      }
    };
    const state = await Promise.race([evalJs(`JSON.stringify({hidden: document.hidden, v: gvr.connected.video})`), sleep(1500).then(() => null)]);
    if (state && !JSON.parse(state).hidden && JSON.parse(state).v) { break; }
    sock.close(); ws = null;
  }
  if (ws || Date.now() >= waitUntil) { break; }
  await sleep(3000);
  }
  if (!ws) { console.log('no active Game View Remote page'); process.exit(2); }

  const info = JSON.parse(await evalJs(`JSON.stringify({fs: !!document.fullscreenElement, hud: hudOn, sched: typeof schedOn !== 'undefined' ? schedOn : null, codec: gvr.last && gvr.last.codec, inner: [innerWidth, innerHeight], rect, dpr})`));
  console.log(`# mode=${mode} seconds=${seconds} page=${JSON.stringify(info)}`);
  await evalJs(`window.__gvrRaf = []; (function loop(t) { if (!window.__gvrRaf) { return; } window.__gvrRaf.push(t); requestAnimationFrame(loop); })(performance.now()); 0`);
  await evalJs(`window.__gvrS = []; clearInterval(window.__gvrSI); window.__gvrSI = setInterval(() => { const c = gvr.sched; window.__gvrS.push([performance.now(), c.delay, c.phase, c.slips, c.vsync]); }, 100); 0`);
  await call('Network.enable');
  const before = JSON.parse(await evalJs(`JSON.stringify({now: performance.now(), superseded: gvr.superseded, catchups: gvr.catchups || 0, dropped: gvr.dropped, late: gvr.late || 0})`));
  const s0 = await stats();
  const t0 = Date.now();

  // touch injection (only while our page is visible and focused: the owner may be using the phone)
  const pageOnScreen = async () => (await Promise.race([evalJs(`!document.hidden && document.hasFocus()`), sleep(800).then(() => false)])) === true;
  let injecting = true;
  const injector = (async () => {
    if (mode === 'tap') {
      await sleep(1000);
      while (injecting && Date.now() - t0 < seconds * 1000 - 1200) {
        const started = Date.now();
        if (!(await pageOnScreen())) { console.log('page not on screen: tap skipped'); await sleep(500); continue; }
        await new Promise((res) => spawn(ADB, press > 0 ? ['shell', 'input', 'swipe', String(tx), String(ty), String(tx + (still ? 0 : 4)), String(ty + (still ? 0 : 4)), String(press)] : ['shell', 'input', 'tap', String(tx), String(ty)]).on('exit', res));
        await sleep(Math.max(0, tapEvery - (Date.now() - started)));
      }
    } else if (mode === 'start') {
      await fetch('http://127.0.0.1:7300/debug?pattern=0');
      await sleep(2500);
      while (injecting && Date.now() - t0 < seconds * 1000 - 3500) {
        if (!(await pageOnScreen())) { console.log('page not on screen: tap skipped'); await sleep(500); continue; }
        await new Promise((res) => spawn(ADB, ['shell', 'input', 'tap', String(tx), String(ty)]).on('exit', res));
        await fetch('http://127.0.0.1:7300/debug?pattern=1');
        await sleep(2500);
        await fetch('http://127.0.0.1:7300/debug?pattern=0');
        await sleep(2500);
      }
    } else if (mode === 'drag') {
      await sleep(1000);
      let flip = false;
      while (injecting && Date.now() - t0 < seconds * 1000 - 3500) {
        const a = flip ? [tx + 150, ty + 200, tx - 150, ty - 200] : [tx - 150, ty - 200, tx + 150, ty + 200];
        flip = !flip;
        if (!(await pageOnScreen())) { console.log('page not on screen: swipe skipped'); await sleep(500); continue; }
        await new Promise((res) => spawn(ADB, ['shell', 'input', 'swipe', ...a.map(String), '2500']).on('exit', res));
      }
    }
  })();
  await sleep(seconds * 1000);
  injecting = false;
  await injector;
  await call('Network.disable');
  const s1 = await stats();
  const page = JSON.parse(await evalJs(`(() => { const raf = window.__gvrRaf; window.__gvrRaf = null; clearInterval(window.__gvrSI); return JSON.stringify({now: performance.now(), raf, ss: window.__gvrS,
    frames: gvr.frames.map(f => [f.id, f.cap, f.recvAt, f.drawnAt, f.decodeMs, f.seq, f.bytes, f.due || 0, f.srvMs, f.encMs]),
    inputs: gvr.inputsSent, t2f: gvr.touchToFrame, superseded: gvr.superseded, catchups: gvr.catchups || 0, dropped: gvr.dropped, late: gvr.late || 0,
    delay: gvr.sched ? gvr.sched.delay : NaN, slips: gvr.sched ? gvr.sched.slips : 0, vsync: gvr.sched ? gvr.sched.vsync : 0, rtt: gvr.rtt, decErr: check.decodeErr, keyReq: gvr.keyRequests || 0}); })()`));
  ws.close();

  // ---- phone frames within the window ----
  const frames = page.frames.filter((f) => f[2] >= before.now).map((f) => ({ id: f[0], cap: f[1], recvAt: f[2], drawnAt: f[3], decodeMs: f[4], seq: f[5], bytes: f[6], due: f[7], srvMs: f[8], encMs: f[9] }));
  const byId = new Map(frames.map((f) => [f.id, f]));
  const arrivals = recv.filter((r) => !(r.flags & 8));
  const span = arrivals.length > 1 ? (arrivals[arrivals.length - 1].t - arrivals[0].t) / 1000 : 0;
  const arrivalGaps = [];
  for (let i = 1; i < arrivals.length; i++) { arrivalGaps.push(arrivals[i].t - arrivals[i - 1].t); }
  // transit = network arrival - capture time (constant clock offset removed by subtracting the minimum)
  const pageOffset = Math.min(...frames.map((f) => f.recvAt - f.cap));
  const transit = frames.map((f) => f.recvAt - f.cap - pageOffset);
  const display = frames.map((f) => f.drawnAt - f.cap - pageOffset);
  const drawGaps = [], drawGapsContinuous = [];
  for (let i = 1; i < frames.length; i++) {
    const g = frames[i].drawnAt - frames[i - 1].drawnAt;
    drawGaps.push(g);
    // only where the source itself was continuous (the server skips identical pictures, so a gap there is not a stutter)
    if (frames[i].cap - frames[i - 1].cap < 25) { drawGapsContinuous.push(g); }
  }
  // presentation step vs. source step: +1 vsync = the previous picture was held one refresh too long (repeat), -1 = the picture advanced early (jump)
  const steps = [];
  for (let i = 1; i < frames.length; i++) { steps.push({ at: frames[i].drawnAt, d: (frames[i].drawnAt - frames[i - 1].drawnAt) - (frames[i].cap - frames[i - 1].cap), gap: frames[i].drawnAt - frames[i - 1].drawnAt, src: frames[i].cap - frames[i - 1].cap }); }
  const shownSpan = frames.length > 1 ? (frames[frames.length - 1].drawnAt - frames[0].drawnAt) / 1000 : 1;
  const holds = steps.filter((x) => x.d > 8), jumps = steps.filter((x) => x.d < -8);
  const drawnIds = new Set(frames.map((f) => f.id));
  const skippedArrivals = arrivals.filter((a) => frames.length && a.id > frames[0].id && a.id < frames[frames.length - 1].id && !drawnIds.has(a.id));
  const longestHold = Math.max(0, ...steps.filter((x) => x.src < 25).map((x) => x.gap));
  const capGaps = []; for (let i = 1; i < frames.length; i++) { capGaps.push(frames[i].cap - frames[i - 1].cap); }
  const notDrawn = arrivals.filter((a) => !byId.has(a.id) && a.id < (frames.length ? frames[frames.length - 1].id : 0) && a.id > (frames.length ? frames[0].id : 0)).length;
  const rafGaps = []; for (let i = 1; i < page.raf.length; i++) { rafGaps.push(page.raf[i] - page.raf[i - 1]); }

  // ---- server frames within the window ----
  const uf = s1.frames.filter((f) => f[1] >= s0.nowMs).map((f) => ({ frame: f[0], t: f[1], dt: f[2], cap: f[3], renders: f[4], screen: f[5], fingers: f[6], ptr: f[7], capMs: f[8], injMs: f[9], waitMs: f[10] }));
  const enc = s1.encodes.filter((e) => e[1] >= s0.nowMs);
  const kinds = [0, 0, 0, 0, 0]; for (const e of enc) { kinds[e[2]]++; }
  const tapsSent = sent.filter((s) => s.data.includes('"t":"d"')).length;
  const movesSent = sent.filter((s) => s.data.includes('"t":"m"')).length;
  const upsSent = sent.filter((s) => s.data.includes('"t":"u"') || s.data.includes('"t":"c"')).length;
  const acks = sent.filter((s) => s.data === 'a').length;
  const t2f = page.t2f.filter((t) => page.inputs.some((i) => i.seq === t.seq && i.at >= before.now)).map((t) => t.ms);

  console.log(`frames arrived ${arrivals.length} (${f1(arrivals.length / span)}/s), drawn ${frames.length} (${f1(frames.length / seconds)}/s), arrived-not-drawn ${notDrawn} (${f1(100 * notDrawn / Math.max(1, arrivals.length))}%), superseded +${page.superseded - before.superseded}, catch-up +${page.catchups - before.catchups}, decoder-drop +${page.dropped - before.dropped}, late +${page.late - before.late}, decErr ${page.decErr}, keyReq ${page.keyReq}, sched delay ${f1(page.delay)} slips ${page.slips} vsync ${f1(page.vsync)}`);
  console.log(`source (capture) interval ms: ${summary(capGaps)} | >25ms ${f1(share(capGaps, (g) => g > 25))}%`);
  console.log(`arrival gap ms: ${summary(arrivalGaps)} | >=40ms ${f1(share(arrivalGaps, (g) => g >= 40))}%`);
  console.log(`transit excess ms (arrival - capture - min): ${summary(transit)} | >=25ms ${f1(share(transit, (g) => g >= 25))}% >=40ms ${f1(share(transit, (g) => g >= 40))}%`);
  console.log(`display interval ms (all): ${summary(drawGaps)} | >25ms ${f1(share(drawGaps, (g) => g > 25))}% >40ms ${f1(share(drawGaps, (g) => g > 40))}%`);
  console.log(`display interval ms (source continuous): ${summary(drawGapsContinuous)} | >25ms ${f1(share(drawGapsContinuous, (g) => g > 25))}% >40ms ${f1(share(drawGapsContinuous, (g) => g > 40))}% <9ms ${f1(share(drawGapsContinuous, (g) => g < 9))}%`);
  console.log(`PRESENTATION: holds (repeat vsync) ${f1(holds.length / shownSpan)}/s, early jumps ${f1(jumps.length / shownSpan)}/s, skipped frames ${f1(skippedArrivals.length / shownSpan)}/s, longest hold ${f1(longestHold)} ms, hold sizes ms ${JSON.stringify(holds.slice(0, 400).reduce((m, x) => { const k = Math.round(x.d / 8.33) * 8; m[k] = (m[k] || 0) + 1; return m; }, {}))}, shown ${f1(frames.length / shownSpan)} fps`);
  console.log(`capture -> display ms (minus clock offset): ${summary(display)}`);
  console.log(`receive -> display ms: ${summary(frames.map((f) => f.drawnAt - f.recvAt))} | decode ms: ${summary(frames.map((f) => f.decodeMs))}`);
  console.log(`rAF interval ms: ${summary(rafGaps)} | rAF/s ${f1(page.raf.length / seconds)} | >25ms ${f1(share(rafGaps, (g) => g > 25))}% <12ms ${f1(share(rafGaps, (g) => g < 12))}%`);
  {
    // display rate actually observed (main-thread rAF), split by state: within 500 ms after a touch-down vs. more than 1000 ms after the last one
    const downsAll = page.inputs.filter((i) => i.t === 'd' && i.at >= before.now).map((i) => i.at);
    const sinceDown = (t) => { let m = Infinity; for (const d of downsAll) { if (t >= d) { m = Math.min(m, t - d); } } return m; };
    const hz = (g) => g.length ? 1000 / (g.reduce((a, b) => a + b, 0) / g.length) : NaN;
    const gT = [], gI = [];
    for (let i = 1; i < page.raf.length; i++) { const sd = sinceDown(page.raf[i]); (sd < 500 ? gT : sd > 1000 ? gI : []).push(page.raf[i] - page.raf[i - 1]); }
    const fast = (g) => f1(share(g, (x) => x < 12));
    // display steps of consecutive source frames, in refresh slots of 8.33 ms
    const hist = {}; for (const x of steps) { if (x.src < 25) { const k = Math.round(x.gap / 8.33); hist[k] = (hist[k] || 0) + 1; } }
    const n = Object.values(hist).reduce((a, b) => a + b, 0) || 1;
    console.log(`RAF RATE: overall ${f1(hz(rafGaps))} Hz (intervals under 12 ms ${fast(rafGaps)}%) | within 500 ms after touch-down ${f1(hz(gT))} Hz (${fast(gT)}% fast) | no touch for 1 s ${f1(hz(gI))} Hz (${fast(gI)}% fast)`);
    console.log(`DISPLAY STEPS (source continuous) in 8.3 ms slots: ${Object.keys(hist).sort((a, b) => a - b).map((k) => `${k}x=${f1(100 * hist[k] / n)}%`).join(' ')}`);
    {
      // refresh-rate changes seen by rAF (60 <-> 120 Hz): 4 intervals in a row on the other side of 12 ms
      const tr = []; let fastNow = null;
      for (let i = 4; i < page.raf.length; i++) {
        const g = [0, 1, 2, 3].map((k) => page.raf[i - k] - page.raf[i - k - 1]);
        const allFast = g.every((x) => x < 12), allSlow = g.every((x) => x >= 12);
        if (fastNow === null) { if (allFast || allSlow) { fastNow = allFast; } continue; }
        if (fastNow && allSlow) { fastNow = false; tr.push({ at: page.raf[i - 3], to: 60 }); } else if (!fastNow && allFast) { fastNow = true; tr.push({ at: page.raf[i - 3], to: 120 }); }
      }
      const ev = steps.filter((x) => x.src < 25 && Math.abs(x.d) > 6);
      const near = (t) => tr.some((q) => t >= q.at - 50 && t < q.at + 400);
      const nearEv = ev.filter((x) => near(x.at));
      const nearSpan = tr.length * 0.45, farSpan = Math.max(0.001, shownSpan - nearSpan);
      console.log(`RATE CHANGES: ${tr.filter((q) => q.to === 120).length} to 120 Hz, ${tr.filter((q) => q.to === 60).length} to 60 Hz | uneven steps (more than 6 ms off the source step) within -50..+400 ms of a change: ${nearEv.length} (${f1(nearEv.length / Math.max(0.001, nearSpan))}/s), elsewhere ${ev.length - nearEv.length} (${f1((ev.length - nearEv.length) / farSpan)}/s)`);
      for (const q of tr.slice(0, 6)) {
        const fr = frames.filter((f) => f.drawnAt >= q.at - 60 && f.drawnAt < q.at + 260);
        const sd = sinceDown(q.at);
        console.log(`  change to ${q.to} Hz (${Number.isFinite(sd) ? Math.round(sd) + ' ms after touch-down' : 'no touch before'}): display steps ms ${fr.slice(1).map((f, i) => Math.round(f.drawnAt - fr[i].drawnAt)).join(',')}`);
      }
    }
    if (process.argv.includes('--series')) {
      // 250 ms bins: [T = touch-down] rAF count | max transit excess | max decode ms | sched delay | uneven steps
      const start = before.now, bins = Math.ceil((page.now - start) / 250); const out = [];
      for (let k = 0; k < bins; k++) {
        const a0 = start + k * 250, z = a0 + 250;
        const fr = frames.filter((f) => f.recvAt >= a0 && f.recvAt < z);
        const sv = (page.ss || []).filter((x) => x[0] >= a0 && x[0] < z);
        out.push(`${downsAll.some((d) => d >= a0 && d < z) ? 'T' : ''}${page.raf.filter((t) => t >= a0 && t < z).length}|${Math.round(Math.max(0, ...fr.map((f) => f.recvAt - f.cap - pageOffset)))}|${Math.round(Math.max(0, ...fr.map((f) => f.decodeMs)))}|${sv.length ? Math.round(sv[sv.length - 1][1]) : '-'}|${steps.filter((x) => x.at >= a0 && x.at < z && x.src < 25 && Math.abs(x.d) > 6).length}`);
      }
      console.log('SERIES 250ms [T]rAF|transit max|decode max|delay|uneven: ' + out.join('  '));
    }
    const ss = page.ss || [];
    if (ss.length > 1) {
      const col = (i) => ss.map((x) => x[i]);
      const slipAt = []; for (let i = 1; i < ss.length; i++) { if (ss[i][3] !== ss[i - 1][3]) { slipAt.push(`${ss[i][3] - ss[i - 1][3]}@${Number.isFinite(sinceDown(ss[i][0])) ? Math.round(sinceDown(ss[i][0])) + 'ms after down' : 'no touch'}`); } }
      console.log(`SCHED: delay ms min ${f1(Math.min(...col(1)))} max ${f1(Math.max(...col(1)))} end ${f1(ss[ss.length - 1][1])} | vsync estimate ms min ${f1(Math.min(...col(4)))} max ${f1(Math.max(...col(4)))} | slips +${ss[ss.length - 1][3] - ss[0][3]} [${slipAt.join(', ')}]`);
    }
  }
  console.log(`unity frames ${uf.length} (${f1(uf.length / seconds)}/s) dt ms: ${summary(uf.map((f) => f.dt))} | >20ms ${uf.filter((f) => f.dt > 20).length} >25ms ${uf.filter((f) => f.dt > 25).length} | capture ms max ${f1(Math.max(...uf.map((f) => f.capMs)))} inject ms max ${f1(Math.max(...uf.map((f) => f.injMs)))} readback wait max ${f1(Math.max(...uf.map((f) => f.waitMs)))}`);
  console.log(`unity captures: requested ${uf.filter((f) => f.cap === 1).length}, stride-skip ${uf.filter((f) => f.cap === 2).length}, inflight-skip ${uf.filter((f) => f.cap === 3).length}, no screen-camera render before capture ${uf.filter((f) => f.cap === 1 && f.screen === 0).length} | encodes: h264 ${kinds[0]} identical-skip ${kinds[1]} backlog-skip ${kinds[2]} jpeg ${kinds[3]} guard ${kinds[4]}`);
  console.log(`touch: down sent ${tapsSent}, move sent ${movesSent} (${f1(movesSent / seconds)}/s), up/cancel sent ${upsSent}; server received +${s1.pointerMessagesReceived - s0.pointerMessagesReceived}, applied +${s1.pointerEventsApplied - s0.pointerEventsApplied}; acks ${acks}; touch->display ms: ${summary(t2f)}`);
  const snap = s1.snapshot;
  console.log(`server 1s snapshot: sent ${f1(snap.sentFps)} fps, ack ${f1(snap.ackMs)}/${f1(snap.ackMaxMs)} ms, send drops ${snap.sendDrops}, stride ${snap.transportStride}, unity max ${f1(snap.unityFrameMaxMs)} ms, gc ${snap.gcCount}, codec ${s1.extra.codec}`);

  {
    // motion onsets: the first frames of a run of continuous frames that follows a pause in the stream (the picture was still or slow, then starts moving)
    const onsets = [];
    for (let i = 1; i + 12 < frames.length; i++) {
      if (frames[i].cap - frames[i - 1].cap < 60) { continue; }
      let ok = true; for (let k = 1; k <= 12; k++) { if (frames[i + k].cap - frames[i + k - 1].cap > 25) { ok = false; break; } }
      if (ok) { onsets.push(i); }
    }
    if (onsets.length) {
      const steady = frames.filter((f, i) => i > 20 && frames[i].cap - frames[i - 1].cap < 25 && frames[i - 20] && frames[i].cap - frames[i - 20].cap < 400);
      const avg = (a) => a.length ? a.reduce((x, y) => x + y, 0) / a.length : NaN;
      const lat = (f) => f.drawnAt - f.cap - pageOffset, tr = (f) => f.recvAt - f.cap - pageOffset;
      console.log(`MOTION ONSET (${onsets.length} onsets after a pause of at least 60 ms): per frame index after the pause, averages [capture->display | transit excess | decode | server capture->encoded] vs steady state [${f1(avg(steady.map(lat)))} | ${f1(avg(steady.map(tr)))} | ${f1(avg(steady.map((f) => f.decodeMs)))} | ${f1(avg(steady.map((f) => f.srvMs)))}]`);
      let line = '';
      for (let k = 0; k < 10; k++) { const fs = onsets.map((i) => frames[i + k]); line += `#${k} ${f1(avg(fs.map(lat)))}|${f1(avg(fs.map(tr)))}|${f1(avg(fs.map((f) => f.decodeMs)))}|${f1(avg(fs.map((f) => f.srvMs)))}  `; }
      console.log('  ' + line);
      console.log(`  first frame worst case: capture->display ${f1(Math.max(...onsets.map((i) => lat(frames[i]))))} ms, display steps of the first 6 frames (ms): ${onsets.slice(0, 4).map((i) => [1, 2, 3, 4, 5, 6].map((k) => Math.round(frames[i + k].drawnAt - frames[i + k - 1].drawnAt)).join(',')).join(' ; ')}`);
    }
  }
  if (mode === 'start') {
    // time series in 250 ms bins: rAF count (display rate), frames drawn, uneven display steps
    const start = before.now, bins = Math.ceil((page.now - start) / 250);
    let line = '';
    for (let b = 0; b < bins; b++) {
      const a = start + b * 250, z = a + 250;
      const raf = page.raf.filter((t) => t >= a && t < z).length;
      const fr = frames.filter((f) => f.drawnAt >= a && f.drawnAt < z);
      let odd = 0; for (const f of fr) { const i = frames.indexOf(f); if (i > 0 && frames[i].cap - frames[i - 1].cap < 25) { const g = frames[i].drawnAt - frames[i - 1].drawnAt; if (g < 12 || g > 21) { odd++; } } }
      const tap = page.inputs.some((i) => i.t === 'd' && i.at >= a && i.at < z) ? 'T' : ' ';
      line += `${tap}${raf}/${fr.length}/${odd} `;
    }
    console.log('250ms bins [T=tap] rAF/frames drawn/uneven steps(<12ms or >21ms): ' + line);
    const moving = [];
    for (let i = 1; i < frames.length; i++) { if (frames[i].cap - frames[i - 1].cap < 25) { moving.push({ g: frames[i].drawnAt - frames[i - 1].drawnAt, fast: page.raf.filter((t) => t >= frames[i].drawnAt - 100 && t < frames[i].drawnAt).length > 9 }); } }
    const at120 = moving.filter((m) => m.fast), at60 = moving.filter((m) => !m.fast);
    const odd = (a) => a.length ? (100 * a.filter((m) => m.g < 12 || m.g > 21).length / a.length).toFixed(1) : '-';
    console.log(`steps while display ran above 90Hz: ${at120.length}, uneven ${odd(at120)}% | steps while at 60Hz: ${at60.length}, uneven ${odd(at60)}%`);
  }
  // ---- per-tap comparison: 500 ms before vs 500 ms after each touch-down ----
  const downs = page.inputs.filter((i) => i.t === 'd' && i.at >= before.now + 600 && i.at <= page.now - 700);
  if (downs.length) {
    const rows = [];
    for (const d of downs) {
      const serverT = d.at - pageOffset;  // page time -> server time (error = minimum transit, a few ms)
      const win = (arr, key, a, b) => arr.filter((x) => x[key] >= a && x[key] < b);
      const drawIv = (a, b) => { const fr = win(frames, 'drawnAt', a, b); const g = []; for (let i = 1; i < fr.length; i++) { g.push(fr[i].drawnAt - fr[i - 1].drawnAt); } return g; };
      const pre = { draw: drawIv(d.at - 500, d.at), uf: win(uf, 't', serverT - 500, serverT), fr: win(frames, 'recvAt', d.at - 500, d.at) };
      const post = { draw: drawIv(d.at, d.at + 500), uf: win(uf, 't', serverT, serverT + 500), fr: win(frames, 'recvAt', d.at, d.at + 500) };
      const tr = (fr) => Math.max(0, ...fr.map((f) => f.recvAt - f.cap - pageOffset));
      const t2 = page.t2f.find((t) => t.seq === d.seq);
      const evts = (a, b) => steps.filter((x) => x.at >= a && x.at < b && Math.abs(x.d) > 8).length + skippedArrivals.filter((x) => { const f = frames.find((y) => y.id > x.id); return f && f.recvAt >= a && f.recvAt < b; }).length;
      const rafMax = (a, b) => { const r = page.raf.filter((t) => t >= a && t < b); let m = 0; for (let i = 1; i < r.length; i++) { m = Math.max(m, r[i] - r[i - 1]); } return m; };
      const decMax = (fr) => Math.max(0, ...fr.map((f) => f.decodeMs));
      pre.evts = evts(d.at - 500, d.at); post.evts = evts(d.at, d.at + 500); pre.raf = rafMax(d.at - 500, d.at); post.raf = rafMax(d.at, d.at + 500); pre.dec = decMax(pre.fr); post.dec = decMax(post.fr);
      { let g = 0; const a = d.at, b = d.at + press + 30; for (let i = 1; i < frames.length; i++) { if (frames[i].recvAt >= a && frames[i - 1].recvAt <= b) { g = Math.max(g, frames[i].recvAt - frames[i - 1].recvAt); } } post.deliveryGap = g; post.holdMax = Math.max(0, ...post.draw); }
      rows.push({ pre, post, t2: t2 ? t2.ms : NaN, d, serverT,
        line: `tap seq ${d.seq}: frames drawn ${pre.fr.length}->${post.fr.length} | display interval max ${f1(Math.max(0, ...pre.draw))}->${f1(Math.max(0, ...post.draw))} | unity dt max ${f1(Math.max(0, ...pre.uf.map((f) => f.dt)))}->${f1(Math.max(0, ...post.uf.map((f) => f.dt)))} | transit excess max ${f1(tr(pre.fr))}->${f1(tr(post.fr))} | touch->display ${f1(t2 ? t2.ms : NaN)}` });
    }
    console.log(`--- per tap (500 ms before -> 500 ms after touch-down), ${rows.length} taps`);
    for (const r of rows) { console.log(r.line); }
    const agg = (sel) => summary(rows.map(sel));
    const tot = (sel) => rows.reduce((a, r) => a + sel(r), 0);
    console.log(`PER TAP (${rows.length} taps, 500 ms before -> after): uneven presentations (hold/jump/skip) ${tot((r) => r.pre.evts)} -> ${tot((r) => r.post.evts)} | rAF interval max ${f1(Math.max(...rows.map((r) => r.pre.raf)))} -> ${f1(Math.max(...rows.map((r) => r.post.raf)))} | decode ms max ${f1(Math.max(...rows.map((r) => r.pre.dec)))} -> ${f1(Math.max(...rows.map((r) => r.post.dec)))} | unity dt max ${f1(Math.max(...rows.map((r) => Math.max(0, ...r.pre.uf.map((f) => f.dt)))))} -> ${f1(Math.max(...rows.map((r) => Math.max(0, ...r.post.uf.map((f) => f.dt)))))} | transit excess max ${f1(Math.max(...rows.map((r) => Math.max(0, ...r.pre.fr.map((f) => f.recvAt - f.cap - pageOffset)))))} -> ${f1(Math.max(...rows.map((r) => Math.max(0, ...r.post.fr.map((f) => f.recvAt - f.cap - pageOffset)))))}`);
    console.log(`PRESS (${press} ms${still ? ', no movement' : ''}): longest gap between frame deliveries during the press: ${rows.map((r) => Math.round(r.post.deliveryGap)).join(', ')} ms | longest picture hold after touch-down: ${rows.map((r) => Math.round(r.post.holdMax)).join(', ')} ms`);
    console.log(`display interval max: before ${agg((r) => Math.max(0, ...r.pre.draw))} | after ${agg((r) => Math.max(0, ...r.post.draw))}`);
    console.log(`unity dt max: before ${agg((r) => Math.max(0, ...r.pre.uf.map((f) => f.dt)))} | after ${agg((r) => Math.max(0, ...r.post.uf.map((f) => f.dt)))}`);
    if (detail) {
      for (const r of rows.slice(0, 3)) {
        console.log(`--- timeline around tap seq ${r.d.seq} (ms relative to touch-down on the page)`);
        const ev = [];
        for (const f of uf.filter((x) => x.t >= r.serverT - 150 && x.t < r.serverT + 450)) {
          const e = enc.find((x) => x[0] === f.frame);
          ev.push([f.t - r.serverT, `U frame ${f.frame} dt ${f1(f.dt)} cap ${f.cap} scr ${f.screen} ptr ${f.ptr} fingers ${f.fingers} inj ${f.injMs.toFixed(2)} wait ${f1(f.waitMs)} enc ${e ? ['h264', 'IDENT', 'BACKLOG', 'jpeg', 'GUARD'][e[2]] : '-'}`]);
        }
        for (const f of frames.filter((x) => x.recvAt >= r.d.at - 150 && x.recvAt < r.d.at + 450)) {
          ev.push([f.recvAt - r.d.at, `  P recv id ${f.id} cap ${f1(f.cap - r.serverT)} transit+${f1(f.recvAt - f.cap - pageOffset)} dec ${f1(f.decodeMs)} seq ${f.seq}`]);
          ev.push([f.drawnAt - r.d.at, `    P draw id ${f.id} (capture->display +${f1(f.drawnAt - f.cap - pageOffset)})`]);
        }
        for (const t of page.raf.filter((x) => x >= r.d.at - 150 && x < r.d.at + 450)) { ev.push([t - r.d.at, '      raf']); }
        ev.sort((a, b) => a[0] - b[0]);
        let rafRun = 0;
        for (const [t, text] of ev) {
          if (text.trim() === 'raf') { rafRun++; continue; }
          if (rafRun) { console.log(`           (${rafRun} rAF)`); rafRun = 0; }
          console.log(`${t.toFixed(1).padStart(8)}  ${text}`);
        }
      }
    }
  }
  process.exit(0);
})().catch((e) => { console.log('measure failed: ' + e.stack); process.exit(1); });
