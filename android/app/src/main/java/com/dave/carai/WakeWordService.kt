package com.dave.carai

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import org.json.JSONObject
import org.vosk.Model
import org.vosk.Recognizer
import org.vosk.android.RecognitionListener
import org.vosk.android.SpeechService
import org.vosk.android.StorageService

/**
 * Listens for "Hé Dave" completely offline (Vosk). Only when it hears that does Dave open and
 * the real speech recognition start. It lets go of the microphone while Dave is listening.
 */
class WakeWordService : Service(), RecognitionListener {

    companion object {
        private const val TAG = "Dave"
        private const val CHANNEL = "wake_word"
        private val WAKE_PHRASES = setOf("hey dave", "hi dave", "okay dave", "hay dave")
        // Near-miss words give the recognizer somewhere else to go, so "hey" alone doesn't trigger.
        private const val GRAMMAR = """["hey dave", "hi dave", "okay dave", "hay dave", "hey", "hi", "okay", "dave", "day", "play", "[unk]"]"""

        @Volatile private var instance: WakeWordService? = null
        /** How many Dave screens are open; the wake word waits while this is above zero. */
        @Volatile private var busy = 0

        fun start(context: Context) {
            if (!Prefs(context).wakeWord) return
            if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return
            runCatching { context.startForegroundService(Intent(context, WakeWordService::class.java)) }
                .onFailure { Log.w(TAG, "Couldn't start wake word listening", it) }
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, WakeWordService::class.java))
        }

        /** Dave (or the quiz) is using the microphone. */
        fun pause() {
            busy++
            Handler(Looper.getMainLooper()).post { instance?.stopListening() }
        }

        fun resume() {
            busy = (busy - 1).coerceAtLeast(0)
            // Short delay so the other recognizer has really let go of the microphone.
            Handler(Looper.getMainLooper()).postDelayed({ if (busy == 0) instance?.startListening() }, 800)
        }
    }

    private var model: Model? = null
    private var recognizer: Recognizer? = null
    private var speech: SpeechService? = null
    private var lastTrigger = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        goForeground()
        StorageService.unpack(this, "model-en-us", "model", { loaded ->
            model = loaded
            recognizer = Recognizer(loaded, 16000f, GRAMMAR)
            Log.d(TAG, "Wake word model loaded")
            if (busy == 0) startListening()
        }, { error -> Log.w(TAG, "Wake word model failed to load", error) })
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int) = START_STICKY

    private fun goForeground() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Hé Dave", NotificationManager.IMPORTANCE_MIN))
        val notification = Notification.Builder(this, CHANNEL)
            .setContentTitle("Dave is listening for “Hé Dave”")
            .setContentText("Offline: nothing leaves the screen until you say it.")
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 29) startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
        else startForeground(1, notification)
    }

    private fun startListening() {
        val rec = recognizer ?: return
        if (speech != null) return
        runCatching {
            rec.reset()
            speech = SpeechService(rec, 16000f).also { it.startListening(this) }
            Log.d(TAG, "Wake word listening")
        }.onFailure { Log.w(TAG, "Wake word listening failed", it) }
    }

    private fun stopListening() {
        speech?.let {
            it.stop()
            it.shutdown() // release the microphone completely
        }
        speech = null
    }

    private fun check(json: String, key: String) {
        val heard = runCatching { JSONObject(json).optString(key) }.getOrDefault("").trim()
        if (heard.isEmpty() || WAKE_PHRASES.none { heard.endsWith(it) }) return
        val now = System.currentTimeMillis()
        if (now - lastTrigger < 3_000) return
        lastTrigger = now
        Log.d(TAG, "Wake word heard: $heard")
        val target = if (QuizActivity.active) QuizActivity::class.java else ListenActivity::class.java
        startActivity(Intent(this, target).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }

    override fun onPartialResult(hypothesis: String) = check(hypothesis, "partial")
    override fun onResult(hypothesis: String) = check(hypothesis, "text")
    override fun onFinalResult(hypothesis: String) = check(hypothesis, "text")
    override fun onError(exception: Exception) { Log.w(TAG, "Wake word error", exception) }
    override fun onTimeout() {}

    override fun onDestroy() {
        stopListening()
        recognizer?.close()
        model?.close()
        instance = null
        super.onDestroy()
    }
}
