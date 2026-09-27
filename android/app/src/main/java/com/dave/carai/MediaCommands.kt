package com.dave.carai

import android.app.SearchManager
import android.content.Context
import android.content.Intent
import android.media.AudioManager
import android.provider.MediaStore
import android.util.Log
import android.view.KeyEvent
import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.roundToInt

/** Carries out the music and volume commands the AI picks. */
object MediaCommands {

    /** What happened: [text] is shown in the bubble; [speak] means it's a problem worth saying out loud. */
    data class Outcome(val text: String, val speak: Boolean = false)

    private const val SPOTIFY = "com.spotify.music"
    private const val VOLUME_STEP = 0.15 // "louder"/"quieter" moves 15% of the range

    /** Blocking (Spotify calls go over the network); call it off the main thread. */
    fun run(context: Context, prefs: Prefs, name: String, args: JSONObject): Outcome {
        Log.d("Dave", "Command $name $args")
        val spotify = SpotifyClient.isConnected(prefs)
        return try {
            when (name) {
                "media_control" -> if (spotify) spotifyControl(prefs, args.optString("action"))
                else mediaControl(context, prefs, args.optString("action"))
                // Volume stays local: CarPlay audio plays through this screen, so this covers it too.
                "set_volume" -> setVolume(context, prefs, args)
                "play_music" -> if (spotify) Outcome("🎵 " + SpotifyClient.play(prefs, args.optString("query"), args.optString("kind")))
                else playMusic(context, prefs, args.optString("query"))
                "now_playing", "like_song", "add_to_playlist", "play_mix" ->
                    if (spotify) spotifyExtra(prefs, name, args) else needSpotify(prefs)
                "remember" -> remember(prefs, args.optString("fact"))
                "forget" -> forget(prefs, args.optString("fact"))
                "set_reminder" -> setReminder(context, prefs, args)
                "cancel_reminders" -> {
                    val count = Reminders.cancelAll(context, prefs)
                    Outcome(AiClient.say(prefs, "Okay, I cancelled $count reminders.", "Oké, ik heb $count herinneringen geannuleerd."), speak = true)
                }
                else -> Outcome(AiClient.say(prefs, "I can't do that yet.", "Dat kan ik nog niet."), speak = true)
            }
        } catch (e: SpotifyClient.SpotifyException) {
            Outcome(e.message ?: "", speak = true)
        }
    }

    private fun needSpotify(prefs: Prefs) = Outcome(AiClient.say(prefs,
        "For that I need Spotify. Connect it in the Dave app first.",
        "Daarvoor heb ik Spotify nodig. Koppel het eerst in de Dave-app."), speak = true)

    private fun spotifyExtra(prefs: Prefs, name: String, args: JSONObject): Outcome = when (name) {
        "now_playing" -> {
            val track = SpotifyClient.nowPlaying(prefs)
            Outcome(
                if (track == null) AiClient.say(prefs, "Nothing is playing right now.", "Er speelt nu niets.")
                else AiClient.say(prefs, "This is ${track.name} by ${track.artist}.", "Dit is ${track.name} van ${track.artist}."),
                speak = true,
            )
        }
        "like_song" -> Outcome("❤ " + SpotifyClient.likeCurrent(prefs).name)
        "add_to_playlist" -> {
            val playlist = args.optString("playlist").ifBlank { "Dave" }
            Outcome("➕ ${SpotifyClient.addCurrentToPlaylist(prefs, playlist).name} → $playlist")
        }
        else -> playMix(prefs, args.optString("description").ifBlank { "mixed hits" }, args.optInt("minutes", 30))
    }

    /** DJ mode: the AI picks songs, Spotify plays them in order. */
    private fun playMix(prefs: Prefs, description: String, minutes: Int): Outcome {
        val count = (minutes / 3.5).roundToInt().coerceIn(5, 20)
        val songs = AiClient.mixSongs(prefs, description, count)
        val uris = (0 until songs.length()).mapNotNull { i ->
            val song = songs.optJSONObject(i) ?: return@mapNotNull null
            runCatching { SpotifyClient.findTrack(prefs, song.optString("title"), song.optString("artist")) }.getOrNull()
        }
        if (uris.isEmpty()) {
            return Outcome(AiClient.say(prefs, "I couldn't put that mix together.", "Ik kon die mix niet samenstellen."), speak = true)
        }
        SpotifyClient.playTracks(prefs, uris)
        return Outcome("🎧 $description · ${uris.size}")
    }

    private fun remember(prefs: Prefs, fact: String): Outcome {
        if (fact.isNotBlank()) {
            val list = prefs.memories
            list.put(fact.trim())
            while (list.length() > 50) list.remove(0) // keep the newest 50
            prefs.memories = list
        }
        return Outcome(AiClient.say(prefs, "Got it, I'll remember that.", "Oké, dat onthoud ik."), speak = true)
    }

    /** Forget the remembered fact that shares the most words with [fact], or everything. */
    private fun forget(prefs: Prefs, fact: String): Outcome {
        val list = prefs.memories
        val wanted = fact.lowercase()
        if (wanted.contains("everything") || wanted.contains("alles") || wanted.isBlank()) {
            prefs.memories = JSONArray()
        } else {
            val words = wanted.split(Regex("\\W+")).filter { it.length > 2 }.toSet()
            val best = (0 until list.length()).maxByOrNull { i ->
                list.optString(i).lowercase().split(Regex("\\W+")).count { it in words }
            }
            if (best != null) {
                list.remove(best)
                prefs.memories = list
            }
        }
        return Outcome(AiClient.say(prefs, "Okay, I've forgotten that.", "Oké, dat ben ik vergeten."), speak = true)
    }

    private fun setReminder(context: Context, prefs: Prefs, args: JSONObject): Outcome {
        val minutes = if (args.has("minutes")) args.optInt("minutes") else null
        val time = args.optString("time").takeIf { it.isNotBlank() }
        val message = args.optString("message").ifBlank { AiClient.say(prefs, "your reminder", "je herinnering") }
        val at = Reminders.schedule(context, prefs, minutes, time, message)
            ?: return Outcome(AiClient.say(prefs, "I didn't get when to remind you.", "Ik snapte niet wanneer ik je moet herinneren."), speak = true)
        val clock = Reminders.clock(at)
        return Outcome(AiClient.say(prefs, "Okay, I'll remind you at $clock.", "Oké, ik herinner je om $clock."), speak = true)
    }

    private fun spotifyControl(prefs: Prefs, action: String): Outcome {
        val text = label(prefs, action)
            ?: return Outcome(AiClient.say(prefs, "I can't do that yet.", "Dat kan ik nog niet."), speak = true)
        SpotifyClient.control(prefs, action)
        return Outcome(text)
    }

    private fun label(prefs: Prefs, action: String) = when (action) {
        "play" -> AiClient.say(prefs, "▶ Play", "▶ Afspelen")
        "pause" -> AiClient.say(prefs, "⏸ Paused", "⏸ Gepauzeerd")
        "next" -> AiClient.say(prefs, "⏭ Next song", "⏭ Volgend nummer")
        "previous" -> AiClient.say(prefs, "⏮ Previous song", "⏮ Vorig nummer")
        else -> null
    }

    private fun mediaControl(context: Context, prefs: Prefs, action: String): Outcome {
        val key = when (action) {
            "play" -> KeyEvent.KEYCODE_MEDIA_PLAY
            "pause" -> KeyEvent.KEYCODE_MEDIA_PAUSE
            "next" -> KeyEvent.KEYCODE_MEDIA_NEXT
            "previous" -> KeyEvent.KEYCODE_MEDIA_PREVIOUS
            else -> return Outcome(AiClient.say(prefs, "I can't do that yet.", "Dat kan ik nog niet."), speak = true)
        }
        val audio = context.getSystemService(AudioManager::class.java)
        audio.dispatchMediaKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, key))
        audio.dispatchMediaKeyEvent(KeyEvent(KeyEvent.ACTION_UP, key))
        return Outcome(label(prefs, action)!!)
    }

    private fun setVolume(context: Context, prefs: Prefs, args: JSONObject): Outcome {
        val audio = context.getSystemService(AudioManager::class.java)
        val max = audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        val current = audio.getStreamVolume(AudioManager.STREAM_MUSIC)
        val step = (max * VOLUME_STEP).roundToInt().coerceAtLeast(1)
        val target = when {
            args.has("level") -> (args.optInt("level").coerceIn(0, 100) / 100.0 * max).roundToInt()
            args.optString("change") == "up" -> current + step
            args.optString("change") == "down" -> current - step
            else -> current
        }.coerceIn(0, max)
        audio.setStreamVolume(AudioManager.STREAM_MUSIC, target, AudioManager.FLAG_SHOW_UI)
        val percent = (target * 100.0 / max).roundToInt()
        return Outcome("🔊 $percent%")
    }

    private fun playMusic(context: Context, prefs: Prefs, query: String): Outcome {
        val search = Intent(MediaStore.INTENT_ACTION_MEDIA_PLAY_FROM_SEARCH)
            .putExtra(SearchManager.QUERY, query)
            .putExtra(MediaStore.EXTRA_MEDIA_FOCUS, "vnd.android.cursor.item/*")
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        // Prefer Spotify when it's installed; otherwise any music app that supports voice search.
        val spotify = Intent(search).setPackage(SPOTIFY)
        val intent = when {
            spotify.resolveActivity(context.packageManager) != null -> spotify
            search.resolveActivity(context.packageManager) != null -> search
            else -> return Outcome(
                AiClient.say(prefs, "I need Spotify on this screen to play music.", "Ik heb Spotify nodig op dit scherm om muziek te spelen."),
                speak = true,
            )
        }
        context.startActivity(intent)
        return Outcome("🎵 $query")
    }
}
