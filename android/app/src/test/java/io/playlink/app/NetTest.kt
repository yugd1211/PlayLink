package io.playlink.app

import io.playlink.app.net.NetPick
import io.playlink.app.net.WsClient
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.InetAddress

/** WsClient(바이트 흐름 위의 프레임 읽기/쓰기)와 주소 고르기 시험. 기기 없이 JVM 에서 돕니다. */
class NetTest {
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

    /** 한 번에 1바이트씩만 내주는 입력(나뉘어 오는 흐름). */
    private class Trickle(data: ByteArray) : ByteArrayInputStream(data) {
        override fun read(b: ByteArray, off: Int, len: Int): Int = super.read(b, off, minOf(len, 1))
    }

    private fun client(input: ByteArray, out: ByteArrayOutputStream = ByteArrayOutputStream(), trickle: Boolean = false) =
        WsClient(if (trickle) Trickle(input) else ByteArrayInputStream(input), out, {})

    /** 클라이언트가 쓴(마스크된) 프레임들을 풉니다. */
    private fun sent(out: ByteArrayOutputStream): List<Pair<Int, String>> {
        val b = out.toByteArray()
        val list = ArrayList<Pair<Int, String>>()
        var i = 0
        while (i < b.size) {
            assertTrue("client frames must be masked", (b[i + 1].toInt() and 0x80) != 0)
            val len = b[i + 1].toInt() and 0x7F
            val p = ByteArray(len) { k -> (b[i + 6 + k].toInt() xor b[i + 2 + (k and 3)].toInt()).toByte() }
            list.add(Pair(b[i].toInt() and 0x0F, String(p)))
            i += 6 + len
        }
        return list
    }

    @Test fun framesOfEveryLengthClass() {
        val sizes = listOf(0, 1, 125, 126, 65535, 65536, 300_000)
        val all = ByteArrayOutputStream()
        for (n in sizes) all.write(frame(2, ByteArray(n) { (it * 7 + n).toByte() }))
        val ws = client(all.toByteArray())
        for (n in sizes) {
            assertEquals(WsClient.OP_BINARY, ws.read()); assertEquals(n, ws.length)
            for (i in 0 until n step 997) assertEquals((i * 7 + n).toByte(), ws.payload[i])
        }
    }

    @Test fun oneByteTrickleFragmentsPingAndClose() {
        val all = ByteArrayOutputStream()
        all.write(frame(1, "he".toByteArray(), fin = false))
        all.write(frame(9, "pp".toByteArray()))                                    // 조각 사이의 ping
        all.write(frame(0, "ll".toByteArray(), fin = false))
        all.write(frame(0, "o".toByteArray()))
        all.write(frame(8, byteArrayOf((4410 shr 8).toByte(), (4410 and 0xFF).toByte())))
        val out = ByteArrayOutputStream()
        val ws = client(all.toByteArray(), out, trickle = true)
        assertEquals(WsClient.OP_TEXT, ws.read())
        assertEquals("hello", String(ws.payload, 0, ws.length))
        assertEquals(WsClient.OP_CLOSE, ws.read())
        assertEquals(4410, ws.closeCode)
        assertEquals(listOf(Pair(WsClient.OP_PONG, "pp")), sent(out))
    }

    @Test fun sendsMaskedTextAndCloseInOneWriteEach() {
        var writes = 0
        var closed = false
        val out = object : ByteArrayOutputStream() { override fun write(b: ByteArray, off: Int, len: Int) { writes++; super.write(b, off, len) } }
        val ws = WsClient(ByteArrayInputStream(ByteArray(0)), out, { closed = true })
        ws.sendText("{\"t\":\"key\"}".toByteArray())
        ws.close()
        assertEquals(2, writes)
        assertTrue(closed)
        assertEquals(listOf(Pair(WsClient.OP_TEXT, "{\"t\":\"key\"}"), Pair(WsClient.OP_CLOSE, String(byteArrayOf(3, 0xE8.toByte())))), sent(out))
    }

    private fun readFails(vararg bytes: ByteArray): Boolean {
        val all = ByteArrayOutputStream(); for (b in bytes) all.write(b)
        return try { client(all.toByteArray()).read(); false } catch (e: IOException) { true }
    }

    @Test fun hostileFramesAreErrors() {
        assertTrue("masked server frame", readFails(frame(2, ByteArray(4), mask = true)))
        assertTrue("17MB length", readFails(byteArrayOf(0x82.toByte(), 127, 0, 0, 0, 0, 1, 0x10, 0, 0)))
        assertTrue("negative length", readFails(byteArrayOf(0x82.toByte(), 127, 0xFF.toByte(), 0, 0, 0, 0, 0, 0, 1)))
        assertTrue("control frame over 125", readFails(byteArrayOf(0x89.toByte(), 126, 0, 126) + ByteArray(126)))
        assertTrue("fragmented control frame", readFails(byteArrayOf(0x09, 0)))
        assertTrue("continuation without start", readFails(frame(0, ByteArray(3))))
        assertTrue("fragments adding up over 16MB", readFails(frame(2, ByteArray(9 * 1024 * 1024), fin = false), frame(0, ByteArray(9 * 1024 * 1024))))
        assertTrue("truncated", readFails(frame(2, ByteArray(100)).copyOf(50)))
    }

    // ------------------------------------------------------------------ 망과 주소 고르기

    @Test fun wifiPickSkipsVpn() {
        fun c(wifi: Boolean, vpn: Boolean, notVpn: Boolean) = NetPick.Candidate(wifi, vpn, notVpn)
        assertEquals(-1, NetPick.pickWifi(emptyList()))
        assertEquals(1, NetPick.pickWifi(listOf(c(false, false, true), c(true, false, true))))                 // 셀룰러, Wi-Fi
        assertEquals(1, NetPick.pickWifi(listOf(c(true, true, false), c(true, false, true))))                  // Wi-Fi 위 VPN 이 먼저 나와도 진짜 Wi-Fi
        assertEquals(-1, NetPick.pickWifi(listOf(c(true, true, false))))                                       // VPN 만 있으면 고르지 않음
        assertEquals(-1, NetPick.pickWifi(listOf(c(true, false, false))))                                      // NOT_VPN 능력이 없는 망
        assertEquals(-1, NetPick.pickWifi(listOf(c(false, false, true))))
    }

    @Test fun onlyLocalPeersAreAccepted() {
        for (a in listOf("127.0.0.1", "10.0.2.2", "172.16.0.1", "172.31.255.254", "192.168.0.5", "169.254.3.4", "100.64.0.1", "100.127.255.254", "::1", "fe80::1", "fd12:3456::1"))
            assertTrue(a, NetPick.isLocalPeer(InetAddress.getByName(a)))
        for (a in listOf("8.8.8.8", "172.32.0.1", "192.169.0.1", "100.63.0.1", "100.128.0.1", "2001:4860:4860::8888"))
            assertFalse(a, NetPick.isLocalPeer(InetAddress.getByName(a)))
        assertFalse(NetPick.isLocalPeer(null))
    }
}
