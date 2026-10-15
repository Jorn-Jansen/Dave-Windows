using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>
/// Setting presets ("Gaming", "Studying"): named sets of Dave's settings that you switch between in one go, in the
/// settings panel or by voice ("switch to my gaming preset"). Keys and connections (AI, Azure, Spotify, calendar,
/// iPhone) aren't in a preset: those stay as they are.
/// </summary>
public static class Presets
{
    /// <summary>Settings that are never part of a preset.</summary>
    public static readonly HashSet<string> Left = new()
    {
        "GroqKey", "AiProvider", "AiKey", "AiModel", "AiBaseUrl", "AzureKey", "AzureRegion", "AzurePlan", "SpotifyClientId", "CalendarLinks",
        "PhoneEnabled", "PhoneNotify", "SocialName",
    };

    /// <summary>A preset was put in use (by voice): the hotkeys, theme etc. need applying.</summary>
    public static event Action? Applied;

    /// <summary>Only what a preset may hold.</summary>
    public static JsonObject Clean(JsonObject? values)
    {
        var clean = new JsonObject();
        foreach (var (key, value) in values ?? new JsonObject())
            if (!Left.Contains(key) && key.Length <= 40) clean[key] = value?.DeepClone();
        return clean;
    }

    public static Settings.Preset? Find(Settings s, string name)
    {
        var n = name.Trim().ToLowerInvariant().Replace("preset", "").Replace("mode", "").Replace("modus", "").Trim();
        if (n.Length == 0) return null;
        return s.Presets.FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
            ?? s.Presets.FirstOrDefault(p => p.Name.ToLowerInvariant().Contains(n) || n.Contains(p.Name.ToLowerInvariant()));
    }

    /// <summary>"Switch to my gaming preset": its settings in use now (and saved). Returns what to say.</summary>
    public static string Use(Settings s, string name)
    {
        if (s.Presets.Count == 0)
            return s.Say("You don't have any presets yet. Make one in my settings: set things up how you like, then \"Save as new preset\".",
                         "Je hebt nog geen presets. Maak er een in mijn instellingen: stel alles in zoals je wilt en klik op \"Opslaan als nieuwe preset\".");
        if (Find(s, name) is not { } preset)
            return s.Say($"I don't have a preset called {name}. Your presets: {string.Join(", ", s.Presets.Select(p => p.Name))}.",
                         $"Ik heb geen preset die {name} heet. Je presets: {string.Join(", ", s.Presets.Select(p => p.Name))}.");
        var values = DaveWindow.ValuesOf(s);
        foreach (var (key, value) in Clean(preset.Values)) values[key] = value?.DeepClone();
        DaveWindow.Apply(s, values);
        s.ActivePreset = preset.Id;
        s.Save();
        Applied?.Invoke();
        return s.Say($"🎛 Switched to your {preset.Name} preset.", $"🎛 Je preset {preset.Name} staat nu aan.");
    }
}
