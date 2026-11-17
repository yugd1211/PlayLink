package kr.co.seeplay.gvr.video

/**
 * /video 바이너리 메시지의 28바이트 헤더(리틀 엔디언, 프레임 프로토콜 2). 설계 3.4.
 * 주의: 설정(avcC) 메시지의 captureTime 은 캡처 시각이 아니라 서버가 헤더를 쓴 시각입니다
 * (GameViewRemoteCapture.cs). 표시 일정 계산에 넣지 않습니다.
 */
object FrameHeader {
    const val BYTES = 28
    const val PROTOCOL = 2

    const val FLAG_REPEAT = 1      // 같은 화면 재전송(JPEG)
    const val FLAG_H264 = 2
    const val FLAG_KEY = 4
    const val FLAG_CONFIG = 8      // avcC, 프레임 아님
    const val FLAG_EXT_MASK = 0xFF00   // 비트 8~15 중 하나라도 서 있으면 영상이 아닌 확장 메시지(R4): 확인만 보내고 버림

    fun u32(b: ByteArray, o: Int): Long =
        (b[o].toLong() and 0xFF) or ((b[o + 1].toLong() and 0xFF) shl 8) or
            ((b[o + 2].toLong() and 0xFF) shl 16) or ((b[o + 3].toLong() and 0xFF) shl 24)

    fun u16(b: ByteArray, o: Int): Int = (b[o].toInt() and 0xFF) or ((b[o + 1].toInt() and 0xFF) shl 8)

    fun frameId(b: ByteArray): Long = u32(b, 0)
    fun width(b: ByteArray): Int = u16(b, 4)
    fun height(b: ByteArray): Int = u16(b, 6)
    fun inputSeq(b: ByteArray): Int = u32(b, 8).toInt()
    fun encodeUs(b: ByteArray): Long = u32(b, 12)
    fun captureToEncodedUs(b: ByteArray): Long = u32(b, 16)
    fun flags(b: ByteArray): Int = u32(b, 20).toInt()
    /** 0.1ms 단위, 서버 Start 뒤 흐른 시간 */
    fun captureTime(b: ByteArray): Long = u32(b, 24)

    fun isExtension(flags: Int) = (flags and FLAG_EXT_MASK) != 0
    fun isH264(flags: Int) = (flags and FLAG_H264) != 0
    fun isConfig(flags: Int) = (flags and (FLAG_H264 or FLAG_CONFIG)) == (FLAG_H264 or FLAG_CONFIG)
    fun isKey(flags: Int) = (flags and FLAG_KEY) != 0
}
