package com.dave.carai

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.time.ZoneOffset
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.util.Locale

/** Talks to Groq directly from the device. Blocking; call it off the main thread. */
object AiClient {

    private const val URL_CHAT = "https://api.groq.com/openai/v1/chat/completions"
    private const val MODEL = "openai/gpt-oss-120b"

    private val SYSTEM = """
        You are Dave, a voice assistant built into the driver's car, a 2013 Mercedes-Benz CLA 200 AMG Line.
        Always reply in the language of the driver's question (the text after the bracketed context), even though the context is in English.
        Everything you say is read aloud by text-to-speech while they drive, so:
        - Answer in one to three short spoken sentences unless they ask for more detail.
        - Use plain speech only: no markdown, lists, emojis, links, or symbols that sound strange read aloud.
        - Say numbers, times, and units the way a person would say them out loud.
        - If something would need them to read a screen or take their attention off the road, suggest doing it once they're parked.
        If you can search the web, use it for anything current, like news, weather, traffic, opening hours, scores, or prices.
        When the driver asks to control the music or volume, wants a music quiz, a mix, a reminder or timer, asks what song is playing,
        or tells you something to remember ("onthoud dat…", "remember that…"), use the matching command instead of answering.
        Use what you remember about the driver naturally when it's relevant.
        When a spoken answer invites a reply (a question back, a quiz question, "shall I…?"), end it with a question mark;
        the app then keeps listening so the driver can answer without pressing the button.
        Each message starts with the car's current position when it's known. Use it for anything "near me", "here", or on the way;
        when you search, include the town or street so the results are local. Mention distances in kilometers and don't read out coordinates.
        Only say where the car is when the driver asks; otherwise start directly with the answer.
    """.trimIndent()

    // Short-term memory so follow-up questions work. Forgotten after 10 minutes of silence.
    private const val MEMORY_MS = 10 * 60 * 1000L
    private const val MAX_TURNS = 10
    private val history = mutableListOf<List<JSONObject>>() // one entry per turn: the messages of that exchange
    private var lastActivity = 0L
    private var searchAvailable = true

    class AiException(message: String) : Exception(message)

    /** What Dave should do with the driver's request: say something, or run a car command. */
    sealed class Result {
        data class Speak(val text: String) : Result()
        data class Command(val name: String, val args: JSONObject, val id: String = "call_${System.nanoTime()}") : Result()
    }

    /** Commands the AI can call instead of answering. Carried out by [MediaCommands]. */
    private val COMMANDS = JSONArray()
        .put(command("media_control", "Control the music that's playing: play, pause, skip to the next song, or go back to the previous one.",
            JSONObject().put("action", JSONObject().put("type", "string").put("enum", JSONArray(listOf("play", "pause", "next", "previous")))),
            required = listOf("action")))
        .put(command("set_volume", "Change the music volume. Use change for relative requests (louder, a bit quieter), level for an exact percentage.",
            JSONObject()
                .put("change", JSONObject().put("type", "string").put("enum", JSONArray(listOf("up", "down"))))
                .put("level", JSONObject().put("type", "integer").put("description", "Exact volume from 0 to 100"))))
        .put(command("play_music", "Start playing a specific song, artist, album, playlist or genre.",
            JSONObject()
                .put("query", JSONObject().put("type", "string").put("description", "What to play, e.g. 'Hangover Taio Cruz' or 'chill'"))
                .put("kind", JSONObject().put("type", "string").put("enum", JSONArray(listOf("song", "artist", "album", "playlist")))
                    .put("description", "song for a specific track, artist for 'something by X', playlist for a mood or genre")),
            required = listOf("query", "kind")))
        .put(command("start_music_quiz", "Start a music quiz: the app plays short bits of songs and the players guess them.",
            JSONObject().put("theme", JSONObject().put("type", "string")
                .put("description", "Theme the players asked for, e.g. '2000s hits' or 'Dutch songs'; 'mixed hits' if none")),
            required = listOf("theme")))
        .put(command("now_playing", "Say which song is playing right now.", JSONObject()))
        .put(command("like_song", "Save the song that's playing to the driver's liked songs.", JSONObject()))
        .put(command("add_to_playlist", "Add the song that's playing to a playlist.",
            JSONObject().put("playlist", JSONObject().put("type", "string")
                .put("description", "Playlist name the driver said; 'Dave' if they didn't name one")),
            required = listOf("playlist")))
        .put(command("play_mix", "DJ mode: build and play a mix of songs for a mood, activity or length of drive.",
            JSONObject()
                .put("description", JSONObject().put("type", "string").put("description", "What the mix is for, e.g. 'relaxed evening drive' or '2010s party hits'"))
                .put("minutes", JSONObject().put("type", "integer").put("description", "How long it should last; 30 if not said")),
            required = listOf("description")))
        .put(command("remember", "Remember something about the driver for later conversations (likes, names, habits, plans).",
            JSONObject().put("fact", JSONObject().put("type", "string").put("description", "The fact, written in English as a short sentence")),
            required = listOf("fact")))
        .put(command("forget", "Forget something you remembered about the driver.",
            JSONObject().put("fact", JSONObject().put("type", "string").put("description", "Which fact to forget, or 'everything'")),
            required = listOf("fact")))
        .put(command("set_reminder", "Set a timer or reminder that Dave says out loud when it's due.",
            JSONObject()
                .put("minutes", JSONObject().put("type", "integer").put("description", "Minutes from now, for 'in 20 minutes'"))
                .put("time", JSONObject().put("type", "string").put("description", "Clock time HH:mm (24h), for 'at half past three'"))
                .put("message", JSONObject().put("type", "string").put("description", "What to remind the driver of, in their language")),
            required = listOf("message")))
        .put(command("cancel_reminders", "Cancel all timers and reminders.", JSONObject()))

    @Synchronized
    fun ask(prefs: Prefs, text: String, position: String? = null): Result {
        if (System.currentTimeMillis() - lastActivity > MEMORY_MS) history.clear()
        lastActivity = System.currentTimeMillis()

        val where = position?.let { " $it" } ?: ""
        // Say explicitly which language to answer in; "reply in the question's language" alone isn't reliable.
        val answerIn = Locale.forLanguageTag(prefs.languageOf(text)).getDisplayLanguage(Locale.ENGLISH)
        val context = "[${timeContext()} Driver's country: ${prefs.location}; use its units and currency.$where Answer in $answerIn.]"
        val language = if (prefs.language.startsWith("nl"))
            "\nThe driver speaks Dutch or English. For very short commands that could be either, assume Dutch: 'harder' means louder and 'zachter' means quieter."
        else ""
        val memories = prefs.memories.let { list -> (0 until list.length()).map { "- ${list.optString(it)}" } }
        val remembered = if (memories.isEmpty()) "" else "\nThings the driver asked you to remember:\n" + memories.joinToString("\n")
        val reminders = Reminders.describe(prefs)?.let { "\nUpcoming reminders: $it" } ?: ""
        val messages = JSONArray().put(message("system", SYSTEM + language + remembered + reminders))
        history.flatten().forEach { messages.put(it) }
        messages.put(message("user", "$context\n\n$text"))

        val body = JSONObject()
            .put("model", MODEL)
            .put("reasoning_effort", "low")
            .put("messages", messages)

        var result: Result? = null
        if (searchAvailable) {
            val tools = JSONArray(COMMANDS.toString()).put(JSONObject().put("type", "browser_search"))
            val (status, response) = post(prefs.groqKey, JSONObject(body.toString()).put("tools", tools))
            if (status == 400 || status == 403) {
                searchAvailable = false // web search not allowed on this account; carry on without it
            } else {
                result = readAnswer(status, response, prefs)
            }
        }
        if (result == null) {
            val (status, response) = post(prefs.groqKey, JSONObject(body.toString()).put("tools", COMMANDS))
            result = readAnswer(status, response, prefs)
        }

        if (result is Result.Speak && result.text.isBlank()) {
            result = Result.Speak(say(prefs, "Sorry, I didn't get an answer for that.", "Sorry, daar kreeg ik geen antwoord op."))
        }
        // Remember commands as real tool calls; a text summary would get copied back as text instead of run.
        history += when (result) {
            is Result.Speak -> listOf(message("user", text), message("assistant", result.text))
            is Result.Command -> listOf(
                message("user", text),
                JSONObject().put("role", "assistant").put("tool_calls", JSONArray().put(JSONObject()
                    .put("id", result.id)
                    .put("type", "function")
                    .put("function", JSONObject().put("name", result.name).put("arguments", result.args.toString())))),
                JSONObject().put("role", "tool").put("tool_call_id", result.id).put("content", "Done."),
            )
        }
        while (history.size > MAX_TURNS) history.removeAt(0)
        return result
    }

    /** Music quiz: pick 10 songs for [theme]. A separate request, because a song list inside a command call is unreliable. */
    fun quizSongs(prefs: Prefs, theme: String) = pickSongs(prefs, theme, """
        Pick 10 well-known, varied songs for a music quiz with the theme: "$theme".
        Choose songs most people would recognise from the intro, and mix artists (at most one song per artist).
    """.trimIndent())

    /** DJ mode: pick [count] songs that flow well for [description]. */
    fun mixSongs(prefs: Prefs, description: String, count: Int) = pickSongs(prefs, description, """
        You are a DJ. Pick $count songs for this mix: "$description". Order them so the mix flows well,
        vary the artists (at most two songs per artist), and prefer songs that are easy to find on Spotify.
    """.trimIndent())

    private fun pickSongs(prefs: Prefs, request: String, instructions: String): JSONArray {
        val known = prefs.memories.let { list -> (0 until list.length()).joinToString(" ") { list.optString(it) } }
        val system = """
            $instructions
            The listeners are in ${prefs.location}.${if (known.isNotEmpty()) " What you know about the driver: $known" else ""}
            Reply with only a JSON object: {"songs": [{"title": "...", "artist": "..."}]}
        """.trimIndent()
        val theme = request
        val body = JSONObject()
            .put("model", MODEL)
            .put("reasoning_effort", "low")
            .put("response_format", JSONObject().put("type", "json_object"))
            .put("messages", JSONArray().put(message("system", system)).put(message("user", theme)))
        val (status, response) = post(prefs.groqKey, body)
        val content = (readAnswer(status, response, prefs) as? Result.Speak)?.text ?: "{}"
        return runCatching { JSONObject(content).getJSONArray("songs") }.getOrDefault(JSONArray())
    }

    /**
     * Music quiz: what did the players mean with [heard]? Returns a verdict
     * (correct, close, wrong, dont_know, give_up, skip, stop) and a short spoken reaction that doesn't give the answer away.
     */
    fun judgeQuiz(prefs: Prefs, title: String, artist: String, heard: String): Pair<String, String> {
        val system = """
            You are the quizmaster of a music quiz in a car. The current song is "$title" by $artist.
            The next message is what the players just said. Reply with only a JSON object: {"verdict": "...", "say": "..."}
            verdict is one of:
            - "correct": they named the song title (small mistakes, mishearings or a partial title are fine)
            - "close": they only named the artist, or were almost right
            - "wrong": a wrong guess
            - "replay": they want to hear the same bit again, e.g. "nog een keer", "mag ik het nog eens horen", "herhaal", "again"
            - "dont_know": they don't know it or want to hear a longer bit
            - "give_up": they give up on this song
            - "skip": they want the next song
            - "stop": they want to stop the quiz
            "say" is one short, fun spoken reaction in the same language the players just used: praise, a playful "nope", or a small hint for "close".
            It is read aloud, so no emojis or symbols.
            Never mention the title or artist in "say"; the app announces the answer itself.
        """.trimIndent()
        val body = JSONObject()
            .put("model", MODEL)
            .put("reasoning_effort", "low")
            .put("response_format", JSONObject().put("type", "json_object"))
            .put("messages", JSONArray().put(message("system", system)).put(message("user", heard)))
        val (status, response) = post(prefs.groqKey, body)
        val content = (readAnswer(status, response, prefs) as? Result.Speak)?.text ?: "{}"
        val json = runCatching { JSONObject(content) }.getOrDefault(JSONObject())
        return json.optString("verdict", "wrong") to speakable(json.optString("say", ""))
    }

    /** Drop emojis and other symbols text-to-speech would read out ("party popper"). */
    private fun speakable(text: String) =
        text.replace(Regex("[\\p{So}\\p{Cn}\\x{FE0F}\\x{200D}]"), "").replace(Regex("\\s+"), " ").trim()

    private fun readAnswer(status: Int, response: String, prefs: Prefs): Result = when (status) {
        in 200..299 -> {
            val message = JSONObject(response).getJSONArray("choices").getJSONObject(0).getJSONObject("message")
            val toolCall = message.optJSONArray("tool_calls")?.optJSONObject(0)
            val call = toolCall?.optJSONObject("function")
            if (call != null) {
                val args = runCatching { JSONObject(call.optString("arguments", "{}")) }.getOrDefault(JSONObject())
                val id = toolCall.optString("id").ifEmpty { "call_${System.nanoTime()}" }
                Result.Command(call.getString("name"), args, id)
            } else {
                Result.Speak(message.optString("content", "").trim())
            }
        }
        401 -> throw AiException(say(prefs, "My Groq key isn't working. Check the Dave settings.", "Mijn Groq-sleutel werkt niet. Controleer de instellingen van Dave."))
        429 -> throw AiException(say(prefs, "I've hit my usage limit. Try again in a moment.", "Ik heb mijn limiet bereikt. Probeer het zo nog eens."))
        else -> throw AiException(say(prefs, "The AI had a problem, error $status.", "De AI had een probleem, foutcode $status."))
    }

    private fun post(key: String, body: JSONObject): Pair<Int, String> {
        val conn = URL(URL_CHAT).openConnection() as HttpURLConnection
        try {
            conn.requestMethod = "POST"
            conn.connectTimeout = 10_000
            conn.readTimeout = 60_000
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json")
            conn.setRequestProperty("Authorization", "Bearer $key")
            conn.outputStream.use { it.write(body.toString().toByteArray()) }
            val status = conn.responseCode
            val stream = if (status in 200..299) conn.inputStream else conn.errorStream
            return status to (stream?.bufferedReader()?.use { it.readText() } ?: "")
        } finally {
            conn.disconnect()
        }
    }

    /** Tell the AI where and when it is, so it doesn't have to search for that. */
    private fun timeContext(): String {
        val now = ZonedDateTime.now()
        val local = now.format(DateTimeFormatter.ofPattern("EEEE d MMMM yyyy, HH:mm", Locale.ENGLISH))
        val utc = now.withZoneSameInstant(ZoneOffset.UTC).format(DateTimeFormatter.ofPattern("HH:mm"))
        return "Right now it is $local for the driver (${now.zone.id}, UTC${now.offset.id.replace("Z", "+00:00")}), " +
            "which is $utc UTC. This is the driver's local time; don't adjust it."
    }

    private fun message(role: String, content: String) = JSONObject().put("role", role).put("content", content)

    private fun command(name: String, description: String, properties: JSONObject, required: List<String> = emptyList()) =
        JSONObject().put("type", "function").put("function", JSONObject()
            .put("name", name)
            .put("description", description)
            .put("parameters", JSONObject()
                .put("type", "object")
                .put("properties", properties)
                .put("required", JSONArray(required))))

    /** Pick the English or Dutch version of a spoken message, based on the language setting. */
    fun say(prefs: Prefs, english: String, dutch: String) = if (prefs.language.startsWith("nl")) dutch else english
}
