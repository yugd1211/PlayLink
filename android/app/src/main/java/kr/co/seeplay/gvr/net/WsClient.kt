package kr.co.seeplay.gvr.net

import java.io.BufferedInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * java.net.Socket 위의 최소 WebSocket 클라이언트(설계 3.1). Android UI 를 모릅니다.
 * - 보내는 메시지 1개 = OutputStream.write 1번(00 문서 L1: 나눠 쓰면 adb 구간에서 40ms 씩 묶임).
 * - 읽기는 스레드 하나만, 쓰기는 writeLock 으로 메시지 단위 직렬화.
 */
class WsClient(val socket: Socket) {
    class HttpResponse(val status: Int, val headers: Map<String, String>, val body: String)

    companion object {
        const val OP_TEXT = 1
        const val OP_BINARY = 2
        const val OP_CLOSE = 8
        const val OP_PING = 9
        const val OP_PONG = 10
        private const val GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
        private val random = SecureRandom()
    }

    private val input: InputStream = BufferedInputStream(socket.getInputStream(), 65536)
    private val output: OutputStream = socket.getOutputStream()
    private val writeLock = Any()
    private val sendBuf = ByteArray(6 + 125)
    private val head = ByteArray(8)

    /** 마지막으로 읽은 메시지. 다음 read 까지 유효합니다. */
    var payload = ByteArray(65536); private set
    var length = 0; private set
    var closeCode = 0; private set

    /**
     * 업그레이드 요청을 write 1번으로 보내고 응답 헤더를 읽습니다. 101 이면 Sec-WebSocket-Accept 를 검증합니다.
     * 101 이 아니면 본문(Content-Length 만큼)까지 읽어 돌려줍니다.
     */
    fun upgrade(hostHeader: String, path: String, authHeader: String?): HttpResponse {
        val keyBytes = ByteArray(16).also { random.nextBytes(it) }
        val key = java.util.Base64.getEncoder().encodeToString(keyBytes)
        val sb = StringBuilder(256)
        sb.append("GET ").append(path).append(" HTTP/1.1\r\n")
        sb.append("Host: ").append(hostHeader).append("\r\n")
        sb.append("Upgrade: websocket\r\nConnection: Upgrade\r\n")
        sb.append("Sec-WebSocket-Key: ").append(key).append("\r\n")
        sb.append("Sec-WebSocket-Version: 13\r\n")
        if (authHeader != null) sb.append("X-GVR-Auth: ").append(authHeader).append("\r\n")
        sb.append("\r\n")
        output.write(sb.toString().toByteArray(Charsets.US_ASCII))

        val statusLine = readLine() ?: throw EOFException("no response")
        val parts = statusLine.split(' ')
        val status = parts.getOrNull(1)?.toIntOrNull() ?: throw IOException("bad status line")
        val headers = HashMap<String, String>()
        var lines = 0
        while (true) {
            val line = readLine() ?: throw EOFException("eof in headers")
            if (line.isEmpty()) break
            if (++lines > 64) throw IOException("too many header lines")
            val colon = line.indexOf(':')
            if (colon > 0) headers[line.substring(0, colon).trim().lowercase()] = line.substring(colon + 1).trim()
        }
        if (status != 101) {
            val n = (headers["content-length"]?.toIntOrNull() ?: 0).coerceIn(0, 8192)
            val body = ByteArray(n)
            var got = 0
            while (got < n) { val r = input.read(body, got, n - got); if (r < 0) break; got += r }
            return HttpResponse(status, headers, String(body, 0, got, Charsets.UTF_8))
        }
        val expect = java.util.Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-1").digest((key + GUID).toByteArray(Charsets.US_ASCII)))
        if (headers["sec-websocket-accept"] != expect) throw IOException("bad Sec-WebSocket-Accept")
        return HttpResponse(status, headers, "")
    }

    private fun readLine(): String? {
        val sb = StringBuilder()
        while (true) {
            val c = input.read()
            if (c < 0) return if (sb.isEmpty()) null else sb.toString()
            if (c == '\n'.code) { if (sb.isNotEmpty() && sb[sb.length - 1] == '\r') sb.setLength(sb.length - 1); return sb.toString() }
            if (sb.length > 8192) throw IOException("header line too long")
            sb.append(c.toChar())
        }
    }

    private fun readFully(buf: ByteArray, off: Int, len: Int) {
        var got = 0
        while (got < len) {
            val r = input.read(buf, off + got, len - got)
            if (r < 0) throw EOFException()
            got += r
        }
    }

    /**
     * 메시지 1개를 읽어 opcode 를 돌려줍니다(텍스트, 바이너리, 닫기). 내용은 payload[0, length).
     * ping 에는 pong 으로 답하고 계속 읽습니다. 서버는 조각을 보내지 않지만 이어 붙이기는 지원합니다.
     */
    fun read(): Int {
        var messageOp = 0
        var total = 0
        while (true) {
            readFully(head, 0, 2)
            val fin = (head[0].toInt() and 0x80) != 0
            val op = head[0].toInt() and 0x0F
            val masked = (head[1].toInt() and 0x80) != 0
            var len = (head[1].toInt() and 0x7F).toLong()
            if (len == 126L) { readFully(head, 0, 2); len = (((head[0].toInt() and 0xFF) shl 8) or (head[1].toInt() and 0xFF)).toLong() }
            else if (len == 127L) { readFully(head, 0, 8); len = 0; for (i in 0 until 8) len = (len shl 8) or (head[i].toLong() and 0xFF) }
            if (masked || len < 0 || len > 16L * 1024 * 1024) throw IOException("bad frame")
            val n = len.toInt()
            if (op >= 8) {
                if (n > 125 || !fin) throw IOException("bad control frame")
                val ctl = ByteArray(n)
                readFully(ctl, 0, n)
                when (op) {
                    OP_PING -> send(OP_PONG, ctl, n)
                    OP_CLOSE -> { closeCode = if (n >= 2) ((ctl[0].toInt() and 0xFF) shl 8) or (ctl[1].toInt() and 0xFF) else 1005; length = 0; return OP_CLOSE }
                }
                continue
            }
            if (op != 0) { messageOp = op; total = 0 } else if (messageOp == 0) throw IOException("continuation without start")
            if (total.toLong() + n > 16L * 1024 * 1024) throw IOException("message too large")
            if (total + n > payload.size) payload = payload.copyOf(maxOf(total + n, payload.size * 2))
            readFully(payload, total, n)
            total += n
            if (fin) { length = total; return messageOp }
        }
    }

    fun sendText(text: ByteArray) = send(OP_TEXT, text, text.size)

    /** 125바이트 이하 메시지를 마스크해 write 1번으로 보냅니다. */
    fun send(opcode: Int, data: ByteArray, len: Int) {
        require(len <= 125)
        synchronized(writeLock) {
            val mask = random.nextInt()
            sendBuf[0] = (0x80 or opcode).toByte()
            sendBuf[1] = (0x80 or len).toByte()
            sendBuf[2] = (mask shr 24).toByte(); sendBuf[3] = (mask shr 16).toByte(); sendBuf[4] = (mask shr 8).toByte(); sendBuf[5] = mask.toByte()
            for (i in 0 until len) sendBuf[6 + i] = (data[i].toInt() xor sendBuf[2 + (i and 3)].toInt()).toByte()
            output.write(sendBuf, 0, 6 + len)
        }
    }

    fun close(code: Int = 1000) {
        try { send(OP_CLOSE, byteArrayOf((code shr 8).toByte(), code.toByte()), 2) } catch (_: Exception) { }
        try { socket.close() } catch (_: Exception) { }
    }

    fun abort() { try { socket.close() } catch (_: Exception) { } }
}
