package io.playlink.app

import android.Manifest
import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.hardware.display.DisplayManager
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.util.TypedValue
import android.view.Gravity
import android.view.MotionEvent
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import io.playlink.app.diag.Counters
import io.playlink.app.diag.Exporter
import io.playlink.app.diag.Settings
import io.playlink.app.diag.Snapshot
import io.playlink.app.diag.TraceRing
import io.playlink.app.input.TouchMapper
import io.playlink.app.link.LinkGate
import io.playlink.app.link.LinkParser
import io.playlink.app.link.LinkResult
import io.playlink.app.link.ServerLink
import io.playlink.app.link.ServerStore
import io.playlink.app.session.Session
import io.playlink.app.session.State
import io.playlink.app.ui.Scanner
import io.playlink.app.video.Decoder
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.atomic.AtomicInteger

/**
 * 단일 Activity. 화면(영상 Surface, 상태 문구, HUD, 스캐너), 수명 주기, 인텐트(딥링크, adb 명령)만 다룹니다.
 * 네트워크, 디코더, 파일을 UI 스레드에서 직접 부르지 않습니다(예외: Surface 가 사라질 때 디코더 해제를 기다림).
 */
class MainActivity : ComponentActivity(), Session.Ui {
    companion object { const val DIAG_ALIAS = "io.playlink.app.Diag" }
    private val tag = "PlayLink"
    private val ui = Handler(Looper.getMainLooper())
    private val settings = Settings()
    private val counters = Counters()
    private val trace = TraceRing()
    private val genSource = AtomicInteger()
    private lateinit var store: ServerStore
    private lateinit var decoder: Decoder
    private lateinit var touch: TouchMapper
    private lateinit var root: FrameLayout
    private lateinit var surfaceView: SurfaceView
    private lateinit var panel: LinearLayout
    private lateinit var statusText: TextView
    private lateinit var scanButton: Button
    private lateinit var takeButton: Button
    private lateinit var staleText: TextView
    private lateinit var hud: TextView
    private var scanner: Scanner? = null
    @Volatile private var session: Session? = null      // 디코더 스레드도 읽습니다
    private var link: ServerLink? = null
    private var pendingTake = true
    private var started = false
    private var surface: Surface? = null
    private var videoW = 0
    private var videoH = 0
    private var state = State.STOPPED
    private var lastBackMs = 0L
    private var wifiLock: WifiManager.WifiLock? = null
    private var hudOn = false
    private var recStartNs = 0L
    private var recStartHead = 0L
    private var recName = ""
    private var recAutoSec = 0
    private var lastHz = 0f
    private var sourceFps = 60
    private var pendingError: String? = null
    private var hintedUpdate = false
    private var displayListener: DisplayManager.DisplayListener? = null
    private val screenOff = Runnable { if (state != State.PLAYING) window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) }
    private val hudGesture = Runnable { setHud(!hudOn) }
    private val recGesture = Runnable { if (hudOn) { if (recStartNs == 0L) startRecording() else stopRecording() } }
    private var lastRecv = 0L; private var lastDecoded = 0L; private var lastReleased = 0L; private var lastSkipped = 0L; private var lastHudNs = 0L

    // ------------------------------------------------------------------ 수명 주기

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = ServerStore(this)
        decoder = Decoder(trace, counters, settings, object : Decoder.Host {
            override fun requestKey(reason: String) { session?.requestKey(reason) }
            override fun onFirstFrameReleased() { session?.onFirstFrameReleased() }
            override fun onSourceFps(fps: Int) { ui.post { if (fps != sourceFps) { sourceFps = fps; applyRefreshPolicy() } } }
        })
        touch = TouchMapper(trace) { bytes, isMove, epoch -> session?.sendPointer(bytes, isMove, epoch) }
        buildViews()
        goFullscreen()
        val dl = object : DisplayManager.DisplayListener {
            override fun onDisplayAdded(displayId: Int) {}
            override fun onDisplayRemoved(displayId: Int) {}
            override fun onDisplayChanged(displayId: Int) { noteRefreshRate() }
        }
        displayListener = dl
        (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).registerDisplayListener(dl, ui)
        noteRefreshRate()
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (scanner?.isActive == true) { stopScanner(); if (link != null) connect() else showPanel("Unity 창(Tools > PlayLink)의 QR 을 찍으세요."); return }     // 스캐너를 열 때 끊었으므로 다시 연결(리뷰 m7)
                val now = System.currentTimeMillis()
                if (state == State.PLAYING && now - lastBackMs > 2000) { lastBackMs = now; Toast.makeText(this@MainActivity, "한 번 더 누르면 나갑니다", Toast.LENGTH_SHORT).show() }
                else finish()
            }
        })
        // 다시 만들어진 Activity(회전, 프로세스 복원)나 최근 앱 목록에서 온 실행은 옛 인텐트를 다시 처리하지 않습니다(리뷰 m6).
        if (savedInstanceState == null && (intent.flags and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY) == 0) handleIntent(intent)
        setIntent(Intent(this, MainActivity::class.java))
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
        setIntent(Intent(this, MainActivity::class.java))      // 토큰이 든 인텐트를 쥐고 있지 않습니다
    }

    override fun onStart() {
        super.onStart()
        started = true
        if (link == null) { link = store.load(); pendingTake = true }      // 앱 아이콘으로 실행: 가장 최근 서버, take=1
        connect()
    }

    override fun onStop() {
        super.onStop()
        started = false
        touch.cancelAll()                // 눌려 있는 포인터마다 c
        dropSession()                    // 뒤에서 연결을 유지하지 않습니다(00 문서 3.3 #5)
        ui.removeCallbacks(screenOff); ui.removeCallbacks(hudGesture); ui.removeCallbacks(recGesture)
        stopScanner()
        releaseWifiLock()
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        ui.removeCallbacks(hudTick)
        pendingTake = false              // 앞으로 돌아오면 자동 재접속(take=0)
    }

    override fun onDestroy() {
        super.onDestroy()
        displayListener?.let { (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).unregisterDisplayListener(it) }
        scanner?.shutdown()
        decoder.shutdown()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) { super.onWindowFocusChanged(hasFocus); if (hasFocus) goFullscreen() }

    // ------------------------------------------------------------------ 인텐트: 딥링크, adb 명령

    private fun handleIntent(intent: Intent?) {
        if (intent == null) return
        // 진단 값과 명령은 진단 빌드에서, adb 만 통과하는 입구(.Diag 별칭, DUMP 권한)로 온 인텐트일 때만 받습니다(코드 리뷰 M5).
        // 그 밖의 인텐트는 브라우저가 열었든 다른 앱이 직접 열었든 똑같이 "밖에서 온 것"입니다.
        // adb 입구 자체(토큰을 --es t 로 받기, 확인 창 없이 연결)는 release 빌드에서도 받습니다: Unity 창의 [USB 로 연결]이 이 입구를 씁니다(08_interop_notes U-4).
        val adb = intent.component?.className == DIAG_ALIAS
        val diag = BuildConfig.DIAG && adb
        val cmd = intent.getStringExtra("cmd")
        if (cmd != null) { if (diag) command(cmd, intent); return }
        if (intent.action != Intent.ACTION_VIEW || intent.data == null) return
        var url = intent.dataString ?: return
        // 토큰이 logcat 의 액티비티 시작 줄에 남지 않게 adb 는 --es t <토큰> 으로 넘길 수 있습니다(리뷰 m3).
        if (adb) intent.getStringExtra("t")?.let { t -> if (!url.contains("t=")) url += (if (url.contains('?')) "&" else "?") + "t=" + t }
        when (val r = LinkParser.parse(url, BuildConfig.SCHEME)) {
            // 읽지 못한 링크는 지금 연결을 건드리지 않습니다(리뷰 m5). 연결이 없으면 상태 문구 자리에 남깁니다.
            is LinkResult.Error -> { Toast.makeText(this, r.message, Toast.LENGTH_LONG).show(); if (link == null) { pendingError = r.message; if (started) showPanel(r.message) } }
            is LinkResult.Ok -> {
                if (diag) applyExtras(r.link.extras)
                val l = if (diag) r.link else r.link.copy(extras = emptyMap())
                val known = store.load()
                if (LinkGate.decide(if (adb) LinkGate.Source.ADB else LinkGate.Source.EXTERNAL, known, l) == LinkGate.Decision.CONFIRM && known != null) {
                    AlertDialog.Builder(this).setMessage("PC(${l.hosts[0]})에 연결할까요?\n지금 기억한 PC: ${store.serverName.ifEmpty { known.hosts[0] }}\n\n직접 찍은 QR 이 아니면 취소하세요.")
                        .setPositiveButton("연결") { _, _ -> useLink(l) }.setNegativeButton("취소", null).show()
                } else useLink(l)
            }
        }
    }

    private fun useLink(l: ServerLink) {
        stopScanner()
        link = l
        pendingTake = true
        pendingError = null
        if (started) connect()
    }

    private fun applyExtras(x: Map<String, String>) {
        x["pace"]?.let { if (it == "now" || it == "sched") settings.pace = it }
        x["buf"]?.let { settings.buf = it }
        x["rr"]?.let { settings.rr = it; applyRefreshPolicy() }
        x["wifilock"]?.let { settings.wifiLock = it != "0" }
        x["recreate"]?.let { settings.recreate = it == "1" }
        x["legacy"]?.let { settings.legacy = it == "1" }
        x["listen"]?.let { settings.listenPort = it.toIntOrNull() ?: 0 }
        x["hold"]?.let { settings.holdLimit = (it.toIntOrNull() ?: 6).coerceIn(1, 12) }
        x["target"]?.let { if (it == "pref" || it == "early") settings.target = it }
        x["pct"]?.let { settings.percentile = ((it.toIntOrNull() ?: 95).coerceIn(50, 100)) / 100.0 }
        x["decoder"]?.let { settings.decoderName = it }
        x["hud"]?.let { setHud(it == "1") }
        x["rec"]?.let { recAutoSec = it.toIntOrNull() ?: 0 }
        x["name"]?.let { recName = it.filter { c -> c.isLetterOrDigit() || c == '-' || c == '_' } }
    }

    private fun command(cmd: String, intent: Intent) {
        val x = HashMap<String, String>()
        for (k in listOf("pace", "buf", "rr", "wifilock", "recreate", "hold", "target", "pct", "hud", "name")) intent.getStringExtra(k)?.let { x[k] = it }
        applyExtras(x)
        when (cmd) {
            "rec_start" -> startRecording()
            "rec_stop" -> stopRecording()
            "hud_on" -> setHud(true)
            "hud_off" -> setHud(false)
            "forget" -> { store.forget(); link = null; dropSession(); showPanel("기억한 PC를 지웠습니다.") }
            "scan" -> startScanner()
        }
    }

    // ------------------------------------------------------------------ 연결

    /** 지금 세션을 멈추고 디코더의 주인 자리를 비웁니다. 이 뒤로 옛 세션의 콜백과 프레임은 버려집니다(코드 리뷰 M2). */
    private fun dropSession() {
        val old = session ?: return
        session = null
        old.stop()
        decoder.attach(null)
        touch.enabled = false
    }

    private fun connect() {
        dropSession()
        val l = link
        if (l == null) {
            showPanel(pendingError ?: "Unity 창(Tools > PlayLink)의 QR 을 찍으세요.")
            if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) startScanner()
            return
        }
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val s = Session(this, l, pendingTake, settings, store, decoder, trace, counters, genSource, this)
        session = s
        decoder.attach(s)
        hintedUpdate = false
        s.start()
        if (hudOn) { ui.removeCallbacks(hudTick); ui.post(hudTick) }
    }

    override fun onConnected(session: Session, epoch: Int) { ui.post { if (session === this.session) touch.newConnection(epoch) } }

    override fun onState(session: Session, state: State, message: String) {
        ui.post {
            if (session !== this.session) return@post       // 멈춘 옛 세션이 늦게 보낸 상태는 버립니다
            this.state = state
            ui.removeCallbacks(screenOff)
            if (state == State.RECONNECT || state == State.LOBBY || state == State.STANDBY) ui.postDelayed(screenOff, 60_000)      // 1분 넘게 화면이 없으면 화면 켜 두기를 풉니다(리뷰 m8)
            else if (state == State.WAITING || state == State.PLAYING) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            touch.enabled = state == State.PLAYING || state == State.WAITING || state == State.NOCODEC
            takeButton.visibility = if (state == State.STANDBY && session.canTake) View.VISIBLE else View.GONE
            when (state) {
                State.PLAYING -> { panel.visibility = View.GONE; acquireWifiLock()
                    if (!hintedUpdate && session.info.serverApp > BuildConfig.VERSION_CODE) { hintedUpdate = true; Toast.makeText(this, "PC 에 더 새 앱이 있습니다. Unity 창에서 앱을 다시 설치하세요.", Toast.LENGTH_LONG).show() }
                    if (recAutoSec > 0) { val sec = recAutoSec; recAutoSec = 0; startRecording(); ui.postDelayed({ stopRecording() }, sec * 1000L) } }
                State.DIALING -> showPanel("연결 중… $message")
                State.HANDSHAKING -> showPanel("연결 중… $message")
                State.WAITING -> showPanel("연결됨. 화면을 기다리는 중")
                State.NOCODEC, State.STANDBY, State.LOBBY, State.RECONNECT -> showPanel(message)
                State.FAILED_TOKEN, State.FAILED_VERSION, State.FAILED_SERVER -> { showPanel(message); window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) }
                State.STOPPED -> {}
            }
            if (state != State.PLAYING) releaseWifiLock()
            Log.i(tag, "state $state $message")
        }
    }

    override fun onVideoSize(session: Session, width: Int, height: Int) { ui.post { if (session === this.session) { videoW = width; videoH = height; layoutVideo() } } }
    override fun onStale(session: Session, message: String?) { ui.post { if (session === this.session) { staleText.text = message ?: ""; staleText.visibility = if (message == null) View.GONE else View.VISIBLE } } }

    private fun acquireWifiLock() {
        if (!settings.wifiLock || wifiLock != null || session?.info?.loopback == true || Build.VERSION.SDK_INT < 29) return
        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "playlink").apply { setReferenceCounted(false); acquire() }
        } catch (e: Exception) { Log.w(tag, "wifi lock failed", e) }
    }

    private fun releaseWifiLock() { try { wifiLock?.release() } catch (e: Exception) { }; wifiLock = null }

    // ------------------------------------------------------------------ 화면

    private fun dp(v: Float) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, resources.displayMetrics).toInt()

    private fun buildViews() {
        root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        surfaceView = SurfaceView(this)
        root.addView(surfaceView, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER))
        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) { surface = holder.surface; applyRefreshPolicy(); decoder.setSurface(holder.surface) }
            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}
            override fun surfaceDestroyed(holder: SurfaceHolder) { surface = null; decoder.setSurface(null) }
        })
        // 터치: 여백을 포함한 전체에서 받아 영상 영역 기준 0~1 로(여백은 가장 가까운 가장자리)
        root.setOnTouchListener { _, e -> watchGesture(e); touch.onTouch(e) }
        root.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> layoutVideo() }

        staleText = TextView(this).apply { setTextColor(Color.WHITE); setBackgroundColor(0xAA000000.toInt()); setPadding(dp(12f), dp(6f), dp(12f), dp(6f)); visibility = View.GONE; textSize = 13f }
        root.addView(staleText, FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = dp(48f) })

        panel = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER; setBackgroundColor(0xCC000000.toInt()); setPadding(dp(24f), dp(24f), dp(24f), dp(24f)) }
        statusText = TextView(this).apply { setTextColor(Color.WHITE); textSize = 16f; gravity = Gravity.CENTER }
        panel.addView(statusText)
        takeButton = Button(this).apply { text = "여기서 보기"; visibility = View.GONE; setOnClickListener { session?.take() } }
        panel.addView(takeButton)
        scanButton = Button(this).apply { text = "QR 스캔"; setOnClickListener { startScanner() } }
        panel.addView(scanButton)
        root.addView(panel, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER))

        hud = TextView(this).apply { setTextColor(0xFF7CFF7C.toInt()); setBackgroundColor(0x99000000.toInt()); typeface = Typeface.MONOSPACE; textSize = 9f; setPadding(dp(6f), dp(4f), dp(6f), dp(4f)); visibility = View.GONE
            isClickable = false; isFocusable = false }      // 터치를 먹지 않습니다(코드 리뷰 M6): 아래의 게임 조작이 그대로 지나갑니다
        root.addView(hud, FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.START).apply { topMargin = dp(36f) })

        setContentView(root)
    }

    /**
     * 진단 빌드의 손짓(터치를 먹지 않고 지켜보기만 합니다. 코드 리뷰 M6: 전에는 오른쪽 위 56dp 와 HUD 글자가 터치를 가로챘음).
     *   세 손가락을 1초 누름 = HUD 켬/끔,  HUD 가 켜져 있을 때 네 손가락을 1초 누름 = 기록 시작/끝
     * 그 손가락들도 게임에는 그대로 전달됩니다.
     */
    private fun watchGesture(e: MotionEvent) {
        if (!BuildConfig.DIAG) return
        when (e.actionMasked) {
            MotionEvent.ACTION_POINTER_DOWN -> {
                ui.removeCallbacks(hudGesture); ui.removeCallbacks(recGesture)
                if (e.pointerCount == 3) ui.postDelayed(hudGesture, 1000) else if (e.pointerCount == 4) ui.postDelayed(recGesture, 1000)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_CANCEL -> { ui.removeCallbacks(hudGesture); ui.removeCallbacks(recGesture) }
        }
    }

    private fun showPanel(text: String) { statusText.text = text; panel.visibility = View.VISIBLE; scanButton.visibility = if (scanner?.isActive == true) View.GONE else View.VISIBLE }

    private fun layoutVideo() {
        if (videoW <= 0 || videoH <= 0 || root.width == 0) return
        val want = if (videoH >= videoW) ActivityInfo.SCREEN_ORIENTATION_PORTRAIT else ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        if (requestedOrientation != want) requestedOrientation = want
        val scale = minOf(root.width.toFloat() / videoW, root.height.toFloat() / videoH)
        val w = Math.round(videoW * scale)
        val h = Math.round(videoH * scale)
        val lp = surfaceView.layoutParams as FrameLayout.LayoutParams
        if (lp.width != w || lp.height != h) { lp.width = w; lp.height = h; lp.gravity = Gravity.CENTER; surfaceView.layoutParams = lp }
        touch.setVideoRect((root.width - w) / 2f, (root.height - h) / 2f, w.toFloat(), h.toFloat())
    }

    private fun goFullscreen() {
        if (Build.VERSION.SDK_INT >= 30) {
            window.setDecorFitsSystemWindows(false)
            window.insetsController?.let { it.hide(WindowInsets.Type.systemBars()); it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE }
            window.attributes = window.attributes.apply { layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS }
        } else {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
        }
    }

    // ------------------------------------------------------------------ 주사율 (설계 7.5)

    @Suppress("DEPRECATION")
    private fun currentDisplay() = if (Build.VERSION.SDK_INT >= 30) display else windowManager.defaultDisplay

    private fun noteRefreshRate() {
        val hz = currentDisplay()?.refreshRate ?: return
        if (hz != lastHz) {
            if (lastHz != 0f) counters.rrSwitches.incrementAndGet()
            lastHz = hz; counters.displayHz = hz
            counters.modeHz = currentDisplay()?.mode?.refreshRate ?: 0f       // 물리 모드(앱에 보이는 값과 다를 수 있음, 리뷰 M3)
            trace.refreshRate(System.nanoTime(), hz)
        }
    }

    private fun applyRefreshPolicy() {
        val d = currentDisplay() ?: return
        val lp = window.attributes
        val cur = d.mode
        val same = d.supportedModes.filter { it.physicalWidth == cur.physicalWidth && it.physicalHeight == cur.physicalHeight }
        lp.preferredDisplayModeId = when (settings.rr) {
            "force60" -> same.minByOrNull { Math.abs(it.refreshRate - 60f) }?.modeId ?: 0
            "max" -> same.maxByOrNull { it.refreshRate }?.modeId ?: 0
            else -> 0
        }
        window.attributes = lp
        val s = surface
        if (s != null && s.isValid && Build.VERSION.SDK_INT >= 30) {
            try {
                val rate = if (settings.rr == "match") sourceFps.toFloat() else 0f      // 원본 프레임률에 맞춤(리뷰 M3: 전에는 60 고정)
                if (Build.VERSION.SDK_INT >= 31) s.setFrameRate(rate, if (rate == 0f) Surface.FRAME_RATE_COMPATIBILITY_DEFAULT else Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE, Surface.CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS)
                else s.setFrameRate(rate, if (rate == 0f) Surface.FRAME_RATE_COMPATIBILITY_DEFAULT else Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE)
            } catch (e: Exception) { Log.w(tag, "setFrameRate failed", e) }
        }
    }

    // ------------------------------------------------------------------ 스캐너

    private fun startScanner() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) { requestPermissions(arrayOf(Manifest.permission.CAMERA), 1); return }
        if (scanner == null) scanner = Scanner(this, root) { text -> onScanned(text) }
        dropSession()
        scanner?.start()
        surfaceView.visibility = View.INVISIBLE
        showPanel("Unity 창의 QR 을 화면에 맞추세요.")
    }

    private fun stopScanner() { if (scanner?.isActive == true) { scanner?.stop(); surfaceView.visibility = View.VISIBLE } }

    private fun onScanned(text: String) {
        when (val r = LinkParser.parse(text, BuildConfig.SCHEME)) {
            is LinkResult.Ok -> useLink(r.link)          // 앱 안 스캐너는 확인 창 없이 연결(사람이 직접 찍음)
            is LinkResult.Error -> statusText.text = r.message
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 1) {
            if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) startScanner()
            else showPanel("카메라를 쓸 수 없습니다. 폰의 기본 카메라로 Unity 창의 QR 을 찍으세요.")
        }
    }

    // ------------------------------------------------------------------ HUD 와 기록

    private fun setHud(on: Boolean) {
        hudOn = on
        hud.visibility = if (on) View.VISIBLE else View.GONE
        ui.removeCallbacks(hudTick)
        if (on) ui.post(hudTick)
    }

    private val hudTick = object : Runnable {
        override fun run() {
            if (!hudOn || !started) return
            val now = System.nanoTime()
            val s = Snapshot(trace, 2_000_000_000L, now)
            val dt = if (lastHudNs == 0L) 1f else (now - lastHudNs) / 1e9f
            val c = counters
            val recv = c.recvH264.get(); val dec = c.decoded.get(); val rel = c.released.get(); val skip = c.skipped.get()
            val info = session?.info
            val sb = StringBuilder()
            sb.append(info?.route ?: "-").append(' ').append(info?.host ?: "").append("  auth ").append(info?.auth ?: "-").append("  seat ").append(info?.seat?.ifEmpty { "-" } ?: "-").append("  ").append(state).append('\n')
            sb.append(wifiLine()).append(String.format(Locale.ROOT, "  lock %s  rtt %.0fms\n", if (wifiLock != null) "on" else "off", c.rttMs))
            sb.append(c.width).append('x').append(c.height).append(" H.264  ").append(c.decoder).append("  ").append(c.decoderKeys).append('\n')
            sb.append(String.format(Locale.ROOT, "recv %.1f/s  dec %.1f  shown %.1f  held %d  jpeg %d\n", (recv - lastRecv) / dt, (dec - lastDecoded) / dt, (rel - lastReleased) / dt, s.held, c.recvJpeg.get()))
            sb.append(String.format(Locale.ROOT, "arrive p50 %.1f p99 %.0f max %.0f  >=40ms %d  burst %d\n", s.arriveP50, s.arriveP99, s.arriveMax, s.stalls, s.bursts))
            sb.append(String.format(Locale.ROOT, "decode p50 %.1f p95 %.1f max %.1f\n", s.decP50, s.decP95, s.decMax))
            sb.append(String.format(Locale.ROOT, "holds %.1f/s  skipped %.1f/s  longest %.0fms  uneven %.1f%%\n", s.holds / s.seconds, (skip - lastSkipped) / dt, s.longestMs, s.uneven))
            sb.append(String.format(Locale.ROOT, "touch>shown %.0fms (n=%d)  cap>shown %.0fms  rendered %d/%d\n", s.touchMs, s.touchN, s.capToShownMs, s.renderedN, s.shown))
            sb.append(String.format(Locale.ROOT, "pace %s  buf %s %.1fms phase %.1f  panel %.1fHz mode %.0f vsync %.1fms (rr %s %dfps, switches %d, slow %d)\n", settings.pace, settings.buf, c.delayMs, c.phaseMs, c.displayHz, c.modeHz, c.vsyncMs, settings.rr, sourceFps, c.rrSwitches.get(), c.slowVsync.get()))
            sb.append(String.format(Locale.ROOT, "keyreq %d  prekey %d  idgap %d  corrupt %d  codecErr %d  reconn %d\n", c.keyRequests.get(), c.droppedPreKey.get(), c.idGaps.get(), c.corrupt.get(), c.codecErrors.get(), c.reconnects.get()))
            sb.append(if (recStartNs == 0L) "[네 손가락 1초: 기록 시작]" else String.format(Locale.ROOT, "[기록 중 %.0f초 - 네 손가락 1초: 끝]", (now - recStartNs) / 1e9))
            hud.text = sb
            lastRecv = recv; lastDecoded = dec; lastReleased = rel; lastSkipped = skip; lastHudNs = now
            ui.postDelayed(this, 500)
        }
    }

    @Suppress("DEPRECATION")
    private fun wifiLine(): String = try {
        val wi = (applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager).connectionInfo
        "Wi-Fi ${wi.frequency}MHz ${wi.linkSpeed}Mbps ${wi.rssi}dBm"
    } catch (e: Exception) { "Wi-Fi ?" }

    private fun startRecording() {
        recStartNs = System.nanoTime(); recStartHead = trace.head
        Log.i(tag, "REC start")
    }

    private fun stopRecording() {
        val start = recStartNs
        if (start == 0L) return
        recStartNs = 0
        val end = System.nanoTime()
        val head = recStartHead
        val name = "rec-" + SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).format(Date()) + (if (recName.isNotEmpty()) "-$recName" else "") + ".json"
        val file = File(getExternalFilesDir("rec"), name)
        val i = session?.info
        val info = linkedMapOf<String, Any?>(
            "client" to "app", "build" to BuildConfig.BUILD_TYPE, "versionCode" to BuildConfig.VERSION_CODE, "name" to recName, "model" to Build.MODEL, "sdk" to Build.VERSION.SDK_INT,
            "route" to (i?.route ?: ""), "via" to (i?.via ?: ""), "host" to (i?.host ?: ""), "auth" to (i?.auth ?: ""), "settings" to settings.describe(), "hud" to hudOn,
            "decoder" to counters.decoder, "decoderKeys" to counters.decoderKeys, "width" to counters.width, "height" to counters.height,
            "displayHz" to counters.displayHz, "modeHz" to counters.modeHz, "sourceFps" to sourceFps, "slowVsync" to counters.slowVsync.get(), "vsyncMs" to counters.vsyncMs, "wifi" to wifiLine(), "wifiLock" to (wifiLock != null))
        Thread({
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_BACKGROUND)
            try { Exporter.write(file, trace, counters, start, end, head, info); Log.i(tag, "REC saved " + file.absolutePath) }
            catch (e: Exception) { Log.e(tag, "REC failed", e) }
        }, "playlink-diag").start()
    }
}
