package io.playlink.app

import io.playlink.app.net.WsClient
import io.playlink.app.proto.Caps
import io.playlink.app.proto.Ecdh
import io.playlink.app.proto.Proto
import io.playlink.app.proto.RecordReader
import io.playlink.app.proto.RecordWriter
import io.playlink.app.proto.Responder
import io.playlink.app.session.Receiver
import io.playlink.app.session.Rules
import io.playlink.app.session.Watch
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.*
import org.junit.Test
import java.io.BufferedInputStream
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.math.BigInteger
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.security.KeyFactory
import java.security.SecureRandom
import java.security.spec.ECPrivateKeySpec
import java.util.Collections
import java.util.concurrent.CopyOnWriteArrayList

/**
 * 연결 프로토콜 1: tools/proto-vectors.json 의 고정 값, JVM 안의 거는 쪽(PC 흉내)을 상대로 한 핸드셰이크, 레코드, 받는 규칙.
 * 거는 쪽은 tools/playlink-proto.js 의 initiate 를 옮긴 것입니다.
 */
class ProtoTest {
    private fun hex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }

    // ------------------------------------------------------------------ 고정 벡터 (tools/proto-vectors.json)

    private val pubI = "040217e617f0b6443928278f96999e69a23a4f2c152bdf6d6cdf66e5b80282d4ed194a7debcb97712d2dda3ca85aa8765a56f45fc758599652f2897c65306e5794"
    private val pubR = "04d65a93977caa3d1b081852ff57a79e465f1660577304baead505dd3a48589cf350185e895372df6221ea3a137557e473fddb6755f05bd507c3c533fce9c91285"
    private val z = "ccfc261f58193c98ca4ad4a53bbac6f0ee29bc4d48438090446908622ca79af6"
    private val th = "7e343fa5c420680f003e723354da0bc44330250a4f1e86824c893ff80d8988b1"
    private val pairKey = ByteArray(32) { 0x33 }

    private fun privateKey(fill: Int) = KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, ByteArray(32) { fill.toByte() }), Ecdh.params))

    @Test fun vectorEcdh() {
        assertEquals(z, hex(Ecdh.agree(privateKey(0x22), hex(pubI))))
        assertEquals(z, hex(Ecdh.agree(privateKey(0x11), hex(pubR))))
        assertEquals(pubI, hex(Ecdh.encode(Ecdh.decode(hex(pubI)))))
        val mine = Ecdh.generate()
        assertEquals(65, Ecdh.encode(mine.public).size)
        // 곡선 위에 없는 점, 길이가 다른 것, 압축 표기는 받지 않습니다
        val bad = hex(pubI); bad[64] = (bad[64].toInt() xor 1).toByte()
        for (p in listOf(bad, hex(pubI).copyOf(64), byteArrayOf(2) + hex(pubI).copyOfRange(1, 33), ByteArray(65).also { it[0] = 4 }.also { it[64] = 1 }))
            assertTrue(try { Ecdh.agree(mine.private, p); false } catch (e: Exception) { true })
    }

    @Test fun vectorTranscriptAndKeys() {
        val t = Proto.transcriptHash("{\"t\":\"hello\",\"v\":1,\"pc\":\"AAAAAAAAAAAAAAAA\"}".toByteArray(), "{\"t\":\"hello\",\"v\":1,\"ph\":\"BBBBBBBBBBBBBBBB\"}".toByteArray(), "{\"t\":\"key\"}".toByteArray())
        assertEquals(th, hex(t))
        val sas = Proto.deriveKeys(t, hex(z), Proto.psk("sas", null))
        assertEquals("ec202dd40f5a7a0a777c40323d2664e43b2ebbee0200a1a6623d70f86b1b429b", hex(sas.encI2R))
        assertEquals("488a841028146430412675d1d596422b219c763a6e288b742f858b1aa35b56f1", hex(sas.macI2R))
        assertEquals("c9a6b9a6223c63b83f878e3bd857e9374b63f537aae90984a786c7eee57b2f24", hex(sas.encR2I))
        assertEquals("2f7c170ed27b341fb941fbff9ae37d97bf91cd6d277b61bd14508e2ca6be1137", hex(sas.macR2I))
        assertEquals("0a89c58ae9eca7f3d5affaa2cf6d9fce54e10c73cf1d4bcd1dd2ecb7c342a944", hex(sas.pair))
        assertEquals("082694", sas.sas)
        val qr = Proto.deriveKeys(t, hex(z), Proto.psk("qr", ByteArray(16) { 0x44 }))
        assertEquals("525a23cb3ce00455c18b647b65b602daaa4c8cb26c826c33edbd5acfaff3783e", hex(qr.encI2R))
        assertEquals("d0dff2039c280bfeecad84ee03075067ccc8b96bf75853cffec2be58445de035", hex(qr.macR2I))
        assertEquals("c0d96073d294a86b1e18a370a93d66933010d90ffea3abdd71a81b199d877f7f", hex(qr.pair))
        assertEquals("125262", qr.sas)
        val paired = Proto.deriveKeys(t, hex(z), Proto.psk("paired", pairKey))
        assertEquals("faabd61a425586ebe5a62aa3bdcf33491aee9c993eb7abf9e744046ad9244172", hex(paired.encI2R))
        assertEquals("8e8f0aba48aaa6387c442214f082951548de289355c70350142bf873d8a2c3e7", hex(paired.macI2R))
        assertEquals("5ac60316862ebef9e50e5660c65725bb9925638591a2f0386458d0f8c4476df2", hex(paired.encR2I))
        assertEquals("97978ab1f5e836c5c9c8a02942755402c123684a84a6dd60d723f11dfc8aa1d6", hex(paired.macR2I))
        assertEquals("2f3f81b7515023e3124f327d5b7ab05f4c36945184232073bc0b5e0ccb0d05a6", hex(paired.pair))
        assertEquals("883657", paired.sas)
    }

    @Test fun vectorRecords() {
        val k = Proto.deriveKeys(hex(th), hex(z), pairKey)
        val plain = "PlayLink record test vector, 40 bytes..!".toByteArray()
        val seq0 = "0028f9fe917f6757ebe1074fd2879364ab9c62ccaac63991c832264a83e783d783996c7a2e230270158348247fa762ad58346aa541342dcbd973"
        val seq5 = "00281cf51ebeebcfdb06f110c839d865aae269c170fe46f958522216a35baf013143e04987967dce19058263b3fd3222b0a7699fbe4bc5b8deba"
        val out = ByteArrayOutputStream()
        val w = RecordWriter(out, k.encI2R, k.macI2R)
        w.write(plain)
        assertEquals(seq0, hex(out.toByteArray()))
        for (i in 1..4) w.write(byteArrayOf(i.toByte()))
        out.reset()
        w.write(plain)
        assertEquals(seq5, hex(out.toByteArray()))
        // 읽기: seq 0 의 레코드를 풀고, 같은 레코드를 seq 1 자리에서 다시 받으면(되풀이) 거절
        val r = RecordReader(ByteArrayInputStream(hex(seq0) + hex(seq0)), k.encI2R, k.macI2R)
        assertArrayEquals(plain, r.readRecord())
        assertTrue(try { r.readRecord(); false } catch (e: IOException) { e.message == "bad record tag" })
    }

    @Test fun recordsRejectTamperingAndCarryLargeWrites() {
        val enc = ByteArray(32) { 1 }; val mac = ByteArray(32) { 2 }
        val out = ByteArrayOutputStream()
        var writes = 0
        val counting = object : java.io.OutputStream() { override fun write(b: Int) = fail(); override fun write(b: ByteArray, off: Int, len: Int) { writes++; out.write(b, off, len) } }
        val big = ByteArray(100_000).also { SecureRandom().nextBytes(it) }
        RecordWriter(counting, enc, mac).write(big)
        assertEquals("one write per message", 1, writes)
        val wire = out.toByteArray()
        assertEquals(100_000 + 7 * 18, wire.size)                    // 16384 바이트씩 7개
        val got = ByteArray(big.size)
        val r = RecordReader(ByteArrayInputStream(wire), enc, mac)
        var n = 0
        while (n < got.size) n += r.read(got, n, got.size - n)
        assertArrayEquals(big, got)
        // 길이, 암호문, 태그의 어느 한 바이트를 바꿔도, 키가 달라도 거절
        fun fails(data: ByteArray, e: ByteArray = enc, m: ByteArray = mac) = try { RecordReader(ByteArrayInputStream(data), e, m).readRecord(); false } catch (x: IOException) { true }
        val one = ByteArrayOutputStream().also { RecordWriter(it, enc, mac).write("hello".toByteArray()) }.toByteArray()
        assertFalse(fails(one))
        for (i in one.indices) assertTrue("byte $i", fails(one.clone().also { it[i] = (it[i].toInt() xor 1).toByte() }))
        assertTrue(fails(one, m = ByteArray(32) { 3 }))
        assertTrue("empty record", fails(byteArrayOf(0, 0) + ByteArray(16)))
        assertTrue("truncated", fails(one.copyOf(one.size - 1)))
    }

    @Test fun vectorDiscoveryMacAndHelpers() {
        assertEquals("LfoKISQg8-O0--MsXKaefA", Proto.discoveryMac(pairKey, "AAAAAAAAAAAAAAAA", "BBBBBBBBBBBBBBBB", "Pixel 8", 7300, true, "AAECAwQFBgc"))
        assertNotEquals("LfoKISQg8-O0--MsXKaefA", Proto.discoveryMac(pairKey, "AAAAAAAAAAAAAAAA", "BBBBBBBBBBBBBBBB", "Pixel 8", 7300, false, "AAECAwQFBgc"))
        assertEquals("AAECAwQFBgc", Proto.b64(ByteArray(8) { it.toByte() }))
        assertArrayEquals(ByteArray(8) { it.toByte() }, Proto.unb64("AAECAwQFBgc"))
        assertNull(Proto.unb64("AAEC+w")); assertNull(Proto.unb64("AAEC="))
        assertEquals("Pixel 8 (Pro)_-.", Proto.cleanName("Pixel 8 (Pro)_-.\"\\\n<>|;"))
        assertEquals("갤럭시 S24", Proto.cleanName("갤럭시 S24"))
        assertEquals(32, Proto.cleanName("a".repeat(100)).length)
        assertTrue(Proto.isId(Proto.newId())); assertFalse(Proto.isId("AAAAAAAAAAAAAAA1")); assertFalse(Proto.isId("AAAA"))
    }

    @Test fun qrText() {
        val q = Proto.parseQr("playlink://pair?v=1&pc=ABCDEFGHIJKLMNOP&n=DEV%20PC%2B1%22&s=AAECAwQFBgcICQoLDA0ODw")!!
        assertEquals("ABCDEFGHIJKLMNOP", q.pc); assertEquals("DEV PC1", q.name); assertArrayEquals(ByteArray(16) { it.toByte() }, q.secret)
        for (bad in listOf(null, "", "hello", "http://192.168.0.5:7300/c#v=1&s=PLKTEST2&t=AAECAwQFBgcICQoLDA0ODw", "playlink://192.168.0.5:7300/c?v=1",
            "playlink://pair?v=2&pc=ABCDEFGHIJKLMNOP&s=AAECAwQFBgcICQoLDA0ODw", "playlink://pair?v=1&pc=abcdefghijklmnop&s=AAECAwQFBgcICQoLDA0ODw", "playlink://pair?v=1&pc=ABCDEFGHIJKLMNOP&s=AAEC",
            "playlink://pair?v=1&pc=ABCDEFGHIJKLMNOP&s=AAECAwQFBgcICQoLDA0OD+", "playlink://pair?v=1&pc=ABCDEFGHIJKLMNOP", "playlink://pair?v=1&pc=ABCDEFGHIJKLMNOP&n=%ZZ&s=AAECAwQFBgcICQoLDA0ODw",
            "playlink://pair?" + "a=1&".repeat(1000), "PLAYLINK://pair?v=1&pc=ABCDEFGHIJKLMNOP&s=AAECAwQFBgcICQoLDA0ODw"))
            assertNull(bad, Proto.parseQr(bad))
    }

    // ------------------------------------------------------------------ 받는 규칙 (PROTOCOL.md 5장) 의 계산

    @Test fun rules() {
        val A = "AAAAAAAAAAAAAAAA"; val B = "BBBBBBBBBBBBBBBB"
        assertEquals(Rules.Verdict.ACCEPT, Rules.paired(A, take = true, watching = null, wanted = false, blocked = false))      // 비어 있으면 묻지 않고
        assertEquals(Rules.Verdict.ACCEPT, Rules.paired(A, take = false, watching = null, wanted = false, blocked = false))     // 자동 재접속도
        assertEquals(Rules.Verdict.ACCEPT, Rules.paired(A, take = false, watching = A, wanted = false, blocked = false))        // 같은 PC: 채널 바꿔 끼우기
        assertEquals(Rules.Verdict.ASK, Rules.paired(B, take = true, watching = A, wanted = false, blocked = false))            // 다른 PC 를 보는 중: 묻기
        assertEquals(Rules.Verdict.BUSY, Rules.paired(B, take = false, watching = A, wanted = false, blocked = false))          // 다른 PC 의 자동 재접속: 묻지 않고 거절
        assertEquals(Rules.Verdict.ACCEPT, Rules.paired(B, take = true, watching = A, wanted = true, blocked = false))          // 폰에서 고른 PC
        assertEquals(Rules.Verdict.BUSY, Rules.paired(A, take = false, watching = null, wanted = false, blocked = true))        // 폰에서 끊은 PC 의 자동 재접속
        assertEquals(Rules.Verdict.ACCEPT, Rules.paired(A, take = true, watching = null, wanted = false, blocked = true))       // 사람이 다시 시작
        assertEquals(Rules.Verdict.ACCEPT, Rules.paired(A, take = false, watching = null, wanted = true, blocked = true))
        assertEquals(Rules.Verdict.ACCEPT, Rules.sas(standby = true, promptOpen = false))
        assertEquals(Rules.Verdict.BUSY, Rules.sas(standby = false, promptOpen = false))
        assertEquals(Rules.Verdict.BUSY, Rules.sas(standby = true, promptOpen = true))
    }

    // ------------------------------------------------------------------ 거는 쪽(PC 흉내)

    private class Refused(val code: String) : Exception(code)
    private class Dialed(val socket: Socket, val reader: RecordReader, val writer: RecordWriter, val keys: Proto.Keys, val fin: JSONObject, val phone: JSONObject)

    private class Pc(val id: String, val name: String, val port: Int) {
        val random = SecureRandom()
        fun open() = Socket(InetAddress.getLoopbackAddress(), port).apply { soTimeout = 5000; tcpNoDelay = true }

        fun who(nonce: String = "AAECAwQFBgc"): JSONObject = open().use { s ->
            Responder.sendPlain(s.getOutputStream(), JSONObject().put("t", "who").put("v", 1).put("pc", id).put("name", name).put("n", nonce).toString().toByteArray())
            JSONObject(String(Responder.readPlain(s.getInputStream())))
        }

        /** tools/playlink-proto.js 의 initiate. 거절되면 Refused(code), 폰이 fin 전에 끊으면 Refused("auth_failed"). */
        fun dial(ch: String, mode: String, secret: ByteArray?, take: Boolean = true, proto: Int = 2, confirm: Boolean = true, lieAboutKey: Boolean = false, sasSeen: (String) -> Unit = {}): Dialed {
            val s = open()
            try {
                val input = BufferedInputStream(s.getInputStream()); val out = s.getOutputStream()
                val eph = Ecdh.generate()
                val epk = Ecdh.encode(eph.public)
                val ni = ByteArray(16).also { random.nextBytes(it) }
                val m1 = JSONObject().put("t", "hello").put("v", 1).put("pc", id).put("name", name).put("ch", ch).put("mode", mode).put("take", take)
                    .put("ni", Proto.b64(ni)).put("commit", Proto.b64(Proto.sha256(epk, ni))).toString().toByteArray()
                Responder.sendPlain(out, m1)
                s.soTimeout = 40000
                val m2 = Responder.readPlain(input)
                s.soTimeout = 5000
                val h = JSONObject(String(m2))
                if (h.getString("t") == "err") throw Refused(h.getString("code"))
                val sent = if (lieAboutKey) Ecdh.encode(Ecdh.generate().public) else epk
                val m3 = ("{\"t\":\"key\",\"epk\":\"" + Proto.b64(sent) + "\"}").toByteArray()
                Responder.sendPlain(out, m3)
                val keys = Proto.deriveKeys(Proto.transcriptHash(m1, m2, m3), Ecdh.agree(eph.private, Proto.unb64(h.getString("epk"))!!), Proto.psk(mode, secret))
                val reader = RecordReader(input, keys.encR2I, keys.macR2I)
                val writer = RecordWriter(out, keys.encI2R, keys.macI2R)
                try {
                    if (mode == "sas") {
                        sasSeen(keys.sas)
                        val theirs = JSONObject(String(reader.readRecord()))
                        if (theirs.getString("t") != "ok") throw Refused(theirs.optString("code", "rejected"))
                        if (!confirm) { writer.writeText("{\"t\":\"err\",\"code\":\"rejected\"}"); throw Refused("rejected") }
                        writer.writeText("{\"t\":\"ok\"}")
                    }
                    writer.writeText("{\"t\":\"fin\",\"proto\":$proto,\"caps\":\"h264,seat,take\"}")
                    val fin = JSONObject(String(reader.readRecord()))
                    if (fin.getString("t") == "err") throw Refused(fin.getString("code"))
                    return Dialed(s, reader, writer, keys, fin, h)
                } catch (e: IOException) { throw Refused("auth_failed") }
            } catch (e: Exception) { s.close(); throw e }
        }

        fun refused(ch: String, mode: String, secret: ByteArray?, take: Boolean = true, proto: Int = 2, confirm: Boolean = true, lieAboutKey: Boolean = false): String =
            try { dial(ch, mode, secret, take, proto, confirm, lieAboutKey).socket.close(); "" } catch (e: Refused) { e.code }
    }

    // ------------------------------------------------------------------ 받는 쪽 전체(Receiver)를 루프백에서

    private class FakeWatch(override val pcId: String) : Watch {
        val attached = CopyOnWriteArrayList<Pair<String, WsClient>>()
        @Volatile var stopped = false
        override fun attach(channel: String, ws: WsClient, remote: InetAddress?, caps: Caps) { attached.add(Pair(channel, ws)); assertTrue(caps.has("take")) }
        override fun stop() { stopped = true; for (a in attached) a.second.abort() }
    }

    private class Phone : Receiver.Pairings, Receiver.Ui {
        override val phoneId = "PHONEPHONEPHONE2"
        override var phoneName = "Test Phone"
        val keys = Collections.synchronizedMap(HashMap<String, ByteArray>())
        val watches = CopyOnWriteArrayList<FakeWatch>()
        val prompts = CopyOnWriteArrayList<Receiver.Prompt>()
        val gone = CopyOnWriteArrayList<Receiver.Prompt>()
        val paired = CopyOnWriteArrayList<String>()
        /** 창이 뜨면 바로 누를 답. null 이면 누르지 않습니다. */
        @Volatile var answer: Boolean? = true
        override fun key(pcId: String) = keys[pcId]
        override fun save(pcId: String, name: String, key: ByteArray) { keys[pcId] = key }
        override fun onChanged() {}
        override fun onPrompt(prompt: Receiver.Prompt) { prompts.add(prompt); answer?.let { prompt.answer(it) } }
        override fun onPromptGone(prompt: Receiver.Prompt) { gone.add(prompt) }
        override fun onPaired(pcId: String, pcName: String) { paired.add(pcId) }
        override fun openWatch(pcId: String, pcName: String): Watch = FakeWatch(pcId).also { watches.add(it) }
    }

    private val phone = Phone()
    private var clock = 1_000_000L
    private val receiver = Receiver(phone, 4, phone, {}, { clock })
    private val port: Int by lazy {
        val base = ServerSocket(0).use { it.localPort }
        receiver.start(base)
        val until = System.currentTimeMillis() + 3000
        while (receiver.port == 0 && System.currentTimeMillis() < until) Thread.sleep(10)
        assertEquals(base, receiver.port)
        base
    }
    private val pcA by lazy { Pc("AAAAAAAAAAAAAAAA", "PC-A", port) }
    private val pcB by lazy { Pc("BBBBBBBBBBBBBBBB", "PC-B", port) }

    @After fun tearDown() { receiver.stop() }

    private fun waitFor(what: String, ok: () -> Boolean) { val until = System.currentTimeMillis() + 3000; while (!ok()) { if (System.currentTimeMillis() > until) fail(what); Thread.sleep(10) } }

    /** 응답의 증명을 PC 처럼 확인합니다(playlink-proto.js 의 checkReply). */
    private fun proof(r: JSONObject, pc: Pc, nonce: String, pairKey: ByteArray?, qrSecret: ByteArray?): String {
        if (r.optString("t") != "r" || r.optInt("v") != 1 || r.optString("n") != nonce) return "bad"
        if (!r.has("mac")) return "none"
        fun eq(key: ByteArray) = r.getString("mac") == Proto.discoveryMac(key, pc.id, r.getString("ph"), r.getString("name"), r.getInt("port"), r.getBoolean("want"), r.getString("n"))
        if (r.optBoolean("qr")) return if (qrSecret != null && eq(Proto.sha256(qrSecret))) "qr" else "bad"
        return if (pairKey != null && eq(pairKey)) "paired" else "bad"
    }

    @Test fun whoAndUdpDiscovery() {
        val r = pcA.who("n-1")
        assertEquals("none", proof(r, pcA, "n-1", null, null))
        assertEquals(phone.phoneId, r.getString("ph")); assertEquals("Test Phone", r.getString("name")); assertEquals(port, r.getInt("port"))
        assertEquals("", r.getString("busy")); assertFalse(r.getBoolean("want"))
        assertEquals(listOf("PC-A"), receiver.seen().map { it.name })
        clock += 6000
        assertTrue("seen entries expire", receiver.seen().isEmpty())
        // UDP: 질문을 보낸 주소와 포트로 바로 답합니다. 모르는 꼴의 질문에는 답하지 않습니다.
        waitFor("udp") { receiver.udpOk }
        DatagramSocket().use { u ->
            u.soTimeout = 300
            fun ask(text: String): JSONObject? {
                val b = text.toByteArray()
                u.send(DatagramPacket(b, b.size, InetAddress.getLoopbackAddress(), port))
                val p = DatagramPacket(ByteArray(1024), 1024)
                return try { u.receive(p); JSONObject(String(p.data, 0, p.length)) } catch (e: IOException) { null }
            }
            assertNull(ask("hello")); assertNull(ask("{\"t\":\"q\",\"v\":2,\"pc\":\"${pcB.id}\",\"n\":\"x\"}")); assertNull(ask("{\"t\":\"q\",\"v\":1,\"pc\":\"nope\",\"n\":\"x\"}"))
            assertNull(ask("{\"t\":\"q\",\"v\":1,\"pc\":\"${pcB.id}\",\"n\":\"x\",\"pad\":\"" + "x".repeat(600) + "\"}"))
            phone.keys[pcB.id] = pairKey
            receiver.pick(pcB.id)
            val a = ask("{\"t\":\"q\",\"v\":1,\"pc\":\"${pcB.id}\",\"name\":\"PC-B\",\"n\":\"AAECAwQFBgc\"}")!!
            assertEquals("paired", proof(a, pcB, "AAECAwQFBgc", pairKey, null))
            assertTrue("picked on the phone", a.getBoolean("want"))
            assertEquals("bad", proof(JSONObject(a.toString()).put("name", "Evil"), pcB, "AAECAwQFBgc", pairKey, null))
            clock += Receiver.WANT_MS + 1
            assertFalse("want expires after 30 s", ask("{\"t\":\"q\",\"v\":1,\"pc\":\"${pcB.id}\",\"name\":\"PC-B\",\"n\":\"n2\"}")!!.getBoolean("want"))
        }
        assertEquals(listOf("PC-B"), receiver.seen().map { it.name })
    }

    @Test fun sasPairingThenPairedChannels() {
        assertEquals("sas only on the standby screen", "busy", pcA.refused("pair", "sas", null))
        receiver.standby = true
        var shown = ""
        val d = pcA.dial("pair", "sas", null, sasSeen = { shown = it })
        d.socket.close()
        assertEquals(1, phone.prompts.size)
        assertTrue(phone.prompts[0].sas); assertEquals("PC-A", phone.prompts[0].pcName)
        assertEquals("both sides show the same digits", shown, phone.prompts[0].digits)
        assertTrue(Regex("^\\d{6}$").matches(shown))
        assertEquals(4, d.fin.getInt("app")); assertEquals(2, d.fin.getInt("proto")); assertEquals("h264,seat", d.fin.getString("caps"))
        waitFor("saved") { phone.paired.contains(pcA.id) }
        assertArrayEquals(d.keys.pair, phone.keys[pcA.id])
        waitFor("dialog closed") { phone.gone.size == 1 }
        assertEquals("paired", proof(pcA.who("n"), pcA, "n", d.keys.pair, null))

        // 페어링 키로 영상/입력 채널: 비어 있으면 묻지 않고 받고, 안에서는 WebSocket 프레임이 흐릅니다(폰 = 클라이언트 역할)
        val v = pcA.dial("video", "paired", d.keys.pair)
        val i = pcA.dial("input", "paired", d.keys.pair)
        waitFor("attached") { phone.watches.size == 1 && phone.watches[0].attached.size == 2 }
        assertEquals(1, phone.prompts.size)
        assertEquals(pcA.id, receiver.watching)
        assertEquals(pcA.id, pcB.who().getString("busy"))
        v.writer.write(byteArrayOf(0x81.toByte(), 5) + "hello".toByteArray())                 // 마스크 없는 텍스트 프레임
        val ws = phone.watches[0].attached.first { it.first == "video" }.second
        assertEquals(WsClient.OP_TEXT, ws.read()); assertEquals("hello", String(ws.payload, 0, ws.length))
        ws.sendText("a".toByteArray())
        val f = ByteArray(7); var n = 0; while (n < 7) n += v.reader.read(f, n, 7 - n)
        assertEquals(0x81.toByte(), f[0]); assertEquals(0x81.toByte(), f[1]); assertEquals('a'.code, f[6].toInt() xor f[2].toInt())
        // 키가 다르면 폰이 fin 전에 끊고(auth_failed), 약속(commit)과 다른 공개키를 내도 끊습니다
        assertEquals("auth_failed", pcA.refused("video", "paired", ByteArray(32) { 9 }))
        assertEquals("auth_failed", pcA.refused("video", "paired", d.keys.pair, lieAboutKey = true))
        assertEquals("proto", pcA.refused("video", "paired", d.keys.pair, proto = 3))
        assertEquals("bad_message", pcA.refused("pair", "paired", d.keys.pair))
        assertEquals("bad_message", pcA.refused("video", "sas", null))
        assertEquals(2, phone.watches[0].attached.size)
        v.socket.close(); i.socket.close()
    }

    @Test fun sasRejectedOnEitherSide() {
        receiver.standby = true
        phone.answer = false
        assertEquals("rejected", pcA.refused("pair", "sas", null))
        phone.answer = true
        assertEquals("rejected", pcA.refused("pair", "sas", null, confirm = false))
        waitFor("dialogs closed") { phone.gone.size == 2 }
        assertTrue(phone.keys.isEmpty()); assertTrue(phone.paired.isEmpty())
        // PC 가 숫자 창을 띄운 채 끊으면 폰의 창도 닫힙니다
        phone.answer = null
        val s = pcA.open()
        val eph = Ecdh.generate(); val epk = Ecdh.encode(eph.public); val ni = ByteArray(16)
        Responder.sendPlain(s.getOutputStream(), JSONObject().put("t", "hello").put("v", 1).put("pc", pcA.id).put("name", "PC-A").put("ch", "pair").put("mode", "sas").put("take", true)
            .put("ni", Proto.b64(ni)).put("commit", Proto.b64(Proto.sha256(epk, ni))).toString().toByteArray())
        Responder.readPlain(s.getInputStream())
        Responder.sendPlain(s.getOutputStream(), ("{\"t\":\"key\",\"epk\":\"" + Proto.b64(epk) + "\"}").toByteArray())
        waitFor("prompt") { phone.prompts.size == 3 }
        assertEquals("a second request while one is open", "busy", pcB.refused("pair", "sas", null))
        s.close()
        waitFor("dialog closed") { phone.gone.size == 3 }
        phone.prompts[2].answer(true)                                 // 닫힌 뒤에 눌러도 아무 일도 없습니다
        Thread.sleep(100)
        assertTrue(phone.keys.isEmpty())
    }

    @Test fun unknownPcAndVersion() {
        assertEquals("unknown_pc", pcA.refused("video", "paired", pairKey))
        assertEquals("unknown_pc", pcA.refused("pair", "qr", ByteArray(16)))
        val s = pcA.open()
        Responder.sendPlain(s.getOutputStream(), "{\"t\":\"hello\",\"v\":9,\"pc\":\"AAAAAAAAAAAAAAAA\"}".toByteArray())
        assertEquals("version", JSONObject(String(Responder.readPlain(s.getInputStream()))).getString("code"))
        s.close()
        assertTrue(phone.watches.isEmpty()); assertTrue(phone.prompts.isEmpty())
    }

    @Test fun qrPairing() {
        val secret = ByteArray(16) { (it + 1).toByte() }
        receiver.scanned(Proto.parseQr("playlink://pair?v=1&pc=${pcA.id}&n=PC-A&s=${Proto.b64(secret)}")!!)
        assertEquals(pcA.id, receiver.waitingFor()!!.first)
        val r = pcA.who("n")
        assertEquals("qr", proof(r, pcA, "n", null, secret)); assertTrue(r.getBoolean("want"))
        assertEquals("none", proof(pcB.who("n"), pcB, "n", null, secret))               // 다른 PC 에는 증명도 want 도 없음
        assertEquals("unknown_pc", pcB.refused("pair", "qr", secret))
        assertEquals("auth_failed", pcA.refused("pair", "qr", ByteArray(16) { 7 }))
        assertTrue(phone.keys.isEmpty())
        val d = pcA.dial("pair", "qr", secret)
        d.socket.close()
        waitFor("saved") { phone.paired.contains(pcA.id) }
        assertArrayEquals(d.keys.pair, phone.keys[pcA.id])
        assertTrue("no approval dialog for QR", phone.prompts.isEmpty())
        assertEquals("QR is used once", "unknown_pc", pcA.refused("pair", "qr", secret))
        assertEquals("paired", proof(pcA.who("n"), pcA, "n", d.keys.pair, null))
        // 찍은 지 2분이 지난 QR 은 쓰지 못합니다
        receiver.scanned(Proto.parseQr("playlink://pair?v=1&pc=${pcB.id}&n=PC-B&s=${Proto.b64(secret)}")!!)
        clock += Receiver.QR_MS + 1
        assertEquals("unknown_pc", pcB.refused("pair", "qr", secret))
        assertEquals("none", proof(pcB.who("n"), pcB, "n", null, secret))
    }

    @Test fun secondPcAsksToSwitchAndSamePcReplaces() {
        val keyA = ByteArray(32) { 1 }; val keyB = ByteArray(32) { 2 }
        phone.keys[pcA.id] = keyA; phone.keys[pcB.id] = keyB
        val a1 = pcA.dial("video", "paired", keyA)
        waitFor("A attached") { phone.watches.size == 1 && phone.watches[0].attached.size == 1 }
        // 같은 PC 가 같은 채널을 다시 걸면 같은 Watch 가 받습니다(이전 연결을 닫는 일은 Session.attach)
        val a2 = pcA.dial("video", "paired", keyA, take = false)
        waitFor("A again") { phone.watches[0].attached.size == 2 }
        assertEquals(1, phone.watches.size)
        // 다른 PC 의 자동 재접속(take=false)은 묻지 않고 busy
        assertEquals("busy", pcB.refused("video", "paired", keyB, take = false))
        assertTrue(phone.prompts.isEmpty())
        // 사람이 시작한 다른 PC: 묻고, [그대로 보기]면 busy
        phone.answer = false
        assertEquals("busy", pcB.refused("video", "paired", keyB))
        assertEquals(1, phone.prompts.size)
        assertFalse(phone.prompts[0].sas); assertEquals(pcA.id, phone.prompts[0].current); assertEquals("PC-B", phone.prompts[0].pcName)
        assertEquals("the answer holds for 10 s (the other channel does not ask again)", "busy", pcB.refused("input", "paired", keyB))
        assertEquals(1, phone.prompts.size)
        assertFalse(phone.watches[0].stopped)
        // [바꾸기]: 영상과 입력 두 채널이 창 하나로 들어오고, 이전 PC 의 연결은 닫힙니다
        clock += 11_000
        phone.answer = null
        val results = CopyOnWriteArrayList<String>()
        val t1 = Thread { results.add(pcB.refused("video", "paired", keyB)) }.apply { start() }
        val t2 = Thread { results.add(pcB.refused("input", "paired", keyB)) }.apply { start() }
        waitFor("prompt") { phone.prompts.size == 2 }
        Thread.sleep(150)
        assertEquals("one dialog for both channels", 2, phone.prompts.size)
        phone.prompts[1].answer(true)
        t1.join(5000); t2.join(5000)
        assertEquals(listOf("", ""), results.toList())
        waitFor("B attached") { phone.watches.size == 2 && phone.watches[1].attached.size == 2 }
        assertTrue(phone.watches[0].stopped)
        assertEquals(pcB.id, receiver.watching)
        assertEquals("the old PC's automatic reconnect is refused", "busy", pcA.refused("video", "paired", keyA, take = false))
        // 답이 없으면 28초 뒤 busy 이고 창이 닫힙니다(시험에서는 시계를 넘겨 둠)
        clock += 11_000
        val before = phone.gone.size
        val t3 = Thread { results.add(pcA.refused("video", "paired", keyA)) }.apply { start() }
        waitFor("prompt") { phone.prompts.size == 3 }
        clock += Receiver.SWITCH_ASK_MS
        // 기다리는 스레드는 실제 시간으로 기다리므로, 창을 닫는 것과 같은 길(답 false)로 끝냅니다
        phone.prompts[2].answer(false)
        t3.join(5000)
        assertEquals("busy", results.last())
        waitFor("dialog closed") { phone.gone.size == before + 1 }
        a1.socket.close(); a2.socket.close()
    }

    @Test fun disconnectBlocksAutomaticReconnectAndPickAllowsSwitch() {
        val keyA = ByteArray(32) { 1 }; val keyB = ByteArray(32) { 2 }
        phone.keys[pcA.id] = keyA; phone.keys[pcB.id] = keyB
        pcA.dial("video", "paired", keyA)
        waitFor("A attached") { phone.watches.size == 1 }
        receiver.disconnect()                                          // [연결 끊기]
        assertTrue(phone.watches[0].stopped); assertNull(receiver.watching)
        assertEquals("busy", pcA.refused("video", "paired", keyA, take = false))
        assertEquals("another PC is not affected", "", pcB.refused("video", "paired", keyB, take = false))
        waitFor("B attached") { phone.watches.size == 2 }
        // 폰에서 A 를 고르면: want 로 답하고, B 를 보는 중이어도 묻지 않고 A 로 바꿉니다
        receiver.pick(pcA.id)
        assertTrue(pcA.who().getBoolean("want")); assertFalse(pcB.who().getBoolean("want"))
        assertEquals("", pcA.refused("video", "paired", keyA, take = false))
        waitFor("A attached") { phone.watches.size == 3 }
        assertTrue(phone.watches[1].stopped); assertTrue(phone.prompts.isEmpty())
        assertFalse("want ends once connected", pcA.who().getBoolean("want"))
        // 세션이 스스로 끝나면(PC 가 끊음) 비어 있는 상태로 돌아가 자동 재접속을 받습니다
        receiver.ended(phone.watches[2])
        assertNull(receiver.watching)
        assertEquals("", pcA.refused("video", "paired", keyA, take = false))
    }
}
