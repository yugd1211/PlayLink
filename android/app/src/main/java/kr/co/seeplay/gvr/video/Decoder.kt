package kr.co.seeplay.gvr.video

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.Process
import android.util.Log
import android.view.Choreographer
import android.view.Surface
import kr.co.seeplay.gvr.diag.Counters
import kr.co.seeplay.gvr.diag.Settings
import kr.co.seeplay.gvr.diag.TraceRing
import java.nio.ByteBuffer
import java.util.ArrayDeque

/**
 * MediaCodec H.264 디코더. 표시 일정의 규칙은 video.Pacer(순수 클래스)에 있고 여기서는 버퍼를 넘기고 버리는 일만 합니다. 설계 7.3, 7.4.
 * 소켓을 모릅니다: 영상 채널이 변환을 마친 Annex B 바이트를 넘겨줍니다.
 * 영상의 길: gvr-video-rx(onFrame) → MediaCodec → gvr-codec(출력 콜백) → gvr-pacer(vsync) → Surface. UI 스레드는 이 길에 없습니다.
 * 예외 하나: Surface 가 사라질 때 UI 스레드가 setSurface(null) 로 디코더 해제가 끝날 때까지 기다립니다(설계 리뷰 m1).
 *
 * 주인(owner): 디코더는 Activity 하나가 오래 쥐고 세션은 바뀝니다. 지금 화면의 주인인 세션만 디코더를 건드릴 수 있게,
 * attach(owner) 로 정한 주인과 다른 호출(reset / onConfig / onFrame / expectFirstFrame)은 버립니다(코드 리뷰 M2:
 * 멈춘 옛 세션이 새 세션의 디코더를 해제하거나 옛 프레임을 넣던 것).
 */
class Decoder(private val trace: TraceRing, private val counters: Counters, private val settings: Settings, private val host: Host) {
    interface Host {
        /** 키프레임이 필요함(오류, 손상, 밀림, Surface 재생성). 세션이 2초에 1번으로 제한해 보냅니다. */
        fun requestKey(reason: String)
        /** expectFirstFrame() 뒤 처음으로 프레임을 Surface 로 넘겼습니다. */
        fun onFirstFrameReleased()
        /** 원본 프레임률(캡처 시각 간격에서 잰 값, 반올림)이 바뀌었습니다. 주사율 요청에 씁니다(코드 리뷰 M3). */
        fun onSourceFps(fps: Int)
    }

    private class InFrame(var data: ByteArray) { var len = 0; var ptsUs = 0L; var key = false; var slot = 0; var atNs = 0L }

    private val tag = "GVR"
    private val codecThread = HandlerThread("gvr-codec", Process.THREAD_PRIORITY_DISPLAY).apply { start() }
    private val pacerThread = HandlerThread("gvr-pacer", Process.THREAD_PRIORITY_URGENT_DISPLAY).apply { start() }
    private val codecHandler = Handler(codecThread.looper)
    private val pacerHandler = Handler(pacerThread.looper)

    private val lock = Any()
    private var owner: Any? = null
    private var codec: MediaCodec? = null
    private var codecGen = 0
    private var surface: Surface? = null
    private var config: AvcConfig? = null
    private var width = 0
    private var height = 0
    private var waitingKey = true
    private var pendingPrefix = false
    @Volatile private var notifyNextRelease = false
    private val pendingInput = ArrayDeque<InFrame>()
    private val freeInputs = ArrayDeque<Int>()
    private val pool = ArrayDeque<InFrame>()
    private val pacer = Pacer()
    private var lastCreateNs = 0L
    @Volatile var generation = 0; private set

    // 원본 프레임 간격(codec 스레드에서만 씀)
    private var lastCapUs = 0L
    private val srcDiff = LongArray(31)
    private var srcN = 0L
    private var srcFps = 0

    // 버퍼를 디코더에 돌려주는 두 가지: 표시 일정이 버림(건너뜀으로 셈) / 조용히 반환
    private val skip = Pacer.Drop { item -> if (item.gen == codecGen) discard(item.index, item.slot) }

    // ---------------------------------------------------------------- vsync / pacer

    private val frameCallback = Choreographer.FrameCallback { t -> onVsync(t, t + 2 * pacer.vsyncNs.toLong()); scheduleVsync() }
    private val vsyncCallback: Any? = if (Build.VERSION.SDK_INT >= 33) Choreographer.VsyncCallback { data ->
        onVsync(data.frameTimeNanos, pickTarget(data)); scheduleVsync()
    } else null
    @Volatile private var running = true

    // 박자 루프 시작. 반드시 위의 running / 콜백 초기화 **뒤**에 있어야 합니다(코틀린은 선언 순서대로 초기화).
    // 통합 시험에서 발견: 이 블록이 running 선언보다 앞에 있을 때, 설치 직후 첫 실행처럼 생성자가 느리면 박자 스레드가 먼저 돌아
    // running 이 아직 false 인 것을 보고 루프를 시작하지 않았습니다.
    init {
        pacerHandler.post { Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY); scheduleVsync() }
    }

    private fun scheduleVsync() {
        if (!running) return
        val ch = Choreographer.getInstance()
        if (Build.VERSION.SDK_INT >= 33) ch.postVsyncCallback(vsyncCallback as Choreographer.VsyncCallback) else ch.postFrameCallback(frameCallback)
    }

    /**
     * 표시 목표 시각. 기본(pref)은 시스템이 권하는 일정(기록에서 넘긴 때 + 2주기)입니다.
     * target=early 면 마감이 아직 지나지 않은 가장 이른 일정을 고릅니다(코드 리뷰 M1 의 4: 한 주기를 줄일 수 있는지 폰에서 볼 값. 미검증).
     */
    private fun pickTarget(data: Choreographer.FrameData): Long {
        if (Build.VERSION.SDK_INT < 33) return data.frameTimeNanos
        val preferred = data.preferredFrameTimeline.expectedPresentationTimeNanos
        if (settings.target != "early") return preferred
        val now = System.nanoTime()
        var best = preferred
        for (t in data.frameTimelines) if (t.deadlineNanos > now && t.expectedPresentationTimeNanos < best) best = t.expectedPresentationTimeNanos
        return best
    }

    private var lastVsyncNs = 0L

    private fun onVsync(frameTimeNs: Long, presentNs: Long) {
        trace.vsync(frameTimeNs)
        // 패널이 원본보다 느리게 도는 것을 셉니다(가변 주사율 폰이 30Hz 로 내려간 경우, 리뷰 M3)
        if (lastVsyncNs != 0L && codec != null && frameTimeNs - lastVsyncNs > counters.sourceIntervalMs * 1.5e6f) counters.slowVsync.incrementAndGet()
        lastVsyncNs = frameTimeNs
        synchronized(lock) {
            val c = codec
            if (c == null) { pacer.clearQueue(); pacer.onVsync(frameTimeNs, skip); return }
            if (settings.pace != "sched") { pacer.drain(skip); pacer.onVsync(frameTimeNs, skip); return }     // 방식이 바뀌면 쥐고 있던 버퍼를 반환(리뷰 m11)
            pacer.maxQueue = settings.holdLimit
            val item = pacer.onVsync(frameTimeNs, skip)
            counters.vsyncMs = (pacer.vsyncNs / 1e6).toFloat()
            counters.phaseMs = (pacer.phaseNs / 1e6).toFloat()
            if (item != null && item.gen == codecGen) release(c, item.index, item.slot, presentNs)
        }
    }

    private fun release(c: MediaCodec, index: Int, slot: Int, targetNs: Long) {
        try {
            val now = System.nanoTime()
            c.releaseOutputBuffer(index, targetNs)
            trace.releaseNs[slot] = now; trace.targetNs[slot] = targetNs
            counters.released.incrementAndGet()
            if (notifyNextRelease) { notifyNextRelease = false; host.onFirstFrameReleased() }
        } catch (e: Exception) { counters.releaseErrors.incrementAndGet() }
    }

    private fun discard(index: Int, slot: Int) {
        try { codec?.releaseOutputBuffer(index, false) } catch (e: Exception) { counters.releaseErrors.incrementAndGet() }
        trace.releaseNs[slot] = -1
        counters.skipped.incrementAndGet()
    }

    private fun applyBufSetting() {
        val buf = settings.buf
        pacer.percentile = settings.percentile
        when (buf) {
            "low" -> { pacer.fixedDelayNs = 0; pacer.floorDelayNs = 0 }
            "smooth" -> { pacer.fixedDelayNs = 50_000_000L; pacer.floorDelayNs = 0 }
            "auto" -> { pacer.fixedDelayNs = -1; pacer.floorDelayNs = 0 }
            else -> { pacer.fixedDelayNs = -1; pacer.floorDelayNs = (buf.toLongOrNull() ?: 0L).coerceIn(0L, 200L) * 1_000_000L }     // 숫자 = 지연의 바닥(페이지의 ?buffer=N)
        }
    }

    private fun noteSource(capUs: Long) {
        if (lastCapUs != 0L && capUs > lastCapUs && capUs - lastCapUs < 200_000) {
            srcDiff[(srcN % srcDiff.size).toInt()] = (capUs - lastCapUs) * 1000; srcN++
            if (srcN % 31 == 0L) {
                val c = srcDiff.copyOf(); c.sort()
                val ns = c[c.size / 2]
                counters.sourceIntervalMs = ns / 1e6f
                val fps = Math.round(1e9 / ns).toInt()
                if (fps != srcFps && fps in 10..240) { srcFps = fps; host.onSourceFps(fps) }
            }
        }
        lastCapUs = capUs
    }

    // ---------------------------------------------------------------- surface / codec lifetime

    /** UI 스레드. 지금부터 이 디코더의 주인은 owner 입니다(null = 주인 없음). 이전 주인의 디코더와 설정은 버립니다. */
    fun attach(newOwner: Any?) {
        synchronized(lock) {
            owner = newOwner
            releaseCodecLocked()
            config = null
            pacer.reset()
        }
    }

    /** UI 스레드. null 이면 디코더 해제가 끝난 뒤 돌아옵니다(Surface 가 무효가 되기 전에). 만드는 일은 gvr-codec 이 합니다. */
    fun setSurface(s: Surface?) {
        synchronized(lock) {
            surface = s
            if (s == null) { releaseCodecLocked(); return }
        }
        codecHandler.post {
            var needKey = false
            synchronized(lock) { if (surface === s && codec == null && config != null) { createCodecLocked(); needKey = codec != null } }
            if (needKey) host.requestKey("surface")
        }
    }

    /** 다음에 Surface 로 넘기는 프레임에서 onFirstFrameReleased 를 부르게 합니다(세션이 WAITING / NOCODEC 에 들어갈 때마다). */
    fun expectFirstFrame(who: Any) { synchronized(lock) { if (who === owner) notifyNextRelease = true } }

    /** 연결이 새로 맺어질 때(세대가 바뀜). 디코더는 다음 설정 메시지에서 새로 만듭니다. 주인이 아니면 false. */
    fun reset(who: Any, newGeneration: Int): Boolean {
        synchronized(lock) {
            if (who !== owner) return false
            generation = newGeneration
            releaseCodecLocked()
            config = null
            pacer.reset(); lastCapUs = 0
            return true
        }
    }

    fun shutdown() {
        running = false
        synchronized(lock) { owner = null; releaseCodecLocked(); surface = null }
        codecThread.quitSafely(); pacerThread.quitSafely()
    }

    private fun releaseCodecLocked() {
        val c = codec ?: return
        codec = null
        codecGen++
        pacer.clearQueue(); freeInputs.clear()
        while (pendingInput.isNotEmpty()) pool.add(pendingInput.poll()!!)
        try { c.stop() } catch (e: Exception) { }
        try { c.release() } catch (e: Exception) { }
        waitingKey = true; pendingPrefix = false
    }

    private fun pickDecoder(): String? {
        if (settings.decoderName.isNotEmpty()) return settings.decoderName
        val all = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
            !info.isEncoder && info.supportedTypes.any { it.equals("video/avc", true) } && !info.name.endsWith(".secure") &&
                (if (Build.VERSION.SDK_INT >= 29) info.isHardwareAccelerated else !(info.name.startsWith("OMX.google.") || info.name.startsWith("c2.android.")))
        }
        fun lowLatency(info: MediaCodecInfo) = Build.VERSION.SDK_INT >= 30 && try {
            info.getCapabilitiesForType("video/avc").isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency) } catch (e: Exception) { false }
        return (all.firstOrNull { it.name.endsWith(".low_latency") } ?: all.firstOrNull { lowLatency(it) } ?: all.firstOrNull())?.name
    }

    private fun createCodecLocked() {
        val cfg = config ?: return
        val s = surface ?: return
        if (!s.isValid) return
        releaseCodecLocked()
        var c: MediaCodec? = null
        try {
            val name = pickDecoder()
            c = if (name != null) MediaCodec.createByCodecName(name) else MediaCodec.createDecoderByType("video/avc")
            val f = MediaFormat.createVideoFormat("video/avc", width, height)
            f.setByteBuffer("csd-0", ByteBuffer.wrap(cfg.sps))
            f.setByteBuffer("csd-1", ByteBuffer.wrap(cfg.pps))
            val keys = StringBuilder()
            if (Build.VERSION.SDK_INT >= 30) { f.setInteger(MediaFormat.KEY_LOW_LATENCY, 1); keys.append("ll=1 ") }
            f.setInteger(MediaFormat.KEY_PRIORITY, 0)
            f.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 1024 * 1024)
            val n = c.name.lowercase()
            if (n.contains("qti") || n.contains("qcom")) {
                f.setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE.toInt())
                f.setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
                f.setInteger("vendor.qti-ext-dec-picture-order.enable", 1)
                keys.append("qti=1 ")
            } else {
                f.setInteger(MediaFormat.KEY_OPERATING_RATE, 120)
                if (n.contains("exynos")) { f.setInteger("vendor.rtc-ext-dec-low-latency.enable", 1); keys.append("exynos=1 ") }
            }
            val gen = ++codecGen
            c.setCallback(object : MediaCodec.Callback() {
                override fun onInputBufferAvailable(mc: MediaCodec, index: Int) { synchronized(lock) { if (gen == codecGen) { freeInputs.add(index); pumpLocked() } } }
                override fun onOutputBufferAvailable(mc: MediaCodec, index: Int, info: MediaCodec.BufferInfo) = onOutput(mc, gen, index, info)
                override fun onError(mc: MediaCodec, e: MediaCodec.CodecException) {
                    Log.w(tag, "codec error " + e.diagnosticInfo)
                    counters.codecErrors.incrementAndGet()
                    // 연달아 실패하면 1초 간격으로만 다시 만듭니다(간격 없이 되풀이하지 않게, 리뷰 m11).
                    val wait = synchronized(lock) { if (gen != codecGen) -1L else { releaseCodecLocked(); if (System.nanoTime() - lastCreateNs < 1_000_000_000L) 1000L else 0L } }
                    if (wait >= 0) codecHandler.postDelayed({
                        synchronized(lock) { if (codec == null) createCodecLocked() }
                        host.requestKey("codec-error")
                    }, wait)
                }
                override fun onOutputFormatChanged(mc: MediaCodec, format: MediaFormat) { Log.i(tag, "output format $format") }
            }, codecHandler)
            c.configure(f, s, null, 0)
            c.setOnFrameRenderedListener({ _, ptsUs, nanoTime ->
                val slot = trace.findByCap(ptsUs, generation)
                if (slot >= 0) { trace.renderedNs[slot] = nanoTime; counters.rendered.incrementAndGet() }
            }, codecHandler)
            c.start()
            codec = c
            lastCreateNs = System.nanoTime()
            waitingKey = true; pendingPrefix = false
            applyBufSetting()
            counters.decoder = c.name; counters.decoderKeys = keys.toString().trim()
            counters.codecCreates.incrementAndGet()
            Log.i(tag, "decoder ${c.name} ${width}x$height keys=[$keys] in=${c.inputFormat}")
        } catch (e: Exception) {
            Log.e(tag, "decoder create failed", e)
            counters.codecErrors.incrementAndGet()
            try { c?.release() } catch (e2: Exception) { }      // 만들다 실패한 코덱을 쥐고 있지 않습니다(리뷰 m11)
            codec = null
            lastCreateNs = System.nanoTime()
        }
    }

    // ---------------------------------------------------------------- input (gvr-video-rx)

    /** 설정 메시지(avcC). 해석하지 못하면 false. 주인이 아니면 조용히 버리고 true. */
    fun onConfig(who: Any, data: ByteArray, offset: Int, length: Int, w: Int, h: Int): Boolean {
        val cfg = AvcConfig.parse(data, offset, length) ?: return false
        synchronized(lock) {
            if (who !== owner) return true
            val sameSize = w == width && h == height
            config = cfg; width = w; height = h
            counters.width = w; counters.height = h
            if (codec == null || !sameSize || settings.recreate) createCodecLocked()
            else { pendingPrefix = true }      // 디코더 유지: 다음 키프레임 앞에 SPS/PPS 를 붙임
        }
        return true
    }

    /** Annex B 로 바꾼 프레임 1장. data 는 호출한 쪽이 다시 쓰므로 여기서 복사합니다. */
    fun onFrame(who: Any, data: ByteArray, offset: Int, length: Int, key: Boolean, ptsUs: Long, slot: Int) {
        var keyReason: String? = null
        synchronized(lock) {
            if (who !== owner || codec == null) { counters.droppedNoCodec.incrementAndGet(); trace.releaseNs[slot] = -1; return }
            if (waitingKey) {
                if (!key) { counters.droppedPreKey.incrementAndGet(); trace.releaseNs[slot] = -1; return }
                waitingKey = false
            }
            val prefix = if (key && pendingPrefix) config?.prefix else null
            if (key) pendingPrefix = false
            val need = length + (prefix?.size ?: 0)
            val f = pool.poll() ?: InFrame(ByteArray(maxOf(65536, need)))
            if (f.data.size < need) f.data = ByteArray(need)
            var p = 0
            if (prefix != null) { System.arraycopy(prefix, 0, f.data, 0, prefix.size); p = prefix.size }
            System.arraycopy(data, offset, f.data, p, length)
            f.len = need; f.ptsUs = ptsUs; f.key = key; f.slot = slot; f.atNs = System.nanoTime()
            pendingInput.add(f)
            // 디코더 입력이 밀림: 가장 오래된 것이 500ms 를 넘기면 전부 버리고 키프레임을 요청
            val oldest = pendingInput.peek()
            if (oldest != null && f.atNs - oldest.atNs > 500_000_000L) {
                while (pendingInput.isNotEmpty()) { val d = pendingInput.poll()!!; trace.releaseNs[d.slot] = -1; counters.droppedBacklog.incrementAndGet(); pool.add(d) }
                waitingKey = true
                keyReason = "backlog"
            } else if (!pumpLocked()) keyReason = "queue-failed"
        }
        keyReason?.let { host.requestKey(it) }
    }

    /** 넣다가 실패하면 false: 그 뒤 프레임은 참조가 깨지므로 키프레임을 기다립니다(리뷰 m11). */
    private fun pumpLocked(): Boolean {
        val c = codec ?: return true
        var ok = true
        while (pendingInput.isNotEmpty() && freeInputs.isNotEmpty()) {
            val f = pendingInput.poll()!!
            val index = freeInputs.poll()!!
            try {
                val buf = c.getInputBuffer(index)!!
                buf.clear(); buf.put(f.data, 0, f.len)
                c.queueInputBuffer(index, 0, f.len, f.ptsUs, if (f.key) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
                trace.queuedNs[f.slot] = System.nanoTime()
                counters.queued.incrementAndGet()
            } catch (e: Exception) { counters.codecErrors.incrementAndGet(); trace.releaseNs[f.slot] = -1; waitingKey = true; ok = false }
            pool.add(f)
        }
        return ok
    }

    // ---------------------------------------------------------------- output (gvr-codec)

    private fun onOutput(mc: MediaCodec, gen: Int, index: Int, info: MediaCodec.BufferInfo) {
        val now = System.nanoTime()
        val slot = trace.findByCap(info.presentationTimeUs, generation)
        counters.decoded.incrementAndGet()
        synchronized(lock) {
            if (gen != codecGen) return
            if (slot < 0) { counters.lostSlot.incrementAndGet(); try { mc.releaseOutputBuffer(index, false) } catch (e: Exception) { }; return }
            trace.decodedNs[slot] = now
            val q = trace.queuedNs[slot]
            if (q > 0) counters.decodeMs += 0.1f * ((now - q) / 1e6f - counters.decodeMs)
            noteSource(trace.capUs[slot])
            applyBufSetting()
            if (settings.pace == "sched") {
                val due = pacer.enqueue(index, slot, gen, trace.capUs[slot] * 1000, now, skip)
                trace.dueNs[slot] = due
                counters.delayMs = pacer.delayNs / 1e6f
            } else { trace.dueNs[slot] = now; release(mc, index, slot, now) }      // 즉시 표시
        }
    }
}
