package kr.co.seeplay.gvr.link

import android.content.Context
import java.security.SecureRandom

/** 기억한 서버(가장 최근 1개)와 이 설치의 기기 표시. SharedPreferences. 토큰은 앱 전용 저장소에만 둡니다. */
class ServerStore(context: Context) {
    private val prefs = context.getSharedPreferences("gvr", Context.MODE_PRIVATE)

    /** 설치마다 한 번 만든 [a-z0-9]{12}. 자리 배정에서 "같은 기기"를 알아보는 키. */
    val deviceId: String by lazy {
        prefs.getString("did", null) ?: run {
            val chars = "abcdefghijklmnopqrstuvwxyz0123456789"
            val r = SecureRandom()
            val id = String(CharArray(12) { chars[r.nextInt(chars.length)] })
            prefs.edit().putString("did", id).apply()
            id
        }
    }

    fun load(): ServerLink? {
        val sid = prefs.getString("sid", null) ?: return null
        val token = prefs.getString("token", "") ?: ""
        val hosts = (prefs.getString("hosts", "") ?: "").split(',').filter { it.isNotEmpty() }
        if (hosts.isEmpty()) return null
        return ServerLink(sid, token, prefs.getInt("port", 7300), hosts, prefs.getString("via", "lan") ?: "lan", false, emptyMap())
    }

    val serverName: String get() = prefs.getString("name", "") ?: ""

    /** 연결에 성공했을 때: 성공한 주소를 맨 앞으로(최대 4개). */
    fun remember(link: ServerLink, okHost: String, name: String) {
        val hosts = (listOf(okHost) + link.hosts).distinct().take(4)
        prefs.edit().putString("sid", link.sid).putString("token", link.token).putString("hosts", hosts.joinToString(","))
            .putInt("port", link.port).putString("via", link.via).putString("name", name).putLong("lastOk", System.currentTimeMillis()).apply()
    }

    /** 4403(토큰 폐기): 기억한 토큰을 지웁니다. */
    fun forget() { prefs.edit().remove("sid").remove("token").remove("hosts").remove("name").apply() }
}
