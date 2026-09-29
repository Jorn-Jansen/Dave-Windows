using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Windows.Devices.Geolocation;

namespace DaveWindows;

/// <summary>Things only the PC version can do: open programs and websites, lock the PC.</summary>
public static class PcActions
{
    /// <summary>
    /// Finds a program by (part of) its name and opens it: first the full app list (includes Store apps like
    /// Notepad, Calculator and Spotify), then Start menu and desktop shortcuts, then a plain command like "notepad".
    /// </summary>
    public static string? OpenApp(string name)
    {
        var wanted = Normalize(name);
        var app = StartApps()
            .Select(a => (a.name, a.id, score: Score(Normalize(a.name), wanted)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .FirstOrDefault();
        if (app.id != null && app.score >= 2)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.id}") { UseShellExecute = true });
            return app.name;
        }
        return OpenShortcut(wanted) ?? (app.id != null ? OpenStartApp(app.name, app.id) : null) ?? RunCommand(name);
    }

    private static string OpenStartApp(string name, string id)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{id}") { UseShellExecute = true });
        return name;
    }

    /// <summary>Last resort: something on the PATH, like "notepad" or "calc".</summary>
    private static string? RunCommand(string name)
    {
        var command = name.Trim().Split(' ')[0];
        if (command.Length == 0 || command.Any(c => "\\/:*?\"<>|".Contains(c))) return null;
        try
        {
            Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });
            return command;
        }
        catch { return null; }
    }

    private static List<(string name, string id)>? startApps;
    private static DateTime startAppsAt;

    /// <summary>Everything in the Start menu's app list, as (name, app id). Cached for 10 minutes.</summary>
    private static List<(string name, string id)> StartApps()
    {
        if (startApps != null && DateTime.Now - startAppsAt < TimeSpan.FromMinutes(10)) return startApps;
        var list = new List<(string, string)>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic item in shell.NameSpace("shell:AppsFolder").Items())
            {
                string itemName = item.Name, itemId = item.Path;
                if (!string.IsNullOrEmpty(itemName) && !string.IsNullOrEmpty(itemId)) list.Add((itemName, itemId));
            }
        }
        catch (Exception e) { Log.Write($"App list unavailable: {e.Message}"); }
        startApps = list;
        startAppsAt = DateTime.Now;
        return list;
    }

    private static string? OpenShortcut(string wanted)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var shortcuts = folders.Where(Directory.Exists)
            .SelectMany(f => SafeFiles(f))
            .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileNameWithoutExtension(f).Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var best = shortcuts
            .Select(f => (file: f, score: Score(Normalize(Path.GetFileNameWithoutExtension(f)), wanted)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .FirstOrDefault();
        if (best.file == null) return null;

        Process.Start(new ProcessStartInfo(best.file) { UseShellExecute = true });
        return Path.GetFileNameWithoutExtension(best.file);
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Exact match beats "starts with" beats "contains".</summary>
    private static int Score(string candidate, string wanted) =>
        candidate == wanted ? 3 : candidate.StartsWith(wanted) ? 2 : candidate.Contains(wanted) || (wanted.Length > 3 && wanted.Contains(candidate)) ? 1 : 0;

    /// <summary>Open a site or search, in [browser] if given (e.g. "Brave"), otherwise Dave's or Windows' default browser.</summary>
    public static string OpenWebsite(string? url, string? search, string? browser)
    {
        var target = !string.IsNullOrWhiteSpace(url)
            ? (url.StartsWith("http") ? url : "https://" + url)
            : "https://www.google.com/search?q=" + Uri.EscapeDataString(search ?? "");
        var exe = string.IsNullOrWhiteSpace(browser) ? null : BrowserPath(browser);
        Log.Write($"Website {target} in browser '{browser}' -> {exe ?? "Windows default"}");
        if (exe != null) Process.Start(new ProcessStartInfo(exe, $"\"{target}\"") { UseShellExecute = false });
        else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return target;
    }

    private static readonly Dictionary<string, string> BrowserExes = new()
    {
        ["brave"] = "brave.exe", ["chrome"] = "chrome.exe", ["google"] = "chrome.exe", ["edge"] = "msedge.exe",
        ["firefox"] = "firefox.exe", ["opera"] = "opera.exe", ["vivaldi"] = "vivaldi.exe",
    };

    /// <summary>Where a browser is installed, via the "App Paths" Windows keeps for installed programs.</summary>
    private static string? BrowserPath(string browser)
    {
        var key = BrowserExes.Keys.FirstOrDefault(k => browser.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (key == null) return null;
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using var appPath = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{BrowserExes[key]}");
            if (appPath?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        return null;
    }

    public static void LockPc() => LockWorkStation();

    // --- Closing programs, without bringing them to the front ---

    /// <summary>Never closed by "close everything": Windows itself, the audio setup, and Dave.</summary>
    private static readonly string[] Protected =
    {
        "dave", "explorer", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "textinputhost", "lockapp",
        "widgets", "systemsettingsbroker", "voicemeeter", "voicemeeterpro", "voicemeeter8", "steelseriesgg", "steelseriesggclient",
        "steelseriesengine", "steelseriessonar",
    };

    /// <summary>Close every window of the program(s) matching [name]. Returns the names of what was closed.</summary>
    public static List<string> CloseApp(string name)
    {
        var wanted = Normalize(name);
        if (wanted.Length == 0) return new List<string>();
        return CloseWindows(w => Matches(w, wanted));
    }

    /// <summary>Close all open programs except those matching one of [keep] (comma-separated names).</summary>
    public static List<string> CloseAllExcept(string keep)
    {
        var keepers = keep.Split(',', ';').Select(Normalize).Where(k => k.Length > 0).ToList();
        return CloseWindows(w => !Protected.Contains(w.process.ToLowerInvariant()) && !keepers.Any(k => Matches(w, k)));
    }

    private static bool Matches((IntPtr handle, string title, string process) window, string wanted)
    {
        var process = Normalize(window.process);
        var title = Normalize(window.title);
        var product = Normalize(ProductName(window.handle));
        return process.Contains(wanted) || title.Contains(wanted) || (product.Length > 0 && product.Contains(wanted))
               || (wanted.Length > 3 && process.Length > 3 && wanted.Contains(process));
    }

    /// <summary>The program's own name, e.g. "Microsoft Word" for WINWORD.EXE, so spoken names match.</summary>
    private static string ProductName(IntPtr window)
    {
        try
        {
            GetWindowThreadProcessId(window, out var pid);
            var module = Process.GetProcessById((int)pid).MainModule;
            return module?.FileVersionInfo.FileDescription ?? module?.FileVersionInfo.ProductName ?? "";
        }
        catch { return ""; } // e.g. programs running as administrator
    }

    private static List<string> CloseWindows(Func<(IntPtr handle, string title, string process), bool> shouldClose)
    {
        var closed = new List<string>();
        foreach (var window in WindowList.List().Where(shouldClose))
        {
            PostMessage(window.handle, 0x0010 /* WM_CLOSE: same as clicking the X */, IntPtr.Zero, IntPtr.Zero);
            // Store apps all run inside "ApplicationFrameHost"; their window title is the readable name.
            var label = window.process.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ? window.title : window.process;
            if (!closed.Contains(label)) closed.Add(label);
        }
        Log.Write($"Closed: {string.Join(", ", closed)}");
        return closed;
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();
}

/// <summary>Timers and reminders, spoken out loud when due. Kept in settings, so they survive a restart.</summary>
public static class Reminders
{
    public static DateTimeOffset? Schedule(Settings s, double? minutes, string? time, string message)
    {
        DateTimeOffset? at = null;
        if (minutes is > 0) at = DateTimeOffset.Now.AddSeconds(Math.Round(minutes.Value * 60));
        else if (!string.IsNullOrWhiteSpace(time) && TimeSpan.TryParse(time.Trim(), out var clock))
        {
            var today = new DateTimeOffset(DateTime.Today.Add(clock));
            at = today < DateTimeOffset.Now ? today.AddDays(1) : today;
        }
        if (at == null) return null;
        s.Reminders.Add(new Settings.Reminder { Id = DateTime.Now.Ticks, At = at.Value, Message = message });
        s.Save();
        return at;
    }

    public static int CancelAll(Settings s)
    {
        var count = s.Reminders.Count;
        s.Reminders.Clear();
        s.Save();
        return count;
    }

    /// <summary>Reminders that are due now (removed from the list). Ones more than an hour late are dropped.</summary>
    public static List<Settings.Reminder> TakeDue(Settings s)
    {
        var now = DateTimeOffset.Now;
        var due = s.Reminders.Where(r => r.At <= now).ToList();
        if (due.Count == 0) return due;
        s.Reminders.RemoveAll(r => r.At <= now);
        s.Save();
        return due.Where(r => now - r.At < TimeSpan.FromHours(1)).ToList();
    }

    /// <summary>For the AI: each reminder with its exact time and how long until then, so it doesn't have to work it out.</summary>
    public static string? Describe(Settings s)
    {
        if (s.Reminders.Count == 0) return null;
        var now = DateTimeOffset.Now;
        return string.Join("; ", s.Reminders.OrderBy(r => r.At).Select(r =>
            $"'{r.Message}' at {r.At:HH:mm:ss}, which is {Duration(r.At - now, dutch: false)} from now"));
    }

    /// <summary>A length of time as you'd say it: "2 minutes and 30 seconds", "1 uur en 5 minuten".</summary>
    public static string Duration(TimeSpan span, bool dutch)
    {
        var total = Math.Max(0, (int)Math.Round(span.TotalSeconds));
        int hours = total / 3600, minutes = total % 3600 / 60, seconds = total % 60;
        var parts = new List<string>();
        if (hours > 0) parts.Add(dutch ? $"{hours} uur" : $"{hours} hour{(hours == 1 ? "" : "s")}");
        if (minutes > 0) parts.Add(dutch ? $"{minutes} {(minutes == 1 ? "minuut" : "minuten")}" : $"{minutes} minute{(minutes == 1 ? "" : "s")}");
        if (seconds > 0 && hours == 0) parts.Add(dutch ? $"{seconds} seconden" : $"{seconds} second{(seconds == 1 ? "" : "s")}");
        if (parts.Count == 0) return dutch ? "0 seconden" : "0 seconds";
        return parts.Count == 1 ? parts[0] : string.Join(", ", parts.SkipLast(1)) + (dutch ? " en " : " and ") + parts[^1];
    }
}

/// <summary>Where the PC is, via Windows location (Wi-Fi based). Optional: if it's off, Dave just uses the country.</summary>
public static class PcLocation
{
    private static (string text, DateTime at)? cached;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    static PcLocation() => Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DaveWindows", "1.0"));

    public static async Task<string?> DescribeAsync()
    {
        if (cached is { } c && DateTime.Now - c.at < TimeSpan.FromMinutes(10)) return c.text;
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return null;
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(3));
            var point = position.Coordinate.Point.Position;
            var coords = FormattableString.Invariant($"{point.Latitude:F5}, {point.Longitude:F5}");

            string? address = null;
            try
            {
                var json = JsonNode.Parse(await Http.GetStringAsync(FormattableString.Invariant(
                    $"https://nominatim.openstreetmap.org/reverse?format=json&zoom=16&lat={point.Latitude}&lon={point.Longitude}")));
                address = json?["display_name"]?.GetValue<string>();
            }
            catch { /* address is a nice-to-have */ }

            var text = $"The user is at {(address != null ? $"{address} ({coords})" : coords)}.";
            cached = (text, DateTime.Now);
            return text;
        }
        catch (Exception e)
        {
            Log.Write($"Location unavailable: {e.Message}");
            return null;
        }
    }
}
