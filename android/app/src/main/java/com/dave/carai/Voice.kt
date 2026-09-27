package com.dave.carai

import android.app.Activity
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.media.ToneGenerator
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.RecognitionService
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import java.util.Locale

/** The Google app's recognizer handles far more languages than the offline ones, so prefer it. */
fun Context.googleRecognizer(): ComponentName? =
    packageManager.queryIntentServices(Intent(RecognitionService.SERVICE_INTERFACE), 0)
        .map { it.serviceInfo }
        .firstOrNull { it.packageName == "com.google.android.googlequicksearchbox" }
        ?.let { ComponentName(it.packageName, it.name) }

/** Set the voice for [languageTag] (one of the two configured languages) and Dave's speed on a text-to-speech engine. */
fun TextToSpeech.useDaveVoice(prefs: Prefs, languageTag: String = prefs.language) {
    setLanguage(Locale.forLanguageTag(languageTag))
    val chosen = if (languageTag == prefs.language) prefs.voiceName else prefs.secondVoiceName
    if (chosen.isNotEmpty()) {
        runCatching { voices?.firstOrNull { it.name == chosen } }.getOrNull()?.let { voice = it }
    }
    setSpeechRate(prefs.speechRate)
}

private val COMMON_WORDS = mapOf(
    "nl" to setOf("de", "het", "een", "en", "ik", "je", "jij", "niet", "van", "op", "dat", "er", "voor", "met", "zijn", "wat",
        "ook", "maar", "nog", "om", "naar", "bij", "als", "dan", "wel", "hier", "nu", "goed", "graag", "uur", "graden", "ongeveer",
        "hoe", "waar", "wanneer", "wie", "waarom", "hoeveel", "kun", "kan", "mijn", "zet", "speel", "vertel", "ver", "laat", "welk", "welke"),
    "en" to setOf("the", "a", "an", "and", "i", "you", "not", "of", "on", "that", "there", "for", "with", "are", "what",
        "also", "but", "still", "to", "at", "if", "then", "here", "now", "good", "it's", "about", "degrees", "o'clock", "your",
        "how", "where", "when", "who", "why", "which", "can", "could", "my", "from", "tell", "play", "turn", "far", "much", "many", "please"),
)

/** Which of the two configured languages [text] is in, judged by common words. */
fun Prefs.languageOf(text: String): String {
    val second = secondLanguage.takeIf { it.isNotEmpty() } ?: return language
    val words = text.lowercase().split(Regex("[^\\p{L}']+"))
    fun score(tag: String) = COMMON_WORDS[tag.take(2)]?.let { common -> words.count { it in common } } ?: 0
    return if (score(second) > score(language)) second else language
}

/** Speech recognition request that understands both configured languages. */
fun Prefs.recognizerIntent() = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
    putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
    putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
    putExtra(RecognizerIntent.EXTRA_LANGUAGE, language)
    if (secondLanguage.isNotEmpty()) {
        putExtra("android.speech.extra.EXTRA_ADDITIONAL_LANGUAGES", arrayOf(secondLanguage)) // Google app
        if (Build.VERSION.SDK_INT >= 34) {
            putExtra(RecognizerIntent.EXTRA_ENABLE_LANGUAGE_DETECTION, true)
            putStringArrayListExtra(RecognizerIntent.EXTRA_LANGUAGE_DETECTION_ALLOWED_LANGUAGES, arrayListOf(language, secondLanguage))
        }
    }
}

/** Speak and listen with callbacks, for back-and-forth conversations like the music quiz. Main thread only. */
class Voice(private val activity: Activity, private val prefs: Prefs) : TextToSpeech.OnInitListener, RecognitionListener {

    private val handler = Handler(Looper.getMainLooper())
    private val audio = activity.getSystemService(AudioManager::class.java)
    private val attributes = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_ASSISTANT)
        .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
        .build()

    private var focus: AudioFocusRequest? = null
    private val tts = TextToSpeech(activity, this)
    private var ttsReady = false
    private var ttsBroken = false
    private var pending: Pair<String, () -> Unit>? = null
    private var onSpoken: (() -> Unit)? = null

    private var recognizer: SpeechRecognizer? = null
    private var onHeard: ((String?) -> Unit)? = null
    private var onPartial: ((String) -> Unit)? = null

    /** Pause other audio (music) while we talk or listen. */
    fun holdFocus() {
        if (focus != null) return
        focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_EXCLUSIVE)
            .setAudioAttributes(attributes)
            .build()
            .also { audio.requestAudioFocus(it) }
    }

    fun releaseFocus() {
        focus?.let { audio.abandonAudioFocusRequest(it) }
        focus = null
    }

    // --- Speaking ---

    fun speak(text: String, onDone: () -> Unit) {
        when {
            ttsReady -> speakNow(text, onDone)
            ttsBroken -> handler.postDelayed(onDone, 2_000)
            else -> pending = text to onDone
        }
    }

    private fun speakNow(text: String, onDone: () -> Unit) {
        onSpoken = onDone
        tts.useDaveVoice(prefs, prefs.languageOf(text))
        if (tts.speak(text, TextToSpeech.QUEUE_FLUSH, null, "voice") != TextToSpeech.SUCCESS) spoken()
    }

    private fun spoken() {
        val done = onSpoken ?: return
        onSpoken = null
        handler.post(done)
    }

    override fun onInit(status: Int) {
        if (status != TextToSpeech.SUCCESS) {
            ttsBroken = true
            pending?.let { pending = null; handler.postDelayed(it.second, 2_000) }
            return
        }
        tts.useDaveVoice(prefs)
        tts.setAudioAttributes(attributes)
        tts.setOnUtteranceProgressListener(object : UtteranceProgressListener() {
            override fun onStart(utteranceId: String?) {}
            override fun onDone(utteranceId: String?) = spoken()
            @Deprecated("Deprecated in Java")
            override fun onError(utteranceId: String?) = spoken()
        })
        ttsReady = true
        pending?.let { pending = null; speakNow(it.first, it.second) }
    }

    fun beep() {
        val tone = ToneGenerator(AudioManager.STREAM_MUSIC, 80)
        tone.startTone(ToneGenerator.TONE_PROP_BEEP, 150)
        handler.postDelayed({ tone.release() }, 400)
    }

    // --- Listening ---

    /** Listen once. [onHeard] gets the text, or null if nothing was understood. */
    fun listen(onPartial: (String) -> Unit, onHeard: (String?) -> Unit) {
        this.onPartial = onPartial
        this.onHeard = onHeard
        recognizer?.destroy()
        val service = activity.googleRecognizer()
        recognizer = (if (service != null) SpeechRecognizer.createSpeechRecognizer(activity, service)
        else SpeechRecognizer.createSpeechRecognizer(activity)).also {
            it.setRecognitionListener(this)
            it.startListening(prefs.recognizerIntent())
        }
    }

    fun stopListening() {
        onHeard = null
        recognizer?.cancel()
    }

    private fun heard(text: String?) {
        val callback = onHeard ?: return
        onHeard = null
        handler.post { callback(text) }
    }

    override fun onResults(results: Bundle?) =
        heard(results?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()?.takeIf { it.isNotBlank() })

    override fun onError(error: Int) = heard(null)

    override fun onPartialResults(partialResults: Bundle?) {
        partialResults?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)
            ?.firstOrNull()?.takeIf { it.isNotBlank() }?.let { onPartial?.invoke(it) }
    }

    override fun onReadyForSpeech(params: Bundle?) {}
    override fun onBeginningOfSpeech() {}
    override fun onRmsChanged(rmsdB: Float) {}
    override fun onBufferReceived(buffer: ByteArray?) {}
    override fun onEndOfSpeech() {}
    override fun onEvent(eventType: Int, params: Bundle?) {}

    fun destroy() {
        handler.removeCallbacksAndMessages(null)
        recognizer?.destroy()
        tts.stop()
        tts.shutdown()
        releaseFocus()
    }
}
