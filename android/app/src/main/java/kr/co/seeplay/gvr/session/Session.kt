package kr.co.seeplay.gvr.session

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import android.os.Process
import android.util.Log
import kr.co.seeplay.gvr.BuildConfig
import kr.co.seeplay.gvr.diag.Counters
import kr.co.seeplay.gvr.diag.Settings
import kr.co.seeplay.gvr.diag.TraceRing
import kr.co.seeplay.gvr.link.ServerLink
import kr.co.seeplay.gvr.link.ServerStore
import kr.co.seeplay.gvr.net.Auth
import kr.co.seeplay.gvr.net.Handshake
import kr.co.seeplay.gvr.net.NetPick
import kr.co.seeplay.gvr.net.WsClient
import kr.co.seeplay.gvr.video.AvcConfig
import kr.co.seeplay.gvr.video.Decoder
import kr.co.seeplay.gvr.video.FrameHeader
import org.json.JSONObject
import java.io.IOException
import java.net.ConnectException
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.SecureRandom
import java.util.Collections
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

enum class State { DIALING, HANDSHAKING, WAITING, PLAYING, NOCODEC, STANDBY, LOBBY, RECONNECT, FAILED_TOKEN, FAILED_VERSION, FAILED_SERVER, STOPPED }

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
 * 연결 하나의 수명(설계 5.1): 걸기 → 업그레이드(+인증, net.Handshake) → 영상/입력 채널 → 끊기면 다시.
 * gvr-conn 스레드 하나가 상태 기계를 돌리고, 채널마다 읽기 스레드가 있습니다. UI 스레드는 여기의 어떤 잠금도 기다리지 않습니다.
 *
 * 수명 경계(코드 리뷰 M2): stop() 뒤에는 이 세션이 화면과 디코더를 건드리지 않습니다.
 *  - 막히는 단계(소켓 연결, 업그레이드) 뒤마다 stopped 를 다시 봅니다. stop() 은 걸고 있는 소켓까지 닫습니다.
 *  - 끊김 신호는 연결 시도마다 새 객체(Attempt)라서 stop() 의 신호가 다음 시도에 지워지지 않습니다.
 *  - Ui 콜백은 세션 자신을 넘기고(받는 쪽이 지금 세션인지 확인), 디코더는 주인이 아닌 세션의 호출을 버립니다.
 */
class Session(
    private val context: Context,
    private val link: ServerLink,
    private val userTake: Boolean,
    private val settings: Settings,
    private val store: ServerStore,
    private val decoder: Decoder,
    private val trace: TraceRing,
    private val counters: Counters,
    private val genSource: java.util.concurrent.atomic.AtomicInteger,
    private val ui: Ui,
) {
    interface Ui {
        fun onState(session: Session, state: State, message: String)
        fun onVideoSize(session: Session, width: Int, height: Int)
        fun onStale(session: Session, message: String?)
        /** 연결이 새로 맺어짐: 입력 번호를 1부터 다시 시작하고, 앞으로 보내는 포인터 메시지에 이 세대(epoch)를 붙입니다. */
        fun onConnected(session: Session, epoch: Int)
    }

    class Info {
        @Volatile var route = ""; @Volatile var host = ""; @Volatile var auth = ""; @Volatile var seat = ""; @Volatile var serverName = ""
        @Volatile var serverApp = 0; @Volatile var caps = Handshake.Caps(""); @Volatile var loopback = false; @Volatile var via = ""
    }
    private class Terminal(val state: State, message: String) : Exception(message)
    private class RetryLater(message: String, val afterMs: Long) : IOException(message)
    private class Opened(val ws: WsClient, val host: String)
    private class Attempt {
        val reason = AtomicReference<String?>(null)
        val latch = CountDownLatch(1)
        fun signal(r: String) { if (reason.compareAndSet(null, r)) latch.countDown() }
    }

    companion object {
        private const val TAG = "GVR"
        private val ACK = byteArrayOf('a'.code.toByte())
        private val CAPS = "{\"t\":\"caps\",\"h264\":1}".toByteArray(Charsets.US_ASCII)      // 서버 판정이 문자열 검사라 글자 그대로
        private val KEY = "{\"t\":\"key\"}".toByteArray(Charsets.US_ASCII)
        private val TAKE = "{\"t\":\"take\"}".toByteArray(Charsets.US_ASCII)
        private val random = SecureRandom()
        const val APP_CAPS = "h264,seat"
    }

    val info = Info()
    @Volatile var state = State.DIALING; private set
    @Volatile private var stopped = false
    @Volatile private var video: WsClient? = null
    @Volatile private var input: WsClient? = null
    @Volatile private var lastKeyRequestNs = 0L
    @Volatile private var lastPongNs = 0L
    @Volatile private var seatActive = true
    // 사람이 시작한 연결의 take=1 은 서버가 자리 배정에 실제로 쓸 때까지 유지합니다(설계 5.1: LOBBY → 4410 → "take 는 처음 값 유지").
    // 대기실(Play 밖)에서는 자리 배정이 없으므로, seat active/standby 를 받았을 때(또는 seat 를 모르는 서버에 붙었을 때) 비로소 끕니다.
    @Volatile private var takePending = userTake
    @Volatile private var attempt = Attempt()
    @Volatile private var inputEpoch = 0
    private val inputQueue = LinkedBlockingQueue<ByteArray>()
    private val thread = Thread({ run() }, "gvr-conn")
    private val dialing: MutableSet<Socket> = Collections.newSetFromMap(ConcurrentHashMap<Socket, Boolean>())
    @Volatile private var listener: ServerSocket? = null
    private val reverseVideo = LinkedBlockingQueue<Socket>()
    private val reverseInput = LinkedBlockingQueue<Socket>()
    private val videoRouter = TextRouter()
    private var helloSeen = false
    private var jpegSinceNs = 0L
    private var expectId = -1L
    @Volatile private var closeCode = 0
    @Volatile private var generation = 0
    private var lastSkipped = 0L

    val canTake: Boolean get() = info.caps.has("take") || info.auth != "ok"

    fun start() { thread.start() }

    /** UI 스레드에서 불러도 됩니다: 소켓을 닫기만 하고 기다리지 않습니다. 걸고 있는 중인 소켓도 닫습니다. */
    fun stop() {
        stopped = true
        attempt.signal("stop")
        thread.interrupt()
        Thread({
            video?.close(); input?.close()
            for (s in dialing) try { s.close() } catch (e: Exception) { }
            closeListener()
        }, "gvr-close").start()
    }

    private fun setState(s: State, message: String = "") {
        if (stopped) return
        state = s
        if (s == State.WAITING || s == State.NOCODEC) decoder.expectFirstFrame(this)     // 이 상태를 벗어나는 길은 "다음 프레임이 화면으로 감" 하나(코드 리뷰 B1)
        ui.onState(this, s, message)
    }
    private fun signal(reason: String) = attempt.signal(reason)
    private fun checkStopped() { if (stopped) throw InterruptedException() }
    private fun closeListener() {
        try { listener?.close() } catch (e: Exception) { }
        listener = null
        while (true) { val s = reverseVideo.poll() ?: reverseInput.poll() ?: break; try { s.close() } catch (e: Exception) { } }
    }

    // ------------------------------------------------------------------ 상태 기계 (gvr-conn)

    private fun run() {
        var backoffMs = 250L
        var everConnected = false
        val startedNs = System.nanoTime()
        var lastClean1000Ns = 0L
        while (!stopped) {
            var immediate = false
            var forcedWaitMs = 0L
            var note = ""
            var connectedNs = 0L
            val at = Attempt()
            attempt = at
            dialing.clear()
            try {
                checkStopped()
                if (settings.listenPort > 0 && listener == null) startReverseListener(settings.listenPort)
                setState(State.DIALING, link.hosts.joinToString(", ") + ":" + link.port)
                val take = takePending
                val v = openChannel("video", take, null)
                video = v.ws
                checkStopped()
                setState(State.HANDSHAKING, v.host)
                val i = openChannel("input", take, v.host)
                input = i.ws
                checkStopped()
                info.host = v.host + ":" + link.port
                helloSeen = false; jpegSinceNs = 0; expectId = -1; closeCode = 0; seatActive = true
                inputQueue.clear()
                lastPongNs = System.nanoTime()
                connectedNs = lastPongNs
                generation = genSource.incrementAndGet()
                if (stopped || !decoder.reset(this, generation)) throw InterruptedException()      // 주인이 아니면(새 세션이 이미 있음) 물러납니다
                inputEpoch = generation
                ui.onConnected(this, generation)
                setState(State.WAITING)
                v.ws.socket.soTimeout = 2000           // hello 를 기다리는 시간(설계 5.1). hello 를 받으면 0 으로
                val rx = Thread({ videoLoop(v.ws) }, "gvr-video-rx").apply { start() }
                val irx = Thread({ inputReadLoop(i.ws) }, "gvr-input-rx").apply { start() }
                val itx = Thread({ inputWriteLoop(i.ws) }, "gvr-input-tx").apply { start() }
                if (!info.caps.has("seat")) takePending = false
                if (link.hasToken && !everConnected) store.remember(link, v.host, info.serverName)
                everConnected = true
                at.latch.await()
                note = at.reason.get() ?: ""
                v.ws.close(); i.ws.close()
                itx.interrupt()
                rx.join(500); irx.join(500); itx.join(500)
                video = null; input = null
                when (closeCode) {
                    4401 -> throw Terminal(State.FAILED_TOKEN, "이 QR 은 더 이상 쓸 수 없습니다. Unity 창의 QR 을 다시 찍으세요.")
                    4403 -> { store.forget(); throw Terminal(State.FAILED_TOKEN, "이 QR 은 더 이상 쓸 수 없습니다. Unity 창의 QR 을 다시 찍으세요.") }
                    // 4409: 같은 기기(did)가 같은 종류의 소켓을 새로 열어 서버가 이 소켓을 버렸습니다(08_interop_notes U-1). 다시 걸면 새 소켓을 밀어내므로 멈춥니다.
                    4409 -> throw Terminal(State.FAILED_SERVER, "이 기기의 다른 연결이 이 연결을 대신했습니다. 앱을 닫았다가 다시 여세요.")
                    4410 -> immediate = true
                    1000 -> lastClean1000Ns = System.nanoTime()
                }
                if (note.startsWith("version")) throw Terminal(State.FAILED_VERSION, "버전이 맞지 않습니다(" + note + "). 앱과 Unity 도구를 같은 버전으로 맞추세요.")
            } catch (t: Terminal) {
                video?.abort(); input?.abort(); video = null; input = null
                closeListener()
                setState(t.state, t.message ?: "")
                return
            } catch (e: InterruptedException) {
                video?.abort(); input?.abort(); video = null; input = null
            } catch (e: Exception) {
                video?.abort(); input?.abort(); video = null; input = null
                note = if (link.via == "usb" && e is ConnectException) "Unity 에서 Play 를 누르세요(USB)" else (e.message ?: e.javaClass.simpleName)
                if (e is ConnectException && link.via == "usb") backoffMs = minOf(backoffMs, 2000L)
                if (e is RetryLater) forcedWaitMs = e.afterMs
            }
            if (stopped) break
            counters.reconnects.incrementAndGet()
            if (connectedNs != 0L && System.nanoTime() - connectedNs > 5_000_000_000L) backoffMs = 250L
            var text = "다시 연결 중: $note"
            if (!everConnected && System.nanoTime() - startedNs > 60_000_000_000L) text += "\nPC 주소가 바뀌었을 수 있습니다. [QR 스캔]"
            setState(State.RECONNECT, text)
            if (!immediate) {
                var cap = if (link.via == "usb") 2000L else 4000L
                if (lastClean1000Ns != 0L && System.nanoTime() - lastClean1000Ns < 30_000_000_000L) cap = 1000L   // 설계 리뷰 m13
                val wait = if (forcedWaitMs > 0) forcedWaitMs else (minOf(backoffMs, cap) * (0.8 + 0.4 * random.nextDouble())).toLong()
                backoffMs = minOf(backoffMs * 2, 4000L)
                try { Thread.sleep(wait) } catch (e: InterruptedException) { }
            }
        }
        for (s in dialing) try { s.close() } catch (e: Exception) { }
        closeListener()
    }

    // ------------------------------------------------------------------ 걸기와 업그레이드

    /** LAN 소켓을 묶을 Wi-Fi 망. VPN 은 고르지 않습니다(net.NetPick, 코드 리뷰 M4). */
    private fun wifiNetwork(): Network? {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        @Suppress("DEPRECATION")
        val nets = cm.allNetworks
        val index = NetPick.pickWifi(nets.map { n ->
            val c = cm.getNetworkCapabilities(n)
            NetPick.Candidate(c?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true, c?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true,
                c?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) == true)
        })
        return if (index >= 0) nets[index] else null
    }

    private fun newSocket(): Socket = Socket().apply { tcpNoDelay = true; keepAlive = true; receiveBufferSize = 256 * 1024 }.also { dialing.add(it) }

    /** 주소 후보를 동시에 걸어 먼저 닿는 쪽을 고릅니다(설계 5.1 DIALING). LAN 주소는 Wi-Fi 망에 묶습니다. */
    private fun dial(hosts: List<String>): Pair<Socket, String> {
        if (hosts.size == 1 || link.via == "usb") {
            val h = hosts[0]
            val s = newSocket()
            if (!h.startsWith("127.")) (wifiNetwork() ?: throw IOException("Wi-Fi 가 꺼져 있습니다")).bindSocket(s)
            s.connect(InetSocketAddress(h, link.port), 3000)
            return Pair(s, h)
        }
        val wifi = wifiNetwork() ?: throw IOException("Wi-Fi 가 꺼져 있습니다")
        val winner = AtomicReference<Pair<Socket, String>?>(null)
        val done = CountDownLatch(hosts.size)
        val first = CountDownLatch(1)
        hosts.forEachIndexed { n, h ->
            Thread({
                try {
                    if (n > 0) Thread.sleep(150)        // 마지막으로 성공한 주소(맨 앞)를 먼저
                    if (winner.get() == null && !stopped) {
                        val s = newSocket()
                        wifi.bindSocket(s)
                        s.connect(InetSocketAddress(h, link.port), 3000)
                        if (winner.compareAndSet(null, Pair(s, h))) first.countDown() else s.close()
                    }
                } catch (e: Exception) { } finally { done.countDown() }
            }, "gvr-dial-$n").start()
        }
        while (winner.get() == null && done.count > 0 && !stopped) first.await(100, TimeUnit.MILLISECONDS)
        return winner.get() ?: throw IOException("PC에 닿지 않습니다(" + hosts.joinToString(", ") + ")")
    }

    /**
     * 실험 F-41(진단 전용): PC가 폰으로 겁니다. 첫 줄 "GVR-REVERSE/1 <채널>\n" 으로 채널을 정합니다(설계 3.7).
     * Wi-Fi 주소에만 엽니다(모바일 데이터 쪽에는 열지 않음). 열지 못하면 문구로 알리고 다시 시도합니다(코드 리뷰 M5 의 5).
     */
    private fun startReverseListener(port: Int) {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val address = wifiNetwork()?.let { cm.getLinkProperties(it) }?.linkAddresses?.map { it.address }?.firstOrNull { it is Inet4Address }
            ?: throw IOException("Wi-Fi 가 꺼져 있습니다(듣기 포트 $port)")
        val ss = try { ServerSocket().apply { reuseAddress = true; bind(InetSocketAddress(address, port)) } }
            catch (e: Exception) { throw IOException("포트 $port 를 열 수 없습니다(" + (e.message ?: "") + ")") }
        listener = ss
        Thread({
            while (!stopped && !ss.isClosed) {
                try {
                    val s = ss.accept()
                    s.tcpNoDelay = true; s.keepAlive = true; s.soTimeout = 2000
                    val sb = StringBuilder()
                    val ins = s.getInputStream()
                    while (sb.length < 32) { val c = ins.read(); if (c < 0 || c == '\n'.code) break; sb.append(c.toChar()) }
                    val queue = when (sb.toString().trim()) { "GVR-REVERSE/1 video" -> reverseVideo; "GVR-REVERSE/1 input" -> reverseInput; else -> null }
                    if (queue == null) s.close()
                    else { while (true) { val old = queue.poll() ?: break; try { old.close() } catch (e: Exception) { } }; queue.add(s) }
                } catch (e: Exception) { if (stopped) break }
            }
        }, "gvr-reverse-listen").start()
    }

    /** channel: "video" | "input" | 확장 채널 "x/<이름>"(R5: 경로는 /x/<이름>, 증명의 채널 이름은 <이름>. Handshake.Request.proofChannel). */
    private fun openChannel(channel: String, take: Boolean, sameHost: String?): Opened {
        val socket: Socket
        val host: String
        if (settings.listenPort > 0) {
            socket = (if (channel == "video") reverseVideo else reverseInput).poll(15, TimeUnit.SECONDS) ?: throw IOException("PC가 걸어 오기를 기다리는 중(포트 ${settings.listenPort})")
            dialing.add(socket)
            host = socket.inetAddress.hostAddress ?: "?"
            info.route = "PC가 건 연결"
        } else {
            val pair = dial(if (sameHost != null) listOf(sameHost) else link.hosts)
            socket = pair.first; host = pair.second
            info.route = if (host.startsWith("127.")) "adb 터널(127.0.0.1)" else "LAN"     // USB 인지 무선 adb 인지는 앱이 알 수 없습니다(코드 리뷰 m15)
        }
        info.loopback = socket.inetAddress.isLoopbackAddress
        info.via = link.via
        socket.soTimeout = 3000
        val ws = WsClient(socket)
        try {
            val rq = Handshake.Request(
                hostHeader = host + ":" + link.port, channel = channel, sid = link.sid, token = if (link.hasToken) link.tokenBytes else null,
                protocol = FrameHeader.PROTOCOL, appVersion = BuildConfig.VERSION_CODE, deviceId = store.deviceId, deviceName = Handshake.deviceName(Build.MODEL),
                take = take, appCaps = APP_CAPS, loopback = info.loopback, allowLegacyLan = BuildConfig.DIAG && settings.legacy,
                clientNonce = Auth.base64Url(ByteArray(16).also { random.nextBytes(it) }))
            when (val r = Handshake.run(ws, rq)) {
                is Handshake.Result.Ok -> { info.auth = r.auth; info.serverApp = r.serverApp; info.serverName = r.serverName; info.caps = r.caps }
                is Handshake.Result.Terminal -> throw Terminal(when (r.kind) { Handshake.Fail.TOKEN -> State.FAILED_TOKEN; Handshake.Fail.VERSION -> State.FAILED_VERSION; Handshake.Fail.SERVER -> State.FAILED_SERVER }, r.message)
                is Handshake.Result.Retry -> throw RetryLater(r.message, r.afterMs)
            }
            return Opened(ws, host)
        } catch (e: Exception) {
            ws.abort()
            throw e
        }
    }

    // ------------------------------------------------------------------ 영상 채널 (gvr-video-rx)

    private fun videoLoop(ws: WsClient) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_DISPLAY)
        videoRouter.on("hello") { o ->
            helloSeen = true
            if (o.optInt("v") != FrameHeader.PROTOCOL) signal("version: 서버 프로토콜 " + o.optInt("v"))
            else { ws.socket.soTimeout = 0; ws.sendText(CAPS) }
        }
        videoRouter.on("s") { o -> ui.onStale(this, if (o.optBoolean("stale")) o.optString("msg", "화면이 멈춰 있습니다") else null) }
        videoRouter.on("seat") { o ->
            when (o.optString("s")) {
                "active" -> { takePending = false; seatActive = true; info.seat = "active"; if (state == State.STANDBY || state == State.LOBBY) setState(State.WAITING) }
                "standby" -> { takePending = false; seatActive = false; info.seat = "standby"; setState(State.STANDBY, o.optString("msg", "다른 기기가 보고 있습니다.")) }
                "lobby" -> { seatActive = false; info.seat = "lobby"; setState(State.LOBBY, o.optString("msg", "Unity 에서 Play 를 누르면 화면이 나옵니다.")) }
            }
        }
        try {
            while (true) {
                val op = ws.read()
                if (op == WsClient.OP_BINARY) {
                    val recvNs = System.nanoTime()
                    ws.sendText(ACK)                 // 받자마자, 디코딩 전에, 수신 스레드에서(00 문서 L3, L4)
                    onBinary(ws.payload, ws.length, recvNs)
                } else if (op == WsClient.OP_TEXT) {
                    videoRouter.dispatch(String(ws.payload, 0, ws.length, Charsets.UTF_8))
                } else if (op == WsClient.OP_CLOSE) {
                    closeCode = ws.closeCode
                    signal("서버가 닫음(" + ws.closeCode + ")")
                    return
                }
            }
        } catch (e: SocketTimeoutException) {
            signal(if (helloSeen) "시간 초과" else "서버가 응답하지 않습니다(hello 없음)")
        } catch (e: Exception) {
            signal("끊김: " + (e.message ?: e.javaClass.simpleName))
        }
    }

    private fun onBinary(b: ByteArray, len: Int, recvNs: Long) {
        if (stopped || len < FrameHeader.BYTES) return
        val flags = FrameHeader.flags(b)
        if (FrameHeader.isExtension(flags)) { counters.recvExt.incrementAndGet(); return }     // R4: 모르는 확장 메시지는 확인만
        counters.bytes.addAndGet(len.toLong())
        if (!FrameHeader.isH264(flags)) {
            counters.recvJpeg.incrementAndGet()
            if (jpegSinceNs == 0L) jpegSinceNs = recvNs
            else if (recvNs - jpegSinceNs > 2_000_000_000L && state != State.NOCODEC && seatActive) setState(State.NOCODEC, "PC가 H.264 를 보내지 않습니다(다른 화면이 같이 붙어 있거나 ffmpeg 문제).")
            return
        }
        jpegSinceNs = 0
        val id = FrameHeader.frameId(b)
        val w = FrameHeader.width(b)
        val h = FrameHeader.height(b)
        if (FrameHeader.isConfig(flags)) {
            counters.recvConfig.incrementAndGet()
            expectId = id + 1                         // 연속성 검사는 설정마다 새로 시작(설계 리뷰 m8)
            ui.onVideoSize(this, w, h)
            if (!decoder.onConfig(this, b, FrameHeader.BYTES, len - FrameHeader.BYTES, w, h)) { counters.corrupt.incrementAndGet(); requestKey("bad-config") }
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
        decoder.onFrame(this, b, FrameHeader.BYTES, len - FrameHeader.BYTES, FrameHeader.isKey(flags), capUs, slot)
    }

    /** 디코더가 expectFirstFrame 뒤의 첫 프레임을 화면으로 넘겼습니다(gvr-pacer / gvr-codec). */
    fun onFirstFrameReleased() { if (state == State.WAITING || state == State.NOCODEC) setState(State.PLAYING) }

    /** 2초에 1번 이하. 서버는 ffmpeg 를 다시 시작하므로(약 0.43초 공백) 아껴 씁니다. */
    fun requestKey(reason: String) {
        val now = System.nanoTime()
        if (stopped || now - lastKeyRequestNs < 2_000_000_000L) return
        lastKeyRequestNs = now
        val ws = video ?: return
        counters.keyRequests.incrementAndGet()
        Log.i(TAG, "key request: $reason")
        Thread({ try { ws.sendText(KEY) } catch (e: Exception) { } }, "gvr-key").start()
    }

    /** [여기서 보기] */
    fun take() { val ws = video ?: return; Thread({ try { ws.sendText(TAKE) } catch (e: Exception) { } }, "gvr-take").start() }

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
            while (true) {
                val now = System.nanoTime()
                if (now >= nextPingNs) {
                    nextPingNs = now + 1_000_000_000L
                    ws.sendText(String.format(Locale.ROOT, "{\"t\":\"p\",\"ts\":%.3f}", now / 1e6).toByteArray(Charsets.US_ASCII))
                    if (now - lastPongNs > 5_000_000_000L) { signal("PC가 응답하지 않습니다(ping 5초)"); return }
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
        } catch (e: Exception) { signal("입력 채널 끊김: " + (e.message ?: "")) }
    }

    private fun inputReadLoop(ws: WsClient) {
        // 서버 → 앱 입력 채널 메시지를 늘릴 자리(R2). 지금은 P(pong) 하나.
        val router = TextRouter()
        router.on("P") { o ->
            lastPongNs = System.nanoTime()
            counters.rttMs = (lastPongNs / 1e6 - o.optDouble("ts", 0.0)).toFloat()
        }
        try {
            ws.socket.soTimeout = 0
            while (true) {
                val op = ws.read()
                if (op == WsClient.OP_TEXT) router.dispatch(String(ws.payload, 0, ws.length, Charsets.UTF_8))
                else if (op == WsClient.OP_CLOSE) {
                    if (closeCode == 0) closeCode = ws.closeCode
                    signal("서버가 닫음(" + ws.closeCode + ")"); return
                }
            }
        } catch (e: Exception) { signal("입력 채널 끊김: " + (e.message ?: e.javaClass.simpleName)) }
    }
}
