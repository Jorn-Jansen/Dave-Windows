using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DaveWindows;

/// <summary>
/// Screen time: which app you're using, counted every few seconds while you're actually at the PC (or in a
/// full-screen game or video). "How long did I play Roblox today?". Kept for 60 days in screentime.json, only on this PC.
/// </summary>
public static class ScreenTime
{
    private const int StepSeconds = 5;
    private static readonly string FilePath = Path.Combine(Settings.Folder, "screentime.json");
    private static readonly object Sync = new();
    private static Dictionary<string, Dictionary<string, int>>? days; // "yyyy-MM-dd" -> app -> seconds
    private static readonly Dictionary<string, string> Names = new(); // process -> friendly name
    private static DateTime lastSave = DateTime.Now;

    public static void Start() => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(StepSeconds)).ConfigureAwait(false);
            try { Tick(); }
            catch (Exception e) { Log.Write($"Screen time: {e.Message}"); }
        }
    });

    /// <summary>How long since the mouse or keyboard was last used (for heads-ups on the phone only when you're away).</summary>
    public static TimeSpan Idle
    {
        get
        {
            var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(Environment.TickCount - (int)info.Time) : TimeSpan.Zero;
        }
    }

    /// <summary>Which app was in front from when to when, newest last (only while Dave runs): for "where did I leave off?".</summary>
    private static readonly List<(string app, DateTime from, DateTime to)> Sessions = new();

    /// <summary>The apps used in the last [within], newest first, each with from–to.</summary>
    public static List<(string app, DateTime from, DateTime to)> RecentSessions(TimeSpan within)
    {
        lock (Sync) return Sessions.Where(s => DateTime.Now - s.to <= within).Reverse().ToList();
    }

    private static void Tick()
    {
        var app = CurrentApp();
        lock (Sync)
        {
            if (app != null)
            {
                var now = DateTime.Now;
                if (Sessions.Count > 0 && Sessions[^1].app == app && now - Sessions[^1].to < TimeSpan.FromSeconds(StepSeconds * 4))
                    Sessions[^1] = (app, Sessions[^1].from, now);
                else Sessions.Add((app, now, now));
                if (Sessions.Count > 200) Sessions.RemoveAt(0);
            }
            var all = Load();
            if (app != null)
            {
                var day = all.TryGetValue(Today, out var d) ? d : all[Today] = new();
                day[app] = day.GetValueOrDefault(app) + StepSeconds;
            }
            if (DateTime.Now - lastSave > TimeSpan.FromMinutes(1)) Save(all);
        }
    }

    private static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    /// <summary>The app you're using right now, or null when you're away (no input for 5 minutes, unless it's full screen) or the PC is locked.</summary>
    private static string? CurrentApp()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        var idle = GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(Environment.TickCount - (int)info.Time) : TimeSpan.Zero;
        if (idle > TimeSpan.FromMinutes(5) && !IsFullScreen(window)) return null; // away, unless watching or gaming full screen

        GetWindowThreadProcessId(window, out var pid);
        if (pid == Environment.ProcessId) return null;
        string process;
        try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
        catch { return null; }
        if (process is "LockApp" or "ShellExperienceHost" or "SearchHost" or "StartMenuExperienceHost") return null;
        if (process == "ApplicationFrameHost") return Title(window).Split(" - ").Last().Trim() is { Length: > 0 } store ? store : null; // Store apps
        if (!Names.TryGetValue(process, out var name))
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                var version = p.MainModule?.FileVersionInfo;
                name = new[] { version?.FileDescription, version?.ProductName }
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && n.Length < 40 && !n.Contains("Microsoft® Windows®")) ?? process;
            }
            catch { name = process; }
            if (process.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase)) name = "Roblox";
            Names[process] = name = name.Trim();
        }
        return name;
    }

    /// <summary>For the AI: time per app per day from [from] to [to], plus the total for the whole period.</summary>
    public static string Report(DateTime from, DateTime to)
    {
        lock (Sync)
        {
            var all = Load();
            var text = new StringBuilder($"Screen time measured since {(all.Count > 0 ? all.Keys.Min() : Today)} (only while Dave runs).\n");
            var total = new Dictionary<string, int>();
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
            {
                if (!all.TryGetValue(day.ToString("yyyy-MM-dd"), out var apps) || apps.Count == 0) continue;
                text.AppendLine($"{day:dddd d MMMM}: {Hours(apps.Values.Sum())} at the PC; " +
                                string.Join(", ", apps.OrderByDescending(a => a.Value).Take(8).Select(a => $"{a.Key} {Hours(a.Value)}")));
                foreach (var (app, seconds) in apps) total[app] = total.GetValueOrDefault(app) + seconds;
            }
            if (total.Count == 0) return text + "No screen time recorded in this period.";
            if (to.Date > from.Date)
                text.AppendLine($"Whole period: {Hours(total.Values.Sum())}; " +
                                string.Join(", ", total.OrderByDescending(a => a.Value).Take(10).Select(a => $"{a.Key} {Hours(a.Value)}")));
            return text.ToString();
        }
    }

    /// <summary>Seconds per app from [from] to [to], most used first.</summary>
    public static List<(string app, int seconds)> AppTotals(DateTime from, DateTime to)
    {
        lock (Sync)
        {
            var all = Load();
            var total = new Dictionary<string, int>();
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
                if (all.TryGetValue(day.ToString("yyyy-MM-dd"), out var apps))
                    foreach (var (app, seconds) in apps) total[app] = total.GetValueOrDefault(app) + seconds;
            return total.OrderByDescending(a => a.Value).Select(a => (a.Key, a.Value)).ToList();
        }
    }

    /// <summary>Total seconds at the PC for each day from [from] to [to].</summary>
    public static List<(DateTime day, int seconds)> DayTotals(DateTime from, DateTime to)
    {
        lock (Sync)
        {
            var all = Load();
            var result = new List<(DateTime, int)>();
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
                result.Add((day, all.TryGetValue(day.ToString("yyyy-MM-dd"), out var apps) ? apps.Values.Sum() : 0));
            return result;
        }
    }

    private static string Hours(int seconds) => seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60}m" : $"{Math.Max(1, seconds / 60)}m";

    private static Dictionary<string, Dictionary<string, int>> Load()
    {
        if (days != null) return days;
        try { days = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) { Log.Write($"Screen time unreadable: {e.Message}"); days = new(); }
        return days;
    }

    private static void Save(Dictionary<string, Dictionary<string, int>> all)
    {
        lastSave = DateTime.Now;
        foreach (var old in all.Keys.Where(k => string.CompareOrdinal(k, DateTime.Today.AddDays(-60).ToString("yyyy-MM-dd")) < 0).ToList()) all.Remove(old);
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(all)); }
        catch (Exception e) { Log.Write($"Screen time not saved: {e.Message}"); }
    }

    /// <summary>Save now (when Dave closes), so the last minute isn't lost.</summary>
    public static void Flush()
    {
        lock (Sync) if (days != null) Save(days);
    }

    private static bool IsFullScreen(IntPtr window)
    {
        if (!GetWindowRect(window, out var r)) return false;
        var screen = Screen.FromHandle(window).Bounds;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }

    private static string Title(IntPtr window)
    {
        var text = new StringBuilder(256);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
}
