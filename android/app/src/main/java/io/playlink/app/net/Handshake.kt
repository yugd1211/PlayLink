package io.playlink.app.net

/**
 * WebSocket 업그레이드 + 인증 1(설계 3.2, 3.3). Android 클래스를 쓰지 않습니다(코드 리뷰 M7: JVM 에서 가짜 서버를 상대로 시험).
 * 소켓을 만들고 고르는 일(Dialer)과 상태 기계(Session)는 이 함수 밖에 있습니다.
 */
object Handshake {
    /** 양쪽이 모두 적은 기능만 쓴다(확장 규칙 R1)를 묻는 자리. */
    class Caps(text: String?) {
        private val set: Set<String> = (text ?: "").split(',').map { it.trim() }.filter { it.isNotEmpty() }.toSet()
        fun has(name: String) = name in set
        override fun toString() = set.joinToString(",")
    }

    class Request(
        val hostHeader: String,
        /** 경로에서 맨 앞 '/' 를 뺀 것: "video", "input", 확장 채널은 "x/<이름>". */
        val channel: String,
        val sid: String,
        /** 토큰 16바이트. null 이면 인증 헤더를 보내지 않습니다(기억한 서버가 토큰 없는 옛 형식일 때). */
        val token: ByteArray?,
        val protocol: Int,
        val appVersion: Int,
        val deviceId: String,
        val deviceName: String,
        val take: Boolean,
        val appCaps: String,
        /** 소켓의 원격 주소가 루프백인가(인증 없는 응답을 받아들일지의 근거). */
        val loopback: Boolean,
        /** 진단 전용: LAN 의 인증 없는 서버도 허용. */
        val allowLegacyLan: Boolean = false,
        /** 클라이언트 난수(base64url 22자). 시험에서만 고정 값을 넣습니다. */
        val clientNonce: String,
    ) {
        /** 증명 문자열에 들어가는 채널 이름: 경로 "x/<이름>" 의 증명 채널은 "<이름>"(설계 15.2 R5). */
        val proofChannel: String get() = if (channel.startsWith("x/")) channel.substring(2) else channel
    }

    enum class Fail { TOKEN, VERSION, SERVER }

    sealed class Result {
        /** auth: "ok" | "none(구버전 서버)" | "none(LEGACY, LAN)" */
        class Ok(val auth: String, val serverApp: Int, val serverName: String, val caps: Caps) : Result()
        /** 다시 시도해도 소용없는 실패(문구를 보이고 멈춤). */
        class Terminal(val kind: Fail, val message: String) : Result()
        /** 다시 시도할 실패. afterMs > 0 이면 그만큼 뒤에(503 busy = 1초). */
        class Retry(val message: String, val afterMs: Long) : Result()
    }

    const val MSG_RESCAN = "PC를 확인할 수 없습니다. Unity 창의 QR 을 다시 찍으세요."

    fun deviceName(model: String): String = model.filter { it.isLetterOrDigit() && it.code < 128 || it == '_' || it == '-' }.take(24)

    /** JSON 본문에서 "msg" 값만 꺼냅니다(org.json 없이. 따옴표 이스케이프가 든 값은 거기서 끊깁니다). */
    private fun msgOf(body: String): String {
        val at = body.indexOf("\"msg\"")
        if (at < 0) return ""
        val q1 = body.indexOf('"', body.indexOf(':', at) + 1)
        if (q1 < 0) return ""
        val q2 = body.indexOf('"', q1 + 1)
        return if (q2 > q1) body.substring(q1 + 1, q2).take(200) else ""
    }

    /** IOException 은 그대로 던집니다(호출한 쪽이 재시도). */
    fun run(ws: WsClient, rq: Request): Result {
        val key = rq.token
        val cn = rq.clientNonce
        val authHeader = if (key != null) Auth.buildHeader(listOf(
            "v" to Auth.VERSION.toString(), "sid" to rq.sid, "cn" to cn, "proto" to rq.protocol.toString(),
            "app" to rq.appVersion.toString(), "did" to rq.deviceId, "dev" to rq.deviceName,
            "take" to (if (rq.take) "1" else "0"), "caps" to rq.appCaps)) else null
        val resp = ws.upgrade(rq.hostHeader, "/" + rq.channel, authHeader)
        if (resp.status != 101) {
            val code = resp.headers["x-playlink-error"] ?: ""
            val msg = msgOf(resp.body)
            return when {
                code == "unknown_server" || (resp.status == 403 && code.isEmpty()) -> Result.Terminal(Fail.SERVER, "이 QR 은 다른 PC의 것입니다. Unity 창의 QR 을 다시 찍으세요.")
                resp.status == 426 -> Result.Terminal(Fail.VERSION, "버전이 맞지 않습니다. $msg".trim())
                resp.status == 400 -> Result.Terminal(Fail.SERVER, "내부 오류(요청 형식). $msg".trim())
                resp.status == 401 -> Result.Terminal(Fail.TOKEN, "이 서버는 QR 연결이 필요합니다. Unity 창의 QR 을 다시 찍으세요.")
                resp.status == 503 -> Result.Retry("PC가 바쁩니다(HTTP 503 $code)".trim(), 1000)
                else -> Result.Retry("HTTP ${resp.status} $code".trim(), 0)
            }
        }
        val srv = resp.headers["x-playlink-auth"]
        if (srv != null && authHeader != null && key != null) {
            val f = Auth.parseHeader(srv)
            if (f["v"] != Auth.VERSION.toString()) return Result.Terminal(Fail.VERSION, "버전이 맞지 않습니다(인증 절차 ${f["v"]}).")
            if (f["proto"] != null && f["proto"] != rq.protocol.toString()) return Result.Terminal(Fail.VERSION, "버전이 맞지 않습니다(프로토콜 ${f["proto"]}).")
            val sn = f["sn"]
            val mac = f["mac"] ?: ""
            if (sn == null || !Auth.isNonce(sn) || !Auth.fixedTimeEquals(mac, Auth.proof(key, "srv", rq.proofChannel, rq.sid, cn, sn)))
                return Result.Terminal(Fail.TOKEN, MSG_RESCAN)          // 서버 증명이 틀리면 우리 증명(auth 메시지)을 보내지 않습니다
            ws.sendText(("{\"t\":\"auth\",\"mac\":\"" + Auth.proof(key, "cli", rq.proofChannel, rq.sid, cn, sn) + "\"}").toByteArray(Charsets.US_ASCII))
            return Result.Ok("ok", f["app"]?.toIntOrNull() ?: 0, f["name"] ?: "", Caps(f["caps"]))
        }
        // 인증 없는 서버(고치기 전의 도구): 루프백일 때만 받아들입니다.
        if (key != null && !rq.loopback && !rq.allowLegacyLan) return Result.Terminal(Fail.TOKEN, MSG_RESCAN)
        return Result.Ok(if (rq.loopback) "none(구버전 서버)" else "none(LEGACY, LAN)", 0, "", Caps(""))
    }
}
