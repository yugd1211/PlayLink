package kr.co.seeplay.gvr.video

/**
 * 표시 일정(설계 7.4, 코드 리뷰 M1). Android 클래스를 쓰지 않습니다: JVM 시험과 기록 파일 재현(PacerTraceTest)에 그대로 씁니다.
 *
 * 규칙은 웹 페이지(GameViewRemotePage.html 의 schedEnqueue / schedTake)와 같습니다. 비교 대상과 규칙이 같아야
 * 남는 차이를 경로 탓으로 말할 수 있기 때문입니다.
 *   표시 예정(due) = 캡처 시각 + base(최근 10초 중 가장 빨리 준비된 프레임의 "디코딩 완료 − 캡처", 두 시계의 차이 포함) + delay
 *   delay          = 최근 120장 흔들림의 95% 지점 + 2ms, 3~25ms. 처음에는 1프레임(16.7ms)
 *   vsync 마다     = due + phase <= vsync 인 것 중 가장 새 것 1장을 내고 그보다 오래된 것은 버림
 *   phase          = due 가 vsync 사이 한가운데에 오도록 조금씩(0.1) 맞추는 보정. ±3/4칸을 넘으면 한 칸 넘김
 * 페이지와 다른 점은 하나: delay 를 10장마다 한 번에 바꾸지 않고 프레임당 slewNs 이하로만 움직입니다(due 의 계단이 120Hz 에서
 * 칸 넘김이 되는 것을 막음. slewNs = Long.MAX_VALUE 면 페이지와 완전히 같음).
 *
 * 스레드: enqueue 와 onVsync 를 같은 잠금 안에서 부르는 것은 호출한 쪽(Decoder)의 일입니다.
 */
class Pacer {
    class Item { var index = 0; var slot = 0; var gen = 0; var dueNs = 0L }
    fun interface Drop { fun drop(item: Item) }

    companion object {
        const val START_DELAY_NS = 16_700_000L
        const val MIN_DELAY_NS = 3_000_000L
        const val MAX_DELAY_NS = 25_000_000L
        const val MARGIN_NS = 2_000_000L
        const val BASE_WINDOW_NS = 10_000_000_000L
        const val JITTER_SAMPLES = 120
        const val PHASE_GAIN = 0.1
    }

    /** 대기열 상한(페이지 SCHED_MAX_QUEUE = 6). 넘으면 가장 오래된 것부터 버립니다. */
    var maxQueue = 6
    /** 지연의 바닥(ms 지정 buf, 페이지의 ?buffer=N). */
    var floorDelayNs = 0L
    /** "auto" 가 아니면 고정 지연(low = 0, smooth = 50ms). 음수면 자동. */
    var fixedDelayNs = -1L
    /** 흔들림의 몇 % 지점까지 덮을지(페이지 0.95). */
    var percentile = 0.95
    /** delay 가 프레임마다 움직일 수 있는 최대 폭. */
    var slewNs = 250_000L

    var delayNs = START_DELAY_NS; private set
    var phaseNs = 0.0; private set
    var vsyncNs = 16_700_000.0; private set
    var slips = 0L; private set
    var late = 0L; private set          // due 보다 늦게 준비된 프레임 수
    val size: Int get() = count

    // 대기열(고리 버퍼, 할당 없음)
    private val queue = Array(16) { Item() }
    private var headAt = 0
    private var count = 0
    // base: 최근 10초의 최솟값(앞쪽이 최솟값인 단조 덱)
    private val baseAt = LongArray(256); private val baseV = LongArray(256); private var baseHead = 0; private var baseCount = 0
    // 흔들림 표본
    private val jitter = LongArray(JITTER_SAMPLES); private var jitterN = 0L
    private val sortBuf = LongArray(JITTER_SAMPLES)
    private var targetDelayNs = START_DELAY_NS
    private var lastVsyncAt = 0L

    fun reset() {
        count = 0; headAt = 0; baseCount = 0; baseHead = 0; jitterN = 0
        delayNs = maxOf(floorDelayNs, START_DELAY_NS); targetDelayNs = delayNs; phaseNs = 0.0; lastVsyncAt = 0
    }

    /** 대기열만 비웁니다(디코더를 새로 만들 때). 버퍼는 이미 무효이므로 drop 을 부르지 않습니다. */
    fun clearQueue() { count = 0; headAt = 0 }

    /** 대기열에 든 것을 전부 drop 으로 돌려줍니다(표시 방식을 바꿀 때 버퍼를 디코더에 반환). */
    fun drain(drop: Drop) { while (count > 0) drop.drop(pop()) }

    private fun pop(): Item { val it = queue[headAt]; headAt = (headAt + 1) % queue.size; count--; return it }

    /** 디코딩이 끝난 프레임의 due 를 정해 대기열에 넣습니다. 돌려주는 값은 due. */
    fun enqueue(index: Int, slot: Int, gen: Int, capNs: Long, decodedNs: Long, drop: Drop): Long {
        val v = decodedNs - capNs
        while (baseCount > 0 && baseV[(baseHead + baseCount - 1) % baseV.size] >= v) baseCount--
        if (baseCount == baseV.size) { baseHead = (baseHead + 1) % baseV.size; baseCount-- }
        val tail = (baseHead + baseCount) % baseV.size
        baseAt[tail] = decodedNs; baseV[tail] = v; baseCount++
        while (baseCount > 1 && baseAt[baseHead] < decodedNs - BASE_WINDOW_NS) { baseHead = (baseHead + 1) % baseV.size; baseCount-- }
        val base = baseV[baseHead]
        jitter[(jitterN % JITTER_SAMPLES).toInt()] = v - base; jitterN++
        if (jitterN >= 30 && jitterN % 10 == 0L) {
            val n = minOf(jitterN, JITTER_SAMPLES.toLong()).toInt()
            System.arraycopy(jitter, 0, sortBuf, 0, n)
            java.util.Arrays.sort(sortBuf, 0, n)
            val p = sortBuf[minOf(n - 1, (n * percentile).toInt())]
            targetDelayNs = maxOf(floorDelayNs, minOf(MAX_DELAY_NS, maxOf(MIN_DELAY_NS, p + MARGIN_NS)))
        }
        if (fixedDelayNs >= 0) { delayNs = fixedDelayNs; targetDelayNs = fixedDelayNs }
        else if (delayNs < targetDelayNs) delayNs = minOf(targetDelayNs, if (slewNs == Long.MAX_VALUE) targetDelayNs else delayNs + slewNs)
        else if (delayNs > targetDelayNs) delayNs = maxOf(targetDelayNs, if (slewNs == Long.MAX_VALUE) targetDelayNs else delayNs - slewNs)
        val due = capNs + base + delayNs
        if (decodedNs > due) late++
        while (count >= maxOf(1, minOf(maxQueue, queue.size))) drop.drop(pop())
        val it = queue[(headAt + count) % queue.size]
        it.index = index; it.slot = slot; it.gen = gen; it.dueNs = due
        count++
        return due
    }

    /** 이번 vsync 에 낼 프레임. 없으면 null. 주기를 재려고 대기열이 비어 있어도 매번 부릅니다. 돌려준 Item 은 다음 enqueue 전까지만 유효합니다. */
    fun onVsync(frameTimeNs: Long, drop: Drop): Item? {
        if (lastVsyncAt != 0L && frameTimeNs > lastVsyncAt && frameTimeNs - lastVsyncAt < 50_000_000L) vsyncNs += 0.1 * (frameTimeNs - lastVsyncAt - vsyncNs)
        lastVsyncAt = frameTimeNs
        var pick = -1
        for (i in 0 until count) {
            if (queue[(headAt + i) % queue.size].dueNs + phaseNs > frameTimeNs) break
            pick = i
        }
        if (pick < 0) return null
        for (i in 0 until pick) drop.drop(pop())
        val item = pop()
        // r = due 가 이번 칸의 어디에 있었는지(0 ~ 칸 길이). 제 칸에 낸 프레임만 보정에 씁니다(늦은 프레임을 쫓아가면 지연이 쌓임).
        val r = frameTimeNs - (item.dueNs + phaseNs)
        if (r < vsyncNs) {
            phaseNs += PHASE_GAIN * (r - vsyncNs / 2)
            if (phaseNs > vsyncNs * 0.75) { phaseNs -= vsyncNs; slips++ }
            else if (phaseNs < -vsyncNs * 0.75) { phaseNs += vsyncNs; slips++ }
        }
        return item
    }
}
