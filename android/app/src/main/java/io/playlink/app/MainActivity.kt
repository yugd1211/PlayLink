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
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
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
import android.widget.CheckBox
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
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
import io.playlink.app.net.NetPick
import io.playlink.app.pair.PairStore
import io.playlink.app.proto.Proto
import io.playlink.app.session.Receiver
import io.playlink.app.session.Session
import io.playlink.app.session.State
import io.playlink.app.session.Watch
import io.playlink.app.ui.Scanner
import io.playlink.app.video.Decoder
import java.io.File
import java.net.Inet4Address
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.atomic.AtomicInteger

/**
 * 단일 Activity. 화면(대기 화면, 영상 Surface, 영상 중 메뉴, 설정, 승인 창, 스캐너), 수명 주기, 인텐트(QR 딥링크, adb 명령)만 다룹니다.
 * 앱은 앞에 있는 동안만(onStart ~ onStop) 포트를 열고 PC 가 걸어 오기를 기다립니다(session.Receiver). 뒤에서는 연결을 유지하지 않습니다.
 * 네트워크, 디코더, 파일을 UI 스레드에서 직접 부르지 않습니다(예외: Surface 가 사라질 때 디코더 해제를 기다림).
 */
class MainActivity : ComponentActivity(), Session.Ui, Receiver.Ui {
    companion object { const val DIAG_ALIAS = "io.playlink.app.Diag" }
    private enum class Screen { STANDBY, SCAN, SETTINGS, VIDEO }

    private val tag = "PlayLink"
    private val ui = Handler(Looper.getMainLooper())
    private val settings = Settings()
    private val counters = Counters()
    private val trace = TraceRing()
    private val genSource = AtomicInteger()
    private lateinit var store: PairStore
    private lateinit var receiver: Receiver
    private lateinit var decoder: Decoder
    private lateinit var touch: TouchMapper
    private lateinit var root: FrameLayout
    private lateinit var surfaceView: SurfaceView
    private lateinit var panel: LinearLayout
    private lateinit var statusText: TextView
    private lateinit var takeButton: Button
    private lateinit var staleText: TextView
    private lateinit var hud: TextView
    private lateinit var handle: TextView
    private lateinit var menu: LinearLayout
    private lateinit var menuInfo: TextView
    private lateinit var hudButton: Button
    private lateinit var standbyView: ScrollView
    private lateinit var nameText: TextView
    private lateinit var addressText: TextView
    private lateinit var portText: TextView
    private lateinit var hintText: TextView
    private lateinit var standbyStatus: TextView
    private lateinit var pcList: LinearLayout
    private lateinit var settingsView: ScrollView
    private lateinit var settingsBody: LinearLayout
    private lateinit var scanBar: LinearLayout
    private lateinit var scanText: TextView
    private var scanner: Scanner? = null
    @Volatile private var session: Session? = null      // 디코더 스레드와 핸드셰이크 스레드도 읽고 씁니다
    private var screen = Screen.STANDBY
    private var started = false
    private var surface: Surface? = null
    private var videoW = 0
    private var videoH = 0
    private var state = State.ENDED
    private var lastMessage = ""
    private var pcListShown = "?"
    private var promptShown: Receiver.Prompt? = null
    private var promptDialog: AlertDialog? = null
    private var dialogsOpen = 0
    private var wifiLock: WifiManager.WifiLock? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private var hudOn = false
    private var recStartNs = 0L
    private var recStartHead = 0L
    private var recName = ""
    private var recAutoSec = 0
    private var lastHz = 0f
    private var sourceFps = 60
    private var displayListener: DisplayManager.DisplayListener? = null
    private val hudGesture = Runnable { setHud(!hudOn) }
    private val recGesture = Runnable { if (hudOn) { if (recStartNs == 0L) startRecording() else stopRecording() } }
    private val refresh = Runnable { refreshStandby() }
    private var lastRecv = 0L; private var lastDecoded = 0L; private var lastReleased = 0L; private var lastSkipped = 0L; private var lastHudNs = 0L
    private var menuReleased = 0L; private var menuBytes = 0L; private var menuNs = 0L

    // ------------------------------------------------------------------ 수명 주기

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = PairStore(this)
        receiver = Receiver(store, BuildConfig.VERSION_CODE, this, { Log.i(tag, it) })
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
                when (screen) {
                    Screen.SCAN -> { stopScanner(); show(Screen.STANDBY) }
                    Screen.SETTINGS -> show(Screen.STANDBY)
                    Screen.VIDEO -> setMenu(menu.visibility != View.VISIBLE)       // 영상 중의 뒤로 가기는 메뉴를 열고 닫습니다
                    Screen.STANDBY -> finish()
                }
            }
        })
        // 다시 만들어진 Activity(회전, 프로세스 복원)나 최근 앱 목록에서 온 실행은 옛 인텐트를 다시 처리하지 않습니다(리뷰 m6).
        if (savedInstanceState == null && (intent.flags and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY) == 0) handleIntent(intent)
        setIntent(Intent(this, MainActivity::class.java))
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
        setIntent(Intent(this, MainActivity::class.java))      // QR 비밀이 든 인텐트를 쥐고 있지 않습니다
    }

    override fun onStart() {
        super.onStart()
        started = true
        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            multicastLock = wm.createMulticastLock("playlink").apply { setReferenceCounted(false); acquire() }       // 브로드캐스트로 오는 탐색 질문을 받으려면 필요합니다
        } catch (e: Exception) { Log.w(tag, "multicast lock failed", e) }
        receiver.start()
        show(Screen.STANDBY)
        ui.removeCallbacks(tick); ui.post(tick)
    }

    override fun onStop() {
        super.onStop()
        started = false
        touch.cancelAll()                // 눌려 있는 포인터마다 c
        receiver.stop()                  // 뒤에서는 듣지 않고 연결도 유지하지 않습니다
        if (session != null) leaveVideo("")
        ui.removeCallbacks(hudGesture); ui.removeCallbacks(recGesture); ui.removeCallbacks(tick); ui.removeCallbacks(hudTick); ui.removeCallbacks(refresh)
        stopScanner()
        closePrompt()
        releaseWifiLock()
        try { multicastLock?.release() } catch (e: Exception) { }
        multicastLock = null
        receiver.standby = false
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    override fun onDestroy() {
        super.onDestroy()
        displayListener?.let { (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).unregisterDisplayListener(it) }
        scanner?.shutdown()
        decoder.shutdown()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) { super.onWindowFocusChanged(hasFocus); if (hasFocus) goFullscreen() }

    // ------------------------------------------------------------------ 인텐트: QR 딥링크, adb 명령

    private fun handleIntent(intent: Intent?) {
        if (intent == null) return
        // 진단 값과 명령은 진단 빌드에서, adb 만 통과하는 입구(.Diag 별칭, DUMP 권한)로 온 인텐트일 때만 받습니다(코드 리뷰 M5).
        // 그 밖의 인텐트는 카메라 앱이 열었든 다른 앱이 직접 열었든 똑같이 "밖에서 온 것"이고, 페어링 QR(playlink://pair?…)만 받습니다.
        val adb = intent.component?.className == DIAG_ALIAS
        if (BuildConfig.DIAG && adb) {
            applyExtras(intent)
            intent.getStringExtra("cmd")?.let { command(it, intent) }
        }
        // QR 내용을 logcat 의 액티비티 시작 줄에 남기지 않으려면 adb 는 --es qr <내용> 으로 넘깁니다. 이 입구는 확인 창 없이 받습니다.
        val extra = if (adb) intent.getStringExtra("qr") else null
        if (extra != null) onQrText(extra, confirm = false)
        else if (intent.action == Intent.ACTION_VIEW && intent.data != null) onQrText(intent.dataString ?: "", confirm = !adb)
    }

    private fun applyExtras(intent: Intent) {
        fun x(k: String) = intent.getStringExtra(k)
        x("pace")?.let { if (it == "now" || it == "sched") settings.pace = it }
        x("buf")?.let { settings.buf = it }
        x("rr")?.let { settings.rr = it; applyRefreshPolicy() }
        x("wifilock")?.let { settings.wifiLock = it != "0" }
        x("recreate")?.let { settings.recreate = it == "1" }
        x("hold")?.let { settings.holdLimit = (it.toIntOrNull() ?: 6).coerceIn(1, 12) }
        x("target")?.let { if (it == "pref" || it == "early") settings.target = it }
        x("pct")?.let { settings.percentile = ((it.toIntOrNull() ?: 95).coerceIn(50, 100)) / 100.0 }
        x("decoder")?.let { settings.decoderName = it }
        x("hud")?.let { setHud(it == "1") }
        x("rec")?.let { recAutoSec = it.toIntOrNull() ?: 0 }
        x("name")?.let { recName = it.filter { c -> c.isLetterOrDigit() || c == '-' || c == '_' } }
    }

    private fun command(cmd: String, intent: Intent) {
        when (cmd) {
            "rec_start" -> startRecording()
            "rec_stop" -> stopRecording()
            "hud_on" -> setHud(true)
            "hud_off" -> setHud(false)
            "menu" -> if (screen == Screen.VIDEO) setMenu(true)
            "disconnect" -> disconnect()
            "pick" -> intent.getStringExtra("pc")?.let { pick(it) }
            "forget" -> { for (pc in store.list()) receiver.unpaired(pc.id); store.removeAll(); if (session != null) leaveVideo("페어링을 모두 지웠습니다."); refreshStandby() }
            "scan" -> startScanner()
        }
    }

    /** 찍은 QR(앱 안 스캐너, 카메라 앱의 딥링크, adb). 밖에서 온 링크는 사람에게 한 번 묻습니다: 남이 만든 링크로 모르는 PC 와 페어링되지 않게. */
    private fun onQrText(text: String, confirm: Boolean) {
        val q = Proto.parseQr(text)
        if (q == null) { Toast.makeText(this, "PlayLink 의 QR 이 아닙니다. Unity 창(Tools > PlayLink)의 QR 을 찍으세요.", Toast.LENGTH_LONG).show(); return }
        if (!confirm) { useQr(q); return }
        modal(AlertDialog.Builder(this).setTitle("페어링").setMessage("‘${q.name.ifEmpty { "PC" }}’ PC 와 페어링할까요?\n\n직접 찍은 QR 이 아니면 취소하세요.")
            .setPositiveButton("페어링") { _, _ -> useQr(q) }.setNegativeButton("취소", null))
    }

    private fun useQr(q: Proto.Qr) {
        receiver.scanned(q)
        lastMessage = ""
        if (screen == Screen.SCAN) { stopScanner(); show(Screen.STANDBY) } else refreshStandby()
    }

    // ------------------------------------------------------------------ 받는 쪽(Receiver)의 알림. 임의 스레드에서 옵니다

    override fun onChanged() { ui.removeCallbacks(refresh); ui.post(refresh) }

    override fun onPaired(pcId: String, pcName: String) {
        ui.post { lastMessage = "‘${store.label(pcId) ?: pcName}’ 과(와) 페어링했습니다."; Toast.makeText(this, lastMessage, Toast.LENGTH_SHORT).show(); pcListShown = "?"; refreshStandby() }
    }

    override fun openWatch(pcId: String, pcName: String): Watch {
        val s = Session(pcId, store.label(pcId) ?: pcName, decoder, trace, counters, genSource, this)
        session = s
        decoder.attach(s)                // 이 뒤로 옛 세션의 프레임과 콜백은 버려집니다(코드 리뷰 M2)
        ui.post { if (s === session) enterVideo(s) }
        return s
    }

    override fun onPrompt(prompt: Receiver.Prompt) {
        ui.post {
            if (!started || promptDialog != null) { prompt.answer(false); return@post }
            val b = AlertDialog.Builder(this).setCancelable(false)
            if (prompt.sas) {
                val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(24f), dp(12f), dp(24f), 0) }
                box.addView(TextView(this).apply { text = "‘${prompt.pcName.ifEmpty { "이름 없는 PC" }}’ PC 가 이 폰과 페어링하려고 합니다."; textSize = 15f })
                box.addView(TextView(this).apply { text = prompt.digits.substring(0, 3) + " " + prompt.digits.substring(3); textSize = 40f; typeface = Typeface.MONOSPACE; gravity = Gravity.CENTER; setPadding(0, dp(16f), 0, dp(16f)) })
                box.addView(TextView(this).apply { text = "Unity 창에 같은 숫자가 보일 때만 [허용]을 누르세요."; textSize = 14f })
                b.setTitle("페어링 요청").setView(box).setPositiveButton("허용") { _, _ -> prompt.answer(true) }.setNegativeButton("거절") { _, _ -> prompt.answer(false) }
            } else {
                b.setTitle("PC 바꾸기").setMessage("‘${store.label(prompt.pcId) ?: prompt.pcName}’ 화면으로 바꿀까요?\n\n지금은 ‘${store.label(prompt.current) ?: "다른 PC"}’ 화면을 보고 있습니다.")
                    .setPositiveButton("바꾸기") { _, _ -> prompt.answer(true) }.setNegativeButton("그대로 보기") { _, _ -> prompt.answer(false) }
            }
            promptShown = prompt
            promptDialog = b.create().also { d -> d.setOnDismissListener { if (promptShown === prompt) { promptShown = null; promptDialog = null; updateStandbyFlag() } }; d.show() }
            updateStandbyFlag()
        }
    }

    override fun onPromptGone(prompt: Receiver.Prompt) { ui.post { if (promptShown === prompt) closePrompt() } }

    private fun closePrompt() { val d = promptDialog; promptShown = null; promptDialog = null; try { d?.dismiss() } catch (e: Exception) { }; updateStandbyFlag() }

    /** 승인 창이 아닌 보통 창. 떠 있는 동안은 "대기 화면"이 아닙니다(sas 요청을 받지 않음). */
    private fun modal(b: AlertDialog.Builder) {
        dialogsOpen++
        b.create().also { d -> d.setOnDismissListener { dialogsOpen--; updateStandbyFlag() }; d.show() }
        updateStandbyFlag()
    }

    private fun updateStandbyFlag() { receiver.standby = started && screen == Screen.STANDBY && promptDialog == null && dialogsOpen == 0 }

    // ------------------------------------------------------------------ 세션(Session)의 알림. 임의 스레드에서 옵니다

    override fun onConnected(session: Session, epoch: Int) { ui.post { if (session === this.session) touch.newConnection(epoch) } }

    override fun onState(session: Session, state: State, message: String) {
        ui.post {
            if (session !== this.session) return@post       // 멈춘 옛 세션이 늦게 보낸 상태는 버립니다
            this.state = state
            touch.enabled = state == State.PLAYING || state == State.WAITING || state == State.NOCODEC
            takeButton.visibility = if (state == State.STANDBY && session.canTake) View.VISIBLE else View.GONE
            when (state) {
                State.PLAYING -> { panel.visibility = View.GONE; acquireWifiLock()
                    if (recAutoSec > 0) { val sec = recAutoSec; recAutoSec = 0; startRecording(); ui.postDelayed({ stopRecording() }, sec * 1000L) } }
                State.WAITING -> showPanel("‘${session.pcName}’ 에 연결됨. 화면을 기다리는 중…")
                State.NOCODEC, State.STANDBY, State.LOBBY, State.RECONNECT -> showPanel(message)
                State.ENDED -> {}
            }
            if (state != State.PLAYING) releaseWifiLock()
            Log.i(tag, "state $state")
        }
    }

    override fun onVideoSize(session: Session, width: Int, height: Int) { ui.post { if (session === this.session) { videoW = width; videoH = height; layoutVideo() } } }
    override fun onStale(session: Session, message: String?) { ui.post { if (session === this.session) { staleText.text = message ?: ""; staleText.visibility = if (message == null) View.GONE else View.VISIBLE } } }

    override fun onEnded(session: Session, message: String) {
        receiver.ended(session)
        ui.post { if (session === this.session) leaveVideo(message) }
    }

    private fun enterVideo(s: Session) {
        stopScanner()
        state = State.WAITING
        videoW = 0; videoH = 0
        staleText.visibility = View.GONE
        show(Screen.VIDEO)
        setMenu(false)
        showPanel("‘${s.pcName}’ 에 연결됨. 화면을 기다리는 중…")
        if (hudOn) { ui.removeCallbacks(hudTick); ui.post(hudTick) }
    }

    /** 지금 세션을 화면에서 내리고 디코더의 주인 자리를 비웁니다. 이 뒤로 옛 세션의 콜백과 프레임은 버려집니다(코드 리뷰 M2). */
    private fun leaveVideo(message: String) {
        session = null
        decoder.attach(null)
        touch.enabled = false
        state = State.ENDED
        releaseWifiLock()
        videoW = 0; videoH = 0
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        (surfaceView.layoutParams as FrameLayout.LayoutParams).let { lp -> lp.width = FrameLayout.LayoutParams.MATCH_PARENT; lp.height = FrameLayout.LayoutParams.MATCH_PARENT; surfaceView.layoutParams = lp }
        lastMessage = message
        show(Screen.STANDBY)
    }

    /** [연결 끊기] */
    private fun disconnect() {
        if (session == null) return
        receiver.disconnect()
        leaveVideo("연결을 끊었습니다.")
    }

    private fun pick(pcId: String) {
        receiver.pick(pcId)
        lastMessage = ""
        refreshStandby()
    }

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
    private fun label(text: String, size: Float, color: Int = Color.WHITE) = TextView(this).apply { this.text = text; textSize = size; setTextColor(color) }
    private fun button(text: String, onClick: () -> Unit) = Button(this).apply { this.text = text; isAllCaps = false; setOnClickListener { onClick() } }
    private fun wrap() = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
    private fun wide(top: Float = 0f) = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(top) }
    private fun fill() = FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT)

    private fun buildViews() {
        val grey = 0xFFAAAAAA.toInt()
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

        // 영상이 나오지 않는 동안의 상태 문구(기다리는 중, 다시 연결 중, 자리 대기)
        panel = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER; setBackgroundColor(0xCC000000.toInt()); setPadding(dp(24f), dp(24f), dp(24f), dp(24f)); visibility = View.GONE }
        statusText = label("", 16f).apply { gravity = Gravity.CENTER }
        panel.addView(statusText)
        takeButton = button("여기서 보기") { session?.take() }.apply { visibility = View.GONE }
        panel.addView(takeButton, wrap())
        panel.addView(button("연결 끊기") { disconnect() }, wrap())
        root.addView(panel, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER))

        hud = TextView(this).apply { setTextColor(0xFF7CFF7C.toInt()); setBackgroundColor(0x99000000.toInt()); typeface = Typeface.MONOSPACE; textSize = 9f; setPadding(dp(6f), dp(4f), dp(6f), dp(4f)); visibility = View.GONE
            isClickable = false; isFocusable = false }      // 터치를 먹지 않습니다(코드 리뷰 M6): 아래의 게임 조작이 그대로 지나갑니다
        root.addView(hud, FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM or Gravity.START).apply { bottomMargin = dp(24f) })

        // 영상 중 메뉴를 여는 작은 손잡이(오른쪽 위). 게임 조작을 가리지 않게 작고 반투명하게 둡니다. 뒤로 가기로도 열립니다.
        handle = TextView(this).apply { text = "≡"; textSize = 20f; gravity = Gravity.CENTER; setTextColor(Color.WHITE); setBackgroundColor(0x66000000); visibility = View.GONE; setOnClickListener { setMenu(true) } }
        root.addView(handle, FrameLayout.LayoutParams(dp(44f), dp(32f), Gravity.TOP or Gravity.END).apply { topMargin = dp(36f) })

        menu = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(0xE6101010.toInt()); setPadding(dp(16f), dp(40f), dp(16f), dp(12f)); visibility = View.GONE; isClickable = true }
        menuInfo = label("", 14f)
        menu.addView(menuInfo, wide())
        menu.addView(button("연결 끊기") { disconnect() }.apply { setTextColor(Color.WHITE); textSize = 17f; setTypeface(typeface, Typeface.BOLD); background = background.mutate().apply { setTint(0xFFC62828.toInt()) } }, wide(10f))
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        hudButton = button("디버그 상태 보기") { setHud(!hudOn) }
        row.addView(hudButton, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        row.addView(button("닫기") { setMenu(false) }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        menu.addView(row, wide(4f))
        root.addView(menu, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP))

        // 대기 화면: 폰 주소(크게), 상태, PC 목록, QR 스캔, 설정
        val body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(24f), dp(48f), dp(24f), dp(32f)) }
        body.addView(label("PlayLink", 24f).apply { setTypeface(typeface, Typeface.BOLD) })
        nameText = label("", 14f, grey)
        body.addView(nameText, wide(2f))
        body.addView(label("이 폰의 주소 (자동으로 안 잡히면 Unity 창에 직접 입력)", 13f, grey), wide(28f))
        addressText = label("", 38f).apply { typeface = Typeface.MONOSPACE; setTypeface(typeface, Typeface.BOLD) }
        body.addView(addressText, wide(4f))
        portText = label("", 20f).apply { typeface = Typeface.MONOSPACE }
        body.addView(portText, wide())
        hintText = label("", 13f, grey)
        body.addView(hintText, wide(6f))
        standbyStatus = label("", 16f, 0xFFFFD54F.toInt())
        body.addView(standbyStatus, wide(24f))
        body.addView(label("PC", 13f, grey), wide(24f))
        pcList = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        body.addView(pcList, wide(4f))
        val buttons = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        buttons.addView(button("QR 스캔") { startScanner() }, LinearLayout.LayoutParams(0, dp(56f), 1f))
        buttons.addView(button("설정") { openSettings() }, LinearLayout.LayoutParams(0, dp(56f), 1f))
        body.addView(buttons, wide(24f))
        standbyView = ScrollView(this).apply { setBackgroundColor(0xFF101010.toInt()); isFillViewport = true; isClickable = true; addView(body) }
        root.addView(standbyView, fill())

        settingsBody = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(24f), dp(48f), dp(24f), dp(32f)) }
        settingsView = ScrollView(this).apply { setBackgroundColor(0xFF101010.toInt()); isFillViewport = true; isClickable = true; visibility = View.GONE; addView(settingsBody) }
        root.addView(settingsView, fill())

        scanBar = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER; setBackgroundColor(0xCC000000.toInt()); setPadding(dp(24f), dp(16f), dp(24f), dp(32f)); visibility = View.GONE; isClickable = true }
        scanText = label("", 16f).apply { gravity = Gravity.CENTER }
        scanBar.addView(scanText)
        scanBar.addView(button("취소") { stopScanner(); show(Screen.STANDBY) }, wrap())
        root.addView(scanBar, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM))

        setContentView(root)
    }

    private fun show(next: Screen) {
        screen = next
        standbyView.visibility = if (next == Screen.STANDBY) View.VISIBLE else View.GONE
        settingsView.visibility = if (next == Screen.SETTINGS) View.VISIBLE else View.GONE
        scanBar.visibility = if (next == Screen.SCAN) View.VISIBLE else View.GONE
        surfaceView.visibility = if (next == Screen.SCAN) View.INVISIBLE else View.VISIBLE       // 스캐너의 미리보기가 맨 아래에 깔립니다
        if (next != Screen.VIDEO) { panel.visibility = View.GONE; menu.visibility = View.GONE; handle.visibility = View.GONE; staleText.visibility = View.GONE; hud.visibility = View.GONE }
        else hud.visibility = if (hudOn) View.VISIBLE else View.GONE
        // 영상 중에는 항상, 대기 화면에서는 설정에 따라 화면을 켜 둡니다(화면이 꺼지면 앱이 뒤로 가서 연결을 받지 못합니다).
        if (next == Screen.VIDEO || store.keepScreenOn) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        updateStandbyFlag()
        if (next == Screen.STANDBY) refreshStandby()
    }

    private fun showPanel(text: String) { if (screen != Screen.VIDEO) return; statusText.text = text; panel.visibility = View.VISIBLE }

    private fun setMenu(open: Boolean) {
        if (screen != Screen.VIDEO) return
        menu.visibility = if (open) View.VISIBLE else View.GONE
        handle.visibility = if (open) View.GONE else View.VISIBLE
        if (open) { menuNs = 0L; refreshMenu() }
    }

    /** 1초마다: 대기 화면의 주소와 PC 목록, 메뉴의 상태 줄. */
    private val tick = object : Runnable {
        override fun run() {
            if (!started) return
            if (screen == Screen.STANDBY) refreshStandby() else if (screen == Screen.VIDEO && menu.visibility == View.VISIBLE) refreshMenu()
            ui.postDelayed(this, 1000)
        }
    }

    private fun refreshMenu() {
        val s = session ?: return
        val now = System.nanoTime()
        val rel = counters.released.get(); val bytes = counters.bytes.get()
        val sb = StringBuilder("‘${s.pcName}’ 화면을 보는 중 · ${s.info.route}")
        if (state != State.PLAYING) sb.append("\n화면을 기다리는 중")
        else if (menuNs != 0L && now > menuNs) {
            val dt = (now - menuNs) / 1e9
            sb.append(String.format(Locale.ROOT, "\n%d×%d · %.0f fps · %.1f Mbps", counters.width, counters.height, (rel - menuReleased) / dt, (bytes - menuBytes) * 8 / dt / 1e6))
            if (counters.rttMs >= 0f) sb.append(String.format(Locale.ROOT, " · 왕복 %.0f ms", counters.rttMs))
        }
        menuInfo.text = sb
        menuReleased = rel; menuBytes = bytes; menuNs = now
        hudButton.text = if (hudOn) "디버그 상태 숨기기" else "디버그 상태 보기"
    }

    /** 화면에 보여 줄 이 폰의 Wi-Fi 주소(IPv4). VPN 은 고르지 않습니다(net.NetPick). 없으면 null. */
    private fun wifiAddress(): String? = try {
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        @Suppress("DEPRECATION")
        val nets = cm.allNetworks
        val index = NetPick.pickWifi(nets.map { n ->
            val c = cm.getNetworkCapabilities(n)
            NetPick.Candidate(c?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true, c?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true,
                c?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) == true)
        })
        if (index < 0) null else cm.getLinkProperties(nets[index])?.linkAddresses?.map { it.address }?.firstOrNull { it is Inet4Address }?.hostAddress
    } catch (e: Exception) { null }

    private fun refreshStandby() {
        if (screen != Screen.STANDBY) return
        nameText.text = "이 폰: ${store.phoneName}"
        val address = wifiAddress()
        val port = receiver.port
        addressText.text = address ?: "Wi-Fi 없음"
        portText.text = if (port != 0) "포트 $port" else receiver.error.ifEmpty { "포트를 여는 중…" }
        hintText.text = when {
            address == null -> "Wi-Fi 에 연결하면 주소가 나옵니다. USB 케이블 연결은 Unity 창에서 바로 쓸 수 있습니다."
            port != 0 && !receiver.udpOk -> "자동 찾기를 쓸 수 없습니다(UDP ${Proto.DEFAULT_PORT}). Unity 창에 위 주소를 직접 입력하세요."
            else -> "PC 와 같은 Wi-Fi 에 있어야 합니다."
        }
        val seen = receiver.seen().associateBy { it.pcId }
        val paired = store.list()
        val waiting = receiver.waitingFor()
        standbyStatus.text = when {
            waiting != null -> "‘${store.label(waiting.first) ?: waiting.second.ifEmpty { "PC" }}’ 의 연결을 기다리는 중…\nUnity 창(Tools > PlayLink)이 Start 상태여야 합니다."
            lastMessage.isNotEmpty() -> lastMessage
            paired.isEmpty() -> "처음이면 Unity 창(Tools > PlayLink)의 QR 을 찍거나, Unity 창의 폰 목록에서 이 폰을 고르세요."
            else -> "연결을 기다리는 중. Unity 창에서 이 폰을 고르거나 아래에서 PC 를 고르세요."
        }
        // 줄: 페어링한 PC 전부(라벨로) + 방금 탐색 질문을 보낸, 페어링하지 않은 PC
        class Row(val id: String, val name: String, val sub: String, val kind: Int)
        val rows = ArrayList<Row>()
        for (pc in paired) rows.add(if (seen.containsKey(pc.id)) Row(pc.id, pc.name, if (waiting?.first == pc.id) "연결을 요청했습니다…" else "Start 됨 · 눌러서 연결", 2) else Row(pc.id, pc.name, "Unity 에서 Start 를 누르면 연결할 수 있습니다", 1))
        for (s in seen.values) if (paired.none { it.id == s.pcId }) rows.add(Row(s.pcId, s.name.ifEmpty { "이름 없는 PC" }, "페어링 안 됨", 0))
        val signature = rows.joinToString("|") { it.id + ":" + it.name + ":" + it.sub }
        if (signature == pcListShown) return
        pcListShown = signature
        pcList.removeAllViews()
        if (rows.isEmpty()) pcList.addView(label("아직 페어링한 PC 가 없습니다.", 14f, 0xFFAAAAAA.toInt()), wide(4f))
        for (r in rows) {
            val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(if (r.kind == 2) 0xFF1E3A5F.toInt() else 0xFF262626.toInt()); setPadding(dp(16f), dp(12f), dp(16f), dp(12f)); isClickable = true }
            box.addView(label(r.name, 18f, if (r.kind == 0) 0xFFAAAAAA.toInt() else Color.WHITE))
            box.addView(label(r.sub, 13f, 0xFFAAAAAA.toInt()))
            box.setOnClickListener {
                when (r.kind) {
                    2 -> pick(r.id)
                    1 -> { pick(r.id); Toast.makeText(this, "‘${r.name}’ 의 Unity 창에서 Start 를 누르세요.", Toast.LENGTH_SHORT).show() }
                    else -> Toast.makeText(this, "먼저 페어링하세요: 이 PC 의 Unity 창에 있는 QR 을 찍거나, Unity 창의 폰 목록에서 이 폰을 고르세요.", Toast.LENGTH_LONG).show()
                }
            }
            pcList.addView(box, wide(6f))
        }
    }

    // ------------------------------------------------------------------ 설정 화면

    private fun openSettings() {
        val grey = 0xFFAAAAAA.toInt()
        val b = settingsBody
        b.removeAllViews()
        b.addView(label("설정", 24f).apply { setTypeface(typeface, Typeface.BOLD) })
        b.addView(label("폰 이름 (Unity 창의 폰 목록에 보이는 이름)", 13f, grey), wide(24f))
        b.addView(label(store.phoneName, 18f), wide(2f))
        b.addView(button("폰 이름 바꾸기") { askName("폰 이름", store.phoneName) { store.phoneName = it; openSettings() } }, wrap())
        b.addView(CheckBox(this).apply { text = "대기 화면에서도 화면 켜 두기"; textSize = 16f; setTextColor(Color.WHITE); isChecked = store.keepScreenOn
            setOnCheckedChangeListener { _, on -> store.keepScreenOn = on } }, wide(20f))
        b.addView(label("앱은 화면이 켜져 있고 앞에 있을 때만 연결을 받습니다. 영상 중에는 항상 켜 둡니다.", 13f, grey), wide())
        b.addView(label("페어링한 PC", 13f, grey), wide(24f))
        val pcs = store.list()
        if (pcs.isEmpty()) b.addView(label("없음", 16f), wide(2f))
        for (pc in pcs) {
            b.addView(label(pc.name, 18f), wide(10f))
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            row.addView(button("이름 바꾸기") { askName("PC 이름", pc.name) { store.rename(pc.id, it); openSettings() } }, wrap())
            row.addView(button("페어링 해제") {
                modal(AlertDialog.Builder(this).setMessage("‘${pc.name}’ 의 페어링을 해제할까요?\n다시 쓰려면 QR 을 찍거나 숫자 확인을 다시 해야 합니다.")
                    .setPositiveButton("해제") { _, _ -> store.remove(pc.id); receiver.unpaired(pc.id); openSettings() }.setNegativeButton("취소", null))
            }, wrap())
            b.addView(row, wide())
        }
        b.addView(label("PlayLink ${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})", 13f, grey), wide(28f))
        b.addView(button("닫기") { show(Screen.STANDBY) }, wide(8f))
        pcListShown = "?"
        show(Screen.SETTINGS)
    }

    private fun askName(title: String, current: String, done: (String) -> Unit) {
        val edit = EditText(this).apply { setText(current); setSingleLine(); setSelection(text.length) }
        val box = FrameLayout(this).apply { setPadding(dp(20f), dp(8f), dp(20f), 0); addView(edit) }
        modal(AlertDialog.Builder(this).setTitle(title).setMessage("글자, 숫자, 공백, _ - . ( ) 만, 32자까지").setView(box)
            .setPositiveButton("저장") { _, _ -> Proto.cleanName(edit.text.toString()).trim().let { if (it.isNotEmpty()) done(it) } }.setNegativeButton("취소", null))
    }

    /**
     * 진단 빌드의 손짓(터치를 먹지 않고 지켜보기만 합니다. 코드 리뷰 M6).
     *   세 손가락을 1초 누름 = 디버그 상태 켬/끔,  켜져 있을 때 네 손가락을 1초 누름 = 기록 시작/끝
     * 그 손가락들도 게임에는 그대로 전달됩니다.
     */
    private fun watchGesture(e: MotionEvent) {
        if (!BuildConfig.DIAG || screen != Screen.VIDEO) return
        when (e.actionMasked) {
            MotionEvent.ACTION_POINTER_DOWN -> {
                ui.removeCallbacks(hudGesture); ui.removeCallbacks(recGesture)
                if (e.pointerCount == 3) ui.postDelayed(hudGesture, 1000) else if (e.pointerCount == 4) ui.postDelayed(recGesture, 1000)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_CANCEL -> { ui.removeCallbacks(hudGesture); ui.removeCallbacks(recGesture) }
        }
    }

    private fun layoutVideo() {
        if (videoW <= 0 || videoH <= 0 || root.width == 0 || screen != Screen.VIDEO) return
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
        if (screen == Screen.VIDEO) return
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) { requestPermissions(arrayOf(Manifest.permission.CAMERA), 1); return }
        if (scanner == null) scanner = Scanner(this, root) { text -> onScanned(text) }
        scanner?.start()
        scanText.text = "Unity 창(Tools > PlayLink)의 QR 을 화면에 맞추세요."
        show(Screen.SCAN)
    }

    private fun stopScanner() { if (scanner?.isActive == true) scanner?.stop() }

    /** 앱 안 스캐너는 확인 창 없이 받습니다(사람이 직접 찍음). */
    private fun onScanned(text: String) {
        val q = Proto.parseQr(text)
        if (q == null) scanText.text = "PlayLink 의 QR 이 아닙니다. Unity 창(Tools > PlayLink)의 QR 을 화면에 맞추세요." else useQr(q)
    }

    @Deprecated("Deprecated in Java")
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 1) {
            if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) startScanner()
            else Toast.makeText(this, "카메라를 쓸 수 없습니다. 폰의 기본 카메라로 Unity 창의 QR 을 찍으세요.", Toast.LENGTH_LONG).show()
        }
    }

    // ------------------------------------------------------------------ 디버그 상태(HUD)와 기록

    private fun setHud(on: Boolean) {
        hudOn = on
        hud.visibility = if (on && screen == Screen.VIDEO) View.VISIBLE else View.GONE
        hudButton.text = if (on) "디버그 상태 숨기기" else "디버그 상태 보기"
        ui.removeCallbacks(hudTick)
        if (on) ui.post(hudTick)
    }

    private val hudTick = object : Runnable {
        override fun run() {
            if (!hudOn || !started) return
            if (screen != Screen.VIDEO) { ui.postDelayed(this, 500); return }
            val now = System.nanoTime()
            val s = Snapshot(trace, 2_000_000_000L, now)
            val dt = if (lastHudNs == 0L) 1f else (now - lastHudNs) / 1e9f
            val c = counters
            val recv = c.recvH264.get(); val dec = c.decoded.get(); val rel = c.released.get(); val skip = c.skipped.get()
            val info = session?.info
            val sb = StringBuilder()
            sb.append(info?.route ?: "-").append(' ').append(info?.host ?: "").append("  pc ").append(session?.pcName ?: "-").append("  seat ").append(info?.seat?.ifEmpty { "-" } ?: "-").append("  ").append(state).append('\n')
            sb.append(wifiLine()).append(String.format(Locale.ROOT, "  lock %s  rtt %.0fms\n", if (wifiLock != null) "on" else "off", c.rttMs))
            sb.append(c.width).append('x').append(c.height).append(" H.264  ").append(c.decoder).append("  ").append(c.decoderKeys).append('\n')
            sb.append(String.format(Locale.ROOT, "recv %.1f/s  dec %.1f  shown %.1f  held %d  jpeg %d\n", (recv - lastRecv) / dt, (dec - lastDecoded) / dt, (rel - lastReleased) / dt, s.held, c.recvJpeg.get()))
            sb.append(String.format(Locale.ROOT, "arrive p50 %.1f p99 %.0f max %.0f  >=40ms %d  burst %d\n", s.arriveP50, s.arriveP99, s.arriveMax, s.stalls, s.bursts))
            sb.append(String.format(Locale.ROOT, "decode p50 %.1f p95 %.1f max %.1f\n", s.decP50, s.decP95, s.decMax))
            sb.append(String.format(Locale.ROOT, "holds %.1f/s  skipped %.1f/s  longest %.0fms  uneven %.1f%%\n", s.holds / s.seconds, (skip - lastSkipped) / dt, s.longestMs, s.uneven))
            sb.append(String.format(Locale.ROOT, "touch>shown %.0fms (n=%d)  cap>shown %.0fms  rendered %d/%d\n", s.touchMs, s.touchN, s.capToShownMs, s.renderedN, s.shown))
            sb.append(String.format(Locale.ROOT, "pace %s  buf %s %.1fms phase %.1f  panel %.1fHz mode %.0f vsync %.1fms (rr %s %dfps, switches %d, slow %d)\n", settings.pace, settings.buf, c.delayMs, c.phaseMs, c.displayHz, c.modeHz, c.vsyncMs, settings.rr, sourceFps, c.rrSwitches.get(), c.slowVsync.get()))
            sb.append(String.format(Locale.ROOT, "keyreq %d  prekey %d  idgap %d  corrupt %d  codecErr %d  reconn %d  ptr %d", c.keyRequests.get(), c.droppedPreKey.get(), c.idGaps.get(), c.corrupt.get(), c.codecErrors.get(), c.reconnects.get(), c.pointerSent.get()))
            if (BuildConfig.DIAG) sb.append(if (recStartNs == 0L) "\n[네 손가락 1초: 기록 시작]" else String.format(Locale.ROOT, "\n[기록 중 %.0f초 - 네 손가락 1초: 끝]", (now - recStartNs) / 1e9))
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
            "route" to (i?.route ?: ""), "host" to (i?.host ?: ""), "pc" to (session?.pcName ?: ""), "settings" to settings.describe(), "hud" to hudOn,
            "decoder" to counters.decoder, "decoderKeys" to counters.decoderKeys, "width" to counters.width, "height" to counters.height,
            "displayHz" to counters.displayHz, "modeHz" to counters.modeHz, "sourceFps" to sourceFps, "slowVsync" to counters.slowVsync.get(), "vsyncMs" to counters.vsyncMs, "wifi" to wifiLine(), "wifiLock" to (wifiLock != null))
        Thread({
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_BACKGROUND)
            try { Exporter.write(file, trace, counters, start, end, head, info); Log.i(tag, "REC saved " + file.absolutePath) }
            catch (e: Exception) { Log.e(tag, "REC failed", e) }
        }, "playlink-diag").start()
    }
}
