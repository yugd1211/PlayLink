package kr.co.seeplay.gvr

import com.google.zxing.BarcodeFormat
import com.google.zxing.qrcode.QRCodeWriter
import kr.co.seeplay.gvr.input.TouchEncoder
import kr.co.seeplay.gvr.link.LinkGate
import kr.co.seeplay.gvr.link.LinkParser
import kr.co.seeplay.gvr.link.LinkResult
import kr.co.seeplay.gvr.link.ServerLink
import kr.co.seeplay.gvr.ui.Scanner
import org.junit.Assert.*
import org.junit.Test

/** QR · 딥링크의 적대적 입력, 확인 창 판단, 터치 좌표(코드 리뷰 7.3 ~ 7.6). */
class LinkAndTouchTest {
    private val T = "AAECAwQFBgcICQoLDA0ODw"
    private fun parse(s: String?) = LinkParser.parse(s, "seeplay-gvr")
    private fun err(s: String?) = (parse(s) as LinkResult.Error).code
    private fun ok(s: String) = (parse(s) as LinkResult.Ok).link

    @Test fun hostileQrPayloads() {
        val q = "#v=1&s=GVRTEST2&t=$T"
        // 공인 주소로는 나가지 않습니다
        for (host in listOf("8.8.8.8", "1.1.1.1", "172.15.0.1", "172.32.0.1", "192.169.0.1", "100.63.0.1", "100.128.0.1", "169.253.1.1", "0.0.0.0", "255.255.255.255", "224.0.0.1"))
            assertEquals(host, "unreadable", err("http://$host:7300/c$q"))
        for (host in listOf("10.0.0.1", "172.16.0.1", "172.31.255.254", "192.168.0.1", "169.254.3.4", "100.64.0.1", "100.127.255.254", "127.0.0.1"))
            assertEquals(host, host, ok("http://$host:7300/c$q").hosts[0])
        // 앞의 0, 8진수·16진수·정수 표기, 이름, IPv6, 사용자 정보로 호스트를 속이는 꼴
        for (host in listOf("010.0.0.1", "192.168.00.1", "192.168.1.01", "0x7f.0.0.1", "2130706433", "192.168.1", "192.168.1.1.1", "localhost", "evil.example", "[::1]", "192.168.0.1@8.8.8.8",
            "192.168.0.1:7300@evil.example", "192.168.0.1 ", "１９２.168.0.1", "192.168.0.256", "-1.0.0.1", "192.168.0.1%00"))
            assertEquals(host, "unreadable", err("http://$host:7300/c$q"))
        for (port in listOf("0", "65536", "-1", "", "7300x", "99999999999"))
            assertEquals(port, "unreadable", err("http://192.168.0.1:$port/c$q"))
        // 서버 표시와 토큰
        for (sid in listOf("", "GVRTEST", "GVRTEST22", "gvrtest2", "GVRTEST1", "GVRTES 2", "GVRTEST2%0A"))
            assertEquals(sid, "unreadable", err("http://192.168.0.1:7300/c#v=1&s=$sid&t=$T"))
        for (t in listOf("", T.dropLast(1), T + "A", T.dropLast(1) + "+", T.dropLast(1) + "/", T.dropLast(1) + "=", "A".repeat(100000)))
            assertEquals("unreadable", err("http://192.168.0.1:7300/c#v=1&s=GVRTEST2&t=$t"))
        // 버전
        assertEquals("update", err("http://192.168.0.1:7300/c#v=2&s=GVRTEST2&t=$T"))
        for (v in listOf("", "x", "1.0", "１"))
            assertEquals("unreadable", err("http://192.168.0.1:7300/c#v=$v&s=GVRTEST2&t=$T"))
        // 모양이 다른 것들
        for (s in listOf(null, "", "   ", "hello", "://", "http://", "http:///c#v=1", "javascript://192.168.0.1:7300/c$q", "intent://192.168.0.1:7300/c$q", "file://192.168.0.1:7300/c$q",
            "https://192.168.0.1:7300/c$q", "http://192.168.0.1:7300/c/../x$q", "http://192.168.0.1:7300//c$q", "http://192.168.0.1:7300/C$q", "WIFI:S:x;T:WPA;P:y;;", "\u0000\u0001\u0002"))
            assertTrue(s ?: "null", parse(s) is LinkResult.Error)
        // 아주 긴 입력에도 죽지 않습니다
        assertTrue(parse("http://192.168.0.1:7300/c#" + "a=1&".repeat(50000)) is LinkResult.Error)
        assertTrue(parse("http://" + "9".repeat(200000) + ":7300/c$q") is LinkResult.Error)
        assertTrue(parse("http://192.168.0.1:7300/c#v=1&s=GVRTEST2&t=$T&a=" + "10.0.0.1,".repeat(50000)) is LinkResult.Ok)
    }

    @Test fun extraAddressesAreFilteredAndCapped() {
        val l = ok("http://192.168.0.1:7300/c#v=1&s=GVRTEST2&t=$T&a=8.8.8.8,10.0.0.2,evil.example,010.0.0.3,192.168.0.1,10.0.0.4,10.0.0.5,10.0.0.6")
        assertEquals(listOf("192.168.0.1", "10.0.0.2", "10.0.0.4", "10.0.0.5"), l.hosts)
        // 같은 이름이 두 번 나오면 뒤의 것. http 는 ? 뒤를 읽지 않습니다(토큰이 서버 로그에 남는 꼴을 받지 않음)
        assertEquals("unreadable", err("http://192.168.0.1:7300/c?v=1&s=GVRTEST2&t=$T"))
        // 진단 값은 해석기가 extras 로만 넘깁니다(받을지는 호출한 쪽: MainActivity 는 adb 입구에서만 받음)
        val d = ok("seeplay-gvr://192.168.0.1:7300/c?v=1&s=GVRTEST2&t=$T&legacy=1&listen=7400&take=1")
        assertEquals("1", d.extras["legacy"]); assertEquals("7400", d.extras["listen"]); assertTrue(d.take)
        assertFalse(d.extras.containsKey("t")); assertFalse(d.extras.containsKey("s"))
    }

    @Test fun confirmUnlessSameServerSameTokenKnownHost() {
        fun link(sid: String = "GVRTEST2", token: String = T, host: String = "192.168.0.5", port: Int = 7300) = ServerLink(sid, token, port, listOf(host), "lan", true, emptyMap())
        val known = ServerLink("GVRTEST2", T, 7300, listOf("192.168.0.5", "10.0.0.7"), "lan", false, emptyMap())
        val other = "EBESExQVFhcYGRobHB0eHw"
        val ext = LinkGate.Source.EXTERNAL
        assertEquals(LinkGate.Decision.USE, LinkGate.decide(ext, null, link()))                                 // 처음 설치: 묻지 않음
        assertEquals(LinkGate.Decision.USE, LinkGate.decide(ext, known, link()))                                // 완전히 같은 서버
        assertEquals(LinkGate.Decision.USE, LinkGate.decide(ext, known, link(host = "10.0.0.7")))               // 기억한 다른 주소
        assertEquals(LinkGate.Decision.CONFIRM, LinkGate.decide(ext, known, link(host = "192.168.0.99")))       // sid·토큰은 같지만 모르는 주소
        assertEquals(LinkGate.Decision.CONFIRM, LinkGate.decide(ext, known, link(token = other)))               // sid 만 같음(리뷰 M5 의 3)
        assertEquals(LinkGate.Decision.CONFIRM, LinkGate.decide(ext, known, link(sid = "GVRTEST3")))
        assertEquals(LinkGate.Decision.CONFIRM, LinkGate.decide(ext, known, link(port = 7301)))
        assertEquals(LinkGate.Decision.USE, LinkGate.decide(ext, known, link(host = "127.0.0.1", port = 7311)))      // adb 터널: 같은 서버 표시 + 같은 토큰이면 묻지 않음
        assertEquals(LinkGate.Decision.CONFIRM, LinkGate.decide(ext, known, link(host = "127.0.0.1", token = other)))
        assertEquals(LinkGate.Decision.CONFIRM, LinkGate.decide(ext, ServerLink("GVRTEST2", "", 7300, listOf("192.168.0.5"), "lan", false, emptyMap()), link(token = "")))   // 토큰 없는 기억은 근거가 못 됨
        for (src in listOf(LinkGate.Source.SCANNER, LinkGate.Source.ADB))
            assertEquals(LinkGate.Decision.USE, LinkGate.decide(src, known, link(sid = "GVRTEST3", token = other, host = "192.168.0.99")))
    }

    @Test fun scannerDecodesGeneratedQr() {
        val text = "http://192.168.25.5:7300/c#v=1&s=GVRTEST2&t=$T&a=172.30.224.1"
        val size = 240; val stride = 256
        val m = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size)
        val luma = ByteArray(stride * size) { 0x55 }              // 행 간격의 남는 칸은 쓰레기 값
        for (y in 0 until size) for (x in 0 until size) luma[y * stride + x] = if (m.get(x, y)) 16 else 235.toByte()
        assertEquals(text, Scanner.decodeLuma(luma, stride, size, size))
        assertEquals("GVRTEST2", ok(Scanner.decodeLuma(luma, stride, size, size)!!).sid)
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
