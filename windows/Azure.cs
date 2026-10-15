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
    /// <param name="speed">Times the speed setting, and [pitch] (1 = the voice's own), for the personality and mood.</param>
    /// <param name="style">A speaking style (excited, calm, whispering…), used only when this voice has it; [degree] how strongly (0.01..2).</param>
    public static async Task<byte[]> SynthesizeAsync(Settings s, string text, string languageTag, CancellationToken cancel,
        double speed = 1, double pitch = 1, string style = "", double degree = 1)
    {
        var voice = languageTag == s.Language ? s.AzureVoice : s.AzureSecondVoice;
        if (voice.Length == 0) throw new InvalidOperationException($"No Azure voice chosen for {languageTag}");

        // "Multilingual" voices speak other languages too, as long as we say which one this sentence is in.
        var body = SecurityElement.Escape(text);
        if (voice.Contains("Multilingual", StringComparison.OrdinalIgnoreCase)) body = $"<lang xml:lang='{languageTag}'>{body}</lang>";
        var rate = (int)Math.Round((s.SpeechRate * speed - 1.0) * 100);
        var tone = (int)Math.Round((pitch - 1.0) * 100);
        var voiceLocale = string.Join('-', voice.Split('-').Take(2));
        var pitchAttribute = tone != 0 ? $" pitch='{(tone > 0 ? "+" : "")}{tone}%'" : ""; // only when changed: not every voice takes it
        body = $"<prosody rate='{(rate >= 0 ? "+" : "")}{rate}%'{pitchAttribute}>{body}</prosody>";
        if (style.Length > 0 && (await StylesAsync(s, voice)).Contains(style))
            body = $"<mstts:express-as style='{style}' styledegree='{Math.Clamp(degree, 0.01, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}'>{body}</mstts:express-as>";
        var ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='{voiceLocale}'>" +
                   $"<voice name='{voice}'>{body}</voice></speak>";

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
        Count(s, text.Length);
        return await response.Content.ReadAsByteArrayAsync(cancel);
    }

    /// <summary>The free plan: this many characters a month.</summary>
    public const long FreeCharacters = 500_000;

    /// <summary>Keeps count of what Azure said this month (a new month starts at 0).</summary>
    private static void Count(Settings s, int characters)
    {
        var month = DateTime.Now.ToString("yyyy-MM");
        if (s.AzureMonth != month) { s.AzureMonth = month; s.AzureCharacters = 0; }
        s.AzureCharacters += characters;
        s.Save();
    }

    /// <summary>Used this month (0 when the count is from another month).</summary>
    public static long UsedThisMonth(Settings s) => s.AzureMonth == DateTime.Now.ToString("yyyy-MM") ? s.AzureCharacters : 0;

    /// <summary>"How much Azure do I have left?": the count, for saying it.</summary>
    public static string Usage(Settings s)
    {
        if (!IsConfigured(s)) return s.Say("You're not using the Azure voices: there's no Azure key in my settings.", "Je gebruikt de Azure-stemmen niet: er staat geen Azure-sleutel in mijn instellingen.");
        var used = UsedThisMonth(s);
        var left = Math.Max(0, FreeCharacters - used);
        var nl = System.Globalization.CultureInfo.GetCultureInfo("nl-NL");
        var en = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var percent = (int)Math.Round(100.0 * used / FreeCharacters);
        return s.Say($"This month I've used about {used.ToString("N0", en)} of the {FreeCharacters.ToString("N0", en)} free Azure characters ({percent}%), so about {left.ToString("N0", en)} are left. That's roughly {left / 100:N0} more answers. It starts over on the 1st.",
                     $"Deze maand heb ik ongeveer {used.ToString("N0", nl)} van de {FreeCharacters.ToString("N0", nl)} gratis Azure-tekens gebruikt ({percent}%), dus er zijn er nog ongeveer {left.ToString("N0", nl)} over. Dat is grofweg {(left / 100).ToString("N0", nl)} antwoorden. Op de 1e begint het opnieuw.");
    }

    private static readonly Dictionary<string, HashSet<string>> Styles = new();

    /// <summary>The speaking styles a voice has (most voices have none), from Azure's voice list; asked once per voice.</summary>
    private static async Task<HashSet<string>> StylesAsync(Settings s, string voice)
    {
        lock (Styles) if (Styles.TryGetValue(voice, out var known)) return known;
        var styles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{s.AzureRegion}.tts.speech.microsoft.com/cognitiveservices/voices/list");
            request.Headers.Add("Ocp-Apim-Subscription-Key", s.AzureKey);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return styles; // try again next time
            foreach (var v in JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray())
            {
                if (v?["ShortName"]?.ToString() is not { } name) continue;
                var list = new HashSet<string>(v["StyleList"]?.AsArray().Select(x => x?.ToString() ?? "") ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                lock (Styles) Styles[name] = list;
            }
            lock (Styles) { if (!Styles.ContainsKey(voice)) Styles[voice] = styles; return Styles[voice]; }
        }
        catch (Exception e)
        {
            Log.Write($"Couldn't get the Azure voice styles: {e.Message}");
            return styles;
        }
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
