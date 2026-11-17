package io.playlink.app.pair

import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Log
import io.playlink.app.proto.Proto
import io.playlink.app.session.Receiver
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * 이 설치의 phoneId · 폰 이름과 페어링한 PC 목록(pcId, 라벨, 페어링 키). SharedPreferences(앱 전용 저장소).
 * 페어링 키는 Android Keystore 의 AES 키(기기 밖으로 나오지 않음)로 AES-GCM 암호화해서 둡니다. 키는 로그에 적지 않습니다.
 * 여러 스레드가 읽습니다(탐색 응답, 핸드셰이크, UI): 메모리의 목록은 잠금으로 지킵니다.
 */
class PairStore(context: Context) : Receiver.Pairings {
    class Pc(val id: String, val name: String)
    private class Entry(val id: String, var name: String, val key: ByteArray, val sealed: String)

    private val prefs = context.getSharedPreferences("playlink", Context.MODE_PRIVATE)
    private val pcs = LinkedHashMap<String, Entry>()

    override val phoneId: String = prefs.getString("phoneId", null)?.takeIf { Proto.isId(it) } ?: Proto.newId().also { prefs.edit().putString("phoneId", it).apply() }

    /** 사람에게 보이는 폰 이름. 기본은 기종 이름. */
    @Volatile override var phoneName: String = prefs.getString("label", null)?.let { Proto.cleanName(it) }?.takeIf { it.isNotBlank() } ?: defaultName()
        set(value) { field = Proto.cleanName(value).trim().ifEmpty { defaultName() }; prefs.edit().putString("label", field).apply() }

    /** 대기 화면에서도 화면을 켜 둠(앱은 화면이 켜져 앞에 있을 때만 연결을 받습니다). */
    var keepScreenOn: Boolean
        get() = prefs.getBoolean("keepOn", true)
        set(value) { prefs.edit().putBoolean("keepOn", value).apply() }

    init {
        // 예전 구조(폰이 PC 로 접속)의 기억은 쓰지 않습니다.
        if (prefs.contains("token") || prefs.contains("sid")) prefs.edit().remove("did").remove("sid").remove("token").remove("hosts").remove("port").remove("via").remove("name").remove("lastOk").apply()
        try {
            val list = JSONArray(prefs.getString("pcs", "[]"))
            for (i in 0 until list.length()) {
                val o = list.getJSONObject(i)
                val id = o.optString("id")
                val sealed = o.optString("k")
                val key = try { open(sealed) } catch (e: Exception) { null }
                if (Proto.isId(id) && key != null && key.size == 32) pcs[id] = Entry(id, Proto.cleanName(o.optString("name")), key, sealed)
                else Log.w("PlayLink", "pairing dropped (cannot read its key)")
            }
        } catch (e: Exception) { Log.w("PlayLink", "pairing list unreadable") }
    }

    private fun defaultName() = Proto.cleanName(Build.MODEL).trim().ifEmpty { "Phone" }

    fun list(): List<Pc> = synchronized(pcs) { pcs.values.map { Pc(it.id, it.name) } }
    fun label(pcId: String): String? = synchronized(pcs) { pcs[pcId]?.name }
    override fun key(pcId: String): ByteArray? = synchronized(pcs) { pcs[pcId]?.key }

    /** 페어링이 끝남. 이미 있는 PC 면 키만 바꾸고 사람이 붙인 라벨은 둡니다. */
    override fun save(pcId: String, name: String, key: ByteArray) {
        val sealed = seal(key)
        synchronized(pcs) {
            val label = pcs[pcId]?.name ?: Proto.cleanName(name).ifBlank { "PC" }
            pcs[pcId] = Entry(pcId, label, key.copyOf(), sealed)
            persist()
        }
    }

    fun rename(pcId: String, name: String) { synchronized(pcs) { val e = pcs[pcId] ?: return; e.name = Proto.cleanName(name).trim().ifEmpty { e.name }; persist() } }
    fun remove(pcId: String) { synchronized(pcs) { pcs.remove(pcId); persist() } }
    fun removeAll() { synchronized(pcs) { pcs.clear(); persist() } }

    private fun persist() {
        val list = JSONArray()
        for (e in pcs.values) list.put(JSONObject().put("id", e.id).put("name", e.name).put("k", e.sealed))
        prefs.edit().putString("pcs", list.toString()).apply()
    }

    // ------------------------------------------------------------------ Keystore

    private fun wrapKey(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val g = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        g.init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        return g.generateKey()
    }

    private fun seal(plain: ByteArray): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, wrapKey())
        return Proto.b64(c.iv + c.doFinal(plain))
    }

    private fun open(text: String): ByteArray? {
        val b = Proto.unb64(text) ?: return null
        if (b.size < 12 + 16) return null
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, wrapKey(), GCMParameterSpec(128, b, 0, 12))
        return c.doFinal(b, 12, b.size - 12)
    }

    private companion object { const val ALIAS = "playlink.pair" }
}
