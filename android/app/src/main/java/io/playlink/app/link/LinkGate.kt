package io.playlink.app.link

/**
 * 들어온 연결 정보(QR, 딥링크)를 바로 쓸지, 사람에게 물을지 정합니다(설계 4.2, 코드 리뷰 M5). 순수 함수(JVM 단위 시험).
 *
 * 출처는 셋입니다.
 *   SCANNER  : 앱 안 스캐너로 사람이 직접 찍음 → 묻지 않음
 *   ADB      : adb 만 통과하는 입구(DUMP 권한이 걸린 .Diag 별칭) → 묻지 않음
 *   EXTERNAL : 그 밖의 모든 인텐트(브라우저의 착륙 페이지, 메신저, 다른 QR 앱 …). BROWSABLE 여부와 상관없이 같은 취급
 * EXTERNAL 은 기억한 서버가 없으면(처음 설치) 묻지 않고(브리프 3.3 의 4), 있으면 "서버 표시도 토큰도 같고 주소가 기억한 주소 중 하나"일 때만
 * 묻지 않습니다. 서버 표시(sid)는 비밀이 아니라서 sid 만 같은 링크는 남이 만들 수 있기 때문입니다.
 * 주소가 루프백(127.x: adb 터널)이면 서버 표시와 토큰이 같을 때 묻지 않습니다: Unity 창의 [USB 로 연결]이 Wi-Fi 로 기억한 PC 에 대해
 * 매번 묻게 되는 것을 피하려는 것이고, 토큰이 같으면 상호 인증(서버 증명 검사)이 그대로 걸립니다.
 */
object LinkGate {
    enum class Source { SCANNER, ADB, EXTERNAL }
    enum class Decision { USE, CONFIRM }

    fun decide(source: Source, known: ServerLink?, link: ServerLink): Decision {
        if (source != Source.EXTERNAL) return Decision.USE
        if (known == null) return Decision.USE
        val same = known.sid == link.sid && known.token.isNotEmpty() && known.token == link.token &&
            link.hosts.isNotEmpty() && ((known.port == link.port && link.hosts[0] in known.hosts) || link.hosts[0].startsWith("127."))
        return if (same) Decision.USE else Decision.CONFIRM
    }
}
