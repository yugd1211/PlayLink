package kr.co.seeplay.gvr.link

import kr.co.seeplay.gvr.net.Auth

/** QR 주소와 딥링크가 가리키는 서버 하나 (설계 4장). */
data class ServerLink(
    val sid: String,
    val token: String,          // base64url 22자. 화면, 로그, 기록에 적지 않습니다.
    val port: Int,
    val hosts: List<String>,    // [주 주소] + a
    val via: String,            // "lan" | "usb"
    val take: Boolean,
    val extras: Map<String, String>,   // 진단 값(legacy, listen, pace, buf, rr, wifilock, hud, rec). 호출한 쪽이 받을지 정합니다.
) {
    val tokenBytes: ByteArray? get() = Auth.base64UrlDecode(token)
    val hasToken: Boolean get() = token.isNotEmpty()
}

sealed class LinkResult {
    data class Ok(val link: ServerLink) : LinkResult()
    /** code: "update"(QR 형식이 더 새로움) | "unreadable" | "notgvr" */
    data class Error(val code: String, val message: String) : LinkResult()
}

/**
 * 세 입구(앱 스캐너, 기본 카메라 → 착륙 페이지 → 딥링크, adb 인텐트)가 같은 값을 얻게 하는 해석기.
 * Android 클래스를 쓰지 않습니다(JVM 단위 시험).
 *   QR     : http://<host>:<port>/c#v=1&s=<sid>&t=<token>[&a=ip,ip]
 *   딥링크 : <scheme>://<host>:<port>/c?v=1&s=…&t=…[&a=…][&via=lan|usb][&take=1]
 */
object LinkParser {
    const val QR_VERSION = 1
    private val SID = Regex("^[A-Z2-7]{8}$")
    private val IPV4 = Regex("^(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})$")

    fun parse(text: String?, scheme: String): LinkResult {
        val raw = text?.trim() ?: return LinkResult.Error("unreadable", "QR 을 읽을 수 없습니다.")
        val schemeEnd = raw.indexOf("://")
        if (schemeEnd <= 0) return LinkResult.Error("notgvr", "Game View Remote 의 QR 이 아닙니다.")
        val sch = raw.substring(0, schemeEnd).lowercase()
        val rest = raw.substring(schemeEnd + 3)
        val isHttp = sch == "http"
        if (!isHttp && sch != scheme) return LinkResult.Error("notgvr", "Game View Remote 의 QR 이 아닙니다.")

        val hashAt = rest.indexOf('#')
        val beforeHash = if (hashAt >= 0) rest.substring(0, hashAt) else rest
        val fragment = if (hashAt >= 0) rest.substring(hashAt + 1) else ""
        val queryAt = beforeHash.indexOf('?')
        val authorityAndPath = if (queryAt >= 0) beforeHash.substring(0, queryAt) else beforeHash
        val query = if (queryAt >= 0) beforeHash.substring(queryAt + 1) else ""
        val slash = authorityAndPath.indexOf('/')
        val authority = if (slash >= 0) authorityAndPath.substring(0, slash) else authorityAndPath
        val path = if (slash >= 0) authorityAndPath.substring(slash) else "/"
        if (path != "/c") return LinkResult.Error("notgvr", "Game View Remote 의 QR 이 아닙니다.")

        val colon = authority.lastIndexOf(':')
        if (colon <= 0) return LinkResult.Error("unreadable", "QR 을 읽을 수 없습니다(포트 없음).")
        val host = authority.substring(0, colon)
        val port = asciiInt(authority.substring(colon + 1))
        if (port == null || port !in 1..65535 || !isPrivateIpv4(host)) return LinkResult.Error("unreadable", "QR 을 읽을 수 없습니다(주소).")

        // http 는 # 뒤, 커스텀 스킴은 ? 뒤에서 읽습니다. 모르는 이름은 무시합니다(R7).
        val params = parseParams(if (isHttp) fragment else query)
        val v = asciiInt(params["v"]) ?: return LinkResult.Error("unreadable", "QR 을 읽을 수 없습니다.")
        if (v != QR_VERSION) return LinkResult.Error("update", "앱을 업데이트하세요(QR 형식 $v).")
        val sid = params["s"] ?: ""
        val token = params["t"] ?: ""
        if (!SID.matches(sid) || !Auth.isNonce(token)) return LinkResult.Error("unreadable", "QR 을 읽을 수 없습니다.")

        val hosts = ArrayList<String>()
        hosts.add(host)
        params["a"]?.split(',')?.forEach { h -> if (isPrivateIpv4(h) && h !in hosts && hosts.size < 4) hosts.add(h) }
        val via = when (params["via"]) {
            "usb" -> "usb"
            "lan" -> "lan"
            else -> if (host.startsWith("127.")) "usb" else "lan"
        }
        val known = setOf("v", "s", "t", "a", "via", "take")
        return LinkResult.Ok(ServerLink(sid, token, port, hosts, via, params["take"] == "1", params.filterKeys { it !in known }))
    }

    /** ASCII 숫자만 받는 정수 해석(toIntOrNull 은 전각 숫자 같은 다른 문자 체계의 숫자도 받습니다). */
    private fun asciiInt(text: String?): Int? = if (text != null && text.length in 1..9 && text.all { it in '0'..'9' }) text.toInt() else null

    fun isIpv4(text: String): Boolean {
        val m = IPV4.matchEntire(text) ?: return false
        return m.groupValues.drop(1).all { it.toInt() <= 255 && (it.length == 1 || it[0] != '0') }      // 앞의 0 은 받지 않습니다(8진수로 읽는 해석기와 어긋남)
    }

    /**
     * 사설 · 링크 로컬 · 통신사 NAT · 루프백 대역만 받습니다(코드 리뷰 M5 의 4): 공인 주소가 든 QR/링크로 앱이 밖으로 나가지 않게.
     * 10/8, 172.16/12, 192.168/16, 169.254/16, 100.64/10, 127/8
     */
    fun isPrivateIpv4(text: String): Boolean {
        if (!isIpv4(text)) return false
        val p = text.split('.').map { it.toInt() }
        return p[0] == 10 || p[0] == 127 || (p[0] == 172 && p[1] in 16..31) || (p[0] == 192 && p[1] == 168) || (p[0] == 169 && p[1] == 254) || (p[0] == 100 && p[1] in 64..127)
    }

    private fun parseParams(text: String): Map<String, String> {
        val map = LinkedHashMap<String, String>()
        for (pair in text.split('&')) {
            val eq = pair.indexOf('=')
            if (eq > 0) map[pair.substring(0, eq)] = percentDecode(pair.substring(eq + 1))
        }
        return map
    }

    private fun percentDecode(s: String): String {
        if (s.indexOf('%') < 0) return s
        val sb = StringBuilder()
        var i = 0
        while (i < s.length) {
            val c = s[i]
            if (c == '%' && i + 2 < s.length) {
                val code = s.substring(i + 1, i + 3).toIntOrNull(16)
                if (code != null) { sb.append(code.toChar()); i += 3; continue }
            }
            sb.append(c); i++
        }
        return sb.toString()
    }
}
