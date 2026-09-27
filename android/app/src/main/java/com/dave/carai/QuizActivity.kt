package com.dave.carai

import android.app.Activity
import android.content.Intent
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.TextUtils
import android.util.Log
import android.view.Gravity
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import org.json.JSONArray
import kotlin.concurrent.thread

/**
 * Music quiz: plays a tiny bit of a song on Spotify (your iPhone through CarPlay), you guess.
 * "Weet ik niet" plays a longer bit, "nog een keer" replays the same bit, "we geven het op" reveals it, "stop" ends the quiz.
 * Pressing the steering wheel button during a snippet stops it so you can answer right away.
 */
class QuizActivity : Activity() {

    companion object {
        const val EXTRA_THEME = "theme"
        /** True while a quiz is running, so the steering wheel button comes here instead of starting Dave. */
        @Volatile var active = false

        /** How long each snippet plays: tiny first, longer every time you don't know it. */
        private val SNIPPET_MS = listOf(1_500L, 3_000L, 6_000L, 12_000L, 20_000L, 35_000L)
        private const val REWARD_START_MS = 45_000
        private const val REWARD_MS = 8_000L
    }

    private data class Song(val title: String, val artist: String, val uri: String)

    private lateinit var prefs: Prefs
    private lateinit var voice: Voice
    private lateinit var bubble: TextView
    private val handler = Handler(Looper.getMainLooper())

    private val songs = mutableListOf<Song>()
    private var index = 0
    private var level = 0
    private var score = 0
    private var misheard = 0
    private var stopped = false
    private var snippetEnd: Runnable? = null

    private val song get() = songs[index]
    private fun t(english: String, dutch: String) = AiClient.say(prefs, english, dutch)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WakeWordService.pause() // hand the microphone to the real speech recognition
        prefs = Prefs(this)
        active = true
        buildBubble()
        voice = Voice(this, prefs)
        voice.holdFocus()
        show(t("🎵 Music quiz\nFinding songs…", "🎵 Muziekquiz\nNummers zoeken…"))

        val theme = intent.getStringExtra(EXTRA_THEME)?.takeIf { it.isNotBlank() } ?: "mixed hits"
        thread {
            val requested = runCatching { AiClient.quizSongs(prefs, theme) }
                .onFailure { Log.w("Dave", "Quiz: picking songs failed", it) }
                .getOrDefault(JSONArray())
            for (i in 0 until requested.length()) {
                val item = requested.optJSONObject(i) ?: continue
                val title = item.optString("title")
                val artist = item.optString("artist")
                runCatching { SpotifyClient.findTrack(prefs, title, artist) }
                    .onFailure { Log.w("Dave", "Quiz: couldn't find $title", it) }
                    .getOrNull()?.let { songs += Song(title, artist, it) }
            }
            songs.shuffle()
            runOnUiThread {
                if (songs.isEmpty()) {
                    voice.speak(t("I couldn't find any songs for the quiz on Spotify.",
                        "Ik kon geen nummers voor de quiz vinden op Spotify.")) { finish() }
                } else {
                    intro()
                }
            }
        }
    }

    private fun intro() {
        voice.speak(t(
            "Music quiz! I'll play a tiny bit of a song. Say the title, say you don't know it to hear more, or say you give up.",
            "Muziekquiz! Ik speel steeds een klein stukje van een nummer. Zeg de titel, zeg 'weet ik niet' voor een langer stukje, of zeg dat je het opgeeft.",
        )) { playSnippet() }
    }

    // --- Rounds ---

    private fun playSnippet() {
        if (stopped) return
        val length = SNIPPET_MS[level.coerceAtMost(SNIPPET_MS.lastIndex)]
        show(t("🎵 Song ${index + 1}/${songs.size} · score $score\n▶ listen…",
            "🎵 Nummer ${index + 1}/${songs.size} · score $score\n▶ luister…"))
        voice.releaseFocus()
        val uri = song.uri
        thread {
            val started = runCatching { SpotifyClient.playTrack(prefs, uri, 0) }
            runOnUiThread {
                started.exceptionOrNull()?.let { return@runOnUiThread fail(it) }
                snippetEnd = Runnable { endSnippet() }.also { handler.postDelayed(it, length) }
            }
        }
    }

    private fun endSnippet() {
        snippetEnd?.let { handler.removeCallbacks(it) }
        snippetEnd = null
        thread {
            runCatching { SpotifyClient.pause(prefs) }
            runOnUiThread { askGuess() }
        }
    }

    private fun askGuess() {
        if (stopped) return
        voice.holdFocus()
        voice.speak(if (level == 0) t("What song is this?", "Welk nummer is dit?") else t("And now?", "En nu?")) { listenForGuess() }
    }

    private fun listenForGuess() {
        if (stopped) return
        voice.beep()
        show(t("🎤 Your guess?", "🎤 Wat denk je?"))
        handler.postDelayed({
            voice.listen(onPartial = { show("🎤 $it") }) { heard ->
                when {
                    heard != null -> { misheard = 0; judge(heard) }
                    misheard++ < 1 -> voice.speak(t("I didn't catch that.", "Dat verstond ik niet.")) { listenForGuess() }
                    else -> { misheard = 0; handle("dont_know", t("Let's hear a bit more.", "Dan een stukje langer.")) }
                }
            }
        }, 300)
    }

    private fun judge(heard: String) {
        show("“$heard”")
        val current = song
        thread {
            val (verdict, reaction) = runCatching { AiClient.judgeQuiz(prefs, current.title, current.artist, heard) }
                .getOrElse {
                    Log.w("Dave", "Quiz judging failed", it)
                    "dont_know" to t("I didn't get that, here's a bit more.", "Dat snapte ik niet, hier is een langer stukje.")
                }
            runOnUiThread { if (!stopped) handle(verdict, reaction) }
        }
    }

    private fun handle(verdict: String, reaction: String) {
        val reveal = t("It was ${song.title} by ${song.artist}.", "Het was ${song.title} van ${song.artist}.")
        when (verdict) {
            "correct" -> {
                score++
                show("✅ ${song.title} – ${song.artist}")
                voice.speak("$reaction $reveal") { reward() }
            }
            "give_up", "skip" -> {
                show("🏳 ${song.title} – ${song.artist}")
                voice.speak("$reaction $reveal") { reward() }
            }
            "stop" -> endQuiz()
            "replay" -> voice.speak(reaction) { playSnippet() } // same length as last time
            else -> { // wrong, close, dont_know: longer snippet
                level++
                voice.speak(reaction) { playSnippet() }
            }
        }
    }

    /** Play a bit of the chorus as a reward, then the next song. */
    private fun reward() {
        if (stopped) return
        voice.releaseFocus()
        val uri = song.uri
        thread { runCatching { SpotifyClient.playTrack(prefs, uri, REWARD_START_MS) } }
        handler.postDelayed({ nextSong() }, REWARD_MS)
    }

    private fun nextSong() {
        index++
        level = 0
        if (index >= songs.size) endQuiz() else playSnippet()
    }

    private fun endQuiz() {
        if (stopped) return
        stopped = true
        snippetEnd?.let { handler.removeCallbacks(it) }
        voice.stopListening()
        thread { runCatching { SpotifyClient.pause(prefs) } }
        voice.holdFocus()
        val played = index.coerceAtMost(songs.size) // songs finished so far
        show(t("🏁 Score: $score / $played", "🏁 Score: $score / $played"))
        voice.speak(t("That's the end of the quiz! You got $score out of $played.",
            "Einde van de quiz! Je had er $score van de $played goed.")) { finish() }
    }

    private fun fail(error: Throwable) {
        stopped = true
        Log.w("Dave", "Quiz failed", error)
        voice.holdFocus()
        voice.speak((error as? SpotifyClient.SpotifyException)?.message
            ?: t("The quiz stopped because Spotify didn't respond.", "De quiz is gestopt omdat Spotify niet reageerde.")) { finish() }
    }

    /** Steering wheel button during a snippet: stop it and answer now. */
    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        if (snippetEnd != null) endSnippet()
    }

    // --- UI ---

    private fun buildBubble() {
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
            setOnClickListener { endQuiz() } // tap the bubble to stop
        }
        setContentView(FrameLayout(this).apply {
            setPadding(dp(16), dp(16), dp(16), dp(32))
            addView(bubble)
        })
        window.apply {
            addFlags(WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL)
            clearFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
            setGravity(Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL)
            setLayout(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        }
    }

    private fun show(text: String) {
        bubble.text = text
    }

    override fun onDestroy() {
        WakeWordService.resume()
        active = false
        stopped = true
        handler.removeCallbacksAndMessages(null)
        voice.destroy()
        super.onDestroy()
    }
}
