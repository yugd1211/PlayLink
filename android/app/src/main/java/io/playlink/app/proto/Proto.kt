package io.playlink.app.proto

import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.interfaces.ECPublicKey
import java.security.spec.ECFieldFp
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * PlayLink 연결 프로토콜 1 의 계산 부분(docs/PROTOCOL.md, 기준 구현 tools/playlink-proto.js).
 * Android 클래스를 쓰지 않습니다(JVM 단위 시험에서 tools/proto-vectors.json 의 값으로 검사).
 * 키와 비밀 값은 어디에도 적지 않습니다.
 */
object Proto {
    const val VERSION = 1
    const val DEFAULT_PORT = 7300
    const val MAX_HANDSHAKE_MESSAGE = 2048
    const val WRITE_CHUNK = 16384
    const val TAG_BYTES = 16
    private const val B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
    private const val BASE32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
    private val ID = Regex("^[A-Z2-7]{16}$")
    private val NOT_NAME = Regex("[^\\p{L}\\p{N} _\\-.()]")

    fun isId(text: String?) = text != null && ID.matches(text)

    /** 패딩 없는 base64url. */
    fun b64(data: ByteArray): String {
        val sb = StringBuilder((data.size * 4 + 2) / 3)
        var i = 0
        while (i < data.size) {
            val b0 = data[i].toInt() and 0xFF
            val b1 = if (i + 1 < data.size) data[i + 1].toInt() and 0xFF else -1
            val b2 = if (i + 2 < data.size) data[i + 2].toInt() and 0xFF else -1
            sb.append(B64[b0 shr 2])
            sb.append(B64[((b0 and 3) shl 4) or (if (b1 >= 0) b1 shr 4 else 0)])
            if (b1 >= 0) sb.append(B64[((b1 and 15) shl 2) or (if (b2 >= 0) b2 shr 6 else 0)])
            if (b2 >= 0) sb.append(B64[b2 and 63])
            i += 3
        }
        return sb.toString()
    }

    /** 패딩 없는 base64url 을 풉니다. 글자가 맞지 않으면 null. */
    fun unb64(text: String?): ByteArray? {
        if (text == null || text.length > 4096) return null
        val out = ByteArray(text.length * 3 / 4)
        var acc = 0
        var bits = 0
        var n = 0
        for (c in text) {
            val v = B64.indexOf(c)
            if (v < 0) return null
            acc = (acc shl 6) or v
            bits += 6
            if (bits >= 8) { bits -= 8; out[n++] = ((acc shr bits) and 0xFF).toByte() }
        }
        return if (n == out.size) out else out.copyOf(n)
    }

    fun sha256(vararg parts: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").let { d -> for (p in parts) d.update(p); d.digest() }

    fun hmac(key: ByteArray, vararg parts: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").let { m -> m.init(SecretKeySpec(key, "HmacSHA256")); for (p in parts) m.update(p); m.doFinal() }

    fun u16(n: Int) = byteArrayOf((n shr 8).toByte(), n.toByte())

    /** 무작위 16자 [A-Z2-7] (phoneId). */
    fun newId(random: SecureRandom = SecureRandom()): String = ByteArray(16).also { random.nextBytes(it) }.map { BASE32[it.toInt() and 31] }.joinToString("")

    /** 이름: 글자·숫자·공백·_-.() 만, 32자까지. */
    fun cleanName(s: String?): String = NOT_NAME.replace(s ?: "", "").take(32)

    /** psk: paired = 페어링 키(32B), qr = SHA256(QR 비밀 16B), sas = 0 32개 */
    fun psk(mode: String, secret: ByteArray?): ByteArray = when (mode) {
        "paired" -> secret!!
        "qr" -> sha256(secret!!)
        else -> ByteArray(32)
    }

    fun transcriptHash(m1: ByteArray, m2: ByteArray, m3: ByteArray): ByteArray =
        sha256("PLK1".toByteArray(Charsets.US_ASCII), u16(m1.size), m1, u16(m2.size), m2, u16(m3.size), m3)

    class Keys(val encI2R: ByteArray, val macI2R: ByteArray, val encR2I: ByteArray, val macR2I: ByteArray, val pair: ByteArray, val sas: String)

    fun deriveKeys(th: ByteArray, z: ByteArray, psk: ByteArray): Keys {
        val prk = hmac(th, z, psk)
        fun k(label: String) = hmac(prk, label.toByteArray(Charsets.US_ASCII), byteArrayOf(1))
        val s = k("sas")
        val n = (((s[0].toLong() and 0xFF) shl 24) or ((s[1].toLong() and 0xFF) shl 16) or ((s[2].toLong() and 0xFF) shl 8) or (s[3].toLong() and 0xFF)) % 1000000L
        return Keys(k("enc i2r"), k("mac i2r"), k("enc r2i"), k("mac r2i"), k("pair"), n.toString().padStart(6, '0'))
    }

    /** 탐색 응답과 who 응답의 증명(HMAC 앞 16바이트, base64url). */
    fun discoveryMac(key: ByteArray, pcId: String, phoneId: String, name: String, port: Int, want: Boolean, nonce: String): String =
        b64(hmac(key, "PLK1-DISC|$pcId|$phoneId|$name|$port|${if (want) 1 else 0}|$nonce".toByteArray(Charsets.UTF_8)).copyOf(16))

    fun fixedTimeEquals(a: ByteArray, b: ByteArray): Boolean = MessageDigest.isEqual(a, b)

    /** QR 내용: playlink://pair?v=1&pc=<pcId>&n=<이름>&s=<비밀 16B base64url> */
    class Qr(val pc: String, val name: String, val secret: ByteArray)

    fun parseQr(text: String?): Qr? {
        val raw = text?.trim() ?: return null
        val prefix = "playlink://pair?"
        if (raw.length > 512 || !raw.startsWith(prefix)) return null
        val p = HashMap<String, String>()
        for (pair in raw.substring(prefix.length).substringBefore('#').split('&')) {
            val eq = pair.indexOf('=')
            if (eq > 0 && !p.containsKey(pair.substring(0, eq))) p[pair.substring(0, eq)] = try { java.net.URLDecoder.decode(pair.substring(eq + 1), "UTF-8") } catch (e: Exception) { return null }
        }
        val secret = unb64(p["s"])
        if (p["v"] != "1" || !isId(p["pc"]) || secret == null || secret.size != 16) return null
        return Qr(p["pc"]!!, cleanName(p["n"]), secret)
    }
}

/** P-256 임시 키. 플랫폼의 EC 구현을 씁니다(minSdk 26). 공개키는 압축 안 한 65바이트(04‖X‖Y). */
object Ecdh {
    private val generator: KeyPairGenerator by lazy { KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) } }
    val params: ECParameterSpec by lazy { (generate().public as ECPublicKey).params }

    fun generate(): KeyPair = synchronized(generator) { generator.generateKeyPair() }

    private fun fixed32(v: BigInteger): ByteArray {
        val b = v.toByteArray()
        val out = ByteArray(32)
        val n = minOf(b.size, 32)
        System.arraycopy(b, b.size - n, out, 32 - n, n)
        return out
    }

    fun encode(pub: java.security.PublicKey): ByteArray {
        val w = (pub as ECPublicKey).w
        return byteArrayOf(4) + fixed32(w.affineX) + fixed32(w.affineY)
    }

    /** 65바이트 점을 공개키로. 곡선 위의 점이 아니면 예외(플랫폼 구현이 검사하지 않는 경우에 대비해 직접 확인). */
    fun decode(bytes: ByteArray): java.security.PublicKey {
        if (bytes.size != 65 || bytes[0].toInt() != 4) throw IOException("bad public key")
        val x = BigInteger(1, bytes.copyOfRange(1, 33))
        val y = BigInteger(1, bytes.copyOfRange(33, 65))
        val curve = params.curve
        val p = (curve.field as ECFieldFp).p
        if (x >= p || y >= p) throw IOException("bad public key")
        val right = x.pow(3).add(curve.a.multiply(x)).add(curve.b).mod(p)
        if (y.multiply(y).mod(p) != right) throw IOException("bad public key")
        return KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(ECPoint(x, y), params))
    }

    /** 공유 X 좌표 32바이트. */
    fun agree(private: PrivateKey, peer: ByteArray): ByteArray {
        val ka = KeyAgreement.getInstance("ECDH")
        ka.init(private)
        ka.doPhase(decode(peer), true)
        val z = ka.generateSecret()
        return if (z.size == 32) z else ByteArray(32).also { System.arraycopy(z, maxOf(0, z.size - 32), it, maxOf(0, 32 - z.size), minOf(32, z.size)) }
    }
}

/**
 * 레코드 쓰기(PROTOCOL.md 3장): len(2B) ‖ AES-256-CTR 암호문 ‖ HMAC 태그 16B. seq 는 0 부터 레코드마다 1 씩.
 * write 한 번 = 아래 스트림에 write 한 번(메시지를 나눠 쓰면 adb 구간에서 묶입니다).
 */
class RecordWriter(private val out: OutputStream, encKey: ByteArray, macKey: ByteArray) : OutputStream() {
    private val key = SecretKeySpec(encKey, "AES")
    private val cipher = Cipher.getInstance("AES/CTR/NoPadding")
    private val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(macKey, "HmacSHA256")) }
    private val iv = ByteArray(16)
    private var seq = 0L
    private var buf = ByteArray(256)

    override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)

    @Synchronized override fun write(b: ByteArray, off: Int, len: Int) {
        if (len <= 0) return
        val records = (len + Proto.WRITE_CHUNK - 1) / Proto.WRITE_CHUNK
        val total = len + records * (2 + Proto.TAG_BYTES)
        if (buf.size < total) buf = ByteArray(total)
        var at = 0
        var done = 0
        while (done < len) {
            val n = minOf(Proto.WRITE_CHUNK, len - done)
            for (i in 0 until 8) iv[i] = (seq ushr (56 - 8 * i)).toByte()
            cipher.init(Cipher.ENCRYPT_MODE, key, IvParameterSpec(iv))
            buf[at] = (n shr 8).toByte(); buf[at + 1] = n.toByte()
            cipher.doFinal(b, off + done, n, buf, at + 2)
            mac.update(iv, 0, 8); mac.update(buf, at, 2 + n)
            System.arraycopy(mac.doFinal(), 0, buf, at + 2 + n, Proto.TAG_BYTES)
            at += 2 + n + Proto.TAG_BYTES
            done += n
            seq++
        }
        out.write(buf, 0, at)
    }

    fun writeText(text: String) { val b = text.toByteArray(Charsets.UTF_8); write(b, 0, b.size) }
    override fun flush() = out.flush()
}

/** 레코드 읽기. 평문을 이어 붙인 바이트 흐름으로 읽거나(read), 레코드 하나를 통째로(readRecord) 읽습니다. 태그가 틀리면 IOException. 스레드 하나만 읽습니다. */
class RecordReader(private val input: InputStream, encKey: ByteArray, macKey: ByteArray) : InputStream() {
    private val key = SecretKeySpec(encKey, "AES")
    private val cipher = Cipher.getInstance("AES/CTR/NoPadding")
    private val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(macKey, "HmacSHA256")) }
    private val iv = ByteArray(16)
    private var seq = 0L
    private val ct = ByteArray(2 + 65535 + Proto.TAG_BYTES)
    private val plain = ByteArray(65535)
    private var pos = 0
    private var end = 0

    private fun readFully(buf: ByteArray, off: Int, len: Int) {
        var got = 0
        while (got < len) { val r = input.read(buf, off + got, len - got); if (r < 0) throw EOFException(); got += r }
    }

    private fun fill() {
        readFully(ct, 0, 2)
        val n = ((ct[0].toInt() and 0xFF) shl 8) or (ct[1].toInt() and 0xFF)
        if (n == 0) throw IOException("empty record")
        readFully(ct, 2, n + Proto.TAG_BYTES)
        for (i in 0 until 8) iv[i] = (seq ushr (56 - 8 * i)).toByte()
        mac.update(iv, 0, 8); mac.update(ct, 0, 2 + n)
        val want = mac.doFinal()
        var diff = 0
        for (i in 0 until Proto.TAG_BYTES) diff = diff or (want[i].toInt() xor ct[2 + n + i].toInt())
        if (diff != 0) throw IOException("bad record tag")
        cipher.init(Cipher.DECRYPT_MODE, key, IvParameterSpec(iv))
        cipher.doFinal(ct, 2, n, plain, 0)
        seq++
        pos = 0; end = n
    }

    override fun read(): Int { if (pos >= end) fill(); return plain[pos++].toInt() and 0xFF }

    override fun read(b: ByteArray, off: Int, len: Int): Int {
        if (len == 0) return 0
        if (pos >= end) fill()
        val n = minOf(len, end - pos)
        System.arraycopy(plain, pos, b, off, n)
        pos += n
        return n
    }

    /** 다음 레코드 하나의 평문(핸드셰이크의 마무리 메시지용). */
    fun readRecord(): ByteArray {
        if (pos >= end) fill()
        return plain.copyOfRange(pos, end).also { pos = end }
    }
}
