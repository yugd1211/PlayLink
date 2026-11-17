package kr.co.seeplay.gvr.input

/**
 * 포인터 사건 → 0~1 좌표 → 입력 JSON(설계 3.6, 7.6). Android 클래스를 쓰지 않습니다(JVM 단위 시험).
 * UI 스레드에서만 부릅니다. 문자열은 StringBuilder 하나를 다시 쓰고 String.format 을 쓰지 않습니다(코드 리뷰 m16).
 */
class TouchEncoder(private val sink: Sink) {
    fun interface Sink { fun send(kind: Char, seq: Int, x: Float, y: Float, message: ByteArray) }

    companion object { const val MAX_POINTERS = 32 }

    private val sb = StringBuilder(64)
    private val down = BooleanArray(MAX_POINTERS)
    private val lastX = IntArray(MAX_POINTERS)       // 0..10000 (소수 4자리)
    private val lastY = IntArray(MAX_POINTERS)
    var seq = 0; private set
    /** 영상 영역(화면 좌표). 크기를 받기 전(0)에는 아무것도 보내지 않습니다(코드 리뷰 m2: 전에는 어떤 터치든 (1,1)로 잘렸음). */
    var left = 0f; var top = 0f; var width = 0f; var height = 0f

    val ready: Boolean get() = width > 0f && height > 0f

    /** 연결마다 입력 번호를 1부터 다시 시작합니다. 눌려 있던 포인터는 잊습니다(옛 연결의 것). */
    fun reset() { seq = 0; java.util.Arrays.fill(down, false) }

    private fun norm(v: Float, origin: Float, size: Float): Int {
        val n = (v - origin) / size
        return Math.round((if (n < 0f) 0f else if (n > 1f) 1f else n) * 10000f)
    }

    fun down(id: Int, x: Float, y: Float) {
        if (!ready || id !in 0 until MAX_POINTERS) return
        down[id] = true
        emit('d', id, norm(x, left, width), norm(y, top, height))
    }

    fun move(id: Int, x: Float, y: Float) {
        if (!ready || id !in 0 until MAX_POINTERS || !down[id]) return      // down 없이 온 move/up 은 서버가 버립니다
        val nx = norm(x, left, width); val ny = norm(y, top, height)
        if (nx == lastX[id] && ny == lastY[id]) return
        emit('m', id, nx, ny)
    }

    /** canceled = 시스템이 이 포인터를 취소함(손바닥 등, FLAG_CANCELED): u 가 아니라 c 로 보냅니다(코드 리뷰 m3). */
    fun up(id: Int, x: Float, y: Float, canceled: Boolean = false) {
        if (id !in 0 until MAX_POINTERS || !down[id]) return
        down[id] = false
        if (canceled || !ready) emit('c', id, lastX[id], lastY[id]) else emit('u', id, norm(x, left, width), norm(y, top, height))
    }

    /** 앱이 뒤로 가거나, 자리를 잃거나, 터치가 꺼질 때: 눌려 있던 포인터마다 c. */
    fun cancelAll() { for (id in down.indices) if (down[id]) { down[id] = false; emit('c', id, lastX[id], lastY[id]) } }

    private fun fixed4(v: Int) {
        sb.append(v / 10000).append('.')
        val f = v % 10000
        if (f < 1000) sb.append('0'); if (f < 100) sb.append('0'); if (f < 10) sb.append('0')
        sb.append(f)
    }

    private fun emit(kind: Char, id: Int, x: Int, y: Int) {
        lastX[id] = x; lastY[id] = y
        val s = ++seq
        sb.setLength(0)
        sb.append("{\"t\":\"").append(kind).append("\",\"id\":").append(id).append(",\"x\":")
        fixed4(x); sb.append(",\"y\":"); fixed4(y)
        sb.append(",\"s\":").append(s).append('}')
        val bytes = ByteArray(sb.length)
        for (i in bytes.indices) bytes[i] = sb[i].code.toByte()
        sink.send(kind, s, x / 10000f, y / 10000f, bytes)
    }
}
