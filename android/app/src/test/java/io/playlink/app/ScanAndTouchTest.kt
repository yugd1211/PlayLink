package io.playlink.app

import com.google.zxing.BarcodeFormat
import com.google.zxing.qrcode.QRCodeWriter
import io.playlink.app.input.TouchEncoder
import io.playlink.app.proto.Proto
import io.playlink.app.ui.Scanner
import org.junit.Assert.*
import org.junit.Test

/** 스캐너의 QR 인식, 터치 좌표(코드 리뷰 7.5, 7.6). */
class ScanAndTouchTest {
    @Test fun scannerDecodesGeneratedQr() {
        val text = "playlink://pair?v=1&pc=ABCDEFGHIJKLMNOP&n=DEV%20PC-1&s=AAECAwQFBgcICQoLDA0ODw"
        val size = 240; val stride = 256
        val m = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size)
        val luma = ByteArray(stride * size) { 0x55 }              // 행 간격의 남는 칸은 쓰레기 값
        for (y in 0 until size) for (x in 0 until size) luma[y * stride + x] = if (m.get(x, y)) 16 else 235.toByte()
        assertEquals(text, Scanner.decodeLuma(luma, stride, size, size))
        assertEquals("DEV PC-1", Proto.parseQr(Scanner.decodeLuma(luma, stride, size, size))!!.name)
        // 밝기가 뒤집힌 QR(어두운 바탕)
        for (y in 0 until size) for (x in 0 until size) luma[y * stride + x] = if (m.get(x, y)) 235.toByte() else 16
        assertEquals(text, Scanner.decodeLuma(luma, stride, size, size))
        assertNull(Scanner.decodeLuma(ByteArray(stride * size) { (it * 31).toByte() }, stride, size, size))
        assertNull(Scanner.decodeLuma(ByteArray(stride * size), stride, size, size))
    }

    // ------------------------------------------------------------------ 터치

    private class Sent(val kind: Char, val seq: Int, val text: String)
    private fun encoder(out: MutableList<Sent>) = TouchEncoder { kind, seq, _, _, message -> out.add(Sent(kind, seq, String(message))) }

    @Test fun touchCoordinatesAndSequence() {
        val out = ArrayList<Sent>()
        val e = encoder(out)
        e.down(0, 100f, 100f)
        assertTrue("no touch before the video size is known", out.isEmpty())          // 리뷰 m2
        e.left = 0f; e.top = 100f; e.width = 1000f; e.height = 2000f                   // 위아래 여백 100
        e.down(0, 500f, 1100f)
        assertEquals("{\"t\":\"d\",\"id\":0,\"x\":0.5000,\"y\":0.5000,\"s\":1}", out[0].text)
        e.move(0, 500f, 1100f)                                                          // 같은 자리: 보내지 않음
        assertEquals(1, out.size)
        e.move(0, 2000f, 50f)                                                           // 영상 밖: 가장 가까운 가장자리
        assertEquals("{\"t\":\"m\",\"id\":0,\"x\":1.0000,\"y\":0.0000,\"s\":2}", out[1].text)
        e.down(3, 1000f, 100f)                                                          // 오른쪽 위 모서리도 그대로(리뷰 M6: 죽은 구역 없음)
        assertEquals("{\"t\":\"d\",\"id\":3,\"x\":1.0000,\"y\":0.0000,\"s\":3}", out[2].text)
        e.up(0, 1f, 2099f)
        assertEquals("{\"t\":\"u\",\"id\":0,\"x\":0.0010,\"y\":0.9995,\"s\":4}", out[3].text)
        e.up(0, 1f, 2099f); e.move(0, 5f, 5f); e.move(7, 5f, 5f); e.up(7, 5f, 5f)       // down 없는 것은 버림
        assertEquals(4, out.size)
        e.up(3, 500f, 600f, canceled = true)                                            // 시스템이 취소: c, 마지막 좌표
        assertEquals("{\"t\":\"c\",\"id\":3,\"x\":1.0000,\"y\":0.0000,\"s\":5}", out[4].text)
        e.down(-1, 0f, 0f); e.down(32, 0f, 0f); e.down(1000, 0f, 0f)                    // 범위 밖 id
        assertEquals(5, out.size)
        assertTrue(out.all { it.text.length <= 125 })
    }

    @Test fun cancelAllAndReset() {
        val out = ArrayList<Sent>()
        val e = encoder(out)
        e.width = 100f; e.height = 100f
        e.down(1, 10f, 20f); e.down(2, 30f, 40f)
        e.cancelAll()
        assertEquals(listOf('d', 'd', 'c', 'c'), out.map { it.kind })
        assertEquals("{\"t\":\"c\",\"id\":2,\"x\":0.3000,\"y\":0.4000,\"s\":4}", out[3].text)
        e.cancelAll()
        assertEquals(4, out.size)
        e.down(1, 10f, 20f)
        e.reset()                                                                       // 새 연결: 번호 1부터, 눌려 있던 것은 잊음
        e.move(1, 50f, 50f); e.cancelAll()
        assertEquals(5, out.size)
        e.down(1, 10f, 20f)
        assertEquals(1, out.last().seq)
    }
}
