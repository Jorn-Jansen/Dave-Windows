using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>
/// "Tell me when…": things Dave keeps an eye on in the background (a program closing or starting, a download finishing,
/// the graphics card or processor getting hot or busy, the battery running low) and speaks up about.
/// Checked every few seconds; forgotten after 12 hours or when Dave closes.
/// </summary>
public static class Watchers
{
    public record Watch(long Id, string Kind, string Target, double Threshold, string Message, DateTime Created)
    {
        public string Describe() => Kind switch
        {
            "program_closes" => $"when {Target} closes",
            "program_starts" => $"when {Target} starts",
            "download_done" => Target == "steam" ? "when the Steam download is done" : Target == "browser" ? "when the download in Downloads is done" : "when your download is done",
            "gpu_temp_above" => $"when the graphics card is above {Threshold:0} °C",
            "gpu_use_above" => $"when the graphics card is over {Threshold:0}% busy",
            "cpu_use_above" => $"when the processor is over {Threshold:0}% busy",
            "ram_use_above" => $"when memory use is over {Threshold:0}%",
            "battery_below" => $"when the battery is under {Threshold:0}%",
            _ => Kind,
        };
    }

    private static readonly List<Watch> Active = new();
    private static readonly object Sync = new();
    private static readonly string[] PartialDownloads = { ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp" };
    private static readonly Dictionary<long, int> Streak = new(); // how many checks in a row a condition held (avoids one-off spikes)
    private static CancellationTokenSource? loop;

    /// <summary>Raised (on a background thread) with the message to say when something you're waiting for happens.</summary>
    public static event Action<string>? Triggered;

    /// <summary>What Dave is keeping an eye on, one line each.</summary>
    public static List<string> List()
    {
        lock (Sync) return Active.Select(w => $"{w.Describe()} (set {w.Created:HH:mm})").ToList();
    }

    public static string? DescribeAll()
    {
        lock (Sync) return Active.Count == 0 ? null : string.Join("; ", Active.Select(w => $"{w.Describe()} (set {w.Created:HH:mm})"));
    }

    /// <summary>Start watching. Returns what Dave should say: "okay" or why it's not needed/possible.</summary>
    public static string Add(Settings s, string kind, string target, double threshold, string message)
    {
        target = target.Trim();
        if (message.Trim().Length == 0) message = s.Say("Here's your heads-up.", "Even een seintje.");
        var watch = new Watch(DateTime.Now.Ticks, kind, target, threshold, message.Trim(), DateTime.Now);

        // Already the case? Then say so straight away instead of waiting.
        switch (kind)
        {
            case "program_closes" or "program_starts" when target.Length == 0:
                return s.Say("Which program should I keep an eye on?", "Op welk programma moet ik letten?");
            case "program_closes" when !IsRunning(target):
                return s.Say($"{target} isn't open right now.", $"{target} staat nu niet open.");
            case "program_starts" when IsRunning(target):
                return s.Say($"{target} is already open.", $"{target} staat al open.");
            case "download_done":
                target = target.ToLowerInvariant().Contains("steam") ? "steam" : target.ToLowerInvariant() is "browser" or "chrome" or "edge" or "brave" or "firefox" ? "browser" : "";
                var browser = target != "steam" && PartialFiles().Count > 0;
                var steam = target != "browser" && SteamDownloads().Count > 0;
                if (!browser && !steam)
                    return target == "steam" ? s.Say("I don't see a Steam download going on.", "Ik zie geen Steam-download bezig.")
                         : target == "browser" ? s.Say("I don't see a download in progress in your Downloads folder.", "Ik zie geen download bezig in je Downloads-map.")
                         : s.Say("I don't see a download going on (in your Downloads folder or Steam).", "Ik zie geen download bezig (in je Downloads-map of Steam).");
                if (target.Length == 0) target = browser && steam ? "" : browser ? "browser" : "steam"; // just the one that's going on
                watch = watch with { Target = target };
                break;
            case "gpu_temp_above" or "gpu_use_above" when Gpu() == null:
                return s.Say("I can only read NVIDIA graphics cards, and I can't find one.", "Ik kan alleen NVIDIA-videokaarten uitlezen, en die vind ik niet.");
            case "battery_below" when SystemInformation.PowerStatus.BatteryChargeStatus == BatteryChargeStatus.NoSystemBattery:
                return s.Say("This PC doesn't have a battery.", "Deze pc heeft geen batterij.");
            case "gpu_temp_above" or "gpu_use_above" or "cpu_use_above" or "ram_use_above" or "battery_below" when threshold <= 0:
                return s.Say("I didn't get the number to watch for.", "Ik snapte niet bij welk getal ik moet waarschuwen.");
        }
        if (kind is not ("program_closes" or "program_starts" or "download_done" or "gpu_temp_above" or "gpu_use_above"
            or "cpu_use_above" or "ram_use_above" or "battery_below"))
            return s.Say("I can't keep an eye on that.", "Daar kan ik niet op letten.");

        lock (Sync)
        {
            Active.Add(watch);
            if (loop == null) { loop = new CancellationTokenSource(); _ = Task.Run(() => RunAsync(loop.Token)); }
        }
        Log.Write($"Watching: {watch.Describe()} -> \"{watch.Message}\"");
        return s.Say($"Okay, I'll tell you {watch.Describe()}.", $"Oké, ik laat het je weten {DutchDescribe(watch)}.");
    }

    public static int CancelAll()
    {
        lock (Sync)
        {
            var count = Active.Count;
            Active.Clear();
            return count;
        }
    }

    private static async Task RunAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancel).ConfigureAwait(false);
            List<Watch> current;
            lock (Sync)
            {
                Active.RemoveAll(w => DateTime.Now - w.Created > TimeSpan.FromHours(12));
                if (Active.Count == 0) { loop = null; return; }
                current = Active.ToList();
            }
            (double temp, double use)? gpu = current.Any(w => w.Kind.StartsWith("gpu")) ? Gpu() : null;
            double cpu = current.Any(w => w.Kind == "cpu_use_above") ? CpuUse() : 0;
            foreach (var w in current)
            {
                bool now;
                try
                {
                    now = w.Kind switch
                    {
                        "program_closes" => !IsRunning(w.Target),
                        "program_starts" => IsRunning(w.Target),
                        "download_done" => (w.Target == "steam" || PartialFiles().Count == 0) && (w.Target == "browser" || SteamDownloads().Count == 0),
                        "gpu_temp_above" => gpu?.temp > w.Threshold,
                        "gpu_use_above" => gpu?.use > w.Threshold,
                        "cpu_use_above" => cpu > w.Threshold,
                        "ram_use_above" => RamUse() > w.Threshold,
                        "battery_below" => SystemInformation.PowerStatus.BatteryLifePercent * 100 < w.Threshold,
                        _ => false,
                    };
                }
                catch (Exception e) { Log.Write($"Watch check failed: {e.Message}"); continue; }

                // Needs to hold for two checks in a row (8 seconds): a program restarting or a short spike doesn't count.
                var streak = now ? Streak.GetValueOrDefault(w.Id) + 1 : 0;
                Streak[w.Id] = streak;
                if (streak < 2) continue;
                lock (Sync) Active.Remove(w);
                Streak.Remove(w.Id);
                Log.Write($"Watch triggered: {w.Describe()}");
                Triggered?.Invoke(w.Kind == "download_done" && Finished(w) is { } what ? $"{w.Message} ({what})" : w.Message);
            }
        }
    }

    // --- What's going on right now ---

    private static string Simple(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

    /// <summary>
    /// Does a program like "Roblox" (RobloxPlayerBeta.exe) or "Discord" have a window open? Its window, not just
    /// its process: Discord and SteelSeries GG keep running in the tray after you close them, and Roblox leaves a
    /// crash handler running, so "closed" is when its last window is gone.
    /// </summary>
    private static bool IsRunning(string name)
    {
        var wanted = Simple(name);
        if (wanted.Length == 0) return false;
        foreach (var (handle, title, process) in WindowList.List())
        {
            if (title == "Program Manager" || WindowControl.IsCloaked(handle)) continue; // the desktop, or a hidden Store app window
            var p = Simple(process);
            if (p.Contains(wanted) || (p.Length > 3 && wanted.Contains(p))) return true;
            if (process == "ApplicationFrameHost" && Simple(title).Contains(wanted)) return true; // Store apps (Calculator, Settings…)
            if (ProductName(handle) is { } product && Simple(product).Contains(wanted)) return true; // "Microsoft Word" for WINWORD
        }
        return false;
    }

    private static readonly Dictionary<IntPtr, string?> Products = new();

    private static string? ProductName(IntPtr window)
    {
        if (Products.TryGetValue(window, out var cached)) return cached;
        string? name = null;
        try
        {
            GetWindowThreadProcessId(window, out var pid);
            using var p = Process.GetProcessById((int)pid);
            name = p.MainModule?.FileVersionInfo.ProductName;
        }
        catch { /* some processes can't be read */ }
        if (Products.Count > 500) Products.Clear();
        return Products[window] = name;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// <summary>Steam games downloading now: the folders in each library's steamapps\downloading (gone when it's done).</summary>
    private static List<string> SteamDownloads()
    {
        var games = new List<string>();
        foreach (var library in SteamLibraries())
        {
            var downloading = Path.Combine(library, "steamapps", "downloading");
            if (!Directory.Exists(downloading)) continue;
            foreach (var dir in Directory.EnumerateDirectories(downloading))
                if (Directory.EnumerateFileSystemEntries(dir).Any()) games.Add(Path.GetFileName(dir)); // the game's app id
        }
        return games;
    }

    /// <summary>Where Steam keeps games: its own folder and the libraries in libraryfolders.vdf.</summary>
    private static List<string> SteamLibraries()
    {
        var libraries = new List<string>();
        try
        {
            if (Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is not string steam) return libraries;
            steam = steam.Replace('/', '\\');
            libraries.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        catch (Exception e) { Log.Write($"Couldn't find Steam's libraries: {e.Message}"); }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>What finished, for the message: the newest file in Downloads since the watch began, or the Steam game's name.</summary>
    private static string? Finished(Watch w)
    {
        try
        {
            if (w.Target != "steam")
            {
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var file = new DirectoryInfo(downloads).EnumerateFiles()
                    .Where(f => f.LastWriteTime >= w.Created.AddMinutes(-1) && !PartialDownloads.Contains(f.Extension.ToLowerInvariant()))
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (file != null) return file.Name;
            }
            foreach (var library in SteamLibraries())
            {
                var manifests = new DirectoryInfo(Path.Combine(library, "steamapps")).EnumerateFiles("appmanifest_*.acf")
                    .Where(f => f.LastWriteTime >= w.Created).OrderByDescending(f => f.LastWriteTime);
                foreach (var manifest in manifests)
                    if (Regex.Match(File.ReadAllText(manifest.FullName), "\"name\"\\s+\"([^\"]+)\"") is { Success: true } name) return name.Groups[1].Value;
            }
        }
        catch (Exception e) { Log.Write($"Couldn't tell what finished downloading: {e.Message}"); }
        return null;
    }

    private static List<string> PartialFiles()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) return new List<string>();
        return Directory.EnumerateFiles(downloads)
            .Where(f => PartialDownloads.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => DateTime.Now - File.GetLastWriteTime(f) < TimeSpan.FromMinutes(10)) // old leftovers of a failed download don't count
            .ToList();
    }

    private static (double temp, double use)? Gpu()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var smi = Process.Start(info);
            var f = smi?.StandardOutput.ReadLine()?.Split(',');
            smi?.WaitForExit(3000);
            if (f == null || f.Length < 2) return null;
            return (double.Parse(f[0].Trim(), CultureInfo.InvariantCulture), double.Parse(f[1].Trim(), CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }

    private static double CpuUse()
    {
        GetSystemTimes(out var idle1, out var kernel1, out var user1);
        Thread.Sleep(500);
        GetSystemTimes(out var idle2, out var kernel2, out var user2);
        double total = (kernel2 - kernel1) + (user2 - user1);
        return total > 0 ? (total - (idle2 - idle1)) / total * 100 : 0;
    }

    private static double RamUse()
    {
        var memory = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref memory) ? memory.MemoryLoad : 0;
    }

    private static string DutchDescribe(Watch w) => w.Kind switch
    {
        "program_closes" => $"als {w.Target} sluit",
        "program_starts" => $"als {w.Target} opstart",
        "download_done" => w.Target == "steam" ? "als de Steam-download klaar is" : "als de download klaar is",
        "gpu_temp_above" => $"als je videokaart boven de {w.Threshold:0} graden komt",
        "gpu_use_above" => $"als je videokaart boven de {w.Threshold:0} procent komt",
        "cpu_use_above" => $"als je processor boven de {w.Threshold:0} procent komt",
        "ram_use_above" => $"als je geheugen boven de {w.Threshold:0} procent komt",
        "battery_below" => $"als je batterij onder de {w.Threshold:0} procent komt",
        _ => w.Kind,
    };

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
