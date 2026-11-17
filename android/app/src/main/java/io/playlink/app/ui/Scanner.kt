package io.playlink.app.ui

import android.util.Log
import android.util.Size
import android.view.ViewGroup
import android.widget.FrameLayout
import androidx.activity.ComponentActivity
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import java.util.concurrent.Executors

/**
 * 앱 안 QR 스캐너: CameraX 미리보기 + ZXing(core) 인식. 읽은 문자열을 그대로 넘깁니다(해석은 proto.Proto.parseQr).
 * 인식은 기기 안에서만 하고 네트워크를 쓰지 않습니다.
 */
class Scanner(private val activity: ComponentActivity, private val container: FrameLayout, private val onText: (String) -> Unit) {
    private val executor = Executors.newSingleThreadExecutor()
    private var provider: ProcessCameraProvider? = null
    private var preview: PreviewView? = null
    @Volatile private var active = false

    companion object {
        /** Y 평면(행 간격 포함)에서 QR 을 찾습니다. 없으면 null. JVM 에서도 돌 수 있게 Android 형식을 받지 않습니다. */
        fun decodeLuma(y: ByteArray, rowStride: Int, width: Int, height: Int): String? {
            val hints = mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE), DecodeHintType.TRY_HARDER to true)
            val source = PlanarYUVLuminanceSource(y, rowStride, height, 0, 0, width, height, false)
            return try { QRCodeReader().decode(BinaryBitmap(HybridBinarizer(source)), hints).text } catch (e: Exception) {
                try { QRCodeReader().decode(BinaryBitmap(HybridBinarizer(source.invert())), hints).text } catch (e2: Exception) { null }
            }
        }
    }

    fun start() {
        if (active) return
        active = true
        val view = PreviewView(activity)
        preview = view
        container.addView(view, 0, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        val future = ProcessCameraProvider.getInstance(activity)
        future.addListener({
            if (!active) return@addListener
            try {
                val p = future.get()
                provider = p
                val pv = Preview.Builder().build().also { it.surfaceProvider = view.surfaceProvider }
                val analysis = ImageAnalysis.Builder()
                    .setResolutionSelector(ResolutionSelector.Builder().setResolutionStrategy(ResolutionStrategy(Size(1280, 720), ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER)).build())
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
                analysis.setAnalyzer(executor) { image -> analyze(image) }
                p.unbindAll()
                p.bindToLifecycle(activity, CameraSelector.DEFAULT_BACK_CAMERA, pv, analysis)
            } catch (e: Exception) { Log.e("PlayLink", "camera start failed", e) }
        }, ContextCompat.getMainExecutor(activity))
    }

    private fun analyze(image: ImageProxy) {
        try {
            if (!active) return
            val plane = image.planes[0]
            val buf = plane.buffer
            val bytes = ByteArray(buf.remaining())
            buf.get(bytes)
            val text = decodeLuma(bytes, plane.rowStride, image.width, image.height)
            if (text != null && active) activity.runOnUiThread { if (active) onText(text) }
        } finally { image.close() }
    }

    fun stop() {
        if (!active) return
        active = false
        try { provider?.unbindAll() } catch (e: Exception) { }
        preview?.let { container.removeView(it) }
        preview = null
    }

    val isActive: Boolean get() = active

    /** Activity 가 사라질 때: 분석 스레드를 끝냅니다. */
    fun shutdown() { stop(); executor.shutdown() }
}
