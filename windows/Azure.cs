using System.Security;
using System.Text;
using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>Natural-sounding Azure voices (online). Free tier: about 500,000 characters a month.</summary>
public static class Azure
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public record Voice(string ShortName, string DisplayName, string Locale, string Gender, bool Multilingual, List<string> OtherLocales)
    {
        public override string ToString() => $"{DisplayName} ({Gender}, {Locale}){(Multilingual ? " – speaks several languages" : "")}";
    }

    public static bool IsConfigured(Settings s) => s.AzureKey.Length > 0 && s.AzureRegion.Length > 0;

    /// <summary>Speech as WAV bytes (24 kHz mono), in [languageTag] with the chosen Azure voice.</summary>
    public static async Task<byte[]> SynthesizeAsync(Settings s, string text, string languageTag, CancellationToken cancel)
    {
        var voice = languageTag == s.Language ? s.AzureVoice : s.AzureSecondVoice;
        if (voice.Length == 0) throw new InvalidOperationException($"No Azure voice chosen for {languageTag}");

        // "Multilingual" voices speak other languages too, as long as we say which one this sentence is in.
        var body = SecurityElement.Escape(text);
        if (voice.Contains("Multilingual", StringComparison.OrdinalIgnoreCase)) body = $"<lang xml:lang='{languageTag}'>{body}</lang>";
        var rate = (int)Math.Round((s.SpeechRate - 1.0) * 100);
        var voiceLocale = string.Join('-', voice.Split('-').Take(2));
        var ssml = $"<speak version='1.0' xml:lang='{voiceLocale}'><voice name='{voice}'>" +
                   $"<prosody rate='{(rate >= 0 ? "+" : "")}{rate}%'>{body}</prosody></voice></speak>";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{s.AzureRegion}.tts.speech.microsoft.com/cognitiveservices/v1")
        {
            Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", s.AzureKey);
        request.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
        request.Headers.Add("User-Agent", "DaveWindows");
        using var response = await Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azure said {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancel)}");
        return await response.Content.ReadAsByteArrayAsync(cancel);
    }

    /// <summary>All Azure voices that can speak [languageTag]'s language (its own voices plus multilingual ones).</summary>
    public static async Task<List<Voice>> VoicesForAsync(Settings s, string languageTag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{s.AzureRegion}.tts.speech.microsoft.com/cognitiveservices/voices/list");
        request.Headers.Add("Ocp-Apim-Subscription-Key", s.AzureKey);
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Azure said {(int)response.StatusCode}. Check the key and region.");

        var language = languageTag.Split('-')[0].ToLowerInvariant();
        var voices = new List<Voice>();
        foreach (var v in JsonNode.Parse(text)!.AsArray())
        {
            if (v == null) continue;
            var locale = v["Locale"]?.ToString() ?? "";
            var others = v["SecondaryLocaleList"]?.AsArray().Select(x => x?.ToString() ?? "").ToList() ?? new List<string>();
            var shortName = v["ShortName"]?.ToString() ?? "";
            var multilingual = shortName.Contains("Multilingual", StringComparison.OrdinalIgnoreCase);
            var own = locale.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase);
            // Multilingual voices handle Dutch and English even when not listed as a secondary locale.
            if (own || (multilingual && (language is "en" or "nl" || others.Any(o => o.StartsWith(language + "-")))))
                voices.Add(new Voice(shortName, v["LocalName"]?.ToString() ?? v["DisplayName"]?.ToString() ?? shortName,
                    locale, v["Gender"]?.ToString() ?? "", multilingual, others));
        }
        // The language's own voices first (the country's own accent on top), then the multilingual ones.
        return voices
            .OrderBy(v => v.Locale.Equals(languageTag, StringComparison.OrdinalIgnoreCase) ? 0 : v.Locale.StartsWith(language) ? 1 : 2)
            .ThenBy(v => v.DisplayName)
            .ToList();
    }
}
