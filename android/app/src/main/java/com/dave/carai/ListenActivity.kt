package com.dave.carai

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.media.ToneGenerator
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.SpeechRecognizer
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import android.text.TextUtils
import android.util.Log
import android.view.Gravity
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import kotlin.concurrent.thread

/**
 * One question to Dave: beep -> listen -> send to backend -> speak the reply -> close.
 * Shows as a small bubble at the bottom of the screen on top of whatever is running.
 * Press the button again (or tap the bubble) to cancel.
 */
class ListenActivity : Activity(), RecognitionListener, TextToSpeech.OnInitListener {

    companion object {
        /** Skip the microphone and ask this text instead (for testing on emulators). */
        const val EXTRA_QUESTION = "question"
        /** Say this reminder out loud instead of listening. */
        const val EXTRA_ANNOUNCE = "announce"
        private const val TAG = "Dave"
    }

    private lateinit var prefs: Prefs
    private lateinit var bubble: TextView
    private lateinit var audio: AudioManager
    private val handler = Handler(Looper.getMainLooper())

    private val speechAttributes = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_ASSISTANT)
        .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
        .build()

    private var focus: AudioFocusRequest? = null
    private var recognizer: SpeechRecognizer? = null
    private var tts: TextToSpeech? = null
    private var ttsReady = false
    private var ttsBroken = false
    private var pendingSpeech: String? = null
    private var gotResult = false
    private var keepListening = false // conversation mode: listen again after this answer
    private var followUp = false
    private val timeout = Runnable { finish() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WakeWordService.pause() // hand the microphone to the real speech recognition
        prefs = Prefs(this)
        audio = getSystemService(AudioManager::class.java)

        bubble = TextView(this).apply {
            textSize = 20f
            setTextColor(Color.WHITE)
            maxLines = 4
            ellipsize = TextUtils.TruncateAt.END
            setPadding(dp(28), dp(16), dp(28), dp(16))
            background = GradientDrawable().apply {
                setColor(0xE6202030.toInt())
                cornerRadius = dp(32).toFloat()
            }
            setOnClickListener { finish() }
        }
        setContentView(FrameLayout(this).apply {
            setPadding(dp(16), dp(16), dp(16), dp(32))
            addView(bubble)
        })

        window.apply {
            addFlags(WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL) // taps outside go to CarPlay
            clearFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
            setGravity(Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL)
            setLayout(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        }

        tts = TextToSpeech(this, this)
        DriverLocation.refresh(this) // fresh GPS fix while you talk
        handler.postDelayed(timeout, 90_000) // never hang around forever

        val typedQuestion = intent.getStringExtra(EXTRA_QUESTION)?.trim()
        val announcement = intent.getStringExtra(EXTRA_ANNOUNCE)
        when {
            announcement != null -> { // a reminder is due
                requestFocus()
                beep()
                handler.postDelayed({ say(t("Reminder: ", "Herinnering: ") + announcement) }, 400)
            }
            prefs.groqKey.isEmpty() ->
                say(t("Open the Dave app and enter your Groq key first.", "Open de Dave-app en vul eerst je Groq-sleutel in."))
            !typedQuestion.isNullOrEmpty() -> {
                requestFocus()
                ask(typedQuestion)
            }
            checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED ->
                say(t("Open the Dave app and allow the microphone first.", "Open de Dave-app en geef eerst toegang tot de microfoon."))
            !SpeechRecognizer.isRecognitionAvailable(this) ->
                say(t("This device has no speech recognition. Install the Google app.", "Dit apparaat heeft geen spraakherkenning. Installeer de Google-app."))
            else -> start()
        }
    }

    /** Pressing the button again while Dave is open cancels it. */
    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        finish()
    }

    private fun requestFocus() {
        focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_EXCLUSIVE)
            .setAudioAttributes(speechAttributes)
            .build()
            .also { audio.requestAudioFocus(it) } // pauses/lowers CarPlay music
    }

    private fun start() {
        requestFocus()
        show(t("Listening…", "Ik luister…"))
        beep()
        handler.postDelayed({ listen() }, 300)
    }

    private fun beep() {
        val tone = ToneGenerator(AudioManager.STREAM_MUSIC, 80)
        tone.startTone(ToneGenerator.TONE_PROP_BEEP, 150)
        handler.postDelayed({ tone.release() }, 400)
    }

    /** Conversation mode: Dave asked something back, so listen again without the button. */
    private fun listenAgain() {
        followUp = true
        gotResult = false
        recognizer?.destroy()
        recognizer = null
        handler.removeCallbacks(timeout)
        handler.postDelayed(timeout, 90_000)
        start()
    }

    private fun listen() {
        val service = googleRecognizer()
        Log.d(TAG, "Using recognizer ${service ?: "system default"}")
        val created = if (service != null) SpeechRecognizer.createSpeechRecognizer(this, service)
        else SpeechRecognizer.createSpeechRecognizer(this)
        recognizer = created.also {
            it.setRecognitionListener(this)
            it.startListening(prefs.recognizerIntent())
        }
    }

    private fun ask(text: String) {
        show("“$text”\n" + t("Thinking…", "Even denken…"))
        thread {
            val reply = try {
                AiClient.ask(prefs, text, DriverLocation.describe(this))
            } catch (e: AiClient.AiException) {
                AiClient.Result.Speak(e.message ?: "")
            } catch (e: Exception) {
                Log.w(TAG, "AI request failed", e)
                AiClient.Result.Speak(t("I can't reach the AI right now. Check the internet connection.",
                    "Ik kan de AI nu niet bereiken. Controleer de internetverbinding."))
            }
            runOnUiThread {
                if (isFinishing) return@runOnUiThread
                when (reply) {
                    is AiClient.Result.Speak -> {
                        keepListening = reply.text.trim().endsWith("?") // Dave asked something back
                        say(reply.text)
                    }
                    is AiClient.Result.Command -> runCommand(reply)
                }
            }
        }
    }

    /**
     * Give the music back first (our audio focus paused it), then send the command,
     * so "next song" or "louder" acts on music that's actually playing.
     */
    private fun runCommand(command: AiClient.Result.Command) {
        if (command.name == "start_music_quiz") return startQuiz(command)
        releaseFocus()
        handler.postDelayed({
            thread {
                val outcome = runCatching { MediaCommands.run(this, prefs, command.name, command.args) }
                    .getOrElse {
                        Log.w(TAG, "Command failed", it)
                        MediaCommands.Outcome(t("That didn't work.", "Dat lukte niet."), speak = true)
                    }
                runOnUiThread { if (!isFinishing) showOutcome(outcome) }
            }
        }, 500)
    }

    private fun showOutcome(outcome: MediaCommands.Outcome) {
        if (outcome.speak) {
            say(outcome.text)
            return
        }
        show(outcome.text)
        ToneGenerator(AudioManager.STREAM_MUSIC, 60).apply {
            startTone(ToneGenerator.TONE_PROP_ACK, 120)
            handler.postDelayed({ release() }, 300)
        }
        handler.postDelayed({ finish() }, 1_500)
    }

    private fun startQuiz(command: AiClient.Result.Command) {
        if (!SpotifyClient.isConnected(prefs)) {
            say(t("The music quiz needs Spotify. Connect it in the Dave app first.",
                "Voor de muziekquiz heb ik Spotify nodig. Koppel het eerst in de Dave-app."))
            return
        }
        startActivity(Intent(this, QuizActivity::class.java)
            .putExtra(QuizActivity.EXTRA_THEME, command.args.optString("theme"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        finish()
    }

    private fun releaseFocus() {
        focus?.let { audio.abandonAudioFocusRequest(it) }
        focus = null
    }

    private fun t(english: String, dutch: String) = AiClient.say(prefs, english, dutch)

    /** Show text in the bubble and read it out, then close when done. */
    private fun say(text: String) {
        Log.d(TAG, "Says: $text")
        show(text)
        when {
            ttsReady -> speakNow(text)
            ttsBroken -> handler.postDelayed({ finish() }, 5_000)
            else -> pendingSpeech = text
        }
    }

    private fun speakNow(text: String) {
        tts?.useDaveVoice(prefs, prefs.languageOf(text)) // Dutch or English voice to match the answer
        val result = tts?.speak(text, TextToSpeech.QUEUE_FLUSH, null, "reply")
        Log.d(TAG, "speak() returned $result")
        if (result != TextToSpeech.SUCCESS) handler.postDelayed({ finish() }, 5_000)
    }

    private fun show(text: String) {
        bubble.text = text
    }

    // --- Text to speech ---

    override fun onInit(status: Int) {
        val engine = tts ?: return
        Log.d(TAG, "TTS init status=$status engine=${engine.defaultEngine} voice=${engine.voice}")
        if (status != TextToSpeech.SUCCESS) {
            ttsBroken = true
            if (pendingSpeech != null) handler.postDelayed({ finish() }, 5_000)
            return
        }
        engine.useDaveVoice(prefs)
        Log.d(TAG, "Voice: ${engine.voice?.name}")
        engine.setAudioAttributes(speechAttributes)
        engine.setOnUtteranceProgressListener(object : UtteranceProgressListener() {
            override fun onStart(utteranceId: String?) {}
            override fun onDone(utteranceId: String?) = runOnUiThread {
                if (keepListening && !isFinishing) {
                    keepListening = false
                    listenAgain()
                } else {
                    finish()
                }
            }
            @Deprecated("Deprecated in Java")
            override fun onError(utteranceId: String?) = runOnUiThread { finish() }
        })
        ttsReady = true
        pendingSpeech?.let {
            pendingSpeech = null
            speakNow(it)
        }
    }

    // --- Speech recognition ---

    override fun onPartialResults(partialResults: Bundle?) {
        partialResults?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)
            ?.firstOrNull()
            ?.takeIf { it.isNotBlank() }
            ?.let { show(it) }
    }

    override fun onResults(results: Bundle?) {
        gotResult = true
        val text = results?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()
        if (text.isNullOrBlank()) say(t("I didn't catch that.", "Dat verstond ik niet.")) else ask(text)
    }

    override fun onError(error: Int) {
        if (gotResult || isFinishing) return // some devices report a harmless error after the result
        if (followUp && (error == SpeechRecognizer.ERROR_NO_MATCH || error == SpeechRecognizer.ERROR_SPEECH_TIMEOUT)) {
            finish() // no answer to Dave's question: just close
            return
        }
        say(
            when (error) {
                SpeechRecognizer.ERROR_NO_MATCH, SpeechRecognizer.ERROR_SPEECH_TIMEOUT ->
                    t("I didn't catch that.", "Dat verstond ik niet.")
                SpeechRecognizer.ERROR_NETWORK, SpeechRecognizer.ERROR_NETWORK_TIMEOUT ->
                    t("Speech recognition needs internet.", "Spraakherkenning heeft internet nodig.")
                SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS ->
                    t("I'm not allowed to use the microphone.", "Ik mag de microfoon niet gebruiken.")
                // ERROR_LANGUAGE_NOT_SUPPORTED / ERROR_LANGUAGE_UNAVAILABLE (Android 12+)
                12, 13 -> t("The speech recognizer on this device doesn't support ${prefs.language}. Install the Google app.",
                    "De spraakherkenning op dit apparaat ondersteunt ${prefs.language} niet. Installeer de Google-app.")
                else -> t("Speech recognition error $error.", "Fout bij spraakherkenning, code $error.")
            }
        )
    }

    override fun onReadyForSpeech(params: Bundle?) {}
    override fun onBeginningOfSpeech() {}
    override fun onRmsChanged(rmsdB: Float) {}
    override fun onBufferReceived(buffer: ByteArray?) {}
    override fun onEndOfSpeech() {}
    override fun onEvent(eventType: Int, params: Bundle?) {}

    override fun onDestroy() {
        WakeWordService.resume()
        handler.removeCallbacksAndMessages(null)
        recognizer?.destroy()
        tts?.stop()
        tts?.shutdown()
        releaseFocus() // CarPlay music comes back
        super.onDestroy()
    }
}
