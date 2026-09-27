package com.dave.carai

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.speech.SpeechRecognizer
import android.speech.tts.TextToSpeech
import android.text.InputType
import android.view.KeyEvent
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.util.Locale

/** Setup screen. Only needed once; after that Dave works from the steering wheel button. */
class MainActivity : Activity() {

    private lateinit var prefs: Prefs
    private lateinit var keyField: EditText
    private lateinit var locationField: EditText
    private lateinit var languageField: EditText
    private lateinit var secondLanguageField: EditText
    private lateinit var spotifyField: EditText
    private lateinit var status: TextView

    private val prefListener = SharedPreferences.OnSharedPreferenceChangeListener { _, _ -> refresh() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)
        val pad = dp(20)

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
        }

        root.addView(label("Groq API key (free at console.groq.com/keys)"))
        keyField = EditText(this).apply {
            hint = "gsk_..."
            setText(prefs.groqKey)
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            isSingleLine = true
        }
        root.addView(keyField)

        root.addView(label("Country (for units and currency; your exact position comes from GPS)"))
        locationField = EditText(this).apply {
            setText(prefs.location)
            isSingleLine = true
        }
        root.addView(locationField)

        root.addView(label("Main language (nl-NL = Dutch, en-US = English)"))
        languageField = EditText(this).apply {
            setText(prefs.language)
            isSingleLine = true
        }
        root.addView(languageField)

        root.addView(label("Second language Dave also understands and answers in (empty = none)"))
        secondLanguageField = EditText(this).apply {
            setText(prefs.secondLanguage)
            hint = "en-US"
            isSingleLine = true
        }
        root.addView(secondLanguageField)

        status = TextView(this).apply {
            textSize = 16f
            setPadding(0, pad, 0, pad)
        }
        root.addView(status)

        root.addView(button("1. Allow microphone and location") {
            requestPermissions(arrayOf(
                Manifest.permission.RECORD_AUDIO,
                Manifest.permission.ACCESS_FINE_LOCATION,
                Manifest.permission.ACCESS_COARSE_LOCATION,
            ), 1)
        })
        root.addView(button("2. Turn on the button listener") {
            startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        })
        root.addView(button("3. Learn steering wheel button") {
            if (!buttonListenerOn()) {
                toast("Do step 2 first")
            } else {
                prefs.learning = true
                toast("Press the button you want to use now")
            }
        })
        root.addView(button("Test Dave now") {
            save()
            startActivity(Intent(this, ListenActivity::class.java))
        })

        root.addView(label("Or type a question (skips the microphone, handy on an emulator)"))
        val questionField = EditText(this).apply {
            hint = "What's the weather in Amsterdam?"
            isSingleLine = true
        }
        root.addView(questionField)
        root.addView(button("Ask by typing") {
            save()
            startActivity(
                Intent(this, ListenActivity::class.java)
                    .putExtra(ListenActivity.EXTRA_QUESTION, questionField.text.toString())
            )
        })

        root.addView(label("Hands-free"))
        wakeButton = button(wakeLabel()) {
            prefs.wakeWord = !prefs.wakeWord
            if (prefs.wakeWord) {
                if (Build.VERSION.SDK_INT >= 33) requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 2)
                WakeWordService.start(this)
                toast("Say “Hé Dave” to start Dave")
            } else {
                WakeWordService.stop(this)
            }
            wakeButton.text = wakeLabel()
        }
        root.addView(wakeButton)

        root.addView(label("Dave's voice"))
        root.addView(button("Choose main-language voice (tap one to hear it)") { save(); chooseVoice(prefs.language) })
        root.addView(button("Choose second-language voice") {
            save()
            if (prefs.secondLanguage.isEmpty()) toast("Set a second language first") else chooseVoice(prefs.secondLanguage)
        })
        speedButton = button(speedLabel()) { cycleSpeed() }
        root.addView(speedButton)

        root.addView(label("Spotify (Premium) — so Dave can play songs on your iPhone through CarPlay"))
        root.addView(TextView(this).apply {
            text = "Create an app at developer.spotify.com/dashboard with redirect URI:\n${SpotifyClient.REDIRECT_URI}\nand paste its Client ID here."
            setTextIsSelectable(true)
        })
        spotifyField = EditText(this).apply {
            hint = "Spotify Client ID"
            setText(prefs.spotifyClientId)
            isSingleLine = true
        }
        root.addView(spotifyField)
        root.addView(button("Connect Spotify") {
            save()
            if (prefs.spotifyClientId.isEmpty()) {
                toast("Paste the Spotify Client ID first")
            } else {
                startActivity(Intent(Intent.ACTION_VIEW, SpotifyClient.loginUrl(prefs)))
            }
        })

        setContentView(ScrollView(this).apply { addView(root) })
    }

    override fun onResume() {
        super.onResume()
        prefs.sp.registerOnSharedPreferenceChangeListener(prefListener)
        refresh()
    }

    override fun onPause() {
        save()
        prefs.sp.unregisterOnSharedPreferenceChangeListener(prefListener)
        super.onPause()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        refresh()
    }

    private fun save() {
        prefs.groqKey = keyField.text.toString()
        prefs.location = locationField.text.toString()
        prefs.language = languageField.text.toString()
        prefs.secondLanguage = secondLanguageField.text.toString()
        if (spotifyField.text.toString().trim() != prefs.spotifyClientId) {
            prefs.spotifyClientId = spotifyField.text.toString()
            prefs.spotifyRefresh = "" // different app: log in again
        }
    }

    private fun refresh() {
        val mic = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        val key = when {
            prefs.learning -> "waiting for you to press it…"
            prefs.keyCode == 0 -> "not set"
            else -> KeyEvent.keyCodeToString(prefs.keyCode)
        }
        status.text = listOf(
            (if (prefs.groqKey.isNotEmpty()) "✅" else "❌") + " Groq key",
            (if (mic) "✅" else "❌") + " Microphone",
            (if (DriverLocation.hasPermission(this)) "✅" else "❌") + " Location",
            (if (buttonListenerOn()) "✅" else "❌") + " Button listener",
            (if (prefs.keyCode != 0) "✅" else "❌") + " Button: $key",
            (if (SpeechRecognizer.isRecognitionAvailable(this)) "✅ Speech recognition"
            else "❌ Speech recognition (install the Google app)"),
            (if (SpotifyClient.isConnected(prefs)) "✅ Spotify connected" else "➖ Spotify not connected (optional)"),
        ).joinToString("\n")
    }

    private lateinit var wakeButton: Button
    private fun wakeLabel() = if (prefs.wakeWord) "“Hé Dave” wake word: ON (tap to turn off)" else "“Hé Dave” wake word: OFF (tap to turn on)"

    // --- Voice picker ---

    private var previewTts: TextToSpeech? = null
    private var previewReady = false
    private lateinit var speedButton: Button
    private val speeds = listOf(0.85f to "slow", 1.0f to "normal", 1.15f to "a bit faster", 1.3f to "fast")

    /** Run [action] once the preview text-to-speech engine is ready. */
    private fun withPreviewTts(action: (TextToSpeech) -> Unit) {
        val existing = previewTts
        if (existing != null) {
            if (previewReady) action(existing)
            return
        }
        previewTts = TextToSpeech(this) { status ->
            if (status == TextToSpeech.SUCCESS) {
                previewReady = true
                previewTts?.let(action)
            } else {
                toast("No text-to-speech engine found")
            }
        }
    }

    /** Pick the voice for [languageTag], which is either the main or the second language. */
    private fun chooseVoice(languageTag: String) = withPreviewTts { tts ->
        val isMain = languageTag == prefs.language
        val wanted = Locale.forLanguageTag(languageTag)
        val voices = runCatching { tts.voices }.getOrNull().orEmpty()
            .filter { it.locale.language == wanted.language }
            .filterNot { TextToSpeech.Engine.KEY_FEATURE_NOT_INSTALLED in it.features }
            .sortedWith(compareBy({ it.locale.country != wanted.country }, { it.isNetworkConnectionRequired }, { it.name }))
        if (voices.isEmpty()) {
            toast("No voices for $languageTag. Install 'Speech Recognition & Synthesis' from Google.")
            return@withPreviewTts
        }
        val labels = voices.mapIndexed { i, v ->
            "Voice ${i + 1}  (${v.locale.displayCountry}${if (v.isNetworkConnectionRequired) ", online" else ""})"
        }
        val current = if (isMain) prefs.voiceName else prefs.secondVoiceName
        AlertDialog.Builder(this)
            .setTitle("Dave's ${wanted.displayLanguage} voice")
            .setSingleChoiceItems(labels.toTypedArray(), voices.indexOfFirst { it.name == current }) { _, which ->
                if (isMain) prefs.voiceName = voices[which].name else prefs.secondVoiceName = voices[which].name
                preview(tts, languageTag)
            }
            .setPositiveButton("OK", null)
            .show()
    }

    private fun cycleSpeed() {
        val next = speeds[(speeds.indexOfFirst { it.first == prefs.speechRate } + 1) % speeds.size]
        prefs.speechRate = next.first
        speedButton.text = speedLabel()
        withPreviewTts { preview(it) }
    }

    private fun speedLabel() = "Speaking speed: " + (speeds.firstOrNull { it.first == prefs.speechRate }?.second ?: "normal")

    private fun preview(tts: TextToSpeech, languageTag: String = prefs.language) {
        tts.useDaveVoice(prefs, languageTag)
        val sample = if (languageTag.startsWith("nl")) "Hoi, ik ben Dave. Waar kan ik je mee helpen?" else "Hi, I'm Dave. How can I help?"
        tts.speak(sample, TextToSpeech.QUEUE_FLUSH, null, "preview")
    }

    override fun onDestroy() {
        previewTts?.shutdown()
        super.onDestroy()
    }

    private fun buttonListenerOn(): Boolean {
        val enabled = Settings.Secure.getString(contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: ""
        return enabled.contains("$packageName/")
    }

    private fun label(text: String) = TextView(this).apply {
        this.text = text
        setPadding(0, dp(12), 0, 0)
    }

    private fun button(text: String, onClick: () -> Unit) = Button(this).apply {
        this.text = text
        setOnClickListener { onClick() }
    }

    private fun toast(text: String) = Toast.makeText(this, text, Toast.LENGTH_LONG).show()
}
