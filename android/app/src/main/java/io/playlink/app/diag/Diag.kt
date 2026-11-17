package io.playlink.app.diag

import java.io.File
import java.util.Locale
import java.util.concurrent.atomic.AtomicLong

/** 실행 중 바꿀 수 있는 설정(설계 4.2 의 진단 값). 여러 스레드가 읽으므로 전부 volatile. */
class Settings {
    @Volatile var pace = "sched"        // now | sched
    @Volatile var buf = "auto"          // low | auto | smooth | <ms>
    @Volatile var rr = "match"          // none | match | force60 | max. 가변 주사율 폰은 요청이 없으면 패널을 30Hz 로 내립니다(05 문서)
    @Volatile var wifiLock = true
    @Volatile var recreate = false      // 같은 크기의 설정 변경에도 디코더를 새로 만듦
    @Volatile var decoderName = ""      // 비교용으로 직접 고름
    @Volatile var holdLimit = 6         // 표시 대기열 상한(페이지 SCHED_MAX_QUEUE 와 같음)
    @Volatile var target = "pref"       // pref | early: 표시 목표 시각(리뷰 M1 의 4, early 는 폰에서 볼 실험 값)
    @Volatile var percentile = 0.95     // 지연 목표가 덮는 흔들림의 비율

    fun describe() = "pace=$pace buf=$buf rr=$rr wifilock=${if (wifiLock) 1 else 0} recreate=${if (recreate) 1 else 0} hold=$holdLimit target=$target pct=$percentile"
}

/** 원자 카운터. 프레임마다 할당하지 않습니다. 분포(p50 등)는 HUD/내보내기 때 TraceRing 에서 계산합니다. */
class Counters {
    val recvH264 = AtomicLong(); val recvConfig = AtomicLong(); val recvJpeg = AtomicLong(); val recvExt = AtomicLong()
    val bytes = AtomicLong()
    val queued = AtomicLong(); val decoded = AtomicLong(); val released = AtomicLong(); val rendered = AtomicLong()
    val skipped = AtomicLong()          // 디코딩했지만 Surface 로 내보내지 않은 수(표시 일정이 버림)
    val droppedPreKey = AtomicLong(); val droppedNoCodec = AtomicLong(); val droppedBacklog = AtomicLong()
    val corrupt = AtomicLong(); val idGaps = AtomicLong(); val keyRequests = AtomicLong()
    val codecErrors = AtomicLong(); val codecCreates = AtomicLong(); val releaseErrors = AtomicLong()
    val reconnects = AtomicLong(); val rrSwitches = AtomicLong()
    val pointerSent = AtomicLong(); val pointerDropped = AtomicLong()
    @Volatile var rttMs = -1f
    @Volatile var delayMs = 0f          // 지터 버퍼 지연 목표
    @Volatile var sourceIntervalMs = 16.7f
    @Volatile var vsyncMs = 0f          // pacer 가 잰 갱신 주기
    @Volatile var phaseMs = 0f          // pacer 의 위상 보정
    @Volatile var decodeMs = 0f         // 디코딩 시간(지수 평균)
    @Volatile var modeHz = 0f           // Display.getMode().getRefreshRate(): 패널의 물리 모드
    val lostSlot = AtomicLong()         // 기록 칸을 찾지 못한 디코더 출력
    val slowVsync = AtomicLong()        // vsync 간격이 원본 프레임 간격의 1.5배를 넘은 횟수(패널이 원본보다 느림)
    @Volatile var displayHz = 0f        // Display.getRefreshRate()
    @Volatile var decoder = ""
    @Volatile var decoderKeys = ""
    @Volatile var width = 0
    @Volatile var height = 0
    @Volatile var lastEncodeUs = 0L
    @Volatile var lastCaptureToEncodedUs = 0L
}

/**
 * 프레임별/터치별/vsync 원자료 고리 버퍼(원시 배열, 설계 8.1).
 * 프레임 칸은 playlink-video-rx 만 새로 만들고, 다른 스레드는 자기 열에만 씁니다(잠금 없음).
 */
class TraceRing {
    companion object { const val N = 16384; const val MASK = N - 1; const val VN = 65536; const val TN = 8192 }

    val gen = IntArray(N); val frameId = LongArray(N); val flags = IntArray(N); val bytes = IntArray(N)
    val capUs = LongArray(N)            // 서버 시계(μs)
    val seq = IntArray(N)
    val recvNs = LongArray(N); val queuedNs = LongArray(N); val decodedNs = LongArray(N); val dueNs = LongArray(N)
    val releaseNs = LongArray(N)        // Surface 로 넘긴 때. 버렸으면 -1, 아직이면 0
    val targetNs = LongArray(N); val renderedNs = LongArray(N)
    @Volatile var head = 0L             // 지금까지 만든 칸 수

    val vsyncNs = LongArray(VN); @Volatile var vsyncHead = 0L
    val tKind = ByteArray(TN); val tSeq = IntArray(TN); val tEventNs = LongArray(TN); val tSendNs = LongArray(TN)
    val tX = FloatArray(TN); val tY = FloatArray(TN); @Volatile var tHead = 0L
    val rrNs = LongArray(256); val rrHz = FloatArray(256); @Volatile var rrHead = 0

    fun add(generation: Int, id: Long, flag: Int, size: Int, capUsValue: Long, inputSeq: Int, recv: Long): Int {
        val h = head
        val i = (h and MASK.toLong()).toInt()
        gen[i] = generation; frameId[i] = id; flags[i] = flag; bytes[i] = size; capUs[i] = capUsValue; seq[i] = inputSeq
        recvNs[i] = recv; queuedNs[i] = 0; decodedNs[i] = 0; dueNs[i] = 0; releaseNs[i] = 0; targetNs[i] = 0; renderedNs[i] = 0
        head = h + 1
        return i
    }

    /** pts(= capUs)로 최근 칸을 찾습니다. 없으면 -1. */
    fun findByCap(capUsValue: Long, generation: Int): Int {
        val h = head
        var k = 1
        while (k <= 96 && k <= h) {
            val i = ((h - k) and MASK.toLong()).toInt()
            if (capUs[i] == capUsValue && gen[i] == generation) return i
            k++
        }
        return -1
    }

    fun vsync(ns: Long) { val h = vsyncHead; vsyncNs[(h and (VN - 1).toLong()).toInt()] = ns; vsyncHead = h + 1 }

    fun touch(kind: Char, s: Int, eventNs: Long, sendNs: Long, x: Float, y: Float) {
        val h = tHead
        val i = (h and (TN - 1).toLong()).toInt()
        tKind[i] = kind.code.toByte(); tSeq[i] = s; tEventNs[i] = eventNs; tSendNs[i] = sendNs; tX[i] = x; tY[i] = y
        tHead = h + 1
    }

    fun refreshRate(ns: Long, hz: Float) { val i = rrHead; rrNs[i and 255] = ns; rrHz[i and 255] = hz; rrHead = i + 1 }
}

/** HUD 용 스냅숏: 최근 windowNs 동안의 TraceRing 에서 계산(UI 스레드, 0.5초마다). 견주는 값은 releaseNs 기준. */
class Snapshot(trace: TraceRing, windowNs: Long, now: Long) {
    var n = 0; var shown = 0; var skippedN = 0
    var arriveP50 = 0f; var arriveP99 = 0f; var arriveMax = 0f; var stalls = 0; var bursts = 0
    var decP50 = 0f; var decP95 = 0f; var decMax = 0f
    var holds = 0; var jumps = 0; var longestMs = 0f; var uneven = 0f
    var held = 0; var seconds = windowNs / 1e9f
    var touchN = 0; var touchMs = 0f; var capToShownMs = 0f; var renderedN = 0

    init {
        val head = trace.head
        val count = minOf(head, 1200L).toInt()
        val idx = IntArray(count)
        var m = 0
        for (k in count downTo 1) {
            val i = ((head - k) and TraceRing.MASK.toLong()).toInt()
            if (now - trace.recvNs[i] <= windowNs) idx[m++] = i
        }
        n = m
        if (m > 2) {
            val gaps = FloatArray(m - 1)
            for (j in 1 until m) gaps[j - 1] = (trace.recvNs[idx[j]] - trace.recvNs[idx[j - 1]]) / 1e6f
            gaps.sort()
            arriveP50 = gaps[gaps.size / 2]; arriveP99 = gaps[minOf(gaps.size - 1, (gaps.size * 0.99f).toInt())]; arriveMax = gaps[gaps.size - 1]
            stalls = gaps.count { it >= 40f }; bursts = gaps.count { it < 3f }
            val dec = FloatArray(m); var dn = 0
            var prev = -1
            val shownGaps = ArrayList<Float>()
            var minLag = Long.MAX_VALUE
            for (j in 0 until m) {
                val i = idx[j]
                if (trace.decodedNs[i] > 0 && trace.queuedNs[i] > 0) dec[dn++] = (trace.decodedNs[i] - trace.queuedNs[i]) / 1e6f
                if (trace.queuedNs[i] > 0 && trace.decodedNs[i] == 0L) held++
                if (trace.renderedNs[i] > 0) renderedN++
                val rel = trace.releaseNs[i]
                if (rel > 0) {
                    shown++
                    val lag = rel - trace.capUs[i] * 1000
                    if (lag < minLag) minLag = lag
                    if (prev >= 0 && trace.gen[prev] == trace.gen[i]) {
                        val gap = (rel - trace.releaseNs[prev]) / 1e6f
                        val src = (trace.capUs[i] - trace.capUs[prev]) / 1e3f
                        if (src < 25f) {
                            val d = gap - src
                            if (d > 8f) holds++ else if (d < -8f) jumps++
                            if (gap > longestMs) longestMs = gap
                            shownGaps.add(gap)
                        }
                    }
                    prev = i
                } else if (rel < 0) skippedN++
            }
            if (dn > 0) { val d = dec.copyOf(dn); d.sort(); decP50 = d[dn / 2]; decP95 = d[minOf(dn - 1, (dn * 0.95f).toInt())]; decMax = d[dn - 1] }
            if (shownGaps.isNotEmpty()) uneven = 100f * shownGaps.count { it < 9f || it > 25f } / shownGaps.size
            if (shown > 0 && minLag != Long.MAX_VALUE) {
                var sum = 0.0
                for (j in 0 until m) { val i = idx[j]; if (trace.releaseNs[i] > 0) sum += (trace.releaseNs[i] - trace.capUs[i] * 1000 - minLag) / 1e6 }
                capToShownMs = (sum / shown).toFloat()
            }
            // 터치 → 표시(보냄 → 그 입력 번호를 실은 첫 프레임을 넘긴 때). down/up 만.
            val th = trace.tHead
            var tk = 1
            var tsum = 0f
            while (tk <= 64 && tk <= th) {
                val ti = ((th - tk) and (TraceRing.TN - 1).toLong()).toInt()
                tk++
                val kind = trace.tKind[ti].toInt().toChar()
                if (kind != 'd' && kind != 'u') continue
                if (now - trace.tSendNs[ti] > windowNs * 3) break
                for (j in 0 until m) {
                    val i = idx[j]
                    if (trace.releaseNs[i] > trace.tSendNs[ti] && trace.seq[i] >= trace.tSeq[ti]) { tsum += (trace.releaseNs[i] - trace.tSendNs[ti]) / 1e6f; touchN++; break }
                }
            }
            if (touchN > 0) touchMs = tsum / touchN
        }
    }
}

/**
 * 기록 구간을 JSON 으로 저장합니다(설계 8.5). 측정 스크립트 record.js 와 같은 이름의 배열을 두어
 * analyze.js 가 읽을 수 있게 하고(frames 의 drawnAt = releaseNs: 페이지의 그리기 호출 시각과 같은 자리, 리뷰 B1),
 * 앱에만 있는 값은 app 아래에 둡니다. 키는 적지 않습니다.
 */
object Exporter {
    fun write(file: File, trace: TraceRing, counters: Counters, startNs: Long, endNs: Long, startHead: Long, info: Map<String, Any?>) {
        file.parentFile?.mkdirs()
        file.bufferedWriter().use { w ->
            fun ms(ns: Long) = if (ns <= 0L) (if (ns < 0) "-1" else "0") else String.format(Locale.ROOT, "%.3f", (ns - startNs) / 1e6)
            val head = trace.head
            val from = maxOf(startHead, head - TraceRing.N + 64)
            w.write("{\"info\":{")
            w.write(info.entries.joinToString(",") { (k, v) -> "\"$k\":" + (if (v is Number || v is Boolean) v.toString() else "\"" + v.toString().replace("\\", "\\\\").replace("\"", "'") + "\"") })
            w.write("},\n\"frames\":[")
            var first = true
            var h = from
            while (h < head) {
                val i = (h and TraceRing.MASK.toLong()).toInt(); h++
                if (trace.releaseNs[i] <= 0 || trace.recvNs[i] < startNs || trace.recvNs[i] > endNs) continue
                if (!first) w.write(","); first = false
                val dec = if (trace.decodedNs[i] > 0 && trace.queuedNs[i] > 0) (trace.decodedNs[i] - trace.queuedNs[i]) / 1e6 else 0.0
                w.write(String.format(Locale.ROOT, "[%d,%.1f,%s,%s,%.2f,%d,%s]", trace.frameId[i], trace.capUs[i] / 1000.0, ms(trace.recvNs[i]), ms(trace.releaseNs[i]), dec, trace.seq[i], ms(trace.dueNs[i])))
            }
            w.write("],\n\"arrivals\":[")
            first = true; h = from
            while (h < head) {
                val i = (h and TraceRing.MASK.toLong()).toInt(); h++
                if (trace.recvNs[i] < startNs || trace.recvNs[i] > endNs) continue
                if (!first) w.write(","); first = false
                w.write("[" + trace.frameId[i] + "," + ms(trace.recvNs[i]) + "]")
            }
            w.write("],\n\"raf\":[")
            first = true
            val vh = trace.vsyncHead
            var v = maxOf(0L, vh - TraceRing.VN + 16)
            while (v < vh) {
                val t = trace.vsyncNs[(v and (TraceRing.VN - 1).toLong()).toInt()]; v++
                if (t < startNs || t > endNs) continue
                if (!first) w.write(","); first = false
                w.write(ms(t))
            }
            w.write("],\n\"ptr\":[")
            first = true
            val th = trace.tHead
            var t = maxOf(0L, th - TraceRing.TN + 16)
            while (t < th) {
                val i = (t and (TraceRing.TN - 1).toLong()).toInt(); t++
                if (trace.tSendNs[i] < startNs || trace.tSendNs[i] > endNs) continue
                if (!first) w.write(","); first = false
                w.write(String.format(Locale.ROOT, "[\"%c\",%s,%s,%.4f,%.4f,%d]", trace.tKind[i].toInt().toChar(), ms(trace.tSendNs[i]), ms(trace.tEventNs[i]), trace.tX[i], trace.tY[i], trace.tSeq[i]))
            }
            w.write("],\n\"t2f\":[],\"unity\":[],\"encodes\":[],\"samples\":[],\n\"app\":{\"frames\":[")
            // [gen,id,flags,bytes,capMs,seq,recv,queued,decoded,due,release,target,rendered]
            first = true; h = from
            while (h < head) {
                val i = (h and TraceRing.MASK.toLong()).toInt(); h++
                if (trace.recvNs[i] < startNs || trace.recvNs[i] > endNs) continue
                if (!first) w.write(","); first = false
                w.write(String.format(Locale.ROOT, "[%d,%d,%d,%d,%.1f,%d,%s,%s,%s,%s,%s,%s,%s]\n", trace.gen[i], trace.frameId[i], trace.flags[i], trace.bytes[i], trace.capUs[i] / 1000.0, trace.seq[i],
                    ms(trace.recvNs[i]), ms(trace.queuedNs[i]), ms(trace.decodedNs[i]), ms(trace.dueNs[i]), ms(trace.releaseNs[i]), ms(trace.targetNs[i]), ms(trace.renderedNs[i])))
            }
            w.write("],\"rr\":[")
            val rh = trace.rrHead
            for (k in maxOf(0, rh - 256) until rh) { if (k > maxOf(0, rh - 256)) w.write(","); w.write("[" + ms(trace.rrNs[k and 255]) + "," + trace.rrHz[k and 255] + "]") }
            w.write("],\"counters\":{")
            val c = counters
            w.write(listOf("recvH264" to c.recvH264.get(), "recvConfig" to c.recvConfig.get(), "recvJpeg" to c.recvJpeg.get(), "decoded" to c.decoded.get(), "released" to c.released.get(),
                "rendered" to c.rendered.get(), "skipped" to c.skipped.get(), "droppedPreKey" to c.droppedPreKey.get(), "droppedBacklog" to c.droppedBacklog.get(), "corrupt" to c.corrupt.get(),
                "idGaps" to c.idGaps.get(), "keyRequests" to c.keyRequests.get(), "codecErrors" to c.codecErrors.get(), "codecCreates" to c.codecCreates.get(), "releaseErrors" to c.releaseErrors.get(),
                "reconnects" to c.reconnects.get(), "rrSwitches" to c.rrSwitches.get(), "pointerSent" to c.pointerSent.get(), "pointerDropped" to c.pointerDropped.get())
                .joinToString(",") { "\"${it.first}\":${it.second}" })
            w.write(String.format(Locale.ROOT, ",\"rttMs\":%.1f,\"delayMs\":%.1f,\"vsyncMs\":%.2f,\"displayHz\":%.1f", c.rttMs, c.delayMs, c.vsyncMs, c.displayHz))
            w.write("},\"durationMs\":" + ms(endNs) + "}}\n")
        }
    }
}
