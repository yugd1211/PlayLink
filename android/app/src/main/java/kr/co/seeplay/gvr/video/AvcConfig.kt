package kr.co.seeplay.gvr.video

/** avcC → SPS/PPS(시작 코드 붙임), AVCC → Annex B 제자리 변환. 설계 3.4, 7.3. 소켓과 Android 를 모릅니다. */
class AvcConfig(val sps: ByteArray, val pps: ByteArray) {
    /** 00 00 00 01 SPS 00 00 00 01 PPS : 키프레임 앞에 붙이는 것 */
    val prefix: ByteArray = sps + pps

    override fun equals(other: Any?) = other is AvcConfig && sps.contentEquals(other.sps) && pps.contentEquals(other.pps)
    override fun hashCode() = sps.contentHashCode() * 31 + pps.contentHashCode()

    companion object {
        private val START = byteArrayOf(0, 0, 0, 1)

        /** data[offset, offset+length) 가 avcC 레코드. 해석하지 못하면 null. */
        fun parse(data: ByteArray, offset: Int, length: Int): AvcConfig? {
            val end = offset + length
            if (length < 7 || data[offset].toInt() != 1) return null
            if ((data[offset + 4].toInt() and 3) != 3) return null      // 길이 필드는 4바이트여야 함
            var p = offset + 5
            val spsCount = data[p].toInt() and 0x1F
            p++
            var sps: ByteArray? = null
            for (i in 0 until spsCount) {
                if (p + 2 > end) return null
                val n = ((data[p].toInt() and 0xFF) shl 8) or (data[p + 1].toInt() and 0xFF)
                p += 2
                if (n <= 0 || p + n > end) return null
                if (sps == null) sps = START + data.copyOfRange(p, p + n)
                p += n
            }
            if (p + 1 > end) return null
            val ppsCount = data[p].toInt() and 0xFF
            p++
            var pps: ByteArray? = null
            for (i in 0 until ppsCount) {
                if (p + 2 > end) return null
                val n = ((data[p].toInt() and 0xFF) shl 8) or (data[p + 1].toInt() and 0xFF)
                p += 2
                if (n <= 0 || p + n > end) return null
                if (pps == null) pps = START + data.copyOfRange(p, p + n)
                p += n
            }
            return if (sps != null && pps != null) AvcConfig(sps, pps) else null
        }

        /**
         * AVCC({u32 BE 길이, NAL} 반복)를 같은 자리에서 Annex B 로 바꿉니다(길이 4바이트 → 00 00 00 01).
         * 길이의 합이 내용 길이와 맞지 않으면 false(손상). 실패해도 이미 덮어쓴 앞부분은 되돌리지 않습니다(버릴 것이므로).
         */
        fun avccToAnnexBInPlace(data: ByteArray, offset: Int, length: Int): Boolean {
            var p = offset
            val end = offset + length
            if (length < 5) return false
            while (p < end) {
                if (p + 4 > end) return false
                val n = ((data[p].toInt() and 0xFF) shl 24) or ((data[p + 1].toInt() and 0xFF) shl 16) or
                    ((data[p + 2].toInt() and 0xFF) shl 8) or (data[p + 3].toInt() and 0xFF)
                if (n <= 0 || n > end - p - 4) return false
                data[p] = 0; data[p + 1] = 0; data[p + 2] = 0; data[p + 3] = 1
                p += 4 + n
            }
            return p == end
        }
    }
}
