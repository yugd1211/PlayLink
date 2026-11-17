package io.playlink.app.net

import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress

/** 망과 주소 고르기. 순수 함수(JVM 단위 시험). */
object NetPick {
    class Candidate(val wifi: Boolean, val vpn: Boolean, val notVpnCapability: Boolean)

    /**
     * 화면에 보여 줄 주소의 Wi-Fi 망 고르기. 고른 망의 목록 안 위치, 없으면 -1.
     * Wi-Fi 위에 올라간 VPN 망도 "Wi-Fi 전송"을 함께 가지므로, VPN 전송이 있거나 NOT_VPN 능력이 없는 망은 고르지 않습니다.
     */
    fun pickWifi(list: List<Candidate>): Int = list.indexOfFirst { it.wifi && !it.vpn && it.notVpnCapability }

    /**
     * 듣는 소켓은 모든 주소에 엽니다(Wi-Fi 주소가 바뀌어도, adb forward 의 루프백으로도 닿게). 대신 상대 주소가
     * 루프백 · 사설 · 링크 로컬 · 통신사 NAT(100.64/10) · IPv6 ULA 일 때만 받습니다: 모바일 데이터 쪽 공인 주소에서 온 것은 버립니다.
     */
    fun isLocalPeer(a: InetAddress?): Boolean {
        if (a == null) return false
        if (a.isLoopbackAddress || a.isSiteLocalAddress || a.isLinkLocalAddress) return true
        val b = a.address
        if (a is Inet4Address) return (b[0].toInt() and 0xFF) == 100 && (b[1].toInt() and 0xC0) == 0x40
        if (a is Inet6Address) return (b[0].toInt() and 0xFE) == 0xFC
        return false
    }
}
