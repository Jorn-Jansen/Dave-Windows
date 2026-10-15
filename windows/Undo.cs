using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>
/// "Undo that": takes back what Dave did last (all of it, when he did several things at once). Before a command runs,
/// what it can change is kept (settings, reminders, memories, the stopwatch, the volume, which programs were open); undoing
/// puts that back, reopens programs he closed, closes ones he opened, cancels a shutdown, deletes a message he sent.
/// Some things can't be taken back (a website he opened, typing, locking the PC): then he says so. Kept for 30 minutes.
/// </summary>
public static class Undo
{
    /// <summary>What one command changed, and how to put it back (returns what was undone, or null when there was nothing to do).</summary>
    public sealed class Step
    {
        public string Name = "";
        public Func<Task<string?>>? Back;
        public string? CantUndo; // why this one can't be taken back
    }

    private static List<Step> last = new();
    private static DateTime lastAt;

    /// <summary>Before running [name]: what's needed to undo it. Finish it with <see cref="After"/> once it ran.</summary>
    public static Step Before(Settings s, string name, JsonObject args)
    {
        var step = new Step { Name = name };
        switch (name)
        {
            case "change_setting" or "set_personality" or "use_preset" or "timers_on_screen" or "quiet_mode":
            {
                var values = DaveWindow.ValuesOf(s);
                var (quiet, preset) = (s.QuietUntil, s.ActivePreset);
                step.Back = () =>
                {
                    DaveWindow.Apply(s, values);
                    (s.QuietUntil, s.ActivePreset) = (quiet, preset);
                    s.Save();
                    SettingsUndone?.Invoke();
                    return Task.FromResult<string?>(s.Say("your settings are back", "je instellingen staan terug"));
                };
                break;
            }
            case "set_reminder" or "cancel_reminders":
            {
                var reminders = Clone(s.Reminders);
                step.Back = () => { s.Reminders = reminders; s.Save(); return Task.FromResult<string?>(s.Say("the reminders are as they were", "de herinneringen staan terug")); };
                break;
            }
            case "remember" or "forget":
            {
                var memories = s.Memories.ToList();
                step.Back = () => { s.Memories = memories; s.Save(); return Task.FromResult<string?>(s.Say("my memory is as it was", "mijn geheugen staat terug")); };
                break;
            }
            case "stopwatch":
            {
                var watch = Clone(s.Stopwatch);
                step.Back = () => { s.Stopwatch = watch; s.Save(); return Task.FromResult<string?>(s.Say("the stopwatch is back", "de stopwatch staat terug")); };
                break;
            }
            case "set_volume" when (args["app"]?.ToString() ?? "").Length == 0:
            {
                var volume = SystemAudio.GetVolume();
                step.Back = () => { SystemAudio.SetMute(false); SystemAudio.SetVolume(volume); return Task.FromResult<string?>(s.Say($"the volume is back at {volume}%", $"het volume staat weer op {volume}%")); };
                break;
            }
            case "media_control":
            {
                var opposite = args["action"]?.ToString() switch { "play" => "pause", "pause" => "play", "next" => "previous", "previous" => "next", _ => null };
                if (opposite != null)
                    step.Back = async () => { if (!await MediaSession.ControlAsync(opposite)) SystemAudio.MediaKey(opposite); return s.Say("the music is back", "de muziek staat terug"); };
                break;
            }
            case "close_app" or "close_all_apps":
            {
                var before = OpenPrograms();
                step.Back = () =>
                {
                    var gone = before.Except(OpenPrograms()).ToList();
                    foreach (var path in gone)
                        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { Log.Write($"Undo: couldn't reopen {path}: {e.Message}"); }
                    return Task.FromResult<string?>(gone.Count == 0 ? null
                        : s.Say("I opened ", "ik heb ") + string.Join(", ", gone.Select(Path.GetFileNameWithoutExtension)) + s.Say(" again", " weer geopend"));
                };
                break;
            }
            case "power":
                step.Back = () =>
                {
                    Process.Start(new ProcessStartInfo("shutdown", "/a") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000);
                    return Task.FromResult<string?>(s.Say("the shutdown is cancelled", "het uitzetten is geannuleerd"));
                };
                break;
            case "open_website": step.CantUndo = s.Say("I can't close a website for you", "een website kan ik niet voor je sluiten"); break;
            case "type_text": step.CantUndo = s.Say("I can't take back typing", "typen kan ik niet terugdraaien"); break;
            case "lock_pc": step.CantUndo = s.Say("locking can't be undone", "vergrendelen kan niet terug"); break;
        }
        return step;
    }

    /// <summary>After [step]'s command ran: what only shows afterwards (the program it opened, the message it sent).</summary>
    public static void After(Settings s, Step step, JsonObject args, Commands.Outcome outcome)
    {
        if (step.Name == "open_app" && outcome.Text.StartsWith("🚀 "))
        {
            var app = outcome.Text[2..].Trim();
            step.Back = () => Task.FromResult<string?>(PcActions.CloseApp(app).Count > 0 ? s.Say($"I closed {app} again", $"ik heb {app} weer gesloten") : null);
        }
        if (step.Name == "send_message" && Social.Find(s, args["to"]?.ToString() ?? "") is { } friend && Social.LastSentId(friend.Code) is { } id)
            step.Back = async () => await Social.UnsendAsync(s, friend.Code, id) == null ? s.Say($"I deleted the message to {friend.Name}", $"ik heb het bericht aan {friend.Name} verwijderd") : null;
    }

    /// <summary>What Dave just did (one or more commands): what "undo" takes back.</summary>
    public static void Remember(List<Step> steps)
    {
        if (steps.Count == 0) return;
        last = steps;
        lastAt = DateTime.Now;
    }

    /// <summary>Settings were put back: hotkeys, theme… need applying.</summary>
    public static event Action? SettingsUndone;

    /// <summary>"Undo that": takes back the last thing(s). Returns what to say.</summary>
    public static async Task<string> RunAsync(Settings s)
    {
        if (last.Count == 0 || DateTime.Now - lastAt > TimeSpan.FromMinutes(30))
            return s.Say("There's nothing I can undo right now.", "Er is nu niets wat ik terug kan draaien.");
        var done = new List<string>();
        var cant = new List<string>();
        foreach (var step in Enumerable.Reverse(last))
        {
            if (step.Back != null)
            {
                try { if (await step.Back() is { } what) done.Add(what); }
                catch (Exception e) { Log.Write($"Undo {step.Name} failed: {e.Message}"); }
            }
            else if (step.CantUndo != null) cant.Add(step.CantUndo);
        }
        last = new();
        if (done.Count == 0 && cant.Count == 0) return s.Say("That was nothing I can take back.", "Dat kan ik niet terugdraaien.");
        var said = done.Count > 0 ? s.Say("Undone: ", "Teruggedraaid: ") + string.Join(", ", done.Distinct()) + "." : "";
        if (cant.Count > 0) said += (said.Length > 0 ? " " : "") + s.Say("But ", "Maar ") + string.Join(", ", cant.Distinct()) + ".";
        return said;
    }

    /// <summary>The programs with a window open, by their file (to reopen ones that got closed).</summary>
    private static HashSet<string> OpenPrograms()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (handle, _, _) in WindowList.List())
        {
            try
            {
                GetWindowThreadProcessId(handle, out var pid);
                using var p = Process.GetProcessById((int)pid);
                if (p.MainModule?.FileName is { } file && !file.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) paths.Add(file);
            }
            catch { /* can't read it (e.g. running as administrator) */ }
        }
        return paths;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
