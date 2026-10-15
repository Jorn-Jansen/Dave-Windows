namespace DaveWindows;

/// <summary>
/// The stopwatch: "start a stopwatch", "lap", "how long has it been?", "pause", "carry on", "stop". One at a time; kept in
/// the settings, so it keeps counting when Dave restarts. Shown as a card with the timers (see <see cref="TimerOverlay"/>).
/// </summary>
public static class StopwatchClock
{
    public static TimeSpan Elapsed(Settings s)
    {
        var w = s.Stopwatch;
        var running = w.RunningSince is { } since ? DateTimeOffset.Now - since : TimeSpan.Zero;
        return TimeSpan.FromSeconds(w.Banked) + (running > TimeSpan.Zero ? running : TimeSpan.Zero);
    }

    public static bool Running(Settings s) => s.Stopwatch.On && s.Stopwatch.RunningSince != null;

    /// <summary>Does what was asked; returns what to say.</summary>
    public static string Run(Settings s, string action)
    {
        var w = s.Stopwatch;
        var now = DateTimeOffset.Now;
        string said;
        switch (action)
        {
            case "start" when Running(s):
                return s.Say($"It's already running: {Spoken(s, Elapsed(s))}.", $"Hij loopt al: {Spoken(s, Elapsed(s))}.");
            case "start":
            case "reset":
                bool keepGoing = action == "start" || Running(s) || !w.On;
                s.Stopwatch = new Settings.StopwatchState { On = true, RunningSince = keepGoing ? now : null };
                said = action == "start" ? s.Say("⏱ Stopwatch started.", "⏱ Stopwatch gestart.") : s.Say("⏱ Back to zero.", "⏱ Terug naar nul.");
                break;
            case "lap":
                if (!w.On) return NotOn(s);
                var total = Elapsed(s);
                var lap = total - TimeSpan.FromSeconds(w.Laps.LastOrDefault());
                w.Laps.Add(total.TotalSeconds);
                said = s.Say($"Lap {w.Laps.Count}: {Short(lap)}", $"Ronde {w.Laps.Count}: {Short(lap)}") + (w.Laps.Count > 1 ? s.Say($" (total {Short(total)})", $" (totaal {Short(total)})") : "");
                break;
            case "pause":
                if (!w.On) return NotOn(s);
                if (!Running(s)) return s.Say($"It's already paused at {Short(Elapsed(s))}.", $"Hij staat al op pauze op {Short(Elapsed(s))}.");
                w.Banked = Elapsed(s).TotalSeconds;
                w.RunningSince = null;
                said = s.Say($"⏸ Paused at {Short(Elapsed(s))}.", $"⏸ Gepauzeerd op {Short(Elapsed(s))}.");
                break;
            case "resume":
                if (!w.On) return NotOn(s);
                if (Running(s)) return s.Say("It's still running.", "Hij loopt nog.");
                w.RunningSince = now;
                said = s.Say($"▶ Going again from {Short(Elapsed(s))}.", $"▶ Hij loopt weer, vanaf {Short(Elapsed(s))}.");
                break;
            case "stop":
                if (!w.On) return NotOn(s);
                var end = Elapsed(s);
                said = s.Say($"⏹ Stopped at {Short(end)}", $"⏹ Gestopt op {Short(end)}") + Laps(s, w.Laps) + ".";
                w.Banked = end.TotalSeconds;
                w.RunningSince = null;
                w.On = false;
                break;
            default: // "check": how long it's been
                if (!w.On) return NotOn(s);
                said = s.Say($"⏱ {Spoken(s, Elapsed(s))}", $"⏱ {Spoken(s, Elapsed(s))}") + (Running(s) ? "" : s.Say(" (paused)", " (gepauzeerd)"))
                    + (w.Laps.Count > 0 ? s.Say($", {w.Laps.Count} lap{(w.Laps.Count == 1 ? "" : "s")}", $", {w.Laps.Count} ronde{(w.Laps.Count == 1 ? "" : "s")}") : "") + ".";
                break;
        }
        s.Save();
        return said;
    }

    private static string NotOn(Settings s) => s.Say("There's no stopwatch running. Say \"start a stopwatch\".", "Er loopt geen stopwatch. Zeg \"start een stopwatch\".");

    /// <summary>", laps: 0:41, 0:39, 0:44" (each lap's own time).</summary>
    private static string Laps(Settings s, List<double> laps)
    {
        if (laps.Count == 0) return "";
        var times = laps.Select((t, i) => Short(TimeSpan.FromSeconds(t - (i > 0 ? laps[i - 1] : 0)))).Take(10);
        return s.Say(", laps: ", ", rondes: ") + string.Join(", ", times) + (laps.Count > 10 ? "…" : "");
    }

    /// <summary>"0:41.3", "12:03.0", "1:05:12" (tenths under an hour).</summary>
    public static string Short(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    /// <summary>"1 minute and 23 seconds", for saying it out loud.</summary>
    private static string Spoken(Settings s, TimeSpan t)
    {
        var parts = new List<string>();
        void Part(int n, string one, string many, string een, string veel) { if (n > 0) parts.Add($"{n} {(n == 1 ? s.Say(one, een) : s.Say(many, veel))}"); }
        Part((int)t.TotalHours, "hour", "hours", "uur", "uur");
        Part(t.Minutes, "minute", "minutes", "minuut", "minuten");
        Part(t.Seconds, "second", "seconds", "seconde", "seconden");
        if (parts.Count == 0) return s.Say("less than a second", "minder dan een seconde");
        return parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + s.Say(" and ", " en ") + parts[^1];
    }
}
