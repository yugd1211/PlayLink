package kr.co.seeplay.gvr

import kr.co.seeplay.gvr.video.Pacer
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.util.Locale
import java.util.Random

/**
 * 표시 일정(video.Pacer)의 시험(코드 리뷰 M1, 7.5).
 *  - 합성 입력으로 규칙의 성질을 봅니다.
 *  - 폰에서 받은 기록(tools/rec 의 json: 프레임별 캡처 · 디코딩 완료 시각, vsync 시각)을 그대로 다시 돌려 "기록 당시의 실제 표시"와
 *    "같은 도착 · 같은 vsync 에 지금 규칙을 적용한 결과"를 견줍니다. 표는 build/reports/pacer-traces.txt 에 씁니다.
 */
class PacerTest {
    private val MS = 1_000_000L

    private class Frame(val id: Long, val capMs: Double, val decodedMs: Double, val recRelease: Double, val recDue: Double) { var shownMs = -1.0; var dueMs = 0.0 }
    private class Result(val holds: Int, val skipped: Int, val shown: Int, val movingSec: Double, val waitP50: Double, val waitP95: Double, val lateP50: Double, val longest: Double)

    /** tools/app-report.js 의 정의 그대로: hold = (표시 간격 − 캡처 간격) > 8ms, 원본이 움직이던 구간(캡처 간격 < 25ms)만. */
    private fun measure(frames: List<Frame>, shownAt: (Frame) -> Double, dueAt: (Frame) -> Double): Result {
        val shown = frames.filter { shownAt(it) > 0 }
        var holds = 0; var moving = 0.0; var longest = 0.0
        for (i in 1 until shown.size) {
            val src = shown[i].capMs - shown[i - 1].capMs
            val gap = shownAt(shown[i]) - shownAt(shown[i - 1])
            if (src < 25) { moving += src; if (gap - src > 8) holds++; if (gap > longest) longest = gap }
        }
        val skipped = if (shown.isEmpty()) 0 else frames.count { it.id > shown.first().id && it.id < shown.last().id && shownAt(it) <= 0 }
        val wait = shown.map { shownAt(it) - it.decodedMs }.sorted()
        val late = shown.map { shownAt(it) - dueAt(it) }.sorted()
        fun q(a: List<Double>, p: Double) = if (a.isEmpty()) Double.NaN else a[minOf(a.size - 1, (a.size * p).toInt())]
        return Result(holds, skipped, shown.size, moving / 1000, q(wait, 0.5), q(wait, 0.95), q(late, 0.5), longest)
    }

    /** 프레임(디코딩 완료 시각 순)과 vsync 목록을 시간순으로 Pacer 에 넣습니다. 표시 시각 = 그 vsync 의 시각. */
    private fun simulate(frames: List<Frame>, vsyncs: DoubleArray, pacer: Pacer = Pacer()): Pacer {
        pacer.reset()
        for (f in frames) { f.shownMs = -1.0; f.dueMs = 0.0 }
        val order = frames.filter { it.decodedMs > 0 }.sortedBy { it.decodedMs }
        val drop = Pacer.Drop { }
        var k = 0
        for (v in vsyncs) {
            // 기록의 vsync 시각은 콜백이 받은 frameTime 이고 콜백은 그보다 조금 뒤에 돕니다: 0.8ms 안에 끝난 디코딩은 이 vsync 가 볼 수 있습니다(리뷰 부록 A 와 같은 가정)
            while (k < order.size && order[k].decodedMs <= v + 0.8) {
                val f = order[k]
                f.dueMs = pacer.enqueue(k, k, 0, (f.capMs * MS).toLong(), (f.decodedMs * MS).toLong(), drop) / 1e6
                k++
            }
            val item = pacer.onVsync((v * MS).toLong(), drop) ?: continue
            order[item.index].shownMs = v
        }
        return pacer
    }

    // ------------------------------------------------------------------ 합성 입력

    private fun synthetic(n: Int, jitterMs: Double, seed: Long, capOffsetMs: Double = 123456.0): List<Frame> {
        val r = Random(seed)
        return (0 until n).map { i -> val cap = i * 1000.0 / 60; Frame(i.toLong(), cap + capOffsetMs, 50 + cap + 12 + r.nextDouble() * jitterMs, 0.0, 0.0) }
    }
    private fun vsyncs(n: Int, periodMs: Double, startMs: Double = 3.0) = DoubleArray(n) { startMs + it * periodMs }

    @Test fun steadySourceIsShownOnePerVsyncWithoutDrops() {
        for (offset in listOf(0.0, 4.0, 8.0, 12.0, 16.0)) {              // vsync 와 캡처의 위상이 어디든
            val frames = synthetic(1200, 3.0, 1)
            val pacer = simulate(frames, vsyncs(1300, 1000.0 / 60, 50 + offset))
            val late = frames.drop(300)
            val r = measure(late, { it.shownMs }, { it.dueMs })
            assertEquals("offset $offset holds", 0, r.holds)
            assertEquals("offset $offset skipped", 0, r.skipped)
            // 위상 보정은 ±3/4칸 안에서 멈추므로(페이지와 같은 규칙) 디코딩 완료 → 표시는 "지연 − 흔들림 + (−1/4 ~ +5/4)칸" 안에 있습니다
            assertTrue("offset $offset wait " + r.waitP50, r.waitP50 > 0 && r.waitP50 < 5 + 1.25 * 16.7)
            assertTrue("phase " + pacer.phaseNs, Math.abs(pacer.phaseNs) <= 0.75 * pacer.vsyncNs)
            assertTrue(pacer.delayNs in Pacer.MIN_DELAY_NS..Pacer.MAX_DELAY_NS)
        }
    }

    @Test fun delayFollowsJitterWithinBoundsAndMovesSlowly() {
        val quiet = simulate(synthetic(900, 0.5, 2), vsyncs(1000, 1000.0 / 60, 50.0))
        assertTrue("quiet link delay " + quiet.delayNs, quiet.delayNs <= 3_500_000L)
        val noisy = simulate(synthetic(900, 12.0, 3), vsyncs(1000, 1000.0 / 60, 50.0))
        assertTrue("noisy link delay " + noisy.delayNs, noisy.delayNs in 10_000_000L..16_000_000L)
        val wild = simulate(synthetic(900, 80.0, 4), vsyncs(1000, 1000.0 / 60, 50.0))
        assertEquals(Pacer.MAX_DELAY_NS, wild.delayNs)
        // 프레임마다 움직이는 폭이 slew 이하
        val p = Pacer(); p.reset()
        var last = p.delayNs
        val drop = Pacer.Drop { }
        for (f in synthetic(600, 12.0, 5)) {
            p.enqueue(0, 0, 0, (f.capMs * MS).toLong(), (f.decodedMs * MS).toLong(), drop)
            assertTrue(Math.abs(p.delayNs - last) <= p.slewNs)
            last = p.delayNs
            p.onVsync((f.decodedMs * MS).toLong() + 30 * MS, drop)
        }
    }

    @Test fun burstShowsNewestAndDropsOlder() {
        val p = Pacer(); p.reset()
        val dropped = ArrayList<Int>()
        val drop = Pacer.Drop { dropped.add(it.index) }
        // 4장이 한꺼번에 도착(40ms 멈춤 뒤)
        for (i in 0 until 4) p.enqueue(i, i, 0, (1000 + i * 16.7).toLong() * MS, 1100 * MS, drop)
        val item = p.onVsync(1200 * MS, drop)
        assertEquals(3, item!!.index)
        assertEquals(listOf(0, 1, 2), dropped)
        assertNull(p.onVsync(1217 * MS, drop))
        // 대기열 상한: 넘으면 가장 오래된 것부터
        p.maxQueue = 3; dropped.clear()
        for (i in 10 until 16) p.enqueue(i, i, 0, (2000 + i * 16.7).toLong() * MS, 2000 * MS + i, drop)
        assertEquals(listOf(10, 11, 12), dropped); assertEquals(3, p.size)
        p.drain(drop)
        assertEquals(listOf(10, 11, 12, 13, 14, 15), dropped); assertEquals(0, p.size)
    }

    @Test fun fixedAndFloorDelay() {
        val low = Pacer().apply { fixedDelayNs = 0 }
        simulate(synthetic(300, 6.0, 6), vsyncs(400, 1000.0 / 60, 50.0), low)
        assertEquals(0L, low.delayNs)
        val floor = Pacer().apply { floorDelayNs = 20 * MS }
        simulate(synthetic(300, 1.0, 7), vsyncs(400, 1000.0 / 60, 50.0), floor)
        assertEquals(20 * MS, floor.delayNs)
    }

    @Test fun fasterAndSlowerPanels() {
        // 120Hz 패널에 60fps: 건너뜀 없이 두 칸에 한 장
        val f120 = synthetic(900, 3.0, 8)
        simulate(f120, vsyncs(2000, 1000.0 / 120, 50.0))
        measure(f120.drop(300), { it.shownMs }, { it.dueMs }).let { assertEquals(0, it.holds); assertEquals(0, it.skipped) }
        // 30Hz 패널에 60fps: 절반을 버리되 가장 새 것을 냅니다(쌓이지 않음)
        val f30 = synthetic(900, 3.0, 9)
        val p30 = simulate(f30, vsyncs(500, 1000.0 / 30, 50.0))
        val shown = f30.filter { it.shownMs > 0 }
        assertTrue(shown.size in 400..460)
        assertTrue(p30.size <= 2)
        assertTrue(shown.takeLast(100).all { it.shownMs - it.decodedMs < 40 })
    }

    // ------------------------------------------------------------------ 폰 기록 재현

    private fun load(file: File): Triple<JSONObject, List<Frame>, DoubleArray> {
        val o = JSONObject(file.readText())
        val arr = o.getJSONObject("app").getJSONArray("frames")
        val all = (0 until arr.length()).map { arr.getJSONArray(it) }
        val gen = all.last().getInt(0)
        // [gen,id,flags,bytes,capMs,seq,recv,queued,decoded,due,release,target,rendered]
        val frames = all.filter { it.getInt(0) == gen && it.getDouble(8) > 0 }.map { Frame(it.getLong(1), it.getDouble(4), it.getDouble(8), it.getDouble(10), it.getDouble(9)) }
        val raf = o.getJSONArray("raf")
        return Triple(o.getJSONObject("info"), frames, DoubleArray(raf.length()) { raf.getDouble(it) })
    }

    @Test fun recordedPhoneTraces() {
        val dir = File("../../tools/rec")
        val files = dir.listFiles { f -> f.name.endsWith(".json") }?.sortedBy { it.name } ?: emptyList()
        assumeTrue("no recordings in " + dir.absolutePath, files.isNotEmpty())
        val sb = StringBuilder()
        sb.append("before = 기록 당시 앱이 실제로 한 표시(폰), after = 같은 디코딩 완료 시각과 같은 vsync 시각에 지금의 video.Pacer 를 적용(JVM 재현)\n")
        sb.append("wait = 디코딩 완료 → 표시(ms), hold = 표시 간격이 캡처 간격보다 8ms 넘게 긴 횟수(app-report.js 정의), skip = 디코딩하고 표시하지 않은 프레임\n")
        sb.append("after 의 표시 시각은 vsync 콜백의 frameTime 입니다(before 는 그보다 0.3~6ms 뒤인 releaseOutputBuffer 호출 시각). 앞 0.7초는 버림.\n\n")
        sb.append(String.format(Locale.ROOT, "%-34s %-22s %6s | %5s %5s %7s %7s %7s | %5s %5s %7s %7s %7s | %5s %6s\n", "file", "settings", "panel",
            "hold", "skip", "wait50", "wait95", "long", "hold", "skip", "wait50", "wait95", "long", "slips", "delay"))
        var beforeSkip = 0; var afterSkip = 0; var beforeHold = 0; var afterHold = 0
        val variants = listOf("page(slew=inf)" to Long.MAX_VALUE, "slew 1ms" to 1_000_000L, "slew 0.25ms" to 250_000L, "slew 0.05ms" to 50_000L)
        val vHold = IntArray(variants.size); val vSkip = IntArray(variants.size); val vWait = DoubleArray(variants.size); var vN = 0
        var v3Before = 0.0; var v3After = 0.0; var v3N = 0
        for (file in files) {
            val (info, frames, raf) = load(file)
            if (frames.size < 100 || raf.size < 100) continue
            val settings = info.optString("settings")
            val sched = settings.contains("pace=sched")
            val pacer = simulate(frames, raf)
            val t0 = frames.first().decodedMs + 700
            val kept = frames.filter { it.decodedMs >= t0 }
            val b = measure(kept, { it.recRelease }, { it.recDue })
            val a = measure(kept, { it.shownMs }, { it.dueMs })
            val hz = 1000.0 / info.optDouble("vsyncMs", 16.7)
            sb.append(String.format(Locale.ROOT, "%-34s %-22s %4.0fHz | %5d %5d %7.1f %7.1f %7.1f | %5d %5d %7.1f %7.1f %7.1f | %5d %6.1f\n",
                file.name.removePrefix("rec-20261114-").removeSuffix(".json"), settings.substringBefore(" wifilock").removePrefix("pace="), hz,
                b.holds, b.skipped, b.waitP50, b.waitP95, b.longest, a.holds, a.skipped, a.waitP50, a.waitP95, a.longest, pacer.slips, pacer.delayNs / 1e6))
            if (sched && hz > 50) {
                vN++
                variants.forEachIndexed { i, (_, slew) ->
                    simulate(frames, raf, Pacer().apply { slewNs = slew })
                    val m = measure(frames.filter { it.decodedMs >= t0 }, { it.shownMs }, { it.dueMs })
                    vHold[i] += m.holds; vSkip[i] += m.skipped; vWait[i] += m.waitP50
                }
                simulate(frames, raf)
                beforeSkip += b.skipped; afterSkip += a.skipped; beforeHold += b.holds; afterHold += a.holds
                if (file.name.contains("w2_")) { v3Before += b.waitP50; v3After += a.waitP50; v3N++ }
            }
        }
        sb.append(String.format(Locale.ROOT, "\n합계(pace=sched, 패널 60Hz 급 기록만): hold %d → %d, skip %d → %d\n", beforeHold, afterHold, beforeSkip, afterSkip))
        if (v3N > 0) sb.append(String.format(Locale.ROOT, "버전 3 으로 받은 기록(w2_*) %d개의 wait 중앙값 평균: %.1f ms → %.1f ms\n", v3N, v3Before / v3N, v3After / v3N))
        sb.append("\ndelay 가 프레임마다 움직일 수 있는 폭(slew)별 합계(같은 기록들):\n")
        variants.forEachIndexed { i, (name, _) -> sb.append(String.format(Locale.ROOT, "  %-16s hold %4d  skip %4d  wait50 평균 %.1f ms\n", name, vHold[i], vSkip[i], vWait[i] / maxOf(1, vN))) }
        File("build/reports").mkdirs()
        File("build/reports/pacer-traces.txt").writeText(sb.toString())
        println(sb)
        // 되돌아가지 않게 하는 선: 지금 규칙이 기록 당시보다 프레임을 더 버리거나 더 멈추면 실패
        assertTrue("holds $beforeHold -> $afterHold", afterHold <= beforeHold)
        if (v3N > 0) assertTrue("v3 wait " + v3Before / v3N + " -> " + v3After / v3N, v3After < v3Before)
    }
}
