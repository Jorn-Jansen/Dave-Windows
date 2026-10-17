using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>
/// The AI (chat) and speech recognition (Whisper). Groq by default (free tier, fastest); the chat can also go to
/// OpenAI, OpenRouter or any other provider with an OpenAI-style API (Settings → AI provider).
/// </summary>
public static class Groq
{
    public const string Model = "openai/gpt-oss-120b";
    private static readonly string Api = Environment.GetEnvironmentVariable("DAVE_GROQ_URL") ?? "https://api.groq.com/openai/v1"; // (another address only for testing)
    private const string OpenAiApi = "https://api.openai.com/v1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public class GroqException(string message) : Exception(message);

    /// <summary>Where the chat goes: name (for messages), address, default chat model and model for images.</summary>
    public record Provider(string Name, string BaseUrl, string ChatModel, string VisionModel);

    public static Provider ProviderFor(Settings s) => s.AiProvider switch
    {
        "openai" => new("OpenAI", OpenAiApi, "gpt-4.1-mini", "gpt-4.1-mini"),
        "openrouter" => new("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-oss-120b", "google/gemini-2.5-flash"),
        "custom" => new("AI provider", s.AiBaseUrl.Trim().TrimEnd('/'), s.AiModel.Trim(), s.AiModel.Trim()),
        _ => new("Groq", Api, Model, Vision.Model),
    };

    public static bool UsesGroq(Settings s) => s.AiProvider is not ("openai" or "openrouter" or "custom");

    /// <summary>
    /// Adapt a request made for Groq to another provider: its model, and without the Groq-only parts
    /// (reasoning settings, built-in web search).
    /// </summary>
    private static JsonObject ForProvider(Settings s, JsonObject request)
    {
        var provider = ProviderFor(s);
        var forImages = request["model"]?.ToString() == Vision.Model;
        request["model"] = forImages ? provider.VisionModel : s.AiModel.Trim().Length > 0 ? s.AiModel.Trim() : provider.ChatModel;
        request.Remove("reasoning_effort");
        request.Remove("reasoning_format");
        if (request["tools"] is JsonArray tools)
        {
            foreach (var tool in tools.Where(t => t?["type"]?.ToString() != "function").ToList()) tools.Remove(tool);
            if (tools.Count == 0) { request.Remove("tools"); request.Remove("tool_choice"); }
        }
        return request;
    }

    /// <summary>Smaller model with its own daily allowance, used when the main one's daily limit is used up (on every key).</summary>
    public const string FallbackModel = "openai/gpt-oss-20b";

    /// <summary>(key, model) → until when that key is at a limit for that model: then the next key in line answers.</summary>
    private static readonly Dictionary<(string key, string model), DateTime> limited = new();

    /// <summary>The Groq keys in their order: the main one, then the extra ones (Settings → Connections → AI).</summary>
    public static List<string> Keys(Settings s) =>
        new[] { s.GroqKey }.Concat(s.GroqKeys ?? new()).Select(k => (k ?? "").Trim()).Where(k => k.Length > 0).Distinct().ToList();

    private static List<string> Free(Settings s, string model)
    {
        lock (limited) return Keys(s).Where(k => !limited.TryGetValue((k, model), out var until) || until <= DateTime.Now).ToList();
    }

    /// <summary>How long until the first key is free again for one of [models] (zero when one is free now).</summary>
    private static TimeSpan Soonest(Settings s, params string[] models)
    {
        lock (limited)
        {
            var times = Keys(s).SelectMany(k => models.Select(m => limited.TryGetValue((k, m), out var until) ? until : DateTime.MinValue)).ToList();
            return times.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(0, (times.Min() - DateTime.Now).Ticks));
        }
    }

    /// <summary>[key] is at a limit for [model] (per minute, per day, any): skipped until Groq says it's over.</summary>
    private static void NoteLimit(Settings s, string key, string model, string text)
    {
        var seconds = LimitSeconds(text) ?? (IsDailyLimit(text) ? 3600 : 60);
        lock (limited) limited[(key, model)] = DateTime.Now.AddSeconds(seconds);
        var more = Free(s, model).Count > 0;
        Log.Write($"Groq key {Keys(s).IndexOf(key) + 1}: {(IsDailyLimit(text) ? "daily" : "per-minute")} limit for {model}, {seconds:F0} s{(more ? "; the next key" : "")}");
    }

    /// <summary>"Please try again in 1h2m5.3s" → seconds (null when not given).</summary>
    private static double? LimitSeconds(string text)
    {
        var m = Regex.Match(text, @"try again in (?:(\d+)h)?(?:(\d+)m(?!s))?(?:([\d.]+)(ms|s))?");
        if (!m.Success || !(m.Groups[1].Success || m.Groups[2].Success || m.Groups[3].Success)) return null;
        return (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) * 3600 : 0) + (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) * 60 : 0)
               + (m.Groups[3].Success ? double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) / (m.Groups[4].Value == "ms" ? 1000 : 1) : 0);
    }

    /// <summary>Send a chat request; returns the "message" object of the first choice.</summary>
    public static async Task<JsonObject> ChatAsync(Settings settings, JsonObject body)
    {
        var (status, text) = await ChatRawAsync(settings, body);
        if (status is >= 200 and < 300)
            return JsonNode.Parse(text)!["choices"]![0]!["message"]!.AsObject();
        throw Failure(settings, status, text);
    }

    /// <summary>
    /// Raw status and body, for callers that handle errors themselves (web search fallback).
    /// Goes down the Groq keys when one is at a limit; when they all are: a short wait for the main model, else the smaller
    /// model (its own allowance) on the keys in the same order, else the last refusal (so the caller can say how long it takes).
    /// </summary>
    public static async Task<(int status, string text)> ChatRawAsync(Settings settings, JsonObject body)
    {
        var request = (JsonObject)body.DeepClone();
        if (!UsesGroq(settings))
        {
            var provider = ProviderFor(settings);
            var json = ForProvider(settings, request).ToJsonString();
            var result = await PostAsync(settings, provider.BaseUrl, settings.AiKey, "/chat/completions", new StringContent(json, Encoding.UTF8, "application/json"));
            if (result.Item1 == 429 && WaitTime(result.Item2) is { } pause)
            {
                Log.Write($"Rate limit at {provider.Name}; waiting {pause.TotalSeconds:F1} s");
                await Task.Delay(pause);
                result = await PostAsync(settings, provider.BaseUrl, settings.AiKey, "/chat/completions", new StringContent(json, Encoding.UTF8, "application/json"));
            }
            return result;
        }
        var wanted = request["model"]?.ToString() ?? Model;
        var models = wanted == Model ? new[] { Model, FallbackModel } : new[] { wanted }; // (pictures use their own model: no smaller one)
        if (Keys(settings).Count == 0)
            return await PostAsync(settings, Api, "", "/chat/completions", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        var last = (status: 429, text: "");
        for (var attempt = 0; attempt < 12; attempt++)
        {
            string? key = null, model = null;
            foreach (var m in models)
            {
                var soonBig = Soonest(settings, wanted);
                if (m == FallbackModel && soonBig > TimeSpan.Zero && soonBig <= TimeSpan.FromSeconds(10)) break; // the main one is back in a moment: wait for it
                var free = Free(settings, m);
                if (free.Count > 0) { (key, model) = (free[0], m); break; }
            }
            if (key == null)
            {
                var wait = Soonest(settings, models);
                if (wait > TimeSpan.FromSeconds(65) || attempt == 11) return last; // (every key at a limit for a while: the caller says so)
                Log.Write($"All Groq keys at a limit; waiting {wait.TotalSeconds:F1} s");
                await Task.Delay(wait + TimeSpan.FromSeconds(0.3));
                continue;
            }
            request["model"] = model;
            var (status, text) = await PostAsync(settings, Api, key, "/chat/completions",
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
            if (status is >= 200 and < 300 && Regex.Match(text, "\"prompt_tokens\":\\s*(\\d+)") is { Success: true } used)
                Log.Write($"Tokens used: {used.Groups[1].Value} in (key {Keys(settings).IndexOf(key) + 1})"); // the free tier allows 8000 per minute
            if (status == 429)
            {
                NoteLimit(settings, key, model!, text);
                last = (status, text);
                continue;
            }
            if (status == 401 && Keys(settings).Count > 1)
            {
                Log.Write($"Groq key {Keys(settings).IndexOf(key) + 1} doesn't work: skipped");
                lock (limited) foreach (var m in models.Append("whisper")) limited[(key, m)] = DateTime.Now.AddHours(1);
                last = (status, text);
                if (models.Any(m => Free(settings, m).Count > 0)) continue;
            }
            return (status, text);
        }
        return last;
    }

    private static bool IsDailyLimit(string text) => text.Contains("per day (TPD)") || text.Contains("per day (RPD)");

    /// <summary>"Please try again in 4.53s" -> 4.8 s; null when it's not given or longer than 15 s (then it's better to say so).</summary>
    private static TimeSpan? WaitTime(string text)
    {
        var m = Regex.Match(text, @"try again in (?:(\d+)m)?([\d.]+)(ms|s)");
        if (!m.Success) return null;
        var seconds = (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) * 60 : 0)
                      + double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) / (m.Groups[3].Value == "ms" ? 1000 : 1);
        return seconds <= 15 ? TimeSpan.FromSeconds(seconds + 0.3) : null;
    }

    public static Exception Failure(Settings settings, int status, string text, string? who = null)
    {
        who ??= ProviderFor(settings).Name;
        Log.Write($"{who} error {status}: {text}");
        return new GroqException(status switch
        {
            401 => settings.Say($"My {who} key isn't working. Check Dave's settings.", $"Mijn {who}-sleutel werkt niet. Controleer de instellingen van Dave."),
            402 => settings.Say($"My {who} account is out of credit.", $"Mijn {who}-account heeft geen tegoed meer."),
            404 when who != "Groq" => settings.Say($"{who} doesn't know that model. Check the model in Dave's settings.",
                $"{who} kent dat model niet. Controleer het model in de instellingen van Dave."),
            429 when IsDailyLimit(text) => settings.Say("I've used up today's free AI allowance. It frees up again gradually over the next hours.",
                "Mijn gratis AI-tegoed voor vandaag is op. Het komt de komende uren stukje bij beetje terug."),
            429 => settings.Say("I've hit my usage limit. Try again in a moment.", "Ik heb mijn limiet bereikt. Probeer het zo nog eens."),
            _ => settings.Say($"The AI had a problem, error {status}.", $"De AI had een probleem, foutcode {status}."),
        });
    }

    /// <summary>Speech to text. Returns the text and the language Whisper heard (e.g. "dutch", "english").</summary>
    /// <remarks>Uses OpenAI when that's the AI provider; otherwise Groq (other providers don't do speech), so it needs a Groq key.</remarks>
    /// <summary>The last recording that was understood, and when each of its words was said (in seconds): for voice messages.</summary>
    public static byte[]? LastRecording { get; private set; }
    public static string LastHeard { get; private set; } = "";
    public static List<(string word, double start, double end)> LastWords { get; private set; } = new();

    /// <param name="fileName">What the audio is ("speech.wav", "x.m4a"): the service goes by its extension.</param>
    /// <param name="remember">False for a friend's voice message: then it isn't taken as the last thing you said.</param>
    public static async Task<(string text, string language)> TranscribeAsync(Settings settings, byte[] wav, string fileName = "speech.wav", bool remember = true)
    {
        var openAi = settings.AiProvider == "openai" && settings.AiKey.Length > 0;
        if (!openAi && Keys(settings).Count == 0)
            throw new GroqException(settings.Say("To understand what you say, I need a free Groq key. Add one in my settings.",
                "Om te verstaan wat je zegt heb ik een gratis Groq-sleutel nodig. Voeg er een toe in mijn instellingen."));
        MultipartFormDataContent Form()
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? "audio/mp4" : "audio/wav");
            form.Add(file, "file", fileName);
            form.Add(new StringContent(openAi ? "whisper-1" : "whisper-large-v3-turbo"), "model");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("word"), "timestamp_granularities[]"); // when each word was said ("send Alex a voice message …")
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
            // A hint helps Whisper spell Dave's name right. Only the name: Whisper repeats its hint when it hears just noise,
            // and a hint with a command in it would then be carried out without anyone saying it.
            form.Add(new StringContent($"{settings.Name}."), "prompt");
            return form;
        }

        var (status, text) = (0, "");
        if (openAi) (status, text) = await PostAsync(settings, OpenAiApi, settings.AiKey, "/audio/transcriptions", Form());
        else
        {
            var free = Free(settings, "whisper");
            foreach (var key in free.Count > 0 ? free : Keys(settings).Take(1).ToList())
            {
                (status, text) = await PostAsync(settings, Api, key, "/audio/transcriptions", Form());
                if (status != 429) break;
                NoteLimit(settings, key, "whisper", text); // at its limit: the next key in line
            }
        }
        if (status == 400 && text.Contains("media file"))
        {
            // The recording itself was refused: treat it as "didn't catch that", and keep it to look at what was wrong
            Log.Write($"Recording refused by speech-to-text ({wav.Length} bytes); saved as last-refused-recording.wav: {text}");
            try { File.WriteAllBytes(Path.Combine(Settings.Folder, "last-refused-recording.wav"), wav); } catch { }
            return ("", "");
        }
        if (status is < 200 or >= 300) throw Failure(settings, status, text, openAi ? "OpenAI" : "Groq");
        var json = JsonNode.Parse(text)!;
        var heard = json["text"]?.GetValue<string>().Trim() ?? "";
        if (remember)
        {
            LastRecording = wav;
            LastHeard = heard;
            LastWords = (json["words"] as JsonArray)?.Select(w => (w?["word"]?.ToString()?.Trim() ?? "", w?["start"]?.GetValue<double>() ?? 0, w?["end"]?.GetValue<double>() ?? 0)).ToList()
                    ?? new List<(string, double, double)>();
        }
        var language = json["language"]?.GetValue<string>() ?? "";
        return (IsHallucination(heard) ? "" : heard, language);
    }

    /// <summary>Whisper sometimes "hears" subtitles credits in silence.</summary>
    private static bool IsHallucination(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Length == 0 || !t.Any(char.IsLetterOrDigit) || t.Contains("ondertitel") || t.Contains("subtitles by") || t.Contains("amara.org")
               || t is "." or "you" or "thank you." or "bedankt voor het kijken.";
    }

    private static async Task<(int, string)> PostAsync(Settings settings, string baseUrl, string key, string path, HttpContent content)
    {
        if (!Uri.TryCreate(baseUrl + path, UriKind.Absolute, out var address))
            throw new GroqException(settings.Say("The AI provider's address in my settings isn't right.", "Het adres van de AI-provider in mijn instellingen klopt niet."));
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = content };
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); // a local AI may not need one
        if (baseUrl.Contains("openrouter.ai")) request.Headers.Add("X-Title", "Dave"); // shows up as the app name on OpenRouter
        try
        {
            using var response = await Http.SendAsync(request);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Write($"AI unreachable ({baseUrl}): {e.Message}");
            throw new GroqException(settings.Say("I can't reach the AI right now. Check the internet connection.",
                "Ik kan de AI nu niet bereiken. Controleer de internetverbinding."));
        }
    }
}
