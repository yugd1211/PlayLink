package io.playlink.app.session

import io.playlink.app.net.Listener
import io.playlink.app.net.WsClient
import io.playlink.app.proto.Caps
import io.playlink.app.proto.Proto
import io.playlink.app.proto.Responder
import org.json.JSONObject
import java.io.BufferedInputStream
import java.net.InetAddress
import java.net.Socket
import java.util.Collections
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/** 한 PC 의 화면을 보는 동안의 연결 묶음(Session). Receiver 는 이 모양만 압니다(JVM 시험에서는 가짜). */
interface Watch {
    val pcId: String
    /** 핸드셰이크가 끝난 채널("video" | "input")을 넘깁니다. 같은 채널이 이미 있으면 이전 것을 닫고 새것을 씁니다. */
    fun attach(channel: String, ws: WsClient, remote: InetAddress?, caps: Caps)
    /** 소켓을 닫습니다. 끝났다는 콜백은 부르지 않습니다. */
    fun stop()
}

/** 폰이 연결을 받는 규칙(PROTOCOL.md 5장) 가운데 계산만 하는 부분. */
object Rules {
    enum class Verdict { ACCEPT, ASK, BUSY }

    /**
     * 페어링된 PC 의 paired 접속. watching = 지금 보고 있는 pcId(없으면 null), wanted = 폰에서 이 PC 를 골랐거나 방금 이 PC 의 QR 을 찍음,
     * blocked = 폰에서 [연결 끊기]로 이 PC 를 끊은 뒤 아직 사람이 다시 시작하지 않음.
     */
    fun paired(pc: String, take: Boolean, watching: String?, wanted: Boolean, blocked: Boolean): Verdict = when {
        watching == pc -> Verdict.ACCEPT                                         // 같은 PC: 채널을 다시 건 것(이전 연결을 바꿈)
        watching == null -> if (blocked && !take && !wanted) Verdict.BUSY else Verdict.ACCEPT      // 비어 있으면 묻지 않고 받음. 사람이 끊은 PC 의 자동 재접속만 빼고
        wanted -> Verdict.ACCEPT                                                 // 폰에서 고른 PC: 이미 사람이 정함
        take -> Verdict.ASK                                                      // 다른 PC 화면을 보는 중: 바꿀지 묻기
        else -> Verdict.BUSY                                                     // 다른 PC 의 자동 재접속은 묻지 않고 거절
    }

    /** sas 는 대기 화면이고 다른 창이 떠 있지 않을 때만. */
    fun sas(standby: Boolean, promptOpen: Boolean): Verdict = if (standby && !promptOpen) Verdict.ACCEPT else Verdict.BUSY
}

/**
 * 받는 쪽의 중심: 듣는 소켓(net.Listener), 탐색 응답, 핸드셰이크(proto.Responder), 받는 규칙, 지금 보고 있는 PC(Watch).
 * 스레드: 받은 TCP 연결마다 playlink-hs 하나가 핸드셰이크를 끝까지 돌리고, 영상/입력 채널이면 Watch 에 넘긴 뒤 끝납니다.
 * Android 클래스를 쓰지 않습니다. Ui 의 함수는 임의 스레드에서 불립니다.
 */
class Receiver(
    private val store: Pairings,
    private val appVersion: Int,
    private val ui: Ui,
    private val log: (String) -> Unit = {},
    private val now: () -> Long = { System.nanoTime() / 1_000_000L },
) {
    interface Pairings {
        val phoneId: String
        val phoneName: String
        fun key(pcId: String): ByteArray?
        fun save(pcId: String, name: String, key: ByteArray)
    }

    interface Ui {
        /** PC 목록이나 상태가 바뀜. */
        fun onChanged()
        /** 승인 창을 띄울 것 / 닫을 것. */
        fun onPrompt(prompt: Prompt)
        fun onPromptGone(prompt: Prompt)
        fun onPaired(pcId: String, pcName: String)
        /** 이 PC 의 화면을 보기 시작합니다(이전 Watch 는 이미 stop 됨). */
        fun openWatch(pcId: String, pcName: String): Watch
    }

    /** 사람에게 묻는 창 하나. sas = true: 페어링 승인(digits), false: "바꿀까요?"(current = 지금 보고 있는 pcId). */
    class Prompt(val sas: Boolean, val pcId: String, val pcName: String, val digits: String, val current: String, private val onAnswer: (Boolean) -> Unit) {
        /** UI 스레드에서 불러도 됩니다(소켓 쓰기는 다른 스레드에서). */
        fun answer(ok: Boolean) { Thread({ onAnswer(ok) }, "playlink-answer").start() }
    }

    class Seen(val pcId: String, val name: String, val atMs: Long)

    private class Ask(val pcId: String, val deadlineMs: Long) {
        val future = CompletableFuture<Boolean>()
        lateinit var prompt: Prompt
        @Volatile var doneAtMs = 0L
    }

    companion object {
        const val WANT_MS = 30_000L
        const val QR_MS = 120_000L
        const val SWITCH_ASK_MS = 28_000L       // PC 는 m2 를 40초까지 기다립니다
        const val SEEN_MS = 5_000L
        private const val MAX_HANDSHAKES = 8
    }

    private val lock = Any()
    private val seen = LinkedHashMap<String, Seen>()
    private var wantPc: String? = null
    private var wantUntilMs = 0L
    private var qr: Proto.Qr? = null
    private var qrUntilMs = 0L
    private var session: Watch? = null
    private val blocked = HashSet<String>()
    private var prompt: Prompt? = null
    private var sasOpen = false
    private var switchAsk: Ask? = null
    private var listener: Listener? = null
    private val handshakes = AtomicInteger()
    private val sockets: MutableSet<Socket> = Collections.newSetFromMap(ConcurrentHashMap<Socket, Boolean>())

    /** 앱이 대기 화면인가(UI 가 정함). sas 접속은 이때만 받습니다. */
    @Volatile var standby = false

    val port: Int get() = listener?.port ?: 0
    val udpOk: Boolean get() = listener?.udpOk ?: false
    val error: String get() = listener?.error ?: ""
    val watching: String? get() = synchronized(lock) { session?.pcId }

    // ------------------------------------------------------------------ 시작과 끝 (앱이 앞에 있을 때만 듣습니다)

    fun start(basePort: Int = Proto.DEFAULT_PORT) {
        synchronized(lock) {
            if (listener != null) return
            listener = Listener({ s -> accept(s) }, { text -> onQuery(text) }, { ui.onChanged() }).also { it.start(basePort) }
        }
    }

    fun stop() {
        val s: Watch?
        val ask: Ask?
        synchronized(lock) {
            listener?.stop(); listener = null
            s = session; session = null
            ask = switchAsk; switchAsk = null
            seen.clear(); wantPc = null
        }
        s?.stop()
        ask?.future?.complete(false)
        for (x in sockets) close(x)
    }

    private fun close(s: Socket) { try { s.close() } catch (e: Exception) { } }

    // ------------------------------------------------------------------ 폰에서 하는 일

    /** 최근(5초 안)에 탐색 질문이나 who 를 보낸 PC. */
    fun seen(): List<Seen> { val t = now(); return synchronized(lock) { seen.values.filter { t - it.atMs <= SEEN_MS } } }

    /** 폰에서 이 PC 를 고름: 30초 동안(또는 연결될 때까지) 이 PC 의 질문에 want 로 답합니다. */
    fun pick(pcId: String) { synchronized(lock) { wantPc = pcId; wantUntilMs = now() + WANT_MS; blocked.remove(pcId) } }

    /** QR 을 찍음: 2분 동안 이 PC 의 질문에 want + qr 로 답하고, 이 PC 의 qr 접속을 받습니다. */
    fun scanned(q: Proto.Qr) { synchronized(lock) { qr = q; qrUntilMs = now() + QR_MS; blocked.remove(q.pc) } }

    /** 연결을 기다리는 PC(고른 PC 또는 QR 을 찍은 PC)의 id 와, QR 쪽이면 그 이름. 없으면 null. */
    fun waitingFor(): Pair<String, String>? = synchronized(lock) {
        val t = now()
        val q = qr
        if (q != null && t < qrUntilMs) Pair(q.pc, q.name) else wantPc?.takeIf { t < wantUntilMs }?.let { Pair(it, "") }
    }

    /** [연결 끊기]: 지금 보는 PC 를 끊고, 그 PC 의 자동 재접속(take=false)은 사람이 다시 시작할 때까지 받지 않습니다. */
    fun disconnect() {
        val s: Watch?
        synchronized(lock) { s = session; session = null; if (s != null) blocked.add(s.pcId) }
        s?.stop()
    }

    /** Watch 가 스스로 끝났을 때(PC 가 끊음, 시간 초과). */
    fun ended(w: Watch) { synchronized(lock) { if (session === w) session = null } }

    /** 페어링을 지운 PC 를 보고 있었다면 끊습니다. */
    fun unpaired(pcId: String) {
        val s: Watch?
        synchronized(lock) { s = session?.takeIf { it.pcId == pcId }; if (s != null) session = null }
        s?.stop()
    }

    // ------------------------------------------------------------------ 탐색 응답 (UDP 와 TCP who 가 같은 내용)

    private fun wantsLocked(pc: String, t: Long) = (wantPc == pc && t < wantUntilMs) || (qr?.pc == pc && t < qrUntilMs)
    private fun qrSecretLocked(pc: String, t: Long) = qr?.takeIf { it.pc == pc && t < qrUntilMs }?.secret

    private fun onQuery(text: String): ByteArray? {
        val q = try { JSONObject(text) } catch (e: Exception) { return null }
        if (q.optString("t") != "q" || q.optInt("v") != Proto.VERSION) return null
        val pc = q.optString("pc")
        val n = q.optString("n")
        if (!Proto.isId(pc) || n.length > 64) return null
        return reply(pc, Proto.cleanName(q.optString("name")), n).toByteArray(Charsets.UTF_8)
    }

    fun reply(pc: String, pcName: String, n: String): String {
        val t = now()
        val want: Boolean
        val secret: ByteArray?
        val busy: String
        val isNew: Boolean
        synchronized(lock) {
            isNew = seen[pc].let { it == null || t - it.atMs > SEEN_MS || it.name != pcName }
            seen.remove(pc); seen[pc] = Seen(pc, pcName, t)
            if (seen.size > 32) seen.remove(seen.keys.first())
            want = wantsLocked(pc, t); secret = qrSecretLocked(pc, t); busy = session?.pcId ?: ""
        }
        val port = this.port
        val name = Proto.cleanName(store.phoneName)
        val o = JSONObject().put("t", "r").put("v", Proto.VERSION).put("ph", store.phoneId).put("name", name).put("port", port).put("busy", busy).put("want", want).put("n", n)
        // 이 PC 의 QR 을 방금 찍었으면 QR 증명이 먼저입니다: PC 가 이 폰을 잊은 뒤 다시 페어링하는 경우에도 QR 로 풀리게.
        if (secret != null) o.put("mac", Proto.discoveryMac(Proto.sha256(secret), pc, store.phoneId, name, port, want, n)).put("qr", true)
        else store.key(pc)?.let { o.put("mac", Proto.discoveryMac(it, pc, store.phoneId, name, port, want, n)) }
        if (isNew) ui.onChanged()
        return o.toString()
    }

    // ------------------------------------------------------------------ 받은 연결 하나 (playlink-hs)

    private fun accept(socket: Socket) {
        if (handshakes.incrementAndGet() > MAX_HANDSHAKES) { handshakes.decrementAndGet(); close(socket); return }
        sockets.add(socket)
        Thread({ try { serve(socket) } finally { handshakes.decrementAndGet(); sockets.remove(socket) } }, "playlink-hs").start()
    }

    private inner class Conn(val socket: Socket) : Responder.Host {
        var maySwitch = false
        var sasReserved = false
        var sasPrompt: Prompt? = null
        override val phoneId get() = store.phoneId
        override val phoneName get() = store.phoneName
        override val appVersion get() = this@Receiver.appVersion
        override fun pairKey(pcId: String) = store.key(pcId)
        override fun qrSecret(pcId: String) = synchronized(lock) { qrSecretLocked(pcId, now()) }
        override fun admit(hello: Responder.Hello) = this@Receiver.admit(this, hello)
        override fun whoReply(pcId: String, pcName: String, nonce: String) = reply(pcId, pcName, nonce)

        override fun askSas(hello: Responder.Hello, digits: String, answer: (Boolean) -> Unit) {
            val p = Prompt(true, hello.pc, hello.name, digits, "") { ok -> answer(ok); if (!ok) close(socket) }
            synchronized(lock) { prompt = p; sasPrompt = p }
            ui.onPrompt(p)
        }

        override fun sasDone(hello: Responder.Hello) {
            val p = sasPrompt ?: return
            synchronized(lock) { if (prompt === p) prompt = null }
            ui.onPromptGone(p)
        }

        fun release() { if (sasReserved) synchronized(lock) { sasOpen = false } }
    }

    private fun admit(conn: Conn, h: Responder.Hello): Responder.Refusal? {
        val busy = Responder.Refusal("busy", "폰이 다른 PC 화면을 보고 있습니다.")
        if (h.mode == "qr") { conn.maySwitch = true; return null }                // 방금 이 PC 의 QR 을 찍음(Responder 가 확인)
        if (h.mode == "sas") {
            synchronized(lock) {
                if (Rules.sas(standby, prompt != null || sasOpen) != Rules.Verdict.ACCEPT) return Responder.Refusal("busy", "폰이 대기 화면이 아닙니다. 폰에서 PlayLink 의 첫 화면을 열어 두세요.")
                sasOpen = true; conn.sasReserved = true
            }
            return null
        }
        val ask: Ask
        var created = false
        synchronized(lock) {
            val t = now()
            val wanted = wantsLocked(h.pc, t)
            when (Rules.paired(h.pc, h.take, session?.pcId, wanted, h.pc in blocked)) {
                Rules.Verdict.ACCEPT -> { conn.maySwitch = wanted; return null }
                Rules.Verdict.BUSY -> return if (session == null) Responder.Refusal("busy", "폰에서 연결을 끊었습니다. Unity 창에서 이 폰을 다시 고르세요.") else busy
                Rules.Verdict.ASK -> {}
            }
            val a = switchAsk
            if (a != null && a.pcId == h.pc && (a.doneAtMs == 0L || t - a.doneAtMs < 10_000L)) ask = a      // 같은 PC 의 다른 채널: 같은 답을 씁니다
            else if (prompt != null) return busy
            else {
                ask = Ask(h.pc, t + SWITCH_ASK_MS)
                ask.prompt = Prompt(false, h.pc, h.name, "", session?.pcId ?: "") { ok -> ask.future.complete(ok) }
                switchAsk = ask; prompt = ask.prompt; created = true
            }
        }
        if (created) ui.onPrompt(ask.prompt)
        val ok = try { ask.future.get(maxOf(1L, ask.deadlineMs - now()), TimeUnit.MILLISECONDS) } catch (e: Exception) { false }
        ask.future.complete(false)
        val gone: Boolean
        synchronized(lock) {
            if (ask.doneAtMs == 0L) ask.doneAtMs = now()
            gone = prompt === ask.prompt
            if (gone) prompt = null
        }
        if (gone) ui.onPromptGone(ask.prompt)
        if (!ok) return busy
        conn.maySwitch = true
        return null
    }

    private fun serve(socket: Socket) {
        val conn = Conn(socket)
        var what = ""
        try {
            socket.tcpNoDelay = true; socket.keepAlive = true; socket.receiveBufferSize = 256 * 1024
            val input = BufferedInputStream(socket.getInputStream(), 65536)
            val r = Responder.run(input, socket.getOutputStream(), { ms -> socket.soTimeout = ms }, conn)
            if (r !is Responder.Result.Ok) { close(socket); return }
            val h = r.hello
            what = h.ch + "/" + h.mode + " " + h.name
            if (h.ch == "pair") {
                store.save(h.pc, h.name, r.keys.pair)
                synchronized(lock) {
                    blocked.remove(h.pc)
                    // QR 은 한 번만 씁니다. 찍은 사람은 이 PC 를 보려는 것이므로 이어지는 paired 접속은 묻지 않고 받습니다.
                    if (h.mode == "qr") { if (qr?.pc == h.pc) qr = null; wantPc = h.pc; wantUntilMs = now() + WANT_MS }
                }
                close(socket)
                log("paired: $what")
                ui.onPaired(h.pc, h.name)
                return
            }
            val ws = WsClient(r.input, r.output, { close(socket) }, { ms -> socket.soTimeout = ms })
            synchronized(lock) {
                if (listener == null) { close(socket); return }
                var s = session
                if (s != null && s.pcId != h.pc) {
                    if (!conn.maySwitch) { close(socket); return }          // 묻는 사이에 상황이 바뀜
                    s.stop(); s = null
                }
                if (s == null) { s = ui.openWatch(h.pc, h.name); session = s }
                if (wantPc == h.pc) wantPc = null
                blocked.remove(h.pc)
                s.attach(h.ch, ws, socket.inetAddress, r.caps)
            }
            log("attached: $what")
        } catch (e: Exception) {
            close(socket)
            log("handshake ended: " + what + " " + ((e as? Responder.Failed)?.code ?: e.javaClass.simpleName))
        } finally { conn.release() }
    }
}
