using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaveWindows;

/// <summary>Everything Dave remembers between runs, in %APPDATA%\Dave Windows\settings.json.</summary>
public class Settings
{
    public string GroqKey { get; set; } = "";
    /// <summary>What the assistant is called ("" = Dave).</summary>
    public string AssistantName { get; set; } = "";
    /// <summary>Wake phrase for hands-free use ("" = "hey [name]").</summary>
    public string WakePhrase { get; set; } = "";

    [JsonIgnore] public string Name => AssistantName.Trim().Length > 0 ? AssistantName.Trim() : "Dave";
    [JsonIgnore] public string EffectiveWakePhrase => WakePhrase.Trim().Length > 0 ? WakePhrase.Trim() : $"hey {Name}";
    /// <summary>Country, for units and currency.</summary>
    public string Country { get; set; } = "the Netherlands";
    public string Language { get; set; } = "nl-NL";
    public string SecondLanguage { get; set; } = "en-US";
    /// <summary>Windows voice names, e.g. "Microsoft Frank".</summary>
    public string Voice { get; set; } = "";
    public string SecondVoice { get; set; } = "";
    /// <summary>"windows" (offline, built-in voices) or "azure" (online, natural voices).</summary>
    public string VoiceEngine { get; set; } = "windows";
    public string AzureKey { get; set; } = "";
    /// <summary>Region of the Azure Speech resource, e.g. "westeurope".</summary>
    public string AzureRegion { get; set; } = "westeurope";
    /// <summary>Azure voice short names, e.g. "nl-NL-FennaNeural".</summary>
    public string AzureVoice { get; set; } = "";
    public string AzureSecondVoice { get; set; } = "";

    /// <summary>1.0 = normal speed.</summary>
    public double SpeechRate { get; set; } = 1.0;
    /// <summary>Global shortcut, e.g. "Ctrl+Alt+D".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+D";
    /// <summary>Shortcut that opens and closes Dave's window (chat history, reminders, screen time…).</summary>
    public string WindowHotkey { get; set; } = "Ctrl+Alt+W";
    public bool WakeWord { get; set; } = false;
    /// <summary>Double-check the wake phrase before reacting: fewer accidental wake-ups, but he reacts a bit later.</summary>
    public bool CarefulWakeWord { get; set; } = false;
    /// <summary>Browser for websites when you don't name one, e.g. "Brave" ("" = Windows' default).</summary>
    public string Browser { get; set; } = "";
    public bool StartWithWindows { get; set; } = false;
    public bool AutoUpdate { get; set; } = true;
    /// <summary>How Dave looks: "aurora" (default), "legacy" (the original), "ember", "toxic", "ocean", "sakura", "midnight".</summary>
    public string Theme { get; set; } = "aurora";
    /// <summary>Quiet mode: until when Dave only shows his answers instead of saying them (MinValue = off).</summary>
    public DateTime QuietUntil { get; set; } = DateTime.MinValue;
    [JsonIgnore] public bool IsQuiet => DateTime.Now < QuietUntil;
    /// <summary>Private iCal link(s) of your calendar(s), space-separated (Google: "Secret address in iCal format").</summary>
    public string CalendarLinks { get; set; } = "";

    // The AI: Groq by default (free, fastest); or another provider with an OpenAI-style API
    /// <summary>"groq", "openai", "openrouter" or "custom".</summary>
    public string AiProvider { get; set; } = "groq";
    /// <summary>Key for the provider when it isn't Groq.</summary>
    public string AiKey { get; set; } = "";
    /// <summary>Model to use instead of the provider's default ("" = default).</summary>
    public string AiModel { get; set; } = "";
    /// <summary>For "custom": the API address, e.g. http://localhost:11434/v1 (Ollama).</summary>
    public string AiBaseUrl { get; set; } = "";

    /// <summary>Whether Dave has what he needs to think (a key for the chosen provider; a local custom one may not need one).</summary>
    [JsonIgnore] public bool HasAiKey => AiProvider switch
    {
        "openai" or "openrouter" => AiKey.Length > 0,
        "custom" => AiBaseUrl.Length > 0,
        _ => GroqKey.Length > 0,
    };
    /// <summary>How quiet other sound gets while Dave talks (0.3 = 30%).</summary>
    public double DuckTo { get; set; } = 0.3;

    // Spotify (Premium): controls the Spotify app on this PC
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyAccess { get; set; } = "";
    public string SpotifyRefresh { get; set; } = "";
    public long SpotifyExpiry { get; set; }
    public string SpotifyScopes { get; set; } = "";

    public List<string> Memories { get; set; } = new();
    /// <summary>Songs you said you don't like: skipped when they come on in Spotify, left out of mixes.</summary>
    public List<Song> DislikedSongs { get; set; } = new();

    public class Song
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
    }
    public List<Reminder> Reminders { get; set; } = new();

    public class Reminder
    {
        public long Id { get; set; }
        public DateTimeOffset At { get; set; }
        public string Message { get; set; } = "";
        /// <summary>Something to do instead of say ("lock the PC"): asked as if you said it when it's due. "" for a normal reminder.</summary>
        public string Action { get; set; } = "";
        /// <summary>"" (once), "daily", "weekdays", "weekends", "weekly" (on <see cref="Days"/>), "monthly" or "every" (<see cref="EveryMinutes"/>).</summary>
        public string Repeat { get; set; } = "";
        public List<DayOfWeek> Days { get; set; } = new();
        public int EveryMinutes { get; set; }
        /// <summary>For monthly: the day of the month (stays the 31st in long months, even after a short one).</summary>
        public int MonthDay { get; set; }
    }

    [JsonIgnore] public bool IsDutch => Language.StartsWith("nl", StringComparison.OrdinalIgnoreCase);

    /// <summary>English or Dutch version of one of Dave's own messages, based on the main language.</summary>
    public string Say(string english, string dutch) => IsDutch ? dutch : english;

    // --- Saving ---

    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dave Windows");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object SaveLock = new();

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e)
        {
            Log.Write($"Settings unreadable, starting fresh: {e.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
    }
}

/// <summary>Simple log file next to the settings, for troubleshooting.</summary>
public static class Log
{
    private static readonly string FilePath = Path.Combine(Settings.Folder, "dave.log");
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch { /* logging must never break Dave */ }
        }
    }
}
