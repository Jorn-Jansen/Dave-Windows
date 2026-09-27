package com.dave.carai

import android.net.Uri
import android.util.Base64
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Controls Spotify on whichever device is playing (your iPhone through CarPlay) via the Spotify Web API.
 * Needs Spotify Premium. Login uses PKCE, so no client secret lives in the app. Blocking; call off the main thread.
 */
object SpotifyClient {

    const val REDIRECT_URI = "com.dave.carai://spotify-callback"
    private const val SCOPES = "user-modify-playback-state user-read-playback-state user-read-currently-playing " +
        "user-library-modify playlist-read-private playlist-modify-private playlist-modify-public"
    private const val ACCOUNTS = "https://accounts.spotify.com"
    private const val API = "https://api.spotify.com/v1"

    class SpotifyException(message: String) : Exception(message)

    fun isConnected(prefs: Prefs) = prefs.spotifyRefresh.isNotEmpty()

    // --- Login ---

    /** The Spotify login page to open in the browser. Remembers the PKCE verifier for [finishLogin]. */
    fun loginUrl(prefs: Prefs): Uri {
        val verifier = randomString(64)
        prefs.spotifyVerifier = verifier
        val challenge = base64Url(MessageDigest.getInstance("SHA-256").digest(verifier.toByteArray()))
        return Uri.parse("$ACCOUNTS/authorize").buildUpon()
            .appendQueryParameter("response_type", "code")
            .appendQueryParameter("client_id", prefs.spotifyClientId)
            .appendQueryParameter("scope", SCOPES)
            .appendQueryParameter("redirect_uri", REDIRECT_URI)
            .appendQueryParameter("code_challenge_method", "S256")
            .appendQueryParameter("code_challenge", challenge)
            .build()
    }

    /** Swap the code Spotify sent back for tokens. */
    fun finishLogin(prefs: Prefs, code: String) {
        requestToken(prefs, mapOf(
            "grant_type" to "authorization_code",
            "code" to code,
            "redirect_uri" to REDIRECT_URI,
            "client_id" to prefs.spotifyClientId,
            "code_verifier" to prefs.spotifyVerifier,
        ))
    }

    private fun accessToken(prefs: Prefs): String {
        if (System.currentTimeMillis() > prefs.spotifyExpiry - 60_000) {
            requestToken(prefs, mapOf(
                "grant_type" to "refresh_token",
                "refresh_token" to prefs.spotifyRefresh,
                "client_id" to prefs.spotifyClientId,
            ))
        }
        return prefs.spotifyAccess
    }

    private fun requestToken(prefs: Prefs, form: Map<String, String>) {
        val body = form.entries.joinToString("&") { (k, v) -> "$k=${URLEncoder.encode(v, "UTF-8")}" }
        val (status, text) = http("POST", "$ACCOUNTS/api/token", null, body, "application/x-www-form-urlencoded")
        if (status !in 200..299) {
            Log.w("Dave", "Spotify token error $status: $text")
            throw SpotifyException(AiClient.say(prefs, "I'm not logged in to Spotify anymore. Connect it again in the Dave app.",
                "Ik ben niet meer ingelogd bij Spotify. Koppel het opnieuw in de Dave-app."))
        }
        val json = JSONObject(text)
        prefs.spotifyAccess = json.getString("access_token")
        prefs.spotifyExpiry = System.currentTimeMillis() + json.optLong("expires_in", 3600) * 1000
        json.optString("refresh_token").takeIf { it.isNotEmpty() }?.let { prefs.spotifyRefresh = it }
        json.optString("scope").takeIf { it.isNotEmpty() }?.let { prefs.spotifyScopes = it }
    }

    /** Newer features need permissions the first login didn't ask for; if so, ask to reconnect. */
    private fun requireScope(prefs: Prefs, scope: String) {
        if (scope !in prefs.spotifyScopes.split(" ")) {
            throw SpotifyException(AiClient.say(prefs, "For that, connect Spotify again in the Dave app.",
                "Koppel Spotify opnieuw in de Dave-app, dan kan ik dat ook."))
        }
    }

    // --- What's playing, liking, playlists ---

    data class Track(val name: String, val artist: String, val uri: String)

    /** The song playing right now, or null if nothing is. */
    fun nowPlaying(prefs: Prefs): Track? {
        requireScope(prefs, "user-read-currently-playing")
        val (status, text) = api(prefs, "GET", "/me/player/currently-playing")
        if (status == 204 || text.isBlank()) return null
        val item = JSONObject(text).optJSONObject("item") ?: return null
        val artist = item.optJSONArray("artists")?.optJSONObject(0)?.optString("name") ?: ""
        return Track(item.optString("name"), artist, item.optString("uri"))
    }

    private fun currentOrThrow(prefs: Prefs) = nowPlaying(prefs)
        ?: throw SpotifyException(AiClient.say(prefs, "Nothing is playing right now.", "Er speelt nu niets."))

    /** Save the current song to your liked songs. */
    fun likeCurrent(prefs: Prefs): Track {
        requireScope(prefs, "user-library-modify")
        val track = currentOrThrow(prefs)
        api(prefs, "PUT", "/me/library?uris=${URLEncoder.encode(track.uri, "UTF-8")}")
        return track
    }

    /** Add the current song to the playlist called [playlistName], creating it if needed. */
    fun addCurrentToPlaylist(prefs: Prefs, playlistName: String): Track {
        requireScope(prefs, "playlist-modify-private")
        val track = currentOrThrow(prefs)
        val id = findPlaylist(prefs, playlistName) ?: createPlaylist(prefs, playlistName)
        api(prefs, "POST", "/playlists/$id/items", JSONObject().put("uris", JSONArray().put(track.uri)))
        return track
    }

    private fun findPlaylist(prefs: Prefs, name: String): String? {
        var path: String? = "/me/playlists?limit=50"
        repeat(5) {
            val current = path ?: return null
            val (_, text) = api(prefs, "GET", current)
            val json = JSONObject(text)
            val items = json.optJSONArray("items") ?: JSONArray()
            for (i in 0 until items.length()) {
                val playlist = items.optJSONObject(i) ?: continue
                if (playlist.optString("name").equals(name, ignoreCase = true)) return playlist.optString("id")
            }
            path = json.optString("next").takeIf { it.startsWith(API) }?.removePrefix(API)
        }
        return null
    }

    private fun createPlaylist(prefs: Prefs, name: String): String {
        val (_, text) = api(prefs, "POST", "/me/playlists",
            JSONObject().put("name", name).put("public", false).put("description", "Made by Dave, your car assistant"))
        return JSONObject(text).getString("id")
    }

    /** Play a list of songs in order (DJ mode). */
    fun playTracks(prefs: Prefs, uris: List<String>) {
        playerCommand(prefs, "PUT", "/me/player/play", JSONObject().put("uris", JSONArray(uris)))
    }

    // --- Playback ---

    /** Find [query] and play it. [kind] is song, artist, album or playlist. Returns what's playing. */
    fun play(prefs: Prefs, query: String, kind: String): String {
        val type = when (kind) { "artist" -> "artist"; "album" -> "album"; "playlist" -> "playlist"; else -> "track" }
        val (_, text) = api(prefs, "GET", "/search?type=$type&limit=5&q=${URLEncoder.encode(query, "UTF-8")}")
        val items = JSONObject(text).optJSONObject("${type}s")?.optJSONArray("items") ?: JSONArray()
        val found = (0 until items.length()).mapNotNull { items.optJSONObject(it) }.firstOrNull()
            ?: throw SpotifyException(AiClient.say(prefs, "I couldn't find $query on Spotify.", "Ik kon $query niet vinden op Spotify."))

        val body = if (type == "track") JSONObject().put("uris", JSONArray().put(found.getString("uri")))
        else JSONObject().put("context_uri", found.getString("uri"))
        playerCommand(prefs, "PUT", "/me/player/play", body)

        val artist = found.optJSONArray("artists")?.optJSONObject(0)?.optString("name")
        return listOfNotNull(found.optString("name"), artist).joinToString(" – ")
    }

    /** Spotify URI of a specific song, or null if it can't be found. */
    fun findTrack(prefs: Prefs, title: String, artist: String): String? {
        for (query in listOf("track:$title artist:$artist", "$title $artist")) {
            val (_, text) = api(prefs, "GET", "/search?type=track&limit=1&q=${URLEncoder.encode(query, "UTF-8")}")
            val uri = JSONObject(text).optJSONObject("tracks")?.optJSONArray("items")?.optJSONObject(0)?.optString("uri")
            if (!uri.isNullOrEmpty()) return uri
        }
        return null
    }

    /** Play one song from [positionMs]. */
    fun playTrack(prefs: Prefs, uri: String, positionMs: Int) {
        playerCommand(prefs, "PUT", "/me/player/play",
            JSONObject().put("uris", JSONArray().put(uri)).put("position_ms", positionMs))
    }

    fun pause(prefs: Prefs) = control(prefs, "pause")

    /** play, pause, next or previous. */
    fun control(prefs: Prefs, action: String) {
        when (action) {
            "play" -> playerCommand(prefs, "PUT", "/me/player/play", null)
            "pause" -> playerCommand(prefs, "PUT", "/me/player/pause", null)
            "next" -> playerCommand(prefs, "POST", "/me/player/next", null)
            "previous" -> playerCommand(prefs, "POST", "/me/player/previous", null)
        }
    }

    /** Send a player command; if nothing is playing anywhere, aim it at your phone instead. */
    private fun playerCommand(prefs: Prefs, method: String, path: String, body: JSONObject?) {
        val (status, _) = api(prefs, method, path, body, allowNotFound = true)
        if (status != 404) return
        val device = pickDevice(prefs)
            ?: throw SpotifyException(AiClient.say(prefs, "Spotify isn't open on any device. Open Spotify on your iPhone once.",
                "Spotify staat nergens open. Open Spotify één keer op je iPhone."))
        val separator = if ('?' in path) '&' else '?'
        api(prefs, method, "$path${separator}device_id=${device}", body)
    }

    private fun pickDevice(prefs: Prefs): String? {
        val (_, text) = api(prefs, "GET", "/me/player/devices")
        val devices = JSONObject(text).optJSONArray("devices") ?: return null
        val all = (0 until devices.length()).map { devices.getJSONObject(it) }
        val chosen = all.firstOrNull { it.optBoolean("is_active") }
            ?: all.firstOrNull { it.optString("type") == "Smartphone" }
            ?: all.firstOrNull()
        return chosen?.optString("id")?.takeIf { it.isNotEmpty() }
    }

    private fun api(prefs: Prefs, method: String, path: String, body: JSONObject? = null, allowNotFound: Boolean = false): Pair<Int, String> {
        val (status, text) = http(method, "$API$path", accessToken(prefs), body?.toString(), "application/json")
        if (status in 200..299 || (allowNotFound && status == 404)) return status to text
        Log.w("Dave", "Spotify $method $path -> $status: $text")
        throw SpotifyException(when (status) {
            401 -> AiClient.say(prefs, "I'm not logged in to Spotify anymore. Connect it again in the Dave app.",
                "Ik ben niet meer ingelogd bij Spotify. Koppel het opnieuw in de Dave-app.")
            403 -> if (text.contains("PREMIUM_REQUIRED")) {
                AiClient.say(prefs, "Spotify won't let me do that. This needs Spotify Premium.",
                    "Spotify staat dat niet toe. Hiervoor is Spotify Premium nodig.")
            } else {
                // Usually "Restriction violated": e.g. skipping when nothing is playing
                AiClient.say(prefs, "Spotify couldn't do that right now. Start some music on your phone first.",
                    "Spotify kon dat nu niet doen. Zet eerst wat muziek aan op je telefoon.")
            }
            404 -> AiClient.say(prefs, "Spotify isn't open on any device. Open Spotify on your iPhone once.",
                "Spotify staat nergens open. Open Spotify één keer op je iPhone.")
            429 -> AiClient.say(prefs, "Spotify is busy. Try again in a moment.", "Spotify heeft het druk. Probeer het zo nog eens.")
            else -> AiClient.say(prefs, "Spotify had a problem, error $status.", "Spotify had een probleem, foutcode $status.")
        })
    }

    private fun http(method: String, url: String, token: String?, body: String?, contentType: String): Pair<Int, String> {
        val conn = URL(url).openConnection() as HttpURLConnection
        try {
            conn.requestMethod = method
            conn.connectTimeout = 10_000
            conn.readTimeout = 20_000
            token?.let { conn.setRequestProperty("Authorization", "Bearer $it") }
            if (body != null || method == "PUT" || method == "POST") {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", contentType)
                conn.outputStream.use { it.write((body ?: "").toByteArray()) }
            }
            val status = conn.responseCode
            val stream = if (status in 200..299) conn.inputStream else conn.errorStream
            return status to (stream?.bufferedReader()?.use { it.readText() } ?: "")
        } finally {
            conn.disconnect()
        }
    }

    private fun randomString(length: Int): String {
        val chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
        val random = SecureRandom()
        return (1..length).map { chars[random.nextInt(chars.length)] }.joinToString("")
    }

    private fun base64Url(bytes: ByteArray) =
        Base64.encodeToString(bytes, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
}
