package io.playlink.app

import io.playlink.app.video.AvcConfig
import io.playlink.app.video.FrameHeader
import org.junit.Assert.*
import org.junit.Test

class CoreTest {
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
