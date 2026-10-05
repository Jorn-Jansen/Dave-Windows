using System.Text.Json;
using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>
/// The settings panel in Dave's window: sends the current settings to the page, and saves what comes back
/// only when you press Save (the ✕ just closes the panel, nothing changes).
/// </summary>
public sealed partial class DaveWindow
{
    private async Task OnSettingsMessageAsync(string type, JsonObject m)
    {
        try
        {
            var values = m["values"] as JsonObject ?? new JsonObject();
            switch (type)
            {
                case "getSettings":
                    Send(SettingsData());
                    break;
                case "voices":
                    var lang = m["lang"]?.ToString() ?? "";
                    Send(new JsonObject
                    {
                        ["type"] = "voices", ["field"] = m["field"]?.ToString(),
                        ["items"] = new JsonArray(Speaker.VoicesFor(lang).Select(v => (JsonNode)v.DisplayName).ToArray()),
                    });
                    break;
                case "azureVoices":
                {
                    var temp = Draft(values);
                    if (!Azure.IsConfigured(temp)) { Send(new JsonObject { ["type"] = "azureVoices", ["status"] = "Fill in the key and region first" }); break; }
                    async Task<JsonArray> List(string tag) => tag.Trim().Length == 0 ? new JsonArray()
                        : new JsonArray((await Azure.VoicesForAsync(temp, tag)).Select(v => (JsonNode)new JsonObject { ["id"] = v.ShortName, ["name"] = v.ToString() }).ToArray());
                    var main = await List(temp.Language);
                    var second = await List(temp.SecondLanguage);
                    Send(new JsonObject { ["type"] = "azureVoices", ["main"] = main, ["second"] = second, ["status"] = $"✅ {main.Count} + {second.Count} voices" });
                    break;
                }
                case "preview":
                {
                    // With the settings as they are in the panel, not yet saved; always heard, also in quiet mode
                    var temp = Draft(values);
                    temp.VoiceEngine = m["engine"]?.ToString() == "azure" ? "azure" : "windows";
                    temp.QuietUntil = DateTime.MinValue;
                    var tag = m["which"]?.ToString() == "second" ? temp.SecondLanguage : temp.Language;
                    if (tag.Trim().Length == 0) break;
                    var sample = tag.StartsWith("nl") ? $"Hoi, ik ben {temp.Name}. Waar kan ik je mee helpen?" : $"Hi, I'm {temp.Name}. How can I help?";
                    await Speaker.SpeakAsync(temp, sample, tag);
                    break;
                }
                case "spotifyConnect":
                {
                    var id = m["clientId"]?.ToString()?.Trim() ?? "";
                    if (id.Length == 0) { Send(new JsonObject { ["type"] = "spotify", ["status"] = "Paste the Client ID first" }); break; }
                    if (id != settings.SpotifyClientId) { settings.SpotifyClientId = id; settings.SpotifyRefresh = ""; }
                    Send(new JsonObject { ["type"] = "spotify", ["status"] = "Log in in your browser…" });
                    try
                    {
                        await Spotify.LoginAsync(settings);
                        settings.Save();
                    }
                    catch (Exception e)
                    {
                        Log.Write($"Spotify login failed: {e}");
                        Send(new JsonObject { ["type"] = "spotify", ["status"] = "❌ " + e.Message });
                        break;
                    }
                    Send(new JsonObject { ["type"] = "spotify", ["status"] = Spotify.IsConnected(settings) ? "✅ connected" : "not connected" });
                    break;
                }
                case "saveSettings":
                    Apply(settings, values);
                    settings.Save();
                    applySettings();
                    Text = settings.Name;
                    Send(new JsonObject { ["type"] = "saved", ["warning"] = WakePhraseWarning() });
                    Push();
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Write($"Settings panel: {e}");
            Send(new JsonObject { ["type"] = "toast", ["text"] = "Something went wrong: " + e.Message });
        }
    }

    /// <summary>Everything the settings panel shows.</summary>
    private JsonObject SettingsData() => new()
    {
        ["type"] = "settingsData",
        ["values"] = new JsonObject
        {
            ["GroqKey"] = settings.GroqKey, ["AiProvider"] = settings.AiProvider, ["AiKey"] = settings.AiKey,
            ["AiModel"] = settings.AiModel, ["AiBaseUrl"] = settings.AiBaseUrl,
            ["AssistantName"] = settings.AssistantName, ["WakePhrase"] = settings.WakePhrase, ["Country"] = settings.Country,
            ["Language"] = settings.Language, ["SecondLanguage"] = settings.SecondLanguage, ["Browser"] = settings.Browser,
            ["VoiceEngine"] = settings.VoiceEngine, ["Voice"] = settings.Voice, ["SecondVoice"] = settings.SecondVoice,
            ["AzureKey"] = settings.AzureKey, ["AzureRegion"] = settings.AzureRegion,
            ["AzureVoice"] = settings.AzureVoice, ["AzureSecondVoice"] = settings.AzureSecondVoice,
            ["SpeechRate"] = settings.SpeechRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Hotkey"] = settings.Hotkey, ["WindowHotkey"] = settings.WindowHotkey,
            ["WakeWord"] = settings.WakeWord, ["StartWithWindows"] = settings.StartWithWindows, ["AutoUpdate"] = settings.AutoUpdate,
            ["CalendarLinks"] = settings.CalendarLinks, ["SpotifyClientId"] = settings.SpotifyClientId,
        },
        ["voices"] = new JsonObject
        {
            ["main"] = new JsonArray(Speaker.VoicesFor(settings.Language).Select(v => (JsonNode)v.DisplayName).ToArray()),
            ["second"] = new JsonArray(Speaker.VoicesFor(settings.SecondLanguage).Select(v => (JsonNode)v.DisplayName).ToArray()),
        },
        ["spotify"] = Spotify.IsConnected(settings) ? "✅ connected" : "not connected",
        ["redirect"] = Spotify.RedirectUri,
        ["version"] = Updater.Current.ToString(3),
    };

    /// <summary>A copy of the settings with the panel's (unsaved) values, for previews and loading voices.</summary>
    private Settings Draft(JsonObject values)
    {
        var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Apply(copy, values);
        return copy;
    }

    /// <summary>The panel's values into [s], with the same defaults as before when a field is left empty.</summary>
    private static void Apply(Settings s, JsonObject v)
    {
        string Str(string key) => v[key]?.ToString()?.Trim() ?? "";
        bool Bool(string key) => v[key] is JsonValue b && b.TryGetValue<bool>(out var x) && x;
        if (v.Count == 0) return;
        s.GroqKey = Str("GroqKey");
        s.AiProvider = Str("AiProvider") is { Length: > 0 } provider ? provider : "groq";
        s.AiKey = Str("AiKey");
        s.AiModel = Str("AiModel");
        s.AiBaseUrl = Str("AiBaseUrl");
        s.AssistantName = Str("AssistantName");
        s.WakePhrase = Str("WakePhrase");
        s.Country = Str("Country") is { Length: > 0 } country ? country : "the Netherlands";
        s.Language = Str("Language") is { Length: > 0 } language ? language : "nl-NL";
        s.SecondLanguage = Str("SecondLanguage");
        s.Browser = Str("Browser");
        s.VoiceEngine = Str("VoiceEngine") == "azure" ? "azure" : "windows";
        s.Voice = Str("Voice");
        s.SecondVoice = Str("SecondVoice");
        s.AzureKey = Str("AzureKey");
        s.AzureRegion = Str("AzureRegion").ToLowerInvariant().Replace(" ", "");
        s.AzureVoice = Str("AzureVoice");
        s.AzureSecondVoice = Str("AzureSecondVoice");
        if (double.TryParse(Str("SpeechRate"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
            s.SpeechRate = Math.Clamp(rate, 0.5, 3.0);
        s.Hotkey = Str("Hotkey") is { Length: > 0 } hotkey ? hotkey : "Ctrl+Alt+D";
        s.WindowHotkey = Str("WindowHotkey") is { Length: > 0 } windowHotkey ? windowHotkey : "Ctrl+Alt+W";
        s.WakeWord = Bool("WakeWord");
        s.StartWithWindows = Bool("StartWithWindows");
        s.AutoUpdate = Bool("AutoUpdate");
        s.CalendarLinks = Str("CalendarLinks");
        if (Str("SpotifyClientId") != s.SpotifyClientId)
        {
            s.SpotifyClientId = Str("SpotifyClientId");
            s.SpotifyRefresh = ""; // a different app: log in again
        }
    }

    /// <summary>The offline listener only knows English words; say so instead of silently never waking up.</summary>
    private string? WakePhraseWarning()
    {
        if (!settings.WakeWord) return null;
        List<string> unknown;
        try { unknown = WakeWord.UnknownWords(settings.EffectiveWakePhrase); }
        catch { return null; } // model not available: nothing to check against
        if (unknown.Count == 0) return null;
        var fallback = WakeWord.UnknownWords($"hey {settings.Name}").Count == 0 ? $"hey {settings.Name}" : "hey dave";
        return $"Saved. The wake-word listener doesn't know \"{string.Join(", ", unknown)}\", so it listens for \"{fallback}\" for now. " +
               "Names like Jarvis, Friday, Alexa and Kyle work.";
    }
}
