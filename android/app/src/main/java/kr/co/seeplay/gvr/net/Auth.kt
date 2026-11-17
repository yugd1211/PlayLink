package kr.co.seeplay.gvr.net

import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** 인증 1 (설계 3.2). 순수 함수만 둡니다. JVM 단위 시험으로 시험 벡터를 검사합니다. */
object Auth {
    const val VERSION = 1
    private const val B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"

    fun base64Url(data: ByteArray): String {
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
    fun base64UrlDecode(text: String): ByteArray? {
        val out = ByteArray(text.length * 3 / 4)
        var acc = 0
        var bits = 0
        var n = 0
        for (c in text) {
            val v = B64.indexOf(c)
            if (v < 0) return null
            acc = (acc shl 6) or v
            bits += 6
            if (bits >= 8) {
                bits -= 8
                out[n++] = ((acc shr bits) and 0xFF).toByte()
            }
        }
        return if (n == out.size) out else out.copyOf(n)
    }

    /** role: "srv" | "cli", channel: "video" | "input" | 확장 채널 이름 */
    fun proof(key: ByteArray, role: String, channel: String, sid: String, cn: String, sn: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        val text = "GVR-AUTH-1|$role|$channel|$sid|$cn|$sn"
        return base64Url(mac.doFinal(text.toByteArray(Charsets.US_ASCII)))
    }

    fun fixedTimeEquals(a: String, b: String): Boolean =
        MessageDigest.isEqual(a.toByteArray(Charsets.US_ASCII), b.toByteArray(Charsets.US_ASCII))

    fun isNonce(text: String?): Boolean = text != null && text.length == 22 && base64UrlDecode(text)?.size == 16

    /** "이름=값; 이름=값" 을 표로. 모르는 이름은 호출한 쪽이 무시합니다. */
    fun parseHeader(value: String): Map<String, String> {
        val map = HashMap<String, String>()
        for (part in value.split(';')) {
            val p = part.trim()
            val eq = p.indexOf('=')
            if (eq > 0) map[p.substring(0, eq).trim()] = p.substring(eq + 1).trim()
        }
        return map
    }

    fun buildHeader(fields: List<Pair<String, String>>): String = fields.joinToString("; ") { it.first + "=" + it.second }
}
