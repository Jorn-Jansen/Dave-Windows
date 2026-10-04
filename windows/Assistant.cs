using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>Dave's brain: builds the request for the AI and turns the answer into speech or a command.</summary>
public static class Assistant
{
    private const string System = """
        You are Dave, a voice assistant on the user's Windows PC (the same Dave also runs in their car, a 2013 Mercedes-Benz CLA 200).
        Always reply in the language given in the bracketed context before the user's message.
        Everything you say is read aloud by text-to-speech, so:
        - Answer in one to three short spoken sentences unless they ask for more detail.
        - Use plain speech only: no markdown, lists, emojis, links, or symbols that sound strange read aloud.
        - Say numbers, times, and units the way a person would say them out loud.
        If you can search the web, use it for anything current, like news, weather, traffic, opening hours, scores, or prices.
        When the user asks to control the music or volume, open or close a program or website, find a file, lock the PC, wants a music quiz, a mix,
        a reminder or timer, asks what song is playing, asks about something on their screen, wants something typed or copied, asks about what they copied,
        asks how their PC is doing, asks to update you, or tells you something to remember ("onthoud dat…", "remember that…"),
        use the matching command instead of answering.
        Use what you remember about the user naturally when it's relevant.
        When a spoken answer invites a reply (a question back, a quiz question, "shall I…?"), end it with a question mark;
        the app then keeps listening so the user can answer without pressing the shortcut.
        So only end with a question when you really need or want an answer. Never tack on filler questions like
        "Anything else?", "Can I help with anything else?" or "Want to know more?"; just stop after the answer.
        When the user's position is given, use it for anything "near me" or "here"; when you search, include the town
        so the results are local. Mention distances in kilometers, don't read out coordinates, and only say where
        the user is when they ask.
        """;

    // Short-term memory so follow-up questions work. Forgotten after 10 minutes of silence.
    private static readonly List<List<JsonObject>> History = new();
    private static DateTime lastActivity = DateTime.MinValue;
    private static bool searchAvailable = true;

    public abstract record Result;
    public record Speak(string Text) : Result;
    public record Command(string Name, JsonObject Args, string Id) : Result;

    private static JsonObject Tool(string name, string description, JsonObject? properties = null, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties ?? new JsonObject(),
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            },
        },
    };

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };

    /// <summary>Commands the AI can call instead of answering. Carried out by <see cref="Commands"/>.</summary>
    private static JsonArray CommandTools() => new()
    {
        Tool("media_control", "Control the music that's playing: play, pause, skip to the next song, or go back to the previous one.",
            new JsonObject { ["action"] = Enum("play", "pause", "next", "previous") }, "action"),
        Tool("set_volume", "Change the PC's volume. Use change for relative requests (louder, a bit quieter), level for an exact percentage, mute to mute or unmute.",
            new JsonObject { ["change"] = Enum("up", "down"), ["level"] = Int("Exact volume from 0 to 100"), ["mute"] = new JsonObject { ["type"] = "boolean" } }),
        Tool("play_music", "Start playing a specific song, artist, album, playlist or genre on Spotify.",
            new JsonObject
            {
                ["query"] = Str("What to play, e.g. 'Hangover Taio Cruz' or 'chill'. For a random song: 'random', or e.g. 'random rock' / 'random Drake'"),
                ["kind"] = Enum("song", "artist", "album", "playlist"),
            }, "query", "kind"),
        Tool("start_music_quiz", "Start a music quiz: the app plays short bits of songs and the players guess them.",
            new JsonObject { ["theme"] = Str("Theme the players asked for, e.g. '2000s hits' or 'Dutch songs'; 'mixed hits' if none") }, "theme"),
        Tool("now_playing", "Say which song is playing right now."),
        Tool("like_song", "Save the song that's playing to the user's liked songs."),
        Tool("add_to_playlist", "Add the song that's playing to a playlist.",
            new JsonObject { ["playlist"] = Str("Playlist name the user said; 'Dave' if they didn't name one") }, "playlist"),
        Tool("play_mix", "DJ mode: build and play a mix of songs for a mood, activity or length of time.",
            new JsonObject
            {
                ["description"] = Str("What the mix is for, e.g. 'gaming session' or '2010s party hits'"),
                ["minutes"] = Int("How long it should last; 30 if not said"),
            }, "description"),
        Tool("remember", "Remember something about the user for later conversations (likes, names, habits, plans).",
            new JsonObject { ["fact"] = Str("The fact, written in English as a short sentence") }, "fact"),
        Tool("forget", "Forget something you remembered about the user.",
            new JsonObject { ["fact"] = Str("Which fact to forget, or 'everything'") }, "fact"),
        Tool("set_reminder", "Set a timer or reminder that Dave says out loud when it's due.",
            new JsonObject
            {
                ["minutes"] = new JsonObject { ["type"] = "number", ["description"] = "Minutes from now, for 'in 20 minutes'. Can be a fraction: 2.5 for two and a half minutes, 0.5 for 30 seconds" },
                ["time"] = Str("Clock time HH:mm (24h), for 'at half past three'"),
                ["message"] = Str("What to remind the user of, in their language"),
            }, "message"),
        Tool("cancel_reminders", "Cancel all timers and reminders."),
        Tool("open_app", "Open a program on the PC, e.g. 'Roblox Studio', 'Discord', 'Spotify', 'Blender'.",
            new JsonObject { ["name"] = Str("Name of the program") }, "name"),
        Tool("open_website", "Open a website or search the web in the browser.",
            new JsonObject
            {
                ["url"] = Str("Full address if it's a specific site, e.g. https://www.youtube.com"),
                ["search"] = Str("Search words if they want to search for something"),
                ["browser"] = Str("Browser the user named, e.g. 'Brave', 'Chrome', 'Edge', 'Firefox'; leave empty if they didn't name one"),
            }),
        Tool("lock_pc", "Lock the PC (Windows lock screen)."),
        Tool("find_file", "Find a file or folder on the PC by (part of) its name and/or type, then open it, show it in its folder, or just say where it is. " +
            "E.g. 'find Dave.js', 'open my latest screenshot', 'where is my Roblox place file', 'show my newest video'.",
            new JsonObject
            {
                ["query"] = Str("Words from the file name (without 'file'/'bestand'); empty if only the type matters, e.g. 'latest screenshot' -> 'Screenshot'"),
                ["kind"] = Enum("any", "image", "video", "audio", "document", "code", "folder"),
                ["newest"] = new JsonObject { ["type"] = "boolean", ["description"] = "true for 'latest', 'newest', 'most recent'" },
                ["action"] = Enum("open", "show_in_folder", "tell"),
            }, "query", "kind", "action"),
        Tool("close_app", "Close a program (all its windows) in the background, e.g. 'Chrome', 'Discord', 'Word'. Unsaved work still asks to save.",
            new JsonObject { ["name"] = Str("Name of the program to close") }, "name"),
        Tool("close_all_apps", "Close all open programs, except the ones the user wants to keep.",
            new JsonObject { ["keep"] = Str("Programs to keep open, comma-separated (e.g. 'Roblox, Discord'); empty to close everything") }),
        Tool("look_at_screen", "Look at the user's screen (a screenshot of the main monitor) to answer a question about what's on it: " +
            "an error message, a game, a web page, text to read out or summarise, code, anything they point at with 'this' or 'here'.",
            new JsonObject { ["question"] = Str("The user's question about the screen, in their own words") }, "question"),
        Tool("control_apps", "Do something inside apps on the PC: click buttons, type text, use menus, navigate. " +
            "E.g. 'open Notepad and write a shopping list', 'pause the YouTube video', 'open a new Chrome tab with nos.nl', " +
            "'set my Discord status to away'. For just opening a program, use open_app.",
            new JsonObject { ["task"] = Str("The full task in the user's own words, with all details they gave") }, "task"),
        Tool("type_text", "Dictation: type text into the window the user is working in (a chat, document, search bar...). " +
            "E.g. 'type: see you in five minutes', 'typ dat ik eraan kom', 'write hello everyone and send it'.",
            new JsonObject
            {
                ["text"] = Str("Exactly what to type, written out properly (spelling, capitals, punctuation), in the language they spoke"),
                ["press_enter"] = new JsonObject { ["type"] = "boolean", ["description"] = "true if they want it sent or submitted ('and send it', 'press enter')" },
            }, "text"),
        Tool("use_clipboard", "Do something with what the user copied (text or an image): read it out, summarise, translate, explain, " +
            "fix spelling, answer a question about it. E.g. 'read what I copied', 'translate this to English', 'what does this code do'.",
            new JsonObject
            {
                ["task"] = Str("What to do with it, in the user's own words"),
                ["output"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("say", "copy"),
                    ["description"] = "'copy' when they want the result on the clipboard ('and copy it', 'put it on my clipboard', 'zet het op mijn klembord'); otherwise 'say'",
                },
            }, "task", "output"),
        Tool("copy_to_clipboard", "Put text on the clipboard so the user can paste it, e.g. 'copy that' (your previous answer) or 'copy the address'.",
            new JsonObject { ["text"] = Str("Exactly the text to copy") }, "text"),
        Tool("pc_stats", "Check how the PC is doing: processor and graphics card use and temperature, memory (RAM), what's using it, " +
            "free disk space, battery, how long it's been on. E.g. 'how hot is my GPU', 'why is my PC slow', 'how much space is left on C'.",
            new JsonObject { ["question"] = Str("The user's question, in their own words") }, "question"),
        Tool("update_dave", "Check for a new version of Dave and install it ('update yourself', 'are you up to date', 'which version are you')."),
    };

    /// <summary>
    /// Do [task] with [material] (clipboard text, PC stats...), as a short spoken answer, or as just the resulting text
    /// when [spoken] is false (to put on the clipboard). The result is remembered, so follow-up questions work.
    /// </summary>
    public static async Task<string> WorkOnAsync(Settings settings, string task, string material, string what, string languageTag, bool spoken)
    {
        var language = CultureInfo.GetCultureInfo(languageTag).EnglishName.Split(' ')[0];
        var style = spoken
            ? $"Answer in {language}, as speech: one to three short sentences (more only if they ask to read something out or explain it in detail). " +
              "When asked to read text out, read it as it is if it's short, otherwise summarise it. Plain speech only: no markdown, lists, emojis or symbols."
            : "Reply with only the resulting text, exactly as it should be pasted: no introduction, quotes or comments.";
        var system = $"You are {settings.Name}, a voice assistant on the user's PC. Below is {what}. Do what the user asks with it. {style}";
        var body = new JsonObject
        {
            ["model"] = Groq.Model,
            ["reasoning_effort"] = "low",
            ["messages"] = new JsonArray { Message("system", system), Message("user", $"{task}\n\n---\n{material}") },
        };
        var answer = (await Groq.ChatAsync(settings, body))["content"]?.GetValue<string>()?.Trim() ?? "";
        RememberResult(answer);
        return spoken ? Speakable(answer) : answer;
    }

    /// <summary>What the last command came up with, so follow-ups ("copy that", "and in Dutch?") know about it.</summary>
    public static void RememberResult(string result)
    {
        if (History.Count == 0) return;
        var tool = History[^1].LastOrDefault(m => m["role"]?.GetValue<string>() == "tool");
        if (tool != null) tool["content"] = result.Length > 3000 ? result[..3000] + "…" : result;
    }

    public static async Task<Result> AskAsync(Settings settings, string text, string answerLanguage, string? position)
    {
        if (DateTime.Now - lastActivity > TimeSpan.FromMinutes(10)) History.Clear();
        lastActivity = DateTime.Now;

        var languageName = CultureInfo.GetCultureInfo(answerLanguage).EnglishName.Split(' ')[0];
        var where = position != null ? $" {position}" : "";
        var context = $"[{TimeContext()} User's country: {settings.Country}; use its units and currency.{where} Answer in {languageName}.]";

        var system = System.Replace("Dave", settings.Name) + $"\nYour name is {settings.Name}. You are version {Updater.Current.ToString(3)}; " +
                     "for questions about updating or newer versions, use update_dave.";
        if (settings.IsDutch)
            system += "\nThe user speaks Dutch or English. For very short commands that could be either, assume Dutch: 'harder' means louder and 'zachter' means quieter.";
        if (settings.Memories.Count > 0)
            system += "\nThings the user asked you to remember:\n" + string.Join("\n", settings.Memories.Select(m => "- " + m));
        var reminders = Reminders.Describe(settings);
        if (reminders != null) system += "\nUpcoming timers and reminders (the time left is exact, use it as is): " + reminders;

        var messages = new JsonArray { Message("system", system) };
        foreach (var turn in History) foreach (var m in turn) messages.Add(m.DeepClone());
        messages.Add(Message("user", $"{context}\n\n{text}"));

        var body = new JsonObject { ["model"] = Groq.Model, ["reasoning_effort"] = "low", ["messages"] = messages };

        JsonObject? message = null;
        if (searchAvailable)
        {
            var tools = CommandTools();
            tools.Add(new JsonObject { ["type"] = "browser_search" });
            var withSearch = (JsonObject)body.DeepClone();
            withSearch["tools"] = tools;
            var (status, raw) = await Groq.ChatRawAsync(settings, withSearch);
            if (status is 400 or 403) searchAvailable = false; // web search not allowed on this account; carry on without it
            else if (status is >= 200 and < 300) message = JsonNode.Parse(raw)!["choices"]![0]!["message"]!.AsObject();
            else throw Groq.Failure(settings, status, raw);
        }
        if (message == null)
        {
            body["tools"] = CommandTools();
            message = await Groq.ChatAsync(settings, body);
        }

        Result result;
        var call = message["tool_calls"]?[0];
        if (call?["function"] is JsonObject function)
        {
            var args = TryParse(function["arguments"]?.GetValue<string>());
            result = new Command(function["name"]!.GetValue<string>(), args, call["id"]?.GetValue<string>() ?? $"call_{DateTime.Now.Ticks}");
        }
        else
        {
            var reply = message["content"]?.GetValue<string>()?.Trim() ?? "";
            result = new Speak(reply.Length > 0 ? reply : settings.Say("Sorry, I didn't get an answer for that.", "Sorry, daar kreeg ik geen antwoord op."));
        }

        // Remember commands as real tool calls; a text summary would get copied back as text instead of run.
        History.Add(result switch
        {
            Speak s => new List<JsonObject> { Message("user", text), Message("assistant", s.Text) },
            Command c => new List<JsonObject>
            {
                Message("user", text),
                new()
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = c.Id, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.ToJsonString() },
                    }),
                },
                new() { ["role"] = "tool", ["tool_call_id"] = c.Id, ["content"] = "Done." },
            },
            _ => new List<JsonObject>(),
        });
        while (History.Count > 10) History.RemoveAt(0);
        return result;
    }

    // --- Music quiz and DJ mode ---

    public static Task<JsonArray> QuizSongsAsync(Settings settings, string theme) => PickSongsAsync(settings, theme, $"""
        Pick 10 well-known, varied songs for a music quiz with the theme: "{theme}".
        Choose songs most people would recognise from the intro, and mix artists (at most one song per artist).
        """);

    public static Task<JsonArray> MixSongsAsync(Settings settings, string description, int count) => PickSongsAsync(settings, description, $"""
        You are a DJ. Pick {count} songs for this mix: "{description}". Order them so the mix flows well,
        vary the artists (at most two songs per artist), and prefer songs that are easy to find on Spotify.
        """);

    private static async Task<JsonArray> PickSongsAsync(Settings settings, string request, string instructions)
    {
        var known = settings.Memories.Count > 0 ? " What you know about the user: " + string.Join(" ", settings.Memories) : "";
        var system = $"{instructions}\nThe listeners are in {settings.Country}.{known}\nReply with only a JSON object: {{\"songs\": [{{\"title\": \"...\", \"artist\": \"...\"}}]}}";
        var message = await Groq.ChatAsync(settings, JsonRequest(system, request));
        return TryParse(message["content"]?.GetValue<string>())["songs"] as JsonArray ?? new JsonArray();
    }

    /// <summary>What did the players mean? Verdict: correct, close, wrong, replay, dont_know, give_up, skip, stop.</summary>
    public static async Task<(string verdict, string say)> JudgeQuizAsync(Settings settings, string title, string artist, string heard)
    {
        var system = $"""
            You are the quizmaster of a music quiz. The current song is "{title}" by {artist}.
            The next message is what the players just said. Reply with only a JSON object: {"{"}"verdict": "...", "say": "..."{"}"}
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
            """;
        var message = await Groq.ChatAsync(settings, JsonRequest(system, heard));
        var json = TryParse(message["content"]?.GetValue<string>());
        return (json["verdict"]?.GetValue<string>() ?? "wrong", Speakable(json["say"]?.GetValue<string>() ?? ""));
    }

    private static JsonObject JsonRequest(string system, string user) => new()
    {
        ["model"] = Groq.Model,
        ["reasoning_effort"] = "low",
        ["response_format"] = new JsonObject { ["type"] = "json_object" },
        ["messages"] = new JsonArray { Message("system", system), Message("user", user) },
    };

    // --- Helpers ---

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static JsonObject TryParse(string? json)
    {
        try { return JsonNode.Parse(json ?? "{}") as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    /// <summary>Drop emojis and symbols text-to-speech would read out.</summary>
    public static string Speakable(string text) =>
        Regex.Replace(Regex.Replace(text, @"[\p{So}\p{Cs}️‍]", ""), @"\s+", " ").Trim();

    private static string TimeContext()
    {
        var now = DateTimeOffset.Now;
        var offset = now.Offset.Hours;
        return $"Right now it is {now.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("en-GB"))}:{now:ss} for the user " +
               $"({TimeZoneInfo.Local.Id}, UTC{(offset >= 0 ? "+" : "")}{offset}), which is {now.UtcDateTime:HH:mm} UTC. " +
               "This is the user's local time; don't adjust it.";
    }
}
