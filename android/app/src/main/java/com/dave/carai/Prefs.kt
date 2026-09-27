package com.dave.carai

import android.content.Context
import android.content.SharedPreferences
import org.json.JSONArray

class Prefs(context: Context) {
    val sp: SharedPreferences = context.getSharedPreferences("dave", Context.MODE_PRIVATE)

    /** Free key from console.groq.com/keys. */
    var groqKey: String
        get() = sp.getString("groqKey", "") ?: ""
        set(value) = sp.edit().putString("groqKey", value.trim()).apply()

    /** Where the car is, so answers use the right units, currency and local info. */
    var location: String
        get() = sp.getString("location", "the Netherlands") ?: "the Netherlands"
        set(value) = sp.edit().putString("location", value.trim().ifEmpty { "the Netherlands" }).apply()

    /** Language for listening and speaking, e.g. "nl-NL" or "en-US". */
    var language: String
        get() = sp.getString("language", "nl-NL") ?: "nl-NL"
        set(value) = sp.edit().putString("language", value.trim().ifEmpty { "nl-NL" }).apply()

    /** Optional second language Dave also understands and answers in, e.g. "en-US" ("" = none). */
    var secondLanguage: String
        get() = sp.getString("secondLanguage", "en-US") ?: ""
        set(value) = sp.edit().putString("secondLanguage", value.trim()).apply()

    /** Text-to-speech voice name for [language], e.g. "nl-nl-x-bmh-network" ("" = engine default). */
    var voiceName: String
        get() = sp.getString("voiceName", "") ?: ""
        set(value) = sp.edit().putString("voiceName", value).apply()

    /** Text-to-speech voice name for [secondLanguage]. */
    var secondVoiceName: String
        get() = sp.getString("secondVoiceName", "") ?: ""
        set(value) = sp.edit().putString("secondVoiceName", value).apply()

    /** 1.0 = normal speed. */
    var speechRate: Float
        get() = sp.getFloat("speechRate", 1.0f)
        set(value) = sp.edit().putFloat("speechRate", value).apply()

    /** Listen for "Hé Dave" in the background. */
    var wakeWord: Boolean
        get() = sp.getBoolean("wakeWord", false)
        set(value) = sp.edit().putBoolean("wakeWord", value).apply()

    /** Things the driver asked Dave to remember, as a JSON array of strings. */
    var memories: JSONArray
        get() = runCatching { JSONArray(sp.getString("memories", "[]")) }.getOrDefault(JSONArray())
        set(value) = sp.edit().putString("memories", value.toString()).apply()

    /** Upcoming reminders, as a JSON array of {id, at (epoch ms), message}. */
    var reminders: JSONArray
        get() = runCatching { JSONArray(sp.getString("reminders", "[]")) }.getOrDefault(JSONArray())
        set(value) = sp.edit().putString("reminders", value.toString()).apply()

    // --- Spotify (optional; controls Spotify on your iPhone through CarPlay) ---

    /** Client ID of your own app at developer.spotify.com/dashboard. */
    var spotifyClientId: String
        get() = sp.getString("spotifyClientId", "") ?: ""
        set(value) = sp.edit().putString("spotifyClientId", value.trim()).apply()

    var spotifyVerifier: String
        get() = sp.getString("spotifyVerifier", "") ?: ""
        set(value) = sp.edit().putString("spotifyVerifier", value).apply()

    var spotifyAccess: String
        get() = sp.getString("spotifyAccess", "") ?: ""
        set(value) = sp.edit().putString("spotifyAccess", value).apply()

    var spotifyRefresh: String
        get() = sp.getString("spotifyRefresh", "") ?: ""
        set(value) = sp.edit().putString("spotifyRefresh", value).apply()

    /** Permissions granted at the last Spotify login (space separated). */
    var spotifyScopes: String
        get() = sp.getString("spotifyScopes", "") ?: ""
        set(value) = sp.edit().putString("spotifyScopes", value).apply()

    var spotifyExpiry: Long
        get() = sp.getLong("spotifyExpiry", 0)
        set(value) = sp.edit().putLong("spotifyExpiry", value).apply()

    /** The steering wheel button Dave reacts to (0 = not set yet). */
    var keyCode: Int
        get() = sp.getInt("keyCode", 0)
        set(value) = sp.edit().putInt("keyCode", value).apply()

    /** True while waiting for the user to press the button they want to use. */
    var learning: Boolean
        get() = sp.getBoolean("learning", false)
        set(value) = sp.edit().putBoolean("learning", value).apply()
}

fun Context.dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()
