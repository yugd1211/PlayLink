package io.playlink.app.session

import android.os.Process
import android.util.Log
import io.playlink.app.diag.Counters
import io.playlink.app.diag.TraceRing
import io.playlink.app.net.WsClient
import io.playlink.app.proto.Caps
import io.playlink.app.video.AvcConfig
import io.playlink.app.video.Decoder
import io.playlink.app.video.FrameHeader
import org.json.JSONObject
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.util.Locale
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

enum class State { WAITING, PLAYING, NOCODEC, STANDBY, LOBBY, RECONNECT, ENDED }

/** 받은 텍스트를 t 값으로 처리기에 나눠 줍니다. 모르는 t 는 버립니다(확장 규칙 R2). 메시지를 늘릴 자리. */
class TextRouter {
    private val handlers = HashMap<String, (JSONObject) -> Unit>()
    fun on(t: String, handler: (JSONObject) -> Unit) { handlers[t] = handler }
    fun dispatch(text: String) {
        val o = try { JSONObject(text) } catch (e: Exception) { return }
        handlers[o.optString("t")]?.invoke(o)
    }
}

/**
 * PC 하나의 화면을 보는 동안. 폰은 걸지 않습니다: PC 가 건 영상/입력 채널을 Receiver 가 핸드셰이크까지 끝내 attach 로 넘겨줍니다.
 * 채널 안의 내용은 예전과 같은 WebSocket 프레임이고 폰이 클라이언트 역할입니다(PROTOCOL.md 4장).
 * 스레드: 채널마다 읽기 스레드(playlink-video-rx, playlink-input-rx), 입력 쓰기(playlink-input-tx), 지켜보기(playlink-conn).
 * 소켓은 이 세션이 쥡니다. 채널이 끊기면 PC 가 다시 걸기를 기다리고(같은 채널이 다시 오면 바꿔 끼움), 두 채널이 6초 넘게 갖춰지지 않으면 끝냅니다.
 *
 * 수명 경계(코드 리뷰 M2): stop() / 끝난 뒤에는 이 세션이 화면과 디코더를 건드리지 않습니다.
 * Ui 콜백은 세션 자신을 넘기고(받는 쪽이 지금 세션인지 확인), 디코더는 주인이 아닌 세션의 호출을 버립니다.
 */
class Session(
    override val pcId: String,
    val pcName: String,
    private val decoder: Decoder,
    private val trace: TraceRing,
    private val counters: Counters,
    private val genSource: AtomicInteger,
    private val ui: Ui,
) : Watch {
    interface Ui {
        fun onState(session: Session, state: State, message: String)
        fun onVideoSize(session: Session, width: Int, height: Int)
        fun onStale(session: Session, message: String?)
        /** 입력 채널이 새로 맺어짐: 입력 번호를 1부터 다시 시작하고, 앞으로 보내는 포인터 메시지에 이 세대(epoch)를 붙입니다. */
        fun onConnected(session: Session, epoch: Int)
        /** 세션이 스스로 끝남(PC 가 끊음, 다시 걸어 오지 않음, 버전이 다름). stop() 으로 끝낸 때는 부르지 않습니다. */
        fun onEnded(session: Session, message: String)
    }

    class Info {
        @Volatile var route = ""; @Volatile var host = ""; @Volatile var seat = ""
        @Volatile var caps = Caps(""); @Volatile var loopback = false
    }

    companion object {
        private const val TAG = "PlayLink"
        private val ACK = byteArrayOf('a'.code.toByte())
        private val CAPS = "{\"t\":\"caps\",\"h264\":1}".toByteArray(Charsets.US_ASCII)      // 서버 판정이 문자열 검사라 글자 그대로
        private val KEY = "{\"t\":\"key\"}".toByteArray(Charsets.US_ASCII)
        private val TAKE = "{\"t\":\"take\"}".toByteArray(Charsets.US_ASCII)
        private const val GRACE_NS = 6_000_000_000L
    }

    val info = Info()
    @Volatile var state = State.WAITING; private set
    private val lock = Any()
    private val over = AtomicBoolean(false)
    @Volatile private var stopped = false
    @Volatile private var video: WsClient? = null
    @Volatile private var input: WsClient? = null
    private var inputTx: Thread? = null
    @Volatile private var lastKeyRequestNs = 0L
    @Volatile private var lastPongNs = 0L
    @Volatile private var seatActive = true
    @Volatile private var inputEpoch = 0
    private val inputQueue = LinkedBlockingQueue<ByteArray>()
    private var lastSkipped = 0L
    private val monitor = Thread({ watch() }, "playlink-conn").apply { start() }

    val canTake: Boolean get() = info.caps.has("take")

    // ------------------------------------------------------------------ 채널 받기 (playlink-hs)

    override fun attach(channel: String, ws: WsClient, remote: InetAddress?, caps: Caps) {
        val old: WsClient?
        synchronized(lock) {
            if (stopped) { ws.abort(); return }
            info.caps = caps
            info.loopback = remote?.isLoopbackAddress == true
            info.route = if (info.loopback) "USB(adb)" else "Wi-Fi"
            info.host = remote?.hostAddress ?: ""
            if (channel == "video") {
                old = video
                val generation = genSource.incrementAndGet()
                if (!decoder.reset(this, generation)) { ws.abort(); return }       // 주인이 아니면(새 세션이 이미 있음) 물러납니다
                video = ws
                seatActive = true
                setState(State.WAITING)
                ws.setTimeout(2000)                    // hello 를 기다리는 시간(설계 5.1). hello 를 받으면 0 으로
                Thread({ videoLoop(ws, generation) }, "playlink-video-rx").start()
            } else {
                old = input
                inputTx?.interrupt()
                input = ws
                inputQueue.clear()
                lastPongNs = System.nanoTime()
                inputEpoch = genSource.incrementAndGet()
                ui.onConnected(this, inputEpoch)
                Thread({ inputReadLoop(ws) }, "playlink-input-rx").start()
                inputTx = Thread({ inputWriteLoop(ws) }, "playlink-input-tx").apply { start() }
            }
        }
        old?.let { closeLater(it) }                    // 같은 PC 가 같은 채널을 다시 걸었음: 이전 연결을 닫습니다
    }

    private fun closeLater(vararg list: WsClient?) { Thread({ for (ws in list) ws?.close() }, "playlink-close").start() }

    /** UI 스레드에서 불러도 됩니다: 소켓을 닫기만 하고 기다리지 않습니다. */
    override fun stop() { if (over.compareAndSet(false, true)) shutdown() }

    private fun end(message: String) {
        if (!over.compareAndSet(false, true)) return
        shutdown()
        state = State.ENDED
        ui.onEnded(this, message)
    }

    private fun shutdown() {
        val v: WsClient?
        val i: WsClient?
        synchronized(lock) {
            stopped = true
            v = video; i = input; video = null; input = null
            inputTx?.interrupt()
        }
        monitor.interrupt()
        closeLater(v, i)
    }

    private fun setState(s: State, message: String = "") {
        if (stopped) return
        state = s
        if (s == State.WAITING || s == State.NOCODEC) decoder.expectFirstFrame(this)     // 이 상태를 벗어나는 길은 "다음 프레임이 화면으로 감" 하나(코드 리뷰 B1)
        ui.onState(this, s, message)
    }

    /** 두 채널이 갖춰지지 않은 채 6초가 지나면 끝냅니다(PC 가 한 채널만 걸었거나, 끊긴 뒤 다시 걸지 않음). */
    private fun watch() {
        var missingSinceNs = 0L
        while (!stopped) {
            try { Thread.sleep(250) } catch (e: InterruptedException) { }
            if (stopped) return
            val now = System.nanoTime()
            if (video != null && input != null) missingSinceNs = 0L
            else if (missingSinceNs == 0L) missingSinceNs = now
            else if (now - missingSinceNs > GRACE_NS) { end("PC 와의 연결이 끊겼습니다."); return }
        }
    }

    /** 채널 하나가 끊김. 이미 새 연결로 바뀐 채널이면 아무것도 하지 않습니다. */
    private fun lost(isVideo: Boolean, ws: WsClient, reason: String, closeCode: Int) {
        synchronized(lock) {
            if (stopped) return
            if (isVideo) { if (video !== ws) return; video = null } else { if (input !== ws) return; input = null; inputTx?.interrupt() }
        }
        ws.abort()
        Log.i(TAG, (if (isVideo) "video" else "input") + " lost: " + reason)
        if (closeCode == 1000 || closeCode == 1001) { end("PC 에서 연결을 끝냈습니다."); return }
        counters.reconnects.incrementAndGet()
        if (isVideo) setState(State.RECONNECT, "PC 가 다시 연결하기를 기다리는 중…\n($reason)")
    }

    // ------------------------------------------------------------------ 영상 채널 (playlink-video-rx)

    /** 연결 하나의 읽기 상태. 채널을 바꿔 끼우면 새로 만들어집니다. */
    private inner class VideoRx(val ws: WsClient, val generation: Int) {
        var helloSeen = false
        var jpegSinceNs = 0L
        var expectId = -1L
        val router = TextRouter()

        init {
            router.on("hello") { o ->
                helloSeen = true
                if (o.optInt("v") != FrameHeader.PROTOCOL) end("버전이 맞지 않습니다(PC 프로토콜 " + o.optInt("v") + "). 앱과 Unity 도구를 같은 버전으로 맞추세요.")
                else { ws.setTimeout(0); ws.sendText(CAPS) }
            }
            router.on("s") { o -> ui.onStale(this@Session, if (o.optBoolean("stale")) o.optString("msg", "화면이 멈춰 있습니다") else null) }
            router.on("seat") { o ->
                when (o.optString("s")) {
                    "active" -> { seatActive = true; info.seat = "active"; if (state == State.STANDBY || state == State.LOBBY) setState(State.WAITING) }
                    "standby" -> { seatActive = false; info.seat = "standby"; setState(State.STANDBY, o.optString("msg", "다른 기기가 보고 있습니다.")) }
                    "lobby" -> { seatActive = false; info.seat = "lobby"; setState(State.LOBBY, o.optString("msg", "Unity 에서 Play 를 누르면 화면이 나옵니다.")) }
                }
            }
        }

        fun onBinary(b: ByteArray, len: Int, recvNs: Long) {
            if (stopped || video !== ws || len < FrameHeader.BYTES) return
            val flags = FrameHeader.flags(b)
            if (FrameHeader.isExtension(flags)) { counters.recvExt.incrementAndGet(); return }     // R4: 모르는 확장 메시지는 확인만
            counters.bytes.addAndGet(len.toLong())
            if (!FrameHeader.isH264(flags)) {
                counters.recvJpeg.incrementAndGet()
                if (jpegSinceNs == 0L) jpegSinceNs = recvNs
                else if (recvNs - jpegSinceNs > 2_000_000_000L && state != State.NOCODEC && seatActive) setState(State.NOCODEC, "PC가 H.264 를 보내지 않습니다(ffmpeg 문제).")
                return
            }
            jpegSinceNs = 0
            val id = FrameHeader.frameId(b)
            val w = FrameHeader.width(b)
            val h = FrameHeader.height(b)
            if (FrameHeader.isConfig(flags)) {
                counters.recvConfig.incrementAndGet()
                expectId = id + 1                         // 연속성 검사는 설정마다 새로 시작(설계 리뷰 m8)
                ui.onVideoSize(this@Session, w, h)
                if (!decoder.onConfig(this@Session, b, FrameHeader.BYTES, len - FrameHeader.BYTES, w, h)) { counters.corrupt.incrementAndGet(); requestKey("bad-config") }
                return
            }
            counters.recvH264.incrementAndGet()
            if (expectId >= 0 && id != expectId) counters.idGaps.incrementAndGet()
            expectId = id + 1
            counters.lastEncodeUs = FrameHeader.encodeUs(b); counters.lastCaptureToEncodedUs = FrameHeader.captureToEncodedUs(b)
            val capUs = FrameHeader.captureTime(b) * 100
            val slot = trace.add(generation, id, flags, len, capUs, FrameHeader.inputSeq(b), recvNs)
            if (!AvcConfig.avccToAnnexBInPlace(b, FrameHeader.BYTES, len - FrameHeader.BYTES)) {
                counters.corrupt.incrementAndGet(); trace.releaseNs[slot] = -1
                requestKey("corrupt")
                return
            }
            decoder.onFrame(this@Session, b, FrameHeader.BYTES, len - FrameHeader.BYTES, FrameHeader.isKey(flags), capUs, slot)
        }
    }

    private fun videoLoop(ws: WsClient, generation: Int) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_DISPLAY)
        val rx = VideoRx(ws, generation)
        try {
            while (true) {
                val op = ws.read()
                if (op == WsClient.OP_BINARY) {
                    val recvNs = System.nanoTime()
                    ws.sendText(ACK)                 // 받자마자, 디코딩 전에, 수신 스레드에서(00 문서 L3, L4)
                    rx.onBinary(ws.payload, ws.length, recvNs)
                } else if (op == WsClient.OP_TEXT) {
                    rx.router.dispatch(String(ws.payload, 0, ws.length, Charsets.UTF_8))
                } else if (op == WsClient.OP_CLOSE) {
                    lost(true, ws, "PC 가 닫음(" + ws.closeCode + ")", ws.closeCode)
                    return
                }
            }
        } catch (e: SocketTimeoutException) {
            lost(true, ws, if (rx.helloSeen) "시간 초과" else "PC 가 응답하지 않습니다(hello 없음)", 0)
        } catch (e: Exception) {
            lost(true, ws, "끊김: " + (e.message ?: e.javaClass.simpleName), 0)
        }
    }

    /** 디코더가 expectFirstFrame 뒤의 첫 프레임을 화면으로 넘겼습니다(playlink-pacer / playlink-codec). */
    fun onFirstFrameReleased() { if (state == State.WAITING || state == State.NOCODEC) setState(State.PLAYING) }

    /** 2초에 1번 이하. PC 는 ffmpeg 를 다시 시작하므로(약 0.43초 공백) 아껴 씁니다. */
    fun requestKey(reason: String) {
        val now = System.nanoTime()
        if (stopped || now - lastKeyRequestNs < 2_000_000_000L) return
        lastKeyRequestNs = now
        val ws = video ?: return
        counters.keyRequests.incrementAndGet()
        Log.i(TAG, "key request: $reason")
        Thread({ try { ws.sendText(KEY) } catch (e: Exception) { } }, "playlink-key").start()
    }

    /** [여기서 보기] */
    fun take() { val ws = video ?: return; Thread({ try { ws.sendText(TAKE) } catch (e: Exception) { } }, "playlink-take").start() }

    // ------------------------------------------------------------------ 입력 채널

    /** UI 스레드: 큐에 넣기만 합니다. 세대가 다른(옛 연결 때 만든) 메시지는 버리고, 밀려 있으면 move 는 버립니다(상한 64). */
    fun sendPointer(message: ByteArray, isMove: Boolean, epoch: Int) {
        if (!seatActive || input == null || epoch != inputEpoch) return
        if (isMove && inputQueue.size >= 64) { counters.pointerDropped.incrementAndGet(); return }
        inputQueue.offer(message)
    }

    private fun inputWriteLoop(ws: WsClient) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_DISPLAY)
        var nextPingNs = System.nanoTime()
        var nextReportNs = System.nanoTime() + 2_000_000_000L
        try {
            while (input === ws) {
                val now = System.nanoTime()
                if (now >= nextPingNs) {
                    nextPingNs = now + 1_000_000_000L
                    ws.sendText(String.format(Locale.ROOT, "{\"t\":\"p\",\"ts\":%.3f}", now / 1e6).toByteArray(Charsets.US_ASCII))
                    if (now - lastPongNs > 5_000_000_000L) { lost(false, ws, "PC가 응답하지 않습니다(ping 5초)", 0); return }
                }
                if (now >= nextReportNs) {
                    nextReportNs = now + 2_000_000_000L
                    // dm = 디코딩 시간(ms, 지수 평균), dp = 지난 2초에 표시 일정이 버린 프레임 수(설계 3.6)
                    val skipped = counters.skipped.get()
                    ws.sendText(String.format(Locale.ROOT, "{\"t\":\"q\",\"dm\":%.1f,\"dp\":%d}", counters.decodeMs, skipped - lastSkipped).toByteArray(Charsets.US_ASCII))
                    lastSkipped = skipped
                }
                val m = inputQueue.poll(maxOf(1L, (nextPingNs - System.nanoTime()) / 1_000_000L), TimeUnit.MILLISECONDS) ?: continue
                ws.sendText(m)
                counters.pointerSent.incrementAndGet()
            }
        } catch (e: InterruptedException) {
        } catch (e: Exception) { lost(false, ws, "입력 채널 끊김: " + (e.message ?: ""), 0) }
    }

    private fun inputReadLoop(ws: WsClient) {
        // PC → 앱 입력 채널 메시지를 늘릴 자리(R2). 지금은 P(pong) 하나.
        val router = TextRouter()
        router.on("P") { o ->
            lastPongNs = System.nanoTime()
            counters.rttMs = (lastPongNs / 1e6 - o.optDouble("ts", 0.0)).toFloat()
        }
        try {
            while (true) {
                val op = ws.read()
                if (op == WsClient.OP_TEXT) router.dispatch(String(ws.payload, 0, ws.length, Charsets.UTF_8))
                else if (op == WsClient.OP_CLOSE) { lost(false, ws, "PC 가 닫음(" + ws.closeCode + ")", ws.closeCode); return }
            }
        } catch (e: Exception) { lost(false, ws, "입력 채널 끊김: " + (e.message ?: e.javaClass.simpleName), 0) }
    }
}
