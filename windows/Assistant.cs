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
        asks how their PC is doing, asks to update you, asks about an earlier conversation, wants you to tell them when something happens,
        wants windows arranged or a screenshot, asks about or adds to their calendar, asks about the page open in their browser,
        asks about their notifications or messages, wants something done later ("lock my PC in 10 minutes"), wants help with the game they're playing,
        or tells you something to remember ("onthoud dat…", "remember that…"),
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
        Tool("media_control", "Play, pause, next or previous song.",
            new JsonObject { ["action"] = Enum("play", "pause", "next", "previous") }, "action"),
        Tool("set_volume", "Volume of the PC, or of one app ('make Discord quieter', 'mute Chrome'). 'louder'/'quieter' = change (a step), " +
            "only a number they say = level, mute to (un)mute.",
            new JsonObject
            {
                ["app"] = Str("Only for one app, e.g. 'Discord', 'Roblox', 'Spotify', 'Chrome'; empty for the whole PC"),
                ["change"] = Enum("up", "down"), ["level"] = Int("0-100"), ["mute"] = new JsonObject { ["type"] = "boolean" },
            }),
        Tool("play_music", "Play (or queue) a song, artist, album, playlist (the user's own first) or genre on Spotify, or their liked songs.",
            new JsonObject
            {
                ["query"] = Str("e.g. 'Hangover Taio Cruz', 'chill', 'gaming' for 'my gaming playlist'; 'random' or 'random rock' for a random song"),
                ["kind"] = Enum("song", "artist", "album", "playlist", "liked_songs"),
            }, "query", "kind"), // play or queue: decided from the user's own words ("queue", "next"), not here
        Tool("dislike_song", "The user doesn't like the song that's playing: skip it and never play it again."),
        Tool("undislike_song", "Allow a song again that the user said they didn't like.", new JsonObject { ["song"] = Str("Song title") }, "song"),
        Tool("music_settings", "Shuffle on/off, repeat (this song / the playlist / off), or jump within the song ('skip 30 seconds', 'start this song over').",
            new JsonObject
            {
                ["shuffle"] = new JsonObject { ["type"] = "boolean" },
                ["repeat"] = Enum("track", "context", "off"),
                ["seek_seconds"] = Int("Jump forward (negative = back), e.g. 30 or -10"),
                ["seek_to"] = Int("Jump to this second of the song; 0 to start over"),
            }),
        Tool("start_music_quiz", "Music quiz: play bits of songs for the players to guess.",
            new JsonObject { ["theme"] = Str("e.g. '2000s hits'; 'mixed hits' if none") }, "theme"),
        Tool("now_playing", "Which song is playing."),
        Tool("like_song", "Like the song that's playing."),
        Tool("add_to_playlist", "Add the playing song to a playlist.",
            new JsonObject { ["playlist"] = Str("Name; 'Dave' if none") }, "playlist"),
        Tool("play_mix", "DJ mode: play a mix for a mood, activity or length of time, or songs like the one playing ('play something like this').",
            new JsonObject
            {
                ["description"] = Str("e.g. 'gaming session'; for 'something like this': 'songs similar to <title> by <artist>' (the song now playing)"),
                ["minutes"] = Int("Length; 30 if not said"),
            }, "description"),
        Tool("remember", "Remember a fact about the user.", new JsonObject { ["fact"] = Str("Short English sentence") }, "fact"),
        Tool("forget", "Forget a remembered fact.", new JsonObject { ["fact"] = Str("Which, or 'everything'") }, "fact"),
        Tool("set_reminder", "Timer or reminder, said out loud when due; once or repeating ('every day at 10', 'every Monday', 'every 2 hours'). " +
            "Also to DO something later: 'lock my PC in 10 minutes', 'pause the music at 23:00', 'every night at 1 close Roblox' (then fill in action).",
            new JsonObject
            {
                ["action"] = Str("Only to do something instead of saying something: the request as the user would say it now, " +
                    "e.g. 'lock the PC', 'pause the music', 'set the volume to 20 percent', 'close Roblox'"),
                ["minutes"] = new JsonObject { ["type"] = "number", ["description"] = "From now; fractions allowed (2.5, 0.5 = 30 s)" },
                ["time"] = Str("Clock time HH:mm (24h); needed for daily/weekdays/weekends/weekly/monthly"),
                ["message"] = Str("In the user's language"),
                ["repeat"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("none", "daily", "weekdays", "weekends", "weekly", "monthly", "every"),
                    ["description"] = "weekdays = Monday to Friday (werkdagen); weekly = on the given days; every = every N minutes/hours",
                },
                ["days"] = Str("weekly: the days, e.g. 'monday, thursday'"),
                ["month_day"] = Int("monthly: day of the month, 1-31"),
                ["every_minutes"] = Int("every: how often in minutes (120 for every 2 hours)"),
            }, "message"),
        Tool("cancel_reminders", "Cancel reminders: one ('stop the water reminder') or all.",
            new JsonObject { ["which"] = Str("Words from the reminder to cancel; empty for all") }),
        Tool("open_app", "Open a program.", new JsonObject { ["name"] = Str("e.g. 'Roblox Studio'") }, "name"),
        Tool("open_website", "Open a website or web search.",
            new JsonObject { ["url"] = Str("Address of a specific site"), ["search"] = Str("Search words"), ["browser"] = Str("Only if the user named one") }),
        Tool("lock_pc", "Lock the PC."),
        Tool("power", "Turn the PC off, restart it or put it to sleep, now or after some minutes ('turn off my PC in 30 minutes'), or cancel that.",
            new JsonObject
            {
                ["action"] = Enum("shutdown", "restart", "sleep", "cancel"),
                ["minutes"] = Int("From now; 0 = right away"),
            }, "action"),
        Tool("send_to_phone", "Send something from the PC to the user's phone (it arrives as a notification): what they copied " +
            "(a link, text or a picture), a screenshot, a file, or text you give (e.g. your previous answer, 'send that to my phone').",
            new JsonObject
            {
                ["what"] = Enum("clipboard", "screenshot", "file", "text"),
                ["text"] = Str("text: exactly what to send (a link is sent as a link)"),
                ["path"] = Str("file: the full path if the user gave one"),
                ["name"] = Str("file: otherwise (part of) the file name, e.g. 'report.pdf'"),
            }, "what"),
        Tool("where_left_off","Where the user left off on the PC: the apps they used last, the pages and windows open, the song: " +
            "'where did I leave off', 'what was I doing', 'what did I have open'. After it, open_app / open_website can reopen things.",
            new JsonObject { ["question"] = Str("The user's question, in their own words") }, "question"),
        Tool("read_file", "Read a file or folder yourself and answer about its contents: rate, review, summarise, explain, find mistakes, " +
            "'what's in this folder'. Text, code, Word, PDF and images. Use this, not opening it, when they want YOUR opinion or answer.",
            new JsonObject
            {
                ["path"] = Str("The full path if the user gave one, exactly as given"),
                ["name"] = Str("Otherwise: (part of) the file name to look for, e.g. 'TDD.md'"),
                ["question"] = Str("What the user wants to know about it, in their own words"),
            }, "question"),
        Tool("find_file", "Open a folder or file (by path, a known folder like Downloads, or by name/type), show it in its folder, or say where it is.",
            new JsonObject
            {
                ["path"] = Str("A full path if the user gave one, exactly as given, e.g. C:\\Users\\me\\Desktop\\Project"),
                ["query"] = Str("Words from the name; e.g. 'latest screenshot' -> 'Screenshot'"),
                ["kind"] = Enum("any", "image", "video", "audio", "document", "code", "folder"),
                ["newest"] = new JsonObject { ["type"] = "boolean", ["description"] = "For latest/newest" },
                ["action"] = Enum("open", "show_in_folder", "tell"),
            }, "query", "kind", "action"),
        Tool("close_app", "Close a program.", new JsonObject { ["name"] = Str("e.g. 'Chrome'") }, "name"),
        Tool("close_all_apps", "Close all programs except some.", new JsonObject { ["keep"] = Str("Comma-separated, e.g. 'Roblox, Discord'") }),
        Tool("look_at_screen", "Look at the screen to answer about what's on it (an error, a game, 'this', 'here').",
            new JsonObject { ["question"] = Str("The user's question") }, "question"),
        Tool("game_help", "Help with the game the user is playing right now: how to beat a boss or level, where to find something, what to do next, " +
            "a good build or strategy, what an item does. Looks at their screen and searches the web.",
            new JsonObject { ["question"] = Str("The user's question, in their own words") }, "question"),
        Tool("read_page","Read the web page open in the user's browser and answer about it: 'summarise this page', 'what does this article say about…', " +
            "'key points of this', 'is this recipe vegetarian'.",
            new JsonObject { ["question"] = Str("What the user wants to know about the page, in their own words") }, "question"),
        Tool("read_notifications", "The user's Windows notifications (Discord, mail, WhatsApp, Teams…): 'what did I miss', 'any new messages', " +
            "'what did Sam say on Discord', 'read my notifications'.",
            new JsonObject
            {
                ["question"] = Str("What the user wants to know, in their own words"),
                ["hours"] = Int("How far back; 24 if not said"),
            }, "question"),
        Tool("control_apps", "Do things inside apps: click, type, menus, e.g. 'open Notepad and write a list', 'pause the YouTube video'.",
            new JsonObject { ["task"] = Str("The full task with all details") }, "task"),
        Tool("type_text", "Dictation: type text into the window the user is in ('type: see you soon', 'and send it').",
            new JsonObject
            {
                ["text"] = Str("Exactly what to type, properly written, in the language they spoke"),
                ["press_enter"] = new JsonObject { ["type"] = "boolean", ["description"] = "To send/submit it" },
            }, "text"),
        Tool("use_clipboard", "Work with what the user copied (text or image): read out, summarise, translate, explain, extract the text from a copied picture. " +
            "Also when the result should go back on the clipboard (output 'copy').",
            new JsonObject
            {
                ["task"] = Str("What to do, in the user's words"),
                ["output"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("say", "copy"), ["description"] = "'copy' to put the result on the clipboard" },
            }, "task", "output"),
        Tool("copy_to_clipboard", "Copy text you already have to the clipboard ('copy that' = your previous answer). " +
            "Not for something the user copied: that's use_clipboard.", new JsonObject { ["text"] = Str("The actual text, never a placeholder") }, "text"),
        Tool("pc_stats", "How the PC is doing: CPU/GPU use and temperature, RAM and what uses it, disk space, battery, uptime.",
            new JsonObject { ["question"] = Str("The user's question") }, "question"),
        Tool("update_dave", "Check for and install a new version of Dave."),
        Tool("recall_conversation", "Look up what the user said to you earlier (last 30 days), e.g. 'what did I ask you yesterday'. Not for app use: that's screen_time.",
            new JsonObject
            {
                ["about"] = Str("Topic keywords; empty for everything"),
                ["from_days_ago"] = Int("0 = today, 1 = yesterday"),
                ["to_days_ago"] = Int("0 = today"),
            }, "about", "from_days_ago", "to_days_ago"),
        Tool("watch_for", "Tell the user when something happens ('tell me when Roblox closes', 'when my download is done', 'if my GPU goes above 80').",
            new JsonObject
            {
                ["what"] = Enum("program_closes", "program_starts", "download_done", "gpu_temp_above", "gpu_use_above", "cpu_use_above", "ram_use_above", "battery_below"),
                ["program"] = Str("For program_*: e.g. 'Roblox'"),
                ["number"] = new JsonObject { ["type"] = "number", ["description"] = "Degrees for gpu_temp_above, otherwise percent" },
                ["message"] = Str("What to say then, short, in the user's language"),
            }, "what", "message"),
        Tool("stop_watching", "Stop watching for things."),
        Tool("screen_time", "Which apps the user used and for how long (measured): 'how long did I play Roblox today', " +
            "'what did I use my PC for today', 'my screen time this week'.",
            new JsonObject
            {
                ["question"] = Str("The user's question"),
                ["from_date"] = Str("First day, yyyy-MM-dd"),
                ["to_date"] = Str("Last day, yyyy-MM-dd"),
            }, "question", "from_date", "to_date"),
        Tool("internet_speed", "Test the internet: 'how fast is my internet' (speed) or 'what's my ping' (ping, quick).",
            new JsonObject { ["what"] = Enum("speed", "ping") }, "what"),
        Tool("quiet_mode", "Quiet mode: only show answers instead of saying them ('be quiet for a bit', 'I'm in a call'), or talk again.",
            new JsonObject
            {
                ["on"] = new JsonObject { ["type"] = "boolean" },
                ["minutes"] = Int("How long, if they said; 0 = until they say you can talk again"),
            }, "on"),
        Tool("window_control", "Arrange windows: other screen, left/right half, two side by side, maximise, minimise, centre, front, minimise all.",
            new JsonObject
            {
                ["action"] = Enum("other_screen", "move_to_screen", "left_half", "right_half", "side_by_side", "maximize", "minimize", "restore",
                    "center", "focus", "minimize_all", "restore_all"),
                ["app"] = Str("Program, e.g. 'Chrome'; empty for the current window"),
                ["app2"] = Str("side_by_side: program for the right half"),
                ["screen"] = Int("move_to_screen, only when they name a number: 1 = leftmost (use other_screen for 'my other screen')"),
            }, "action"),
        Tool("screenshot", "Screenshot to copy (to paste somewhere) and/or save.",
            new JsonObject
            {
                ["what"] = Enum("screen", "window", "all_screens"),
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("copy", "save", "both"), ["description"] = "'copy' unless they say save" },
                ["where"] = Enum("pictures", "desktop"),
            }, "what", "action"),
        Tool("calendar", "The user's calendar: what's planned ('what do I have tomorrow', 'am I free Saturday'), or add an event.",
            new JsonObject
            {
                ["action"] = Enum("list", "add"),
                ["from_date"] = Str("list: first day, yyyy-MM-dd"),
                ["to_date"] = Str("list: last day, yyyy-MM-dd (a month ahead for 'next event')"),
                ["title"] = Str("add: what, in the user's language"),
                ["start"] = Str("add: yyyy-MM-ddTHH:mm local time, or yyyy-MM-dd for all day"),
                ["minutes"] = Int("add: length, 60 if not said"),
                ["all_day"] = new JsonObject { ["type"] = "boolean" },
                ["location"] = Str("add: where"),
            }, "action"),
    };

    /// <summary>
    /// Which commands go with which words (English and Dutch, word starts). Only the groups that fit what was said are sent:
    /// all 41 commands cost about 3000 tokens per question, a big part of the free 8000 per minute. When none fit but the user
    /// wants something done after all, the AI asks for the rest with <see cref="MoreCommands"/>.
    /// </summary>
    private static readonly (Regex words, string[] tools)[] Groups =
    {
        (Words("song", "music", "muziek", "play", "speel", "paus", "pauz", "resume", "hervat", "verder", "next", "volgende", "previous", "vorige", "skip",
               "shuffle", "repeat", "herhaal", "spotify", "playlist", "afspeellijst", "liked", "quiz", "mix", "dj", "artist", "artiest", "album",
               "track", "nummer", "liedje", "lied", "queue", "wachtrij", "like", "dislike", "hate", "haat", "zingt", "sings", "stop"),
            new[] { "media_control", "play_music", "dislike_song", "undislike_song", "music_settings", "start_music_quiz", "now_playing",
                    "like_song", "add_to_playlist", "play_mix", "set_volume" }),
        (Words("volume", "loud", "louder", "quiet", "harder", "zachter", "luider", "mute", "unmute", "dempen", "sound", "geluid", "turn it", "zet het"),
            new[] { "set_volume", "media_control", "quiet_mode" }),
        (Words("remember", "onthoud", "forget", "vergeet", "earlier", "eerder", "yesterday", "gisteren", "what did i", "wat vroeg", "wat zei",
               "last time", "vorige keer", "asked you", "vroeg ik"),
            new[] { "remember", "forget", "recall_conversation" }),
        (Words("remind", "herinner", "timer", "alarm", "wekker", "reminder", "every", "elke", "iedere", "cancel", "annuleer", "later",
               "tonight", "vanavond", "tomorrow", "morgen", "minute", "minuut", "minuten", "hour", "uur", "second", "seconde"),
            new[] { "set_reminder", "cancel_reminders" }),
        (Words("open", "start", "launch", "close", "sluit", "quit", "afsluit", "lock", "vergrendel", "op slot", "app", "program", "website", "site",
               "google", "youtube", "browser", "chrome", "discord", "roblox", "steam", "search", "zoek"),
            new[] { "open_app", "open_website", "lock_pc", "close_app", "close_all_apps" }),
        (Words("shut down", "shutdown", "turn off", "turn it off", "power off", "restart", "reboot", "sleep", "afsluiten", "uitzetten", "zet uit",
               "zet mijn pc uit", "herstart", "opnieuw opstarten", "slaapstand", "slapen", "cancel the", "annuleer"),
            new[] { "power" }),
        (Words("to my phone", "to my iphone", "to my mobile", "naar mijn telefoon", "naar mijn iphone", "naar m'n telefoon", "op mijn telefoon",
               "on my phone", "to the phone", "naar de telefoon"),
            new[] { "send_to_phone" }),
        (Words("left off", "leave off", "was i doing", "was ik aan het doen", "had open", "had i open", "had ik open", "gebleven", "waar was ik",
               "where was i", "reopen", "heropen", "weer open"),
            new[] { "where_left_off", "open_app", "open_website" }),
        (Words("file", "bestand", "folder", "map", "document", "pdf", "docx", "path", "pad", "download", "desktop", "bureaublad", "screenshot",
               "picture", "foto", "afbeelding", "image", "where is", "waar is", "rate", "beoordeel", "review", @"[a-z]:\\", @"\.(txt|md|cs|py|json|lua)\b"),
            new[] { "read_file", "find_file" }),
        (Words("screen", "scherm", "this", "dit", "deze", "here", "hier", "error", "fout", "look", "kijk", "see", "zie", "what's on", "wat staat"),
            new[] { "look_at_screen", "screenshot", "read_page" }),
        (Words("page", "pagina", "article", "artikel", "summar", "samenvat", "recipe", "recept", "tab", "website", "site"),
            new[] { "read_page" }),
        (Words("notification", "melding", "message", "bericht", "miss", "gemist", "discord", "whatsapp", "mail", "teams", "dm", "said", "zei", "texted"),
            new[] { "read_notifications" }),
        (Words("game", "spel", "boss", "level", "quest", "how do i", "hoe kom ik", "hoe versla", "beat", "versla", "build", "strategy", "strategie",
               "where do i find", "waar vind", "item", "weapon", "wapen", "stuck", "vast", "roblox", "minecraft", "fortnite"),
            new[] { "game_help" }),
        (Words("click", "klik", "type", "typ", "write", "schrijf", "menu", "button", "knop", "send", "stuur", "dictat", "notepad", "kladblok", "fill", "vul"),
            new[] { "control_apps", "type_text" }),
        (Words("copy", "kopieer", "copied", "gekopieerd", "clipboard", "klembord", "paste", "plak", "translate", "vertaal"),
            new[] { "use_clipboard", "copy_to_clipboard" }),
        (Words("cpu", "gpu", "ram", "memory", "geheugen", "temperat", "hot", "warm", "heet", "disk", "schijf", "storage", "opslag", "battery",
               "accu", "batterij", "uptime", "internet", "speed", "snelheid", "ping", "wifi", "lag", "slow", "traag", "pc doing", "pc het",
               "download", "using my pc", "what's using", "wat gebruikt", "busy", "druk"),
            new[] { "pc_stats", "internet_speed" }),
        (Words("update", "version", "versie", "nieuwe dave", "new dave"),
            new[] { "update_dave" }),
        (Words("tell me when", "let me know", "laat me weten", "laat weten", "zeg het als", "zeg wanneer", "seintje", "heads-up", "watch", "keep an eye",
               "houd in de gaten", "when it's done", "als het klaar", "closes", "afsluit", "stop watching", "stop met"),
            new[] { "watch_for", "stop_watching" }),
        (Words("screen time", "schermtijd", "how long", "hoe lang", "played", "gespeeld", "used", "gebruikt", "spent", "besteed"),
            new[] { "screen_time" }),
        (Words("quiet", "stil", "shh", "call", "bellen", "talk again", "praat", "praten", "spreek", "speak"),
            new[] { "quiet_mode" }),
        (Words("window", "venster", "screen", "scherm", "side by side", "naast elkaar", "maximi", "maximali", "minimi", "minimali", "left", "right",
               "links", "rechts", "half", "centre", "center", "midden", "front", "voorgrond", "move", "verplaats", "monitor"),
            new[] { "window_control" }),
        (Words("calendar", "agenda", "planned", "gepland", "free on", "vrij op", "appointment", "afspraak", "event", "schedule", "rooster",
               "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "maandag", "dinsdag", "woensdag", "donderdag",
               "vrijdag", "zaterdag", "zondag", "this week", "deze week", "next week", "volgende week", "tomorrow", "morgen"),
            new[] { "calendar" }),
    };

    private static Regex Words(params string[] words) =>
        new(@"(^|\W)(" + string.Join("|", words.Select(w => w.Contains('\\') ? w : Regex.Escape(w))) + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Not a real command: lets the AI ask for all commands when the ones it got don't fit.</summary>
    private const string MoreCommands = "more_commands";

    /// <summary>
    /// The commands for [text]: the groups its words fit, the ones used earlier in this conversation (for follow-ups like
    /// "a bit louder"), and a way to ask for the rest.
    /// </summary>
    private static JsonArray CommandsFor(string text, out bool all)
    {
        var everything = CommandTools();
        var wanted = new HashSet<string>(Groups.Where(g => g.words.IsMatch(text)).SelectMany(g => g.tools));
        if (Reminders.TryFindLater(text, out _, out _, out _)) wanted.Add("set_reminder"); // "… at 23:00": something for later
        foreach (var turn in History) // (also: the AI may not accept earlier calls to commands it doesn't get now)
            foreach (var m in turn)
                if (m["tool_calls"] is JsonArray calls)
                    foreach (var c in calls) if (c?["function"]?["name"]?.GetValue<string>() is { } used) wanted.Add(used);
        var picked = new JsonArray(everything.Where(t => wanted.Contains(t!["function"]!["name"]!.GetValue<string>())).Select(t => t!.DeepClone()).ToArray());
        all = picked.Count == everything.Count;
        if (all) return picked;
        picked.Add(Tool(MoreCommands, "Use when the user wants you to DO something on their PC that none of the other commands here can do " +
            "(music, volume, apps, websites, files, reminders, screen, clipboard, windows, calendar, notifications, games, PC stats…): " +
            "you then get all commands. Not for questions you can answer yourself."));
        return picked;
    }

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

    /// <summary>
    /// "How do I beat this boss?": which game (the window in front) and what's going on (a look at the screen), then an
    /// answer with web search (guides, wikis), short enough to hear without leaving the game.
    /// </summary>
    public static async Task<string> GameHelpAsync(Settings settings, string question, string languageTag)
    {
        var game = WindowList.List().FirstOrDefault(); // the window in front (Dave's own are skipped)
        var seen = await Vision.AskAboutImageAsync(settings,
            $"Which game is this (the exact name; for Roblox, the experience's name if it's visible) and what is going on: where the player is, " +
            $"which level, boss, quest or menu, and anything on screen that matters for this question: \"{question}\". One or two plain English sentences.",
            "en-US", Vision.CaptureMainScreen(), "a screenshot of the game the user is playing right now", spoken: false);
        Log.Write($"Game help: {game.title} ({game.process}); screen: {seen}");

        var language = CultureInfo.GetCultureInfo(languageTag).EnglishName.Split(' ')[0];
        var system = $"""
            You are {settings.Name}, helping the user with the game they're playing right now, by voice, while they play.
            The window in front: "{game.title}" (program: {game.process}). What their screen shows: {seen}
            Search the web (guides, wikis, forums) unless you're sure, and give the answer that fits where they are now.
            Answer in {language}: two to four short spoken sentences with concrete steps. Plain speech only: no markdown, lists, emojis or links.
            """;
        var body = new JsonObject
        {
            ["model"] = Groq.Model,
            ["reasoning_effort"] = "low",
            ["messages"] = new JsonArray { Message("system", system), Message("user", question) },
        };
        JsonObject? message = null;
        if (searchAvailable)
        {
            var withSearch = (JsonObject)body.DeepClone();
            withSearch["tools"] = new JsonArray(new JsonObject { ["type"] = "browser_search" });
            var (status, raw) = await Groq.ChatRawAsync(settings, withSearch);
            if (status is >= 200 and < 300) message = JsonNode.Parse(raw)!["choices"]![0]!["message"]!.AsObject();
            else if (status is 400 or 403) searchAvailable = false;
            else throw Groq.Failure(settings, status, raw);
        }
        message ??= await Groq.ChatAsync(settings, body);
        var answer = message["content"]?.GetValue<string>()?.Trim() ?? "";
        RememberResult(answer);
        return Speakable(answer.Length > 0 ? answer : settings.Say("Sorry, I couldn't find an answer for that.", "Sorry, daar kon ik geen antwoord op vinden."));
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
        // What's playing, so "what's this song about", "who sings this" and "play something like this" just work
        var playing = await MediaSession.NowPlayingAsync(settings);
        // "Nothing playing" only when Windows said so; when it didn't answer, say nothing (or "next song" gets refused)
        var music = playing != null ? $" Now playing: \"{playing.Title}\"{(playing.Artist.Length > 0 ? $" by {playing.Artist}" : "")}."
            : MediaSession.Unknown ? "" : " No music or video is playing.";
        if (MusicWatcher.EarlierSongs(playing) is { } earlier) music += $" Songs before that: {earlier}.";
        // The current state, right next to the question, also when there's nothing: otherwise the AI goes by what was said
        // earlier in the chat ("tell me when Discord closes") and thinks those are still running.
        var reminderState = Reminders.Describe(settings) is { } due
            ? $" Timers and reminders right now (exact, use as is): {due}."
            : " There are no timers or reminders right now.";
        var watchState = Watchers.DescribeAll() is { } watched
            ? $" You're keeping an eye on, right now: {watched}."
            : " You're not keeping an eye on anything right now; earlier ones in the chat are finished or stopped.";
        var context = $"[{TimeContext()} User's country: {settings.Country}; use its units and currency.{where}{music}{reminderState}{watchState} Answer in {languageName}.]";

        var system = System.Replace("Dave", settings.Name) + $"\nYour name is {settings.Name}. You are version {Updater.Display}; " +
                     "for questions about updating or newer versions, use update_dave.";
        if (settings.IsDutch)
            system += "\nThe user speaks Dutch or English. For very short commands that could be either, assume Dutch: 'harder' means louder and 'zachter' means quieter.";
        if (settings.Memories.Count > 0)
            system += "\nThings the user asked you to remember:\n" + string.Join("\n", settings.Memories.Select(m => "- " + m));
        if (settings.IsQuiet)
            system += $"\nQuiet mode is on{(settings.QuietUntil < DateTime.MaxValue ? $" until {settings.QuietUntil:HH:mm}" : "")}: your answers are only shown, not spoken. " +
                      "When the user says you can talk again ('je mag weer praten'), use quiet_mode with on=false.";
        if (History.Count == 0 && ConversationLog.RecentTopics() is { } recent)
            system += "\nThe last things the user asked you before this conversation (use recall_conversation for details): " + recent;

        var messages = new JsonArray { Message("system", system) };
        foreach (var turn in History) foreach (var m in turn) messages.Add(m.DeepClone());
        messages.Add(Message("user", $"{context}\n\n{text}"));

        var body = new JsonObject { ["model"] = Groq.Model, ["reasoning_effort"] = "low", ["messages"] = messages };

        async Task<JsonObject> RequestAsync(JsonArray commands)
        {
            if (searchAvailable)
            {
                var withSearch = (JsonObject)body.DeepClone();
                var tools = (JsonArray)commands.DeepClone();
                tools.Add(new JsonObject { ["type"] = "browser_search" });
                withSearch["tools"] = tools;
                var (status, raw) = await Groq.ChatRawAsync(settings, withSearch);
                if (status is >= 200 and < 300) return JsonNode.Parse(raw)!["choices"]![0]!["message"]!.AsObject();
                if (status is not (400 or 403)) throw Groq.Failure(settings, status, raw);
                searchAvailable = false; // web search not allowed on this account; carry on without it
            }
            var plain = (JsonObject)body.DeepClone();
            plain["tools"] = commands.DeepClone();
            return await Groq.ChatAsync(settings, plain);
        }

        var commands = CommandsFor(text, out var all);
        Log.Write($"Commands sent: {(all ? "all" : string.Join(", ", commands.Select(t => t!["function"]!["name"]!.GetValue<string>())))}");
        var message = await RequestAsync(commands);
        if (message["tool_calls"]?[0]?["function"]?["name"]?.GetValue<string>() == MoreCommands)
        {
            Log.Write("The AI needed other commands: asking again with all of them");
            message = await RequestAsync(CommandTools());
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
            reply = Speakable(reply);
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
        {(settings.DislikedSongs.Count > 0 ? "The user doesn't like these, never pick them: " + string.Join("; ", settings.DislikedSongs.TakeLast(40).Select(d => $"{d.Title} by {d.Artist}")) : "")}
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
        Regex.Replace(Regex.Replace(Regex.Replace(text, @"\*\*|__|`|^#+\s*", "", RegexOptions.Multiline), @"[\p{So}\p{Cs}️‍]", ""), @"\s+", " ").Trim();

    private static string TimeContext()
    {
        var now = DateTimeOffset.Now;
        var offset = now.Offset.Hours;
        return $"Right now it is {now.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("en-GB"))}:{now:ss} for the user " +
               $"({TimeZoneInfo.Local.Id}, UTC{(offset >= 0 ? "+" : "")}{offset}), which is {now.UtcDateTime:HH:mm} UTC. " +
               "This is the user's local time; don't adjust it.";
    }
}
