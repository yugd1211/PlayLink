package kr.co.seeplay.gvr

import kr.co.seeplay.gvr.link.LinkParser
import kr.co.seeplay.gvr.link.LinkResult
import kr.co.seeplay.gvr.net.Auth
import kr.co.seeplay.gvr.video.AvcConfig
import kr.co.seeplay.gvr.video.FrameHeader
import org.junit.Assert.*
import org.junit.Test

class CoreTest {
    private val key = ByteArray(16) { it.toByte() }
    private val cn = Auth.base64Url(ByteArray(16) { (0x10 + it).toByte() })
    private val sn = Auth.base64Url(ByteArray(16) { (0x20 + it).toByte() })

    // 설계 3.2 의 시험 벡터 4개
    @Test fun authVectors() {
        assertEquals("AAECAwQFBgcICQoLDA0ODw", Auth.base64Url(key))
        assertEquals("EBESExQVFhcYGRobHB0eHw", cn)
        assertEquals("ICEiIyQlJicoKSorLC0uLw", sn)
        assertEquals("zlYoCj8gUA86fWTs4qY2TadXPchzopR2enL1BEPxpJI", Auth.proof(key, "srv", "video", "GVRTEST1", cn, sn))
        assertEquals("Mi9NohBmpvSBE-2kTt0VfZBmIWgNa6EWB0QnffebIdo", Auth.proof(key, "cli", "video", "GVRTEST1", cn, sn))
        assertEquals("Kk7tLqrnHm_XeaEg6zmPoh0GPiqeXig3W5oM7UBACbE", Auth.proof(key, "srv", "input", "GVRTEST1", cn, sn))
        assertEquals("HvOPi01ERvyDRbwzKcVNWcHperDPX6_b4mrWq30vi3w", Auth.proof(key, "cli", "input", "GVRTEST1", cn, sn))
        assertArrayEquals(key, Auth.base64UrlDecode("AAECAwQFBgcICQoLDA0ODw"))
        assertTrue(Auth.isNonce(cn)); assertFalse(Auth.isNonce("short")); assertFalse(Auth.isNonce("EBESExQVFhcYGRobHB0e+w"))
        val h = Auth.parseHeader("v=1; sn=$sn;mac=abc ; unknown=x")
        assertEquals("1", h["v"]); assertEquals(sn, h["sn"]); assertEquals("abc", h["mac"])
    }

    @Test fun linkQrAndDeepLinkGiveSameResult() {
        val qr = LinkParser.parse("http://192.168.25.5:7300/c#v=1&s=GVRTEST2&t=AAECAwQFBgcICQoLDA0ODw&a=172.30.224.1,10.0.0.7", "seeplay-gvr") as LinkResult.Ok
        val dl = LinkParser.parse("seeplay-gvr://192.168.25.5:7300/c?v=1&s=GVRTEST2&t=AAECAwQFBgcICQoLDA0ODw&a=172.30.224.1,10.0.0.7", "seeplay-gvr") as LinkResult.Ok
        assertEquals(qr.link.sid, dl.link.sid); assertEquals(qr.link.token, dl.link.token); assertEquals(qr.link.hosts, dl.link.hosts); assertEquals(qr.link.port, dl.link.port)
        assertEquals(listOf("192.168.25.5", "172.30.224.1", "10.0.0.7"), qr.link.hosts)
        assertEquals(7300, qr.link.port); assertEquals("lan", qr.link.via); assertFalse(qr.link.take)
        assertArrayEquals(key, qr.link.tokenBytes)
    }

    @Test fun linkVariants() {
        val usb = LinkParser.parse("seeplay-gvr://127.0.0.1:7311/c?v=1&s=GVRTEST2&t=AAECAwQFBgcICQoLDA0ODw&take=1&pace=now&future=zzz", "seeplay-gvr") as LinkResult.Ok
        assertEquals("usb", usb.link.via); assertTrue(usb.link.take); assertEquals("now", usb.link.extras["pace"]); assertEquals("zzz", usb.link.extras["future"])
        fun err(s: String?) = (LinkParser.parse(s, "seeplay-gvr") as LinkResult.Error).code
        assertEquals("update", err("http://192.168.25.5:7300/c#v=2&s=GVRTEST2&t=AAECAwQFBgcICQoLDA0ODw"))
        assertEquals("unreadable", err("http://192.168.25.5:7300/c#v=1&s=GVRTEST2"))
        assertEquals("unreadable", err("http://192.168.25.5:7300/c#v=1&s=gvrtest2&t=AAECAwQFBgcICQoLDA0ODw"))
        assertEquals("unreadable", err("http://192.168.25.5:7300/c?v=1&s=GVRTEST2&t=AAECAwQFBgcICQoLDA0ODw"))   // http 는 # 뒤만
        assertEquals("unreadable", err("http://example.com:7300/c#v=1&s=GVRTEST2&t=AAECAwQFBgcICQoLDA0ODw"))
        assertEquals("notgvr", err("https://192.168.25.5:7300/c#v=1&s=GVRTEST2&t=AAECAwQFBgcICQoLDA0ODw"))
        assertEquals("notgvr", err("http://192.168.25.5:7300/#v=1"))
        assertEquals("notgvr", err("hello"))
        assertEquals("unreadable", err("http://192.168.25.5:7300/c#v=1&s=GVRTEST1&t=AAECAwQFBgcICQoLDA0ODw"))   // 1 은 base32 글자가 아님
        assertEquals("unreadable", err(null))
    }

    @Test fun avcConfigAndConversion() {
        val sps = byteArrayOf(0x67, 0x64, 0x00, 0x20, 0x11)
        val pps = byteArrayOf(0x68, 0x22, 0x33)
        val avcc = byteArrayOf(1, 0x64, 0x00, 0x20, 0xFF.toByte(), 0xE1.toByte(), 0, 5) + sps + byteArrayOf(1, 0, 3) + pps + byteArrayOf(9, 9)
        val padded = ByteArray(28) + avcc
        val cfg = AvcConfig.parse(padded, 28, avcc.size)!!
        assertArrayEquals(byteArrayOf(0, 0, 0, 1) + sps, cfg.sps)
        assertArrayEquals(byteArrayOf(0, 0, 0, 1) + pps, cfg.pps)
        assertNull(AvcConfig.parse(byteArrayOf(1, 0x64, 0, 0x20, 0xFD.toByte(), 0xE1.toByte(), 0, 5) + sps + byteArrayOf(1, 0, 3) + pps, 0, 19))   // 길이 2바이트
        assertNull(AvcConfig.parse(avcc, 0, 10))

        val frame = byteArrayOf(0, 0, 0, 2, 0x09, 0x10, 0, 0, 0, 3, 0x65, 1, 2)
        val buf = ByteArray(28) + frame
        assertTrue(AvcConfig.avccToAnnexBInPlace(buf, 28, frame.size))
        assertArrayEquals(byteArrayOf(0, 0, 0, 1, 0x09, 0x10, 0, 0, 0, 1, 0x65, 1, 2), buf.copyOfRange(28, buf.size))
        assertFalse(AvcConfig.avccToAnnexBInPlace(byteArrayOf(0, 0, 0, 9, 1, 2), 0, 6))
        assertFalse(AvcConfig.avccToAnnexBInPlace(byteArrayOf(0, 0, 0, 1, 1, 0, 0), 0, 7))
    }

    @Test fun frameHeader() {
        val b = ByteArray(28)
        fun put32(o: Int, v: Long) { for (i in 0 until 4) b[o + i] = (v shr (8 * i)).toByte() }
        put32(0, 4000000000L); b[4] = 0xD0.toByte(); b[5] = 2; b[6] = 0; b[7] = 5
        put32(8, 41); put32(12, 4200); put32(16, 9100); put32(20, 6); put32(24, 123456)
        assertEquals(4000000000L, FrameHeader.frameId(b)); assertEquals(720, FrameHeader.width(b)); assertEquals(1280, FrameHeader.height(b))
        assertEquals(41, FrameHeader.inputSeq(b)); assertEquals(4200L, FrameHeader.encodeUs(b)); assertEquals(123456L, FrameHeader.captureTime(b))
        val f = FrameHeader.flags(b)
        assertTrue(FrameHeader.isH264(f)); assertTrue(FrameHeader.isKey(f)); assertFalse(FrameHeader.isConfig(f)); assertFalse(FrameHeader.isExtension(f))
        assertTrue(FrameHeader.isConfig(2 or 4 or 8)); assertTrue(FrameHeader.isExtension(0x102)); assertFalse(FrameHeader.isH264(1))
    }
}
