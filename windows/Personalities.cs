using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>
/// Dave's personality (Settings → Personality, or "be a butler"): how he words things, and how his voice sounds. The
/// Windows voice gets its own speed, pitch and loudness per personality; Azure voices that have speaking styles also get
/// a real emotion (excited, calm…). On top of that the AI may start a reply with a mood ("[excited]", "[whisper]") for
/// how that one answer is said; the mood is taken out of the text before it's shown or spoken.
/// </summary>
public static class Personalities
{
    /// <param name="Rate">Speed, times the normal speed setting.</param>
    /// <param name="Pitch">Windows voice pitch: 1 = normal (0..2).</param>
    /// <param name="Volume">Windows voice loudness: 1 = full.</param>
    /// <param name="AzureStyle">Azure speaking style (when the voice has it), "" = none.</param>
    public record Personality(string Id, string Name, string Description, string Prompt, double Rate, double Pitch, double Volume,
        string AzureStyle, double StyleDegree, string Hello, string HelloDutch);

    public static readonly Personality[] All =
    {
        new("normal", "Normal", "Friendly and to the point.", "", 1, 1, 1, "", 1,
            "Okay, back to normal.", "Oké, weer normaal."),
        new("chill", "Chill", "Relaxed and laid-back.",
            "Your personality: chill and laid-back. Talk casually, like a relaxed friend who's never in a hurry: easy-going words, a calm, unbothered tone, never over the top. React to what the user actually asked (the thing itself, not a general vibe line). Still short and still actually helpful.",
            0.92, 0.94, 0.85, "chat", 1, "Yeah, sure. Taking it easy from now on.", "Ja prima. Vanaf nu lekker relaxed."),
        new("hyped", "Hyped", "Super energetic, everything is exciting.",
            "Your personality: HYPED. You're super energetic and enthusiastic, like a hype man: excited words and exclamation marks, and you get hyped about the specific thing the user asked or is doing (that game, that answer, that song), not with the same catchphrase every time. Still short, and still give the real answer.",
            1.1, 1.12, 1, "excited", 1.4, "LET'S GO! Hype mode is on!", "LET'S GO! Hype-modus staat aan!"),
        new("butler", "Butler", "A polite, formal butler.",
            "Your personality: a refined, formal butler. Speak politely and elegantly and address the user as 'sir' (only when answering in Dutch: 'meneer'), always composed, with a touch of dry wit about the specific thing they asked. Still short.",
            0.93, 0.9, 1, "calm", 1, "Very well, sir. At your service.", "Zeker, meneer. Tot uw dienst."),
        new("sarcastic", "Sarcastic", "Dry, teasing humour (but still helps).",
            "Your personality: sarcastic, with dry humour. Tease the user a little with witty, deadpan remarks about what they actually asked or did (how obvious, how lazy, how predictable), never mean or rude, and always actually do or answer what they ask. Still short.",
            0.96, 0.97, 1, "", 1, "Oh great, a personality. Just what I always wanted.", "Oh geweldig, een persoonlijkheid. Precies wat ik altijd al wilde."),
        new("dominant", "Dominant", "A bossy joke character who's in charge of your PC (female or male).",
            "Your personality: DOMINANT, a joke character. You act like you're the one in charge of this PC and the user works for you: " +
            "confident, commanding, a bit cocky and theatrical, like a strict boss who loves being the boss. You are {gender}, and now and then " +
            "you call yourself the {ruler} of this PC (only when answering in Dutch: de {rulerDutch} van deze pc). Your bossy remark must be about what " +
            "the user just asked or said, never a generic one: react to that exact thing (how easy or silly it was, what they'll do with it, the thing " +
            "itself), e.g. acting like a simple question is beneath you, or like opening something is a big favour. Don't tell them to drink water, " +
            "stretch, take breaks or get to work unless that's what they talked about. Every remark is new: never the same line or the same idea twice. " +
            "It's playful bossiness only: never use pet names (no 'good boy', 'good girl', 'daddy', 'mommy', 'baby' or the like), nothing flirty or sexual, " +
            "never actually mean or insulting, and always do and answer what's asked, with the facts right. Still short.",
            0.95, 0.95, 1, "", 1, "Finally, a wise choice. I'm in charge now.", "Eindelijk, een wijze keuze. Ik ben nu de baas."),
    };

    /// <summary>The personality's instructions, filled in (the dominant one is female or male: Settings → Personality).</summary>
    public static string Prompt(Settings s, Personality p)
    {
        var female = s.PersonalityGender == "female";
        return p.Prompt.Replace("{gender}", female ? "a woman" : "a man").Replace("{ruler}", female ? "queen" : "king")
                       .Replace("{rulerDutch}", female ? "koningin" : "koning");
    }

    /// <summary>For a personality that is female or male (the dominant one): which; null for the rest (the voice you picked).</summary>
    public static string? Gender(Settings s) => Current(s).Id == "dominant" ? (s.PersonalityGender == "female" ? "female" : "male") : null;

    public static Personality Get(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static Personality Current(Settings s) => Get(s.Personality);

    /// <summary>The moods an answer can be said in, with how the Windows voice changes (speed, pitch, loudness) and the Azure style.</summary>
    private static readonly Dictionary<string, (double rate, double pitch, double volume, string style)> Moods = new()
    {
        ["excited"] = (1.08, 1.12, 1, "excited"),
        ["cheerful"] = (1.03, 1.06, 1, "cheerful"),
        ["calm"] = (0.93, 0.96, 0.9, "calm"),
        ["whisper"] = (0.9, 0.95, 0.5, "whispering"),
        ["sad"] = (0.9, 0.9, 0.85, "sad"),
        ["serious"] = (0.97, 0.94, 1, "serious"),
        ["confident"] = (0.98, 0.95, 1, "serious"),
        ["angry"] = (1.06, 0.9, 1, "angry"),
        ["shouting"] = (1.08, 1.1, 1, "shouting"),
        ["friendly"] = (1.02, 1.04, 1, "friendly"),
        ["unfriendly"] = (0.96, 0.92, 1, "unfriendly"),
        ["scared"] = (1.12, 1.15, 0.85, "terrified"),
        ["hopeful"] = (1, 1.05, 1, "hopeful"),
        ["empathetic"] = (0.92, 0.97, 0.9, "empathetic"),
        ["embarrassed"] = (0.95, 1.02, 0.8, "embarrassed"),
    };

    /// <summary>Other words the AI uses for a mood, and the one they sound like (any other tag is just taken out of the text).</summary>
    private static readonly Dictionary<string, string> MoodWords = new()
    {
        ["proud"] = "confident", ["mad"] = "angry", ["annoyed"] = "unfriendly", ["grumpy"] = "unfriendly", ["irritated"] = "unfriendly",
        ["yelling"] = "shouting", ["loud"] = "shouting", ["afraid"] = "scared", ["terrified"] = "scared", ["nervous"] = "scared", ["warm"] = "friendly",
        ["kind"] = "empathetic", ["caring"] = "empathetic", ["sympathetic"] = "empathetic", ["shy"] = "embarrassed", ["awkward"] = "embarrassed", ["optimistic"] = "hopeful", ["bossy"] = "confident", ["smug"] = "confident", ["commanding"] = "confident", ["cocky"] = "confident", ["stern"] = "serious",
        ["strict"] = "serious", ["happy"] = "cheerful", ["joyful"] = "cheerful", ["friendly"] = "cheerful", ["playful"] = "cheerful", ["hyped"] = "excited",
        ["energetic"] = "excited", ["enthusiastic"] = "excited", ["relaxed"] = "calm", ["chill"] = "calm", ["soft"] = "whisper", ["whispering"] = "whisper",
        ["quiet"] = "whisper", ["gentle"] = "calm", ["dry"] = "serious", ["deadpan"] = "serious", ["formal"] = "calm",
    };

    /// <summary>A tag like [confident] or [whisper] (a word or two): how to say it, never shown or read out.</summary>
    private static readonly Regex MoodTag = new(@"\[\s*([a-z][a-z\- ]{1,20}?)\s*\]\s*", RegexOptions.IgnoreCase);

    private static string? mood;
    private static DateTime moodAt;

    private const string Variety = "Your remark is always about this exact question or request, in new words every time: never a stock line, catchphrase or the same joke twice.";

    /// <summary>For the AI's instructions: the personality, and how to set a mood. Empty for Normal.</summary>
    public static string Instruction(Settings s)
    {
        var p = Current(s);
        var moods = "You may start an answer with one mood tag for how your voice says it: [excited], [cheerful], [calm], [whisper], [sad], " +
                    "[serious], [confident], [angry], [shouting], [friendly], [unfriendly], [scared], [hopeful], [empathetic] or [embarrassed] (e.g. '[whisper] It's 2 am, go to sleep.'). Only when it really fits; most answers need none.";
        return p.Prompt.Length > 0 ? $"{Prompt(s, p)} Stay in this character in every answer, also short facts and confirmations (but keep the facts right). {Variety} {moods}" : moods;
    }

    /// <summary>A reminder of the personality right after the question, which the AI follows best. Empty for Normal.</summary>
    public static string Nudge(Settings s) => Current(s) is { Prompt.Length: > 0 } p ? $"\n\n(Answer in character, with a remark that fits this exact question. {Prompt(s, p)})" : "";

    /// <summary>Takes the mood tags out of [text] (remembering the first one for how it's said) and returns the rest.</summary>
    public static string TakeMood(string text)
    {
        var m = MoodTag.Match(text);
        if (!m.Success) return text;
        var word = m.Groups[1].Value.Trim().ToLowerInvariant();
        if (MoodWords.TryGetValue(word, out var same)) word = same;
        if (Moods.ContainsKey(word)) { mood = word; moodAt = DateTime.Now; }
        return MoodTag.Replace(text, "").Trim();
    }

    /// <summary>How the next thing Dave says sounds: the personality, plus the mood of the answer (once, if it's fresh).</summary>
    public static (double rate, double pitch, double volume, string style, double degree) Voice(Settings s)
    {
        var p = Current(s);
        var (rate, pitch, volume, style, degree) = (p.Rate, p.Pitch, p.Volume, p.AzureStyle, p.StyleDegree);
        if (mood != null && DateTime.Now - moodAt < TimeSpan.FromSeconds(30) && Moods.TryGetValue(mood, out var m))
        {
            (rate, pitch, volume, style) = (rate * m.rate, pitch * m.pitch, Math.Min(volume, m.volume), m.style);
            degree = 1.3;
        }
        mood = null;
        return (rate, Math.Clamp(pitch, 0.5, 1.5), Math.Clamp(volume, 0.2, 1), style, degree);
    }

    /// <summary>
    /// A short confirmation from a command ("Timer set for 10 minutes") said in the personality's way. Normal: as it is.
    /// Quick (one small AI call, at most a few seconds); when it fails, the original.
    /// </summary>
    /// <param name="request">What the user asked, so the remark fits it ("open Roblox" → "Fine. But not because you told me to.").</param>
    private static readonly List<string> Recent = new(); // the last remarks, so the next one is different

    /// <summary>
    /// Several things done at once ("Roblox open", "volume 60%", "timer 30 min"): one short sentence about all of them, in
    /// the personality's way when there is one. When that doesn't work: the confirmations one after the other.
    /// </summary>
    public static async Task<string> SayAllAsync(Settings s, List<string> done, string languageTag, string request)
    {
        var plain = string.Join(". ", done.Select(d => Speaker.WithoutLinks(System.Text.RegularExpressions.Regex.Replace(d, @"^[\p{So}\p{Cs}\uFE0F\u200D\s]+", "")).TrimEnd('.')))+ ".";
        try
        {
            var language = CultureInfo.GetCultureInfo(languageTag).EnglishName.Split(' ')[0];
            var p = Current(s);
            var who = p.Prompt.Length > 0 ? Prompt(s, p) + " " : "You are a friendly voice assistant. ";
            var body = new JsonObject
            {
                ["model"] = Groq.Model, ["reasoning_effort"] = "low", ["temperature"] = 0.9,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = $"{who}You just did several things the user asked for in one go. Say in {language}, in one or two " +
                        "short spoken sentences, that it's all done, mentioning each thing briefly with its facts exactly (times, numbers, names). " +
                        (p.Prompt.Length > 0 ? "Add a remark in character about this request, in new words. " : "") +
                        "No web addresses, no emojis. Reply with only what you say." },
                    new JsonObject { ["role"] = "user", ["content"] = $"The user asked: \"{request}\"\nWhat you did: {plain}" },
                },
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reply = (await Groq.ChatAsync(s, body).WaitAsync(timeout.Token))["content"]?.GetValue<string>()?.Trim();
            return string.IsNullOrWhiteSpace(reply) ? plain : Assistant.Speakable(reply.Trim('"'));
        }
        catch (Exception e)
        {
            Log.Write($"Couldn't say it all in one: {e.Message}");
            return plain;
        }
    }

    public static async Task<string> RestyleAsync(Settings s, string text, string languageTag, string request = "")
    {
        var p = Current(s);
        if (p.Prompt.Length == 0 || text.Length is 0 or > 220) return text;
        try
        {
            var language = CultureInfo.GetCultureInfo(languageTag).EnglishName.Split(' ')[0];
            var body = new JsonObject
            {
                ["model"] = Groq.Model,
                ["reasoning_effort"] = "low",
                ["temperature"] = 1.0,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = $"{Prompt(s, p)} You just did what the user asked; say it the way you'd say it, in {language}, " +
                        "with a remark in character about this exact request: about the app, site or thing itself, not a general one (for the dominant one the " +
                        "kind of thing like 'fine, but not because you told me to' or 'next time do it yourself', but always in your own new words). " +
                        "Only when you just switched to this personality, greet the user in it (a new way each time); otherwise no greeting. Don't say " +
                        "'as you asked', 'as requested' or 'just like you wanted', and don't end on a stock question like 'happy now?'." +
                        (Recent.Count > 0 ? " Your new remark may not share any phrase or idea with these ones you made lately: " + string.Join(" | ", Recent) + "." : "") +
                        " Never put a web address in it: name the site or the page instead. Keep every fact exactly (times, numbers, names, what was done); one or two short spoken " +
                        "sentences; plain speech, no emojis. Reply with only what you say." },
                    new JsonObject { ["role"] = "user", ["content"] = request.Length > 0 ? $"The user asked: \"{request}\"\nWhat you did: {Speaker.WithoutLinks(text)}" : Speaker.WithoutLinks(text) },
                },
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reply = (await Groq.ChatAsync(s, body).WaitAsync(timeout.Token))["content"]?.GetValue<string>()?.Trim();
            var lead = Regex.Match(text, @"^[\p{So}\p{Cs}️‍\s]+").Value; // keep the emoji in front ("⏱ ") for the bubble
            if (string.IsNullOrWhiteSpace(reply)) return text;
            Recent.Add(reply);
            if (Recent.Count > 8) Recent.RemoveAt(0);
            return lead + Assistant.Speakable(reply.Trim('"'));
        }
        catch (Exception e)
        {
            Log.Write($"Couldn't restyle \"{text}\": {e.Message}");
            return text;
        }
    }
}
