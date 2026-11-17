package io.playlink.app.net

/**
 * LAN 소켓을 묶을 Wi-Fi 망 고르기(PRD F-32, 코드 리뷰 M4). 순수 함수(JVM 단위 시험).
 * Wi-Fi 위에 올라간 VPN 망도 "Wi-Fi 전송"을 함께 가지므로, VPN 전송이 있거나 NOT_VPN 능력이 없는 망은 고르지 않습니다.
 * 인터넷 · 검증 능력은 요구하지 않습니다(인터넷 없는 공유기에서도 PC 에는 닿아야 함).
 */
object NetPick {
    class Candidate(val wifi: Boolean, val vpn: Boolean, val notVpnCapability: Boolean)

    /** 고른 망의 목록 안 위치. 없으면 -1. */
    fun pickWifi(list: List<Candidate>): Int = list.indexOfFirst { it.wifi && !it.vpn && it.notVpnCapability }
}
