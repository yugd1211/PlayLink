package io.playlink.app.net

import io.playlink.app.proto.Proto
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket

/**
 * 듣는 소켓 둘: TCP 7300(못 열면 7301~7309)과 UDP 7300(탐색 응답). Android 클래스를 쓰지 않습니다.
 * 스레드: playlink-accept(열기 + accept), playlink-udp(질문을 받는 즉시 그 주소와 포트로 답함).
 * 받은 TCP 연결은 onSocket 으로 넘기고(받은 쪽이 스레드를 만들어 핸드셰이크), UDP 질문은 onQuery 가 돌려준 바이트로 답합니다(null 이면 답하지 않음).
 */
class Listener(private val onSocket: (Socket) -> Unit, private val onQuery: (String) -> ByteArray?, private val onReady: () -> Unit) {
    /** 실제로 연 TCP 포트. 아직이거나 못 열었으면 0. */
    @Volatile var port = 0; private set
    @Volatile var udpOk = false; private set
    /** 열기에 실패했을 때의 문구. */
    @Volatile var error = ""; private set
    @Volatile private var stopped = false
    @Volatile private var server: ServerSocket? = null
    @Volatile private var udp: DatagramSocket? = null

    fun start(basePort: Int = Proto.DEFAULT_PORT) {
        Thread({ acceptLoop(basePort) }, "playlink-accept").start()
    }

    fun stop() {
        stopped = true
        try { server?.close() } catch (e: Exception) { }
        try { udp?.close() } catch (e: Exception) { }
    }

    private fun acceptLoop(basePort: Int) {
        var ss: ServerSocket? = null
        for (p in basePort..basePort + 9) {
            try { ss = ServerSocket().apply { reuseAddress = true; bind(InetSocketAddress(p), 16) }; break } catch (e: Exception) { ss = null }
        }
        if (ss == null) { error = "포트 $basePort~${basePort + 9} 를 열 수 없습니다."; onReady(); return }
        server = ss
        port = ss.localPort
        if (stopped) { stop(); return }
        Thread({ udpLoop(basePort) }, "playlink-udp").start()
        onReady()
        while (!stopped) {
            try {
                val s = ss.accept()
                if (NetPick.isLocalPeer(s.inetAddress)) onSocket(s) else s.close()
            } catch (e: Exception) { if (stopped || ss.isClosed) break }
        }
    }

    private fun udpLoop(udpPort: Int) {
        val socket = try { DatagramSocket(null).apply { reuseAddress = true; bind(InetSocketAddress(udpPort)) } } catch (e: Exception) { onReady(); return }
        udp = socket
        udpOk = true
        if (stopped) { socket.close(); return }
        onReady()
        val buf = ByteArray(1024)
        val packet = DatagramPacket(buf, buf.size)
        while (!stopped) {
            try {
                packet.setData(buf, 0, buf.size)
                socket.receive(packet)
                if (packet.length > 512 || !NetPick.isLocalPeer(packet.address)) continue
                val reply = onQuery(String(buf, 0, packet.length, Charsets.UTF_8)) ?: continue
                socket.send(DatagramPacket(reply, reply.size, packet.address, packet.port))
            } catch (e: Exception) { if (stopped || socket.isClosed) break }
        }
        udpOk = false
    }
}
