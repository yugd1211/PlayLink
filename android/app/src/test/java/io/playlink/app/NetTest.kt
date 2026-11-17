package io.playlink.app

import io.playlink.app.net.Auth
import io.playlink.app.net.Handshake
import io.playlink.app.net.NetPick
import io.playlink.app.net.WsClient
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.security.MessageDigest
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/** 가짜 서버(루프백 소켓)를 상대로 한 WsClient · Handshake 시험(코드 리뷰 7.1, 7.2). 기기 없이 JVM 에서 돕니다. */
class NetTest {
    private val key = ByteArray(16) { it.toByte() }
    private val cn = Auth.base64Url(ByteArray(16) { (0x10 + it).toByte() })
    private val sn = Auth.base64Url(ByteArray(16) { (0x20 + it).toByte() })

    private class Request(val head: String) {
        val headers: Map<String, String> = head.split("\r\n").drop(1).mapNotNull { val c = it.indexOf(':'); if (c > 0) it.substring(0, c).lowercase() to it.substring(c + 1).trim() else null }.toMap()
        val path: String = head.substringBefore("\r\n").split(' ')[1]
    }

    /** 서버 쪽 일을 server 람다에 맡기고, 클라이언트 소켓으로 body 를 돌립니다. server 의 결과를 돌려줍니다. */
    private fun <S, C> withServer(server: (Socket) -> S, body: (Socket) -> C): Pair<S, C> {
        ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { ss ->
            val fs = CompletableFuture.supplyAsync { ss.accept().use { s -> s.soTimeout = 3000; server(s) } }
            val c = Socket(InetAddress.getLoopbackAddress(), ss.localPort)
            c.soTimeout = 3000
            val r = try { body(c) } finally { try { c.close() } catch (e: Exception) { } }
            return Pair(fs.get(5, TimeUnit.SECONDS), r)
        }
    }

    private fun readRequest(ins: InputStream): Request {
        val sb = StringBuilder()
        while (!sb.endsWith("\r\n\r\n")) { val c = ins.read(); if (c < 0) break; sb.append(c.toChar()) }
        return Request(sb.toString())
    }

    private fun accept(rq: Request): String = java.util.Base64.getEncoder().encodeToString(
        MessageDigest.getInstance("SHA-1").digest((rq.headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").toByteArray()))

    private fun switching(rq: Request, extra: String = "") =
        ("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept(rq) + "\r\n" + extra + "\r\n").toByteArray()

    private fun frame(op: Int, payload: ByteArray, fin: Boolean = true, mask: Boolean = false): ByteArray {
        val o = ByteArrayOutputStream()
        o.write((if (fin) 0x80 else 0) or op)
        val m = if (mask) 0x80 else 0
        when {
            payload.size < 126 -> o.write(m or payload.size)
            payload.size < 65536 -> { o.write(m or 126); o.write(payload.size shr 8); o.write(payload.size and 0xFF) }
            else -> { o.write(m or 127); for (i in 7 downTo 0) o.write(((payload.size.toLong() shr (8 * i)) and 0xFF).toInt()) }
        }
        if (mask) o.write(byteArrayOf(0, 0, 0, 0))
        o.write(payload)
        return o.toByteArray()
    }

    /** 클라이언트가 보낸(마스크된) 프레임 1개를 읽습니다. 못 읽으면(닫힘, 시간 초과) null. */
    private fun readClientFrame(ins: InputStream): Pair<Int, ByteArray>? {
        return try {
            val b0 = ins.read(); val b1 = ins.read()
            if (b0 < 0 || b1 < 0) return null
            assertTrue("client frames must be masked", (b1 and 0x80) != 0)
            val len = b1 and 0x7F
            val mask = ByteArray(4); for (i in 0 until 4) mask[i] = ins.read().toByte()
            val p = ByteArray(len); for (i in 0 until len) p[i] = (ins.read() xor mask[i and 3].toInt()).toByte()
            Pair(b0 and 0x0F, p)
        } catch (e: IOException) { null }
    }

    private fun request(channel: String = "video", sid: String = "PLKTEST1", token: ByteArray? = key, loopback: Boolean = true, legacy: Boolean = false, take: Boolean = true) =
        Handshake.Request("192.168.25.5:7300", channel, sid, token, 2, 3, "abcdefghijkl", "SM-F731N", take, "h264,seat", loopback, legacy, cn)

    // ------------------------------------------------------------------ WsClient

    @Test fun framesOfEveryLengthClass() {
        val sizes = listOf(0, 1, 125, 126, 65535, 65536, 300_000)
        val (_, got) = withServer({ s ->
            val rq = readRequest(s.getInputStream()); s.getOutputStream().write(switching(rq))
            for (n in sizes) s.getOutputStream().write(frame(2, ByteArray(n) { (it * 7 + n).toByte() }))
            s.getOutputStream().flush(); readClientFrame(s.getInputStream())
        }, { c ->
            val ws = WsClient(c)
            assertEquals(101, ws.upgrade("h:1", "/video", null).status)
            sizes.map { n ->
                assertEquals(WsClient.OP_BINARY, ws.read()); assertEquals(n, ws.length)
                for (i in 0 until n step 997) assertEquals((i * 7 + n).toByte(), ws.payload[i])
                n
            }.also { ws.close() }
        })
        assertEquals(sizes, got)
    }

    @Test fun oneByteTrickleFragmentsPingAndClose() {
        val (pong, _) = withServer({ s ->
            val o: OutputStream = s.getOutputStream()
            val rq = readRequest(s.getInputStream())
            for (b in switching(rq)) { o.write(b.toInt()); o.flush() }                 // 응답 헤더도 1바이트씩
            val all = ByteArrayOutputStream()
            all.write(frame(1, "he".toByteArray(), fin = false))
            all.write(frame(9, "pp".toByteArray()))                                    // 조각 사이의 ping
            all.write(frame(0, "ll".toByteArray(), fin = false))
            all.write(frame(0, "o".toByteArray()))
            all.write(frame(8, byteArrayOf((4410 shr 8).toByte(), (4410 and 0xFF).toByte())))
            for (b in all.toByteArray()) { o.write(b.toInt()); o.flush() }
            readClientFrame(s.getInputStream())
        }, { c ->
            val ws = WsClient(c)
            ws.upgrade("h:1", "/video", null)
            assertEquals(WsClient.OP_TEXT, ws.read())
            assertEquals("hello", String(ws.payload, 0, ws.length))
            assertEquals(WsClient.OP_CLOSE, ws.read())
            assertEquals(4410, ws.closeCode)
        })
        assertEquals(WsClient.OP_PONG, pong!!.first)
        assertEquals("pp", String(pong.second))
    }

    private fun readFails(vararg bytes: ByteArray): Boolean = withServer({ s ->
        val rq = readRequest(s.getInputStream()); s.getOutputStream().write(switching(rq))
        try { for (b in bytes) s.getOutputStream().write(b); s.getOutputStream().flush(); s.getInputStream().read() } catch (e: IOException) { }      // 클라이언트가 먼저 끊으면 쓰기가 실패합니다
    }, { c ->
        val ws = WsClient(c); ws.upgrade("h:1", "/video", null)
        try { ws.read(); false } catch (e: IOException) { true }
    }).second

    @Test fun hostileFramesAreErrors() {
        assertTrue("masked server frame", readFails(frame(2, ByteArray(4), mask = true)))
        assertTrue("17MB length", readFails(byteArrayOf(0x82.toByte(), 127, 0, 0, 0, 0, 1, 0x10, 0, 0)))
        assertTrue("negative length", readFails(byteArrayOf(0x82.toByte(), 127, 0xFF.toByte(), 0, 0, 0, 0, 0, 0, 1)))
        assertTrue("control frame over 125", readFails(byteArrayOf(0x89.toByte(), 126, 0, 126) + ByteArray(126)))
        assertTrue("fragmented control frame", readFails(byteArrayOf(0x09, 0)))
        assertTrue("continuation without start", readFails(frame(0, ByteArray(3))))
        assertTrue("fragments adding up over 16MB", readFails(frame(2, ByteArray(9 * 1024 * 1024), fin = false), frame(0, ByteArray(9 * 1024 * 1024))))
    }

    @Test fun upgradeRejectsBadAcceptAndEndlessHeaders() {
        fun upgradeFails(response: (Request) -> ByteArray) = withServer({ s -> s.getOutputStream().write(response(readRequest(s.getInputStream()))); s.getOutputStream().flush(); try { s.getInputStream().read() } catch (e: IOException) { } },
            { c -> try { WsClient(c).upgrade("h:1", "/video", null); false } catch (e: IOException) { true } }).second
        assertTrue(upgradeFails { "HTTP/1.1 101 X\r\nSec-WebSocket-Accept: AAAA\r\n\r\n".toByteArray() })
        assertTrue(upgradeFails { rq -> ("HTTP/1.1 101 X\r\nSec-WebSocket-Accept: " + accept(rq) + "\r\n" + "X-A: 1\r\n".repeat(100) + "\r\n").toByteArray() })
        assertTrue(upgradeFails { ("HTTP/1.1 200 OK\r\nX: " + "a".repeat(10000) + "\r\n\r\n").toByteArray() })
        assertTrue(upgradeFails { "garbage\r\n\r\n".toByteArray() })
    }

    // ------------------------------------------------------------------ Handshake (설계 3.2 시험 벡터)

    @Test fun handshakeMatchesDesignVectors() {
        for ((channel, srvMac, cliMac) in listOf(
            Triple("video", "rASdAm0R4E9eOg8WrTdhWEl_tYFZPYQaTVDJQFcq7MI", "Kum7Uc2NTxLHeflx39gkJOZfTH0T9JUHS--WvYy_kR8"),
            Triple("input", "L04dFts1PdX9hTbKhFnitvOCwRrHVJlvmdKtLpSdFms", "5GsiBVEovxdUf6qehtU9FF2vtYkyHPwfOdV_X5P_Ijg"))) {
            val (server, result) = withServer({ s ->
                val rq = readRequest(s.getInputStream())
                s.getOutputStream().write(switching(rq, "X-PlayLink-Auth: v=1; sn=$sn; mac=$srvMac; proto=2; app=7; name=DEV-PC; caps=h264,seat,take; future=1\r\n"))
                Pair(rq, readClientFrame(s.getInputStream()))
            }, { c -> Handshake.run(WsClient(c), request(channel)) })
            val ok = result as Handshake.Result.Ok
            assertEquals("ok", ok.auth); assertEquals(7, ok.serverApp); assertEquals("DEV-PC", ok.serverName)
            assertTrue(ok.caps.has("take")); assertTrue(ok.caps.has("seat")); assertFalse(ok.caps.has("tak")); assertFalse(ok.caps.has(""))
            assertEquals("/$channel", server.first.path)
            assertEquals("v=1; sid=PLKTEST1; cn=$cn; proto=2; app=3; did=abcdefghijkl; dev=SM-F731N; take=1; caps=h264,seat", server.first.headers["x-playlink-auth"])
            assertEquals(WsClient.OP_TEXT, server.second!!.first)
            assertEquals("{\"t\":\"auth\",\"mac\":\"$cliMac\"}", String(server.second!!.second))
        }
    }

    /** 서버 응답 헤더를 바꿔 가며 결과와 "auth 메시지를 보냈는가"를 봅니다. */
    private fun handshake(authHeader: String?, rq: Handshake.Request = request()): Pair<Handshake.Result, Boolean> {
        val (sent, result) = withServer({ s ->
            val r = readRequest(s.getInputStream())
            s.getOutputStream().write(switching(r, if (authHeader != null) "X-PlayLink-Auth: $authHeader\r\n" else ""))
            s.soTimeout = 300
            readClientFrame(s.getInputStream()) != null
        }, { c -> Handshake.run(WsClient(c), rq).also { Thread.sleep(50) } })
        return Pair(result, sent)
    }

    private fun kind(r: Handshake.Result) = (r as Handshake.Result.Terminal).kind

    @Test fun wrongServerProofSendsNothing() {
        val good = Auth.proof(key, "srv", "video", "PLKTEST1", cn, sn)
        val bad = good.dropLast(1) + (if (good.last() == 'A') 'B' else 'A')
        handshake("v=1; sn=$sn; mac=$bad").let { (r, sent) -> assertEquals(Handshake.Fail.TOKEN, kind(r)); assertFalse("must not send its own proof", sent) }
        handshake("v=1; sn=$sn").let { (r, sent) -> assertEquals(Handshake.Fail.TOKEN, kind(r)); assertFalse(sent) }
        // 다른 채널의 증명을 되돌려 쓰는 것(input 의 mac 을 video 에)도 거부
        handshake("v=1; sn=$sn; mac=" + Auth.proof(key, "srv", "input", "PLKTEST1", cn, sn)).let { (r, sent) -> assertEquals(Handshake.Fail.TOKEN, kind(r)); assertFalse(sent) }
        // 클라이언트 증명을 그대로 돌려주는 것(반사)도 거부
        handshake("v=1; sn=$sn; mac=" + Auth.proof(key, "cli", "video", "PLKTEST1", cn, sn)).let { (r, sent) -> assertEquals(Handshake.Fail.TOKEN, kind(r)); assertFalse(sent) }
        // sn 이 21자 / 23자 / 글자 틀림
        for (badSn in listOf(sn.dropLast(1), sn + "A", sn.dropLast(1) + "+")) {
            val mac = Auth.proof(key, "srv", "video", "PLKTEST1", cn, badSn)
            handshake("v=1; sn=$badSn; mac=$mac").let { (r, sent) -> assertEquals(Handshake.Fail.TOKEN, kind(r)); assertFalse(sent) }
        }
    }

    @Test fun versionMismatchIsTerminal() {
        val mac = Auth.proof(key, "srv", "video", "PLKTEST1", cn, sn)
        handshake("v=2; sn=$sn; mac=$mac").let { (r, sent) -> assertEquals(Handshake.Fail.VERSION, kind(r)); assertFalse(sent) }
        handshake("v=1; sn=$sn; mac=$mac; proto=3").let { (r, sent) -> assertEquals(Handshake.Fail.VERSION, kind(r)); assertFalse(sent) }
        handshake("v=1; sn=$sn; mac=$mac; proto=2").let { (r, sent) -> assertTrue(r is Handshake.Result.Ok); assertTrue(sent) }
    }

    @Test fun serverWithoutAuthOnlyOnLoopback() {
        handshake(null, request(loopback = true)).let { (r, sent) -> assertEquals("none(구버전 서버)", (r as Handshake.Result.Ok).auth); assertFalse(sent) }
        handshake(null, request(loopback = false)).let { (r, sent) -> assertEquals(Handshake.Fail.TOKEN, kind(r)); assertFalse(sent) }
        handshake(null, request(loopback = false, legacy = true)).let { (r, _) -> assertEquals("none(LEGACY, LAN)", (r as Handshake.Result.Ok).auth) }
        // 토큰이 없으면(옛 형식으로 기억한 서버) 인증 헤더를 보내지 않고, 서버가 인증을 내밀어도 증명을 보내지 않습니다
        val (rq, _) = withServer({ s -> readRequest(s.getInputStream()).also { s.getOutputStream().write(switching(it)) } }, { c -> Handshake.run(WsClient(c), request(token = null)) })
        assertNull(rq.headers["x-playlink-auth"])
    }

    @Test fun extensionChannelUsesItsNameInTheProof() {
        val rq = request(channel = "x/audio")
        assertEquals("audio", rq.proofChannel)
        val (server, result) = withServer({ s ->
            val r = readRequest(s.getInputStream())
            s.getOutputStream().write(switching(r, "X-PlayLink-Auth: v=1; sn=$sn; mac=" + Auth.proof(key, "srv", "audio", "PLKTEST1", cn, sn) + "\r\n"))
            Pair(r.path, readClientFrame(s.getInputStream()))
        }, { c -> Handshake.run(WsClient(c), rq) })
        assertTrue(result is Handshake.Result.Ok)
        assertEquals("/x/audio", server.first)
        assertEquals("{\"t\":\"auth\",\"mac\":\"" + Auth.proof(key, "cli", "audio", "PLKTEST1", cn, sn) + "\"}", String(server.second!!.second))
    }

    @Test fun httpErrorTable() {
        fun http(status: Int, code: String?, body: String = "{\"t\":\"err\",\"code\":\"x\",\"msg\":\"앱 0.1.2 이상이 필요합니다\"}"): Handshake.Result = withServer({ s ->
            readRequest(s.getInputStream())
            val b = body.toByteArray()
            s.getOutputStream().write(("HTTP/1.1 $status X\r\n" + (if (code != null) "X-PlayLink-Error: $code\r\n" else "") + "Content-Type: application/json\r\nContent-Length: ${b.size}\r\nConnection: close\r\n\r\n").toByteArray() + b)
            s.getOutputStream().flush(); try { s.getInputStream().read() } catch (e: IOException) { }
        }, { c -> Handshake.run(WsClient(c), request()) }).second
        assertEquals(Handshake.Fail.TOKEN, kind(http(401, "auth_required")))
        assertEquals(Handshake.Fail.SERVER, kind(http(403, "unknown_server")))
        assertEquals(Handshake.Fail.SERVER, kind(http(403, null)))
        (http(426, "version") as Handshake.Result.Terminal).let { assertEquals(Handshake.Fail.VERSION, it.kind); assertTrue(it.message.contains("앱 0.1.2 이상이 필요합니다")) }
        assertEquals(Handshake.Fail.SERVER, kind(http(400, "bad_auth")))
        (http(503, "busy") as Handshake.Result.Retry).let { assertEquals(1000L, it.afterMs) }
        (http(500, null, "") as Handshake.Result.Retry).let { assertEquals(0L, it.afterMs) }
        (http(403, "origin") as Handshake.Result.Retry).let { assertEquals(0L, it.afterMs) }      // 모르는 코드의 403 은 멈추지 않고 다시 시도
        // 본문이 JSON 이 아니거나 이상해도 죽지 않습니다
        assertEquals(Handshake.Fail.VERSION, kind(http(426, "version", "\"msg\"")))
        assertEquals(Handshake.Fail.VERSION, kind(http(426, "version", "<html>")))
    }

    @Test fun deviceNameIsHeaderSafe() {
        assertEquals("SM-F731N", Handshake.deviceName("SM-F731N"))
        assertEquals("Pixel8Prox", Handshake.deviceName("Pixel 8 Pro;\r\nx"))
        assertEquals(24, Handshake.deviceName("a".repeat(100)).length)
        assertEquals("", Handshake.deviceName("갤럭시"))
    }

    // ------------------------------------------------------------------ 망 고르기 (리뷰 M4)

    @Test fun wifiPickSkipsVpn() {
        fun c(wifi: Boolean, vpn: Boolean, notVpn: Boolean) = NetPick.Candidate(wifi, vpn, notVpn)
        assertEquals(-1, NetPick.pickWifi(emptyList()))
        assertEquals(1, NetPick.pickWifi(listOf(c(false, false, true), c(true, false, true))))                 // 셀룰러, Wi-Fi
        assertEquals(1, NetPick.pickWifi(listOf(c(true, true, false), c(true, false, true))))                  // Wi-Fi 위 VPN 이 먼저 나와도 진짜 Wi-Fi
        assertEquals(-1, NetPick.pickWifi(listOf(c(true, true, false))))                                       // VPN 만 있으면 고르지 않음
        assertEquals(-1, NetPick.pickWifi(listOf(c(true, false, false))))                                      // NOT_VPN 능력이 없는 망
        assertEquals(-1, NetPick.pickWifi(listOf(c(false, false, true))))
    }
}
