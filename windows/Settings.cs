using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaveWindows;

/// <summary>Everything Dave remembers between runs, in %APPDATA%\Dave Windows\settings.json.</summary>
public class Settings
{
    public string GroqKey { get; set; } = "";
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
    public bool WakeWord { get; set; } = false;
    /// <summary>Browser for websites when you don't name one, e.g. "Brave" ("" = Windows' default).</summary>
    public string Browser { get; set; } = "";
    public bool StartWithWindows { get; set; } = false;
    /// <summary>How quiet other sound gets while Dave talks (0.3 = 30%).</summary>
    public double DuckTo { get; set; } = 0.3;

    // Spotify (Premium): controls the Spotify app on this PC
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyAccess { get; set; } = "";
    public string SpotifyRefresh { get; set; } = "";
    public long SpotifyExpiry { get; set; }
    public string SpotifyScopes { get; set; } = "";

    public List<string> Memories { get; set; } = new();
    public List<Reminder> Reminders { get; set; } = new();

    public class Reminder
    {
        public long Id { get; set; }
        public DateTimeOffset At { get; set; }
        public string Message { get; set; } = "";
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
