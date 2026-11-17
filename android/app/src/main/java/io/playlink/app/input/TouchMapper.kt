package io.playlink.app.input

import android.os.Build
import android.view.MotionEvent
import io.playlink.app.diag.TraceRing

/**
 * MotionEvent 를 TouchEncoder 에 넘기는 얇은 층. UI 스레드에서만 부릅니다.
 * 좌표 변환과 문자열 조립은 TouchEncoder(순수), 소켓 쓰기는 playlink-input-tx 가 합니다.
 * sink 의 세 번째 값은 입력 세대(epoch)입니다: 연결이 새로 맺어지면 세션이 세대를 올리고, 세대가 다른 메시지는 세션이 버립니다
 * (코드 리뷰 m4: 옛 번호의 메시지가 새 연결로 나가 터치 → 표시 계산을 흐리던 것).
 */
class TouchMapper(private val trace: TraceRing, private val sink: (ByteArray, Boolean, Int) -> Unit) {
    private var eventNs = 0L
    private var epoch = 0
    val encoder = TouchEncoder { kind, seq, x, y, message ->
        trace.touch(kind, seq, eventNs, System.nanoTime(), x, y)
        sink(message, kind == 'm', epoch)
    }

    /** 꺼질 때(자리 잃음, 재접속, 대기) 눌려 있던 포인터를 취소로 보냅니다(코드 리뷰 m3). */
    var enabled = false
        set(value) { if (field && !value) cancelAll(); field = value }

    /** 연결이 새로 맺어짐: 번호를 1부터, 세대를 새 값으로. */
    fun newConnection(newEpoch: Int) { encoder.reset(); epoch = newEpoch }

    fun setVideoRect(left: Float, top: Float, width: Float, height: Float) { encoder.left = left; encoder.top = top; encoder.width = width; encoder.height = height }

    /** 소비하지 않고 관찰만 하는 쪽(HUD 제스처)과 함께 쓰므로 항상 true 를 돌려줍니다(뿌리 뷰가 이후 사건을 계속 받게). */
    fun onTouch(e: MotionEvent): Boolean {
        if (!enabled) return true
        eventNs = e.eventTime * 1_000_000L
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> { val i = e.actionIndex; encoder.down(e.getPointerId(i), e.getX(i), e.getY(i)) }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                val i = e.actionIndex
                val canceled = Build.VERSION.SDK_INT >= 33 && (e.flags and MotionEvent.FLAG_CANCELED) != 0
                encoder.up(e.getPointerId(i), e.getX(i), e.getY(i), canceled)
            }
            MotionEvent.ACTION_MOVE -> for (i in 0 until e.pointerCount) encoder.move(e.getPointerId(i), e.getX(i), e.getY(i))
            MotionEvent.ACTION_CANCEL -> encoder.cancelAll()
        }
        return true
    }

    fun cancelAll() { eventNs = System.nanoTime(); encoder.cancelAll() }
}
