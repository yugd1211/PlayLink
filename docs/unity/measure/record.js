// Passive recorder: no injected input. Records what the phone page presents and what Unity does while the owner plays.
// usage: node record.js <seconds> <outfile.json>
const fs = require('fs');
const seconds = Number(process.argv[2] || 60);
const out = process.argv[3] || 'record.json';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  let ws = null, next = 1;
  const waiting = new Map();
  const arrivals = [];
  const call = (method, params = {}) => new Promise((res) => { const mid = next++; waiting.set(mid, res); ws.send(JSON.stringify({ id: mid, method, params })); });
  const evalJs = async (expression) => { const r = await Promise.race([call('Runtime.evaluate', { expression, returnByValue: true }), sleep(3000).then(() => null)]); return r && r.result ? r.result.value : undefined; };
  const pages = (await (await fetch('http://127.0.0.1:9333/json')).json()).filter((p) => p.url.startsWith('http://localhost:7300'));
  for (const p of pages) {
    const sock = new WebSocket(p.webSocketDebuggerUrl);
    if (!(await new Promise((r) => { sock.onopen = () => r(true); sock.onerror = () => r(false); }))) { continue; }
    ws = sock;
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id && waiting.has(m.id)) { const r = waiting.get(m.id); waiting.delete(m.id); r(m.result); return; }
      if (m.method === 'Network.webSocketFrameReceived' && m.params.response.opcode === 2) {
        const b = Buffer.from(m.params.response.payloadData.slice(0, 40), 'base64');
        if (!(b.readUInt32LE(20) & 8)) { arrivals.push([b.readUInt32LE(0), m.params.timestamp * 1000]); }
      }
    };
    const st = await evalJs(`JSON.stringify({hidden: document.hidden, v: gvr.connected.video})`);
    if (st && !JSON.parse(st).hidden && JSON.parse(st).v) { break; }
    sock.close(); ws = null;
  }
  if (!ws) { console.log('no visible Game View Remote page'); process.exit(2); }
  const info = JSON.parse(await evalJs(`JSON.stringify({url: location.href, fs: !!document.fullscreenElement, hud: hudOn, sched: typeof schedOn !== 'undefined' ? schedOn : null, mode: typeof presentMode !== 'undefined' ? presentMode : null, protocol: PROTOCOL_VERSION})`));
  // rAF timestamps and every pointer event (real finger), kept in page memory and drained by polling
  await evalJs(`(() => { if (window.__gvrRec) { return 0; } window.__gvrRec = { raf: [], ptr: [], out: [] };
    (function loop(t) { if (!window.__gvrRec) { return; } window.__gvrRec.raf.push(t); requestAnimationFrame(loop); })(performance.now());
    for (const type of ['pointerdown', 'pointerup', 'pointercancel', 'pointermove']) { addEventListener(type, (e) => { if (window.__gvrRec) { window.__gvrRec.ptr.push([type[7], performance.now(), e.timeStamp, Math.round(e.clientX), Math.round(e.clientY)]); } }, { capture: true, passive: true }); }
    return 1; })()`);
  await call('Network.enable');
  const s0 = await (await fetch('http://127.0.0.1:7300/stats')).json();
  const rec = { info, startedAt: new Date().toISOString(), serverStartMs: s0.nowMs, frames: [], raf: [], ptr: [], inputs: [], t2f: [], unity: [], encodes: [], samples: [], arrivals };
  let lastId = Number(await evalJs(`gvr.last ? gvr.last.id : -1`)), lastUnity = s0.frames.length ? s0.frames[s0.frames.length - 1][0] : -1, lastEncT = s0.nowMs, lastSeq = 0, lastT2f = 0;
  const t0 = Date.now();
  while (Date.now() - t0 < seconds * 1000) {
    await sleep(3000);
    const v = await evalJs(`(() => { const r = window.__gvrRec; const raf = r.raf; r.raf = []; const ptr = r.ptr; r.ptr = [];
      return JSON.stringify({ now: performance.now(), hidden: document.hidden, raf, ptr,
        frames: gvr.frames.filter(f => f.id > ${lastId}).map(f => [f.id, f.cap, f.recvAt, f.drawnAt, f.decodeMs, f.seq, f.due || 0, f.bytes, f.srvMs, f.encMs]),
        inputs: gvr.inputsSent.filter(i => i.seq > ${lastSeq}), t2f: gvr.touchToFrame.filter(t => t.seq > ${lastT2f}),
        c: { superseded: gvr.superseded, late: gvr.late || 0, dropped: gvr.dropped, keyReq: gvr.keyRequests || 0, decErr: check.decodeErr, sched: gvr.sched || null, fps: gvr.fps, rtt: gvr.rtt } }); })()`);
    if (!v) { console.log('page did not answer (hidden or busy)'); continue; }
    const p = JSON.parse(v);
    for (const f of p.frames) { rec.frames.push(f); lastId = Math.max(lastId, f[0]); }
    rec.raf.push(...p.raf); rec.ptr.push(...p.ptr);
    for (const i of p.inputs) { rec.inputs.push(i); lastSeq = Math.max(lastSeq, i.seq); }
    for (const t of p.t2f) { rec.t2f.push(t); lastT2f = Math.max(lastT2f, t.seq); }
    const s = await (await fetch('http://127.0.0.1:7300/stats')).json();
    for (const f of s.frames) { if (f[0] > lastUnity) { rec.unity.push(f); lastUnity = f[0]; } }
    for (const e of s.encodes) { if (e[1] > lastEncT) { rec.encodes.push(e); } }
    if (s.encodes.length) { lastEncT = Math.max(lastEncT, s.encodes[s.encodes.length - 1][1]); }
    rec.samples.push({ pageNow: p.now, serverNow: s.nowMs, hidden: p.hidden, c: p.c, snap: { sent: s.snapshot.sentFps, ack: s.snapshot.ackMs, ackMax: s.snapshot.ackMaxMs, drops: s.snapshot.sendDrops, stride: s.extra.transportStride, unityMax: s.snapshot.unityFrameMaxMs, gc: s.snapshot.gcCount, identical: s.snapshot.skippedIdentical, codec: s.extra.codec }, recv: s.pointerMessagesReceived, applied: s.pointerEventsApplied });
    fs.writeFileSync(out, JSON.stringify(rec));
    const downs = rec.ptr.filter((e) => e[0] === 'd').length;
    process.stdout.write(`\rt=${Math.round((Date.now() - t0) / 1000)}s frames ${rec.frames.length} sent ${s.snapshot.sentFps.toFixed(0)}fps finger downs ${downs} stride ${s.extra.transportStride}   `);
  }
  await call('Network.disable');
  await evalJs(`window.__gvrRec = null; 0`);
  fs.writeFileSync(out, JSON.stringify(rec));
  console.log(`\nsaved ${out}: frames ${rec.frames.length}, rAF ${rec.raf.length}, finger downs ${rec.ptr.filter((e) => e[0] === 'd').length}, unity frames ${rec.unity.length}, arrivals ${arrivals.length}`);
  process.exit(0);
})().catch((e) => { console.log('record failed: ' + e.stack); process.exit(1); });
