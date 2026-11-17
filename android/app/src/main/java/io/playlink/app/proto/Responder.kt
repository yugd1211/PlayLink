package io.playlink.app.proto

import org.json.JSONObject
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.security.KeyPair
import java.security.SecureRandom
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/** 양쪽이 모두 적은 기능만 씁니다. PC 의 fin 에 든 caps. */
class Caps(text: String?) {
    private val set: Set<String> = (text ?: "").split(',').map { it.trim() }.filter { it.isNotEmpty() }.toSet()
    fun has(name: String) = name in set
    override fun toString() = set.joinToString(",")
}

/**
 * 받는 쪽(폰) 핸드셰이크: PROTOCOL.md 2장. 연결 하나를 스레드 하나가 처음부터 fin 까지 돌립니다.
 * 받을지 말지(5장의 규칙)와 사람에게 묻는 일은 Host 가 합니다. Android 클래스를 쓰지 않습니다.
 */
object Responder {
    const val INNER_PROTOCOL = 2
    const val APP_CAPS = "h264,seat"
    const val MSG_VERSION = "앱과 Unity 도구의 버전이 다릅니다."

    class Hello(val pc: String, val name: String, val ch: String, val mode: String, val take: Boolean)
    class Refusal(val code: String, val msg: String)
    /** m2 자리에서 거절했거나(code = 보낸 오류 코드) 그 뒤에 실패했습니다(auth_failed, bad_commit, rejected, proto …). */
    class Failed(val code: String, message: String = code) : IOException(message)

    interface Host {
        val phoneId: String
        val phoneName: String
        val appVersion: Int
        fun pairKey(pcId: String): ByteArray?
        /** 지금 유효한, 이 PC 의 QR 비밀(찍은 지 2분 안). 없으면 null. */
        fun qrSecret(pcId: String): ByteArray?
        /** m2 를 보내기 전. null 이면 받습니다. "바꿀까요?"를 묻는 동안 30초까지 막고 있어도 됩니다. */
        fun admit(hello: Hello): Refusal?
        /** sas: 숫자를 보여 주고 사람의 답을 answer 로 알립니다(막지 않음). 끝나면(어떤 결과든) sasDone 이 불립니다. */
        fun askSas(hello: Hello, digits: String, answer: (Boolean) -> Unit)
        fun sasDone(hello: Hello)
        /** TCP who 의 응답(UDP 탐색 응답과 같은 JSON). */
        fun whoReply(pcId: String, pcName: String, nonce: String): String
    }

    sealed class Result {
        object Who : Result()
        class Ok(val hello: Hello, val keys: Proto.Keys, val input: RecordReader, val output: RecordWriter, val caps: Caps) : Result()
    }

    /** 시험에서만 고정 값을 넣습니다. */
    class Fixed(val eph: KeyPair, val nr: ByteArray)

    private fun readFully(input: InputStream, buf: ByteArray) {
        var got = 0
        while (got < buf.size) { val r = input.read(buf, got, buf.size - got); if (r < 0) throw EOFException(); got += r }
    }

    fun readPlain(input: InputStream): ByteArray {
        val head = ByteArray(2)
        readFully(input, head)
        val len = ((head[0].toInt() and 0xFF) shl 8) or (head[1].toInt() and 0xFF)
        if (len == 0 || len > Proto.MAX_HANDSHAKE_MESSAGE) throw IOException("bad handshake length")
        return ByteArray(len).also { readFully(input, it) }
    }

    fun sendPlain(output: OutputStream, body: ByteArray) {
        if (body.size > Proto.MAX_HANDSHAKE_MESSAGE) throw IOException("handshake message too long")
        output.write(Proto.u16(body.size) + body)
    }

    private fun json(bytes: ByteArray): JSONObject = try { JSONObject(String(bytes, Charsets.UTF_8)) } catch (e: Exception) { throw Failed("bad_message") }

    /**
     * timeout: 읽기 제한 시간(ms)을 바꿉니다(소켓의 soTimeout). 실패는 Failed 또는 IOException 으로 던지고, 소켓은 부른 쪽이 닫습니다.
     */
    fun run(input: InputStream, output: OutputStream, timeout: (Int) -> Unit, host: Host, fixed: Fixed? = null): Result {
        timeout(5000)
        val m1 = readPlain(input)
        val h = json(m1)
        fun refuse(code: String, msg: String = ""): Nothing {
            try { sendPlain(output, JSONObject().put("t", "err").put("code", code).put("msg", msg).toString().toByteArray(Charsets.UTF_8)) } catch (e: IOException) { }
            throw Failed(code, msg)
        }
        val pc = h.optString("pc")
        if (h.optString("t") == "who") {
            if (h.optInt("v") != Proto.VERSION || !Proto.isId(pc)) throw Failed("bad_message")
            sendPlain(output, host.whoReply(pc, Proto.cleanName(h.optString("name")), h.optString("n").take(64)).toByteArray(Charsets.UTF_8))
            return Result.Who
        }
        if (h.optString("t") != "hello") refuse("bad_message")
        if (h.optInt("v") != Proto.VERSION) refuse("version", MSG_VERSION)
        val hello = Hello(pc, Proto.cleanName(h.optString("name")), h.optString("ch"), h.optString("mode"), h.optBoolean("take"))
        val ni = Proto.unb64(h.optString("ni"))
        val commit = Proto.unb64(h.optString("commit"))
        if (!Proto.isId(pc) || ni == null || ni.size != 16 || commit == null || commit.size != 32) refuse("bad_message")
        var secret: ByteArray? = null
        when (hello.mode) {
            "paired" -> {
                if (hello.ch != "video" && hello.ch != "input") refuse("bad_message")
                secret = host.pairKey(pc) ?: refuse("unknown_pc", "이 폰은 이 PC 와 페어링되어 있지 않습니다.")
            }
            "qr" -> {
                if (hello.ch != "pair") refuse("bad_message")
                secret = host.qrSecret(pc) ?: refuse("unknown_pc", "이 폰이 찍은 QR 이 아닙니다.")
            }
            "sas" -> if (hello.ch != "pair") refuse("bad_message")
            else -> refuse("bad_message")
        }
        host.admit(hello)?.let { refuse(it.code, it.msg) }

        val eph = fixed?.eph ?: Ecdh.generate()
        val nr = fixed?.nr ?: ByteArray(16).also { SecureRandom().nextBytes(it) }
        val m2 = ("{\"t\":\"hello\",\"v\":" + Proto.VERSION + ",\"ph\":\"" + host.phoneId + "\",\"name\":\"" + Proto.cleanName(host.phoneName) + "\",\"app\":" + host.appVersion +
            ",\"epk\":\"" + Proto.b64(Ecdh.encode(eph.public)) + "\",\"nr\":\"" + Proto.b64(nr) + "\"}").toByteArray(Charsets.UTF_8)
        sendPlain(output, m2)
        timeout(5000)
        val m3 = readPlain(input)
        val k = json(m3)
        if (k.optString("t") != "key") throw Failed("bad_message")
        val peerPub = Proto.unb64(k.optString("epk")) ?: throw Failed("bad_message")
        if (!Proto.fixedTimeEquals(Proto.sha256(peerPub, ni), commit)) throw Failed("bad_commit")
        val z = try { Ecdh.agree(eph.private, peerPub) } catch (e: Exception) { throw Failed("bad_message") }
        val keys = Proto.deriveKeys(Proto.transcriptHash(m1, m2, m3), z, Proto.psk(hello.mode, secret))
        val reader = RecordReader(input, keys.encI2R, keys.macI2R)
        val writer = RecordWriter(output, keys.encR2I, keys.macR2I)

        if (hello.mode == "sas") {
            // 폰의 답([허용]/[거절])은 사람이 누르는 대로 바로 보내고, 이 스레드는 PC 의 답을 기다립니다(PC 가 먼저 끊으면 창을 닫으려고).
            val decided = CompletableFuture<Boolean>()
            try {
                host.askSas(hello, keys.sas) { ok ->
                    if (decided.complete(ok)) try { writer.writeText(if (ok) "{\"t\":\"ok\"}" else "{\"t\":\"err\",\"code\":\"rejected\"}") } catch (e: IOException) { }
                }
                timeout(70000)
                val theirs = try { json(reader.readRecord()) } catch (e: IOException) { throw if (decided.getNow(true)) Failed("auth_failed", e.message ?: "") else Failed("rejected") }
                if (theirs.optString("t") != "ok") throw Failed(theirs.optString("code").ifEmpty { "rejected" })
                // 정상인 PC 는 폰의 ok 를 받은 뒤에만 ok 를 보냅니다. 먼저 왔더라도 사람이 [허용]을 누르기 전에는 끝내지 않습니다.
                val mine = try { decided.get(60, TimeUnit.SECONDS) } catch (e: Exception) { false }
                if (!mine) throw Failed("rejected")
            } finally {
                decided.complete(false)
                host.sasDone(hello)
            }
        }
        timeout(5000)
        // 여기서 태그가 틀리면 상대의 키가 다른 것입니다(페어링 키나 QR 비밀이 다름): 그냥 끊습니다.
        val fin = try { json(reader.readRecord()) } catch (e: IOException) { throw Failed("auth_failed", e.message ?: "") }
        if (fin.optString("t") != "fin") throw Failed("bad_message")
        if (fin.optInt("proto") != INNER_PROTOCOL) {
            try { writer.writeText(JSONObject().put("t", "err").put("code", "proto").put("msg", MSG_VERSION).toString()) } catch (e: IOException) { }
            throw Failed("proto", MSG_VERSION)
        }
        writer.writeText("{\"t\":\"fin\",\"proto\":$INNER_PROTOCOL,\"app\":${host.appVersion},\"caps\":\"$APP_CAPS\"}")
        timeout(0)
        return Result.Ok(hello, keys, reader, writer, Caps(fin.optString("caps")))
    }
}
