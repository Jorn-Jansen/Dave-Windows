using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>Groq: the AI (chat) and speech recognition (Whisper). Free tier.</summary>
public static class Groq
{
    public const string Model = "openai/gpt-oss-120b";
    private const string Api = "https://api.groq.com/openai/v1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public class GroqException(string message) : Exception(message);

    /// <summary>Smaller model with its own daily allowance, used when the main one's daily limit is used up.</summary>
    public const string FallbackModel = "openai/gpt-oss-20b";
    private static DateTime useFallbackUntil = DateTime.MinValue;

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
    /// When the main model's daily limit is reached, switches to the smaller model for an hour.
    /// </summary>
    public static async Task<(int status, string text)> ChatRawAsync(Settings settings, JsonObject body)
    {
        var request = (JsonObject)body.DeepClone();
        if (DateTime.Now < useFallbackUntil && request["model"]?.ToString() == Model) request["model"] = FallbackModel;

        var (status, text) = await PostAsync(settings, "/chat/completions",
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        if (status == 429 && IsDailyLimit(text) && request["model"]?.ToString() == Model)
        {
            Log.Write($"Daily limit reached for {Model}; switching to {FallbackModel} for an hour");
            useFallbackUntil = DateTime.Now.AddHours(1);
            request["model"] = FallbackModel;
            (status, text) = await PostAsync(settings, "/chat/completions",
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        }
        return (status, text);
    }

    private static bool IsDailyLimit(string text) => text.Contains("per day (TPD)") || text.Contains("per day (RPD)");

    public static Exception Failure(Settings settings, int status, string text)
    {
        Log.Write($"Groq error {status}: {text}");
        return new GroqException(status switch
        {
            401 => settings.Say("My Groq key isn't working. Check Dave's settings.", "Mijn Groq-sleutel werkt niet. Controleer de instellingen van Dave."),
            429 when IsDailyLimit(text) => settings.Say("I've used up today's free AI allowance. It frees up again gradually over the next hours.",
                "Mijn gratis AI-tegoed voor vandaag is op. Het komt de komende uren stukje bij beetje terug."),
            429 => settings.Say("I've hit my usage limit. Try again in a moment.", "Ik heb mijn limiet bereikt. Probeer het zo nog eens."),
            _ => settings.Say($"The AI had a problem, error {status}.", $"De AI had een probleem, foutcode {status}."),
        });
    }

    /// <summary>Speech to text. Returns the text and the language Whisper heard (e.g. "dutch", "english").</summary>
    public static async Task<(string text, string language)> TranscribeAsync(Settings settings, byte[] wav)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent("whisper-large-v3-turbo"), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        // A hint helps Whisper spell Dave's name and common commands right.
        form.Add(new StringContent("Hé Dave, zet de muziek harder. Hey Dave, play the next song."), "prompt");

        var (status, text) = await PostAsync(settings, "/audio/transcriptions", form);
        if (status is < 200 or >= 300) throw Failure(settings, status, text);
        var json = JsonNode.Parse(text)!;
        var heard = json["text"]?.GetValue<string>().Trim() ?? "";
        var language = json["language"]?.GetValue<string>() ?? "";
        return (IsHallucination(heard) ? "" : heard, language);
    }

    /// <summary>Whisper sometimes "hears" subtitles credits in silence.</summary>
    private static bool IsHallucination(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Length == 0 || t.Contains("ondertitel") || t.Contains("subtitles by") || t.Contains("amara.org")
               || t is "." or "you" or "thank you." or "bedankt voor het kijken.";
    }

    private static async Task<(int, string)> PostAsync(Settings settings, string path, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GroqKey);
        try
        {
            using var response = await Http.SendAsync(request);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Write($"Groq unreachable: {e.Message}");
            throw new GroqException(settings.Say("I can't reach the AI right now. Check the internet connection.",
                "Ik kan de AI nu niet bereiken. Controleer de internetverbinding."));
        }
    }
}
