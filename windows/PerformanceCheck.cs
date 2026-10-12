using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace DaveWindows;

/// <summary>
/// "Why is my game lagging?": measures the PC for two seconds while you play (the game in front, the processor, the
/// graphics card, memory, the disk, the ping and Wi-Fi, what runs in the background) and writes down the likely causes,
/// for the AI to explain. Frames per second can't be measured from outside the game, so it goes by what slows games down.
/// </summary>
public static class PerformanceCheck
{
    private const int MeasureMs = 2000;

    /// <summary>Programs that are part of Windows: still listed, but marked, so they're never offered to close.</summary>
    private static readonly HashSet<string> PartOfWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "fontdrvhost",
        "Memory Compression", "audiodg", "explorer", "sihost", "ctfmon", "conhost", "RuntimeBroker", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "TextInputHost", "dllhost", "taskhostw", "spoolsv", "WmiPrvSE", "Dave",
    };

    /// <summary>Background work that's known to slow games down, with what it is.</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TiWorker"] = "Windows Update installing", ["TrustedInstaller"] = "Windows Update installing", ["MoUsoCoreWorker"] = "Windows Update",
        ["wuauclt"] = "Windows Update", ["MsMpEng"] = "Windows Defender scanning", ["MpDefenderCoreService"] = "Windows Defender",
        ["SearchIndexer"] = "Windows search indexing files", ["OneDrive"] = "OneDrive syncing", ["steam"] = "Steam (may be downloading)",
        ["steamwebhelper"] = "Steam's browser", ["EpicGamesLauncher"] = "Epic Games launcher (may be downloading)",
        ["Battle.net"] = "Battle.net (may be downloading)", ["obs64"] = "OBS recording/streaming", ["chrome"] = "Chrome", ["msedge"] = "Edge",
        ["brave"] = "Brave", ["firefox"] = "Firefox", ["opera"] = "Opera", ["Discord"] = "Discord", ["Spotify"] = "Spotify",
        ["NVIDIA Share"] = "NVIDIA overlay/recording", ["nvcontainer"] = "NVIDIA services", ["WallpaperEngine"] = "Wallpaper Engine",
        ["wallpaper32"] = "Wallpaper Engine", ["wallpaper64"] = "Wallpaper Engine", ["Lossless Scaling"] = "Lossless Scaling",
    };

    public static string Collect()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; // "2.8", not "2,8"
        try { return Measure(); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static string Measure()
    {
        var report = new StringBuilder();
        var causes = new List<string>();

        // The game: whatever is in front
        var (gameId, gameName) = Foreground();
        report.AppendLine(gameName != null ? $"In front (probably the game): {gameName}." : "Nothing recognisable in front.");

        // Measure everything at the same time, for two seconds
        var pings = Task.Run(() => Ping("1.1.1.1", 10));
        var gateway = Gateway();
        var localPings = gateway != null ? Task.Run(() => Ping(gateway, 10)) : Task.FromResult<(double average, double worst, int lost)?>(null);
        var gpuBefore = Task.Run(Gpu);
        var cpuBefore = ProcessTimes();
        var ioBefore = IoBytes();
        var (received1, _) = NetworkBytes();
        GetSystemTimes(out var idle1, out var kernel1, out var user1);
        var watch = Stopwatch.StartNew();
        Thread.Sleep(MeasureMs);
        GetSystemTimes(out var idle2, out var kernel2, out var user2);
        var cpuAfter = ProcessTimes();
        var ioAfter = IoBytes();
        var (received2, _) = NetworkBytes();
        var seconds = watch.Elapsed.TotalSeconds;
        var gpuAfter = Gpu();

        // Processor
        double total = (kernel2 - kernel1) + (user2 - user1), cpu = total > 0 ? (total - (idle2 - idle1)) / total * 100 : 0;
        int threads = Environment.ProcessorCount;
        var usage = cpuAfter.Select(p => (p.Key, cores: (p.Value.time - cpuBefore.GetValueOrDefault(p.Key).time).TotalSeconds / seconds, p.Value.name))
            .Where(p => p.cores > 0).ToList();
        double gameCores = usage.Where(p => p.Key == gameId).Sum(p => p.cores);
        report.AppendLine($"Processor: {cpu:F0}% in use overall ({threads} threads).");
        if (gameName != null) report.AppendLine($"The game uses {gameCores:F1} of {threads} processor threads ({gameCores / threads * 100:F0}% of the processor).");
        var background = usage.Where(p => p.Key != gameId && p.name is not ("Idle" or "Dave")).GroupBy(p => p.name)
            .Select(g => (name: g.Key, percent: g.Sum(p => p.cores) / threads * 100)).Where(p => p.percent >= 2)
            .OrderByDescending(p => p.percent).Take(6).ToList();
        report.AppendLine("Busiest in the background (processor): " + Join(background.Select(p => $"{Describe(p.name)} {p.percent:F0}%")));
        if (cpu > 90) causes.Add($"The processor is maxed out ({cpu:F0}%)" + (background.Count > 0 ? $"; in the background: {Describe(background[0].name)} ({background[0].percent:F0}%)." : "."));
        else if (gameName != null && gameCores > 0.85 && gameCores < 1.6 && cpu < 70)
            causes.Add("The game seems held back by one processor core (it uses about one thread fully while the rest is free): " +
                       "lower graphics settings help little; closing background apps and a good cooling help more.");

        // Graphics card
        var gpu = gpuAfter ?? gpuBefore.Result;
        if (gpu is { } g)
        {
            double util = gpuBefore.Result is { } b ? (b.util + g.util) / 2 : g.util;
            report.AppendLine($"Graphics card: {g.name}, {util:F0}% in use, {g.temp:F0} degrees C, video memory {g.memUsed / 1024:F1} of {g.memTotal / 1024:F1} GB" +
                              (g.power > 0 ? $", {g.power:F0} of {g.powerLimit:F0} watts" : "") + (g.throttle.Length > 0 ? $", slowing down because of: {g.throttle}" : "") + ".");
            if (util >= 95) causes.Add($"The graphics card is working at its limit ({util:F0}%). That's normal if the game's frame rate isn't capped; if it stutters or the frames are low, lower the graphics quality or resolution.");
            if (g.temp >= 83 || g.throttle.Contains("heat")) causes.Add($"The graphics card is hot ({g.temp:F0} degrees C) and may slow itself down: clean the dust out, improve airflow.");
            if (g.memTotal > 0 && g.memUsed / g.memTotal > 0.92) causes.Add("The video memory is nearly full: lower the texture quality; close other games or browsers using the graphics card.");
        }
        else report.AppendLine("Graphics card: no details available (only NVIDIA cards can be read).");

        // Memory
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            report.AppendLine($"Memory (RAM): {memory.MemoryLoad}% in use, {memory.AvailPhys / 1e9:F1} GB free of {memory.TotalPhys / 1e9:F1} GB.");
            if (memory.MemoryLoad >= 90)
            {
                var hungriest = Process.GetProcesses().Where(p => p.Id != gameId)
                    .GroupBy(p => p.ProcessName)
                    .Select(x => (name: x.Key, gb: x.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } }) / 1e9))
                    .Where(x => !PartOfWindows.Contains(x.name)).OrderByDescending(x => x.gb).Take(3);
                causes.Add($"Memory is nearly full ({memory.MemoryLoad}%), so Windows swaps to the disk, which stutters. Most memory: " +
                           Join(hungriest.Select(x => $"{Describe(x.name)} {x.gb:F1} GB")));
            }
        }

        // Disk: what reads and writes a lot in the background
        var disk = ioAfter.Select(p => (p.Key, mb: (p.Value.bytes - ioBefore.GetValueOrDefault(p.Key).bytes) / 1e6 / seconds, p.Value.name))
            .Where(p => p.Key != gameId && p.mb >= 5).GroupBy(p => p.name).Select(x => (name: x.Key, mb: x.Sum(p => p.mb)))
            .OrderByDescending(x => x.mb).Take(4).ToList();
        report.AppendLine("Heavy disk use in the background: " + Join(disk.Select(d => $"{Describe(d.name)} {d.mb:F0} MB/s")));
        if (disk.Count > 0 && disk[0].mb >= 25) causes.Add($"{Describe(disk[0].name)} is using the disk heavily ({disk[0].mb:F0} MB/s), which can cause stutters and slow loading.");

        // Network: ping to the internet and to the router, and downloads running alongside
        double downMb = (received2 - received1) / 1e6 / seconds;
        var wifi = WifiSignal();
        report.AppendLine($"Connection: {(wifi != null ? $"Wi-Fi, signal {wifi}%" : "cable (or Wi-Fi signal unknown)")}; downloading {downMb:F1} MB/s right now.");
        if (pings.Result is { } net)
        {
            report.AppendLine($"Ping to the internet (1.1.1.1): average {net.average:F0} ms, worst {net.worst:F0} ms, {net.lost} of 10 lost.");
            var local = localPings.Result;
            if (local is { } l) report.AppendLine($"Ping to the router: average {l.average:F0} ms, worst {l.worst:F0} ms, {l.lost} of 10 lost.");
            bool shaky = net.lost > 0 || net.worst - net.average > 40 || net.average > 80;
            if (shaky)
            {
                var where = local is { } r && (r.lost > 0 || r.worst > 20) ? "between the PC and the router (Wi-Fi or cable): move closer to the router or use a cable"
                          : downMb > 2 ? $"probably because something is downloading ({downMb:F1} MB/s)"
                          : "outside the house (the internet connection or the game's server)";
                causes.Add($"The connection is shaky (ping {net.average:F0} ms, up to {net.worst:F0} ms, {net.lost} lost): {where}. That's lag, not low frames.");
            }
            else if (downMb > 5) causes.Add($"Something is downloading ({downMb:F1} MB/s); the ping is still fine, but it can make online games laggy.");
        }
        else report.AppendLine("Ping: couldn't reach the internet.");
        if (wifi is < 50) causes.Add($"The Wi-Fi signal is weak ({wifi}%): move closer to the router or use a cable.");

        // Power and how long the PC has been on
        var power = SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery && power.PowerLineStatus != PowerLineStatus.Online)
            causes.Add("The laptop is on battery, which usually makes it slower: plug it in.");
        var plan = PowerPlan();
        if (plan != null)
        {
            report.AppendLine($"Power plan: {plan}.");
            if (plan.Contains("saver", StringComparison.OrdinalIgnoreCase) || plan.Contains("besparing", StringComparison.OrdinalIgnoreCase))
                causes.Add($"Windows is set to save energy (\"{plan}\"), which slows the PC down: pick Balanced or High performance.");
        }
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        report.AppendLine($"PC on for {(int)up.TotalDays} days {up.Hours} hours.");
        if (up.TotalDays >= 5) causes.Add($"The PC hasn't restarted in {(int)up.TotalDays} days: a restart often makes it smoother.");

        report.AppendLine();
        report.AppendLine(causes.Count > 0
            ? "Likely causes (worked out from the numbers above):\n- " + string.Join("\n- ", causes)
            : "Likely causes: nothing stands out right now. If it only lags now and then, ask again while it's lagging.");
        report.AppendLine("Frames per second can't be measured from outside the game.");
        return report.ToString();
    }

    private static string Describe(string process) =>
        Known.TryGetValue(process, out var what) ? (what.Equals(process, StringComparison.OrdinalIgnoreCase) ? what : $"{what} ({process})") : PartOfWindows.Contains(process) ? $"{process} (part of Windows)" : process;

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count > 0 ? string.Join(", ", list) + "." : "nothing notable.";
    }

    private static (int id, string? name) Foreground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0) return (-1, null);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (p.ProcessName is "explorer" or "Dave") return ((int)pid, null);
            var name = p.ProcessName.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase) ? "Roblox" : p.ProcessName;
            return ((int)pid, name);
        }
        catch { return (-1, null); }
    }

    private static Dictionary<int, (TimeSpan time, string name)> ProcessTimes()
    {
        var times = new Dictionary<int, (TimeSpan, string)>();
        foreach (var p in Process.GetProcesses())
        {
            try { times[p.Id] = (p.TotalProcessorTime, p.ProcessName); }
            catch { /* system processes can't be read */ }
            finally { p.Dispose(); }
        }
        return times;
    }

    /// <summary>Bytes read and written so far, per process.</summary>
    private static Dictionary<int, (ulong bytes, string name)> IoBytes()
    {
        var io = new Dictionary<int, (ulong, string)>();
        foreach (var p in Process.GetProcesses())
        {
            var handle = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
            try
            {
                if (handle != IntPtr.Zero && GetProcessIoCounters(handle, out var c)) io[p.Id] = (c.ReadTransferCount + c.WriteTransferCount, p.ProcessName);
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
                p.Dispose();
            }
        }
        return io;
    }

    private static (long received, long sent) NetworkBytes()
    {
        long received = 0, sent = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try { var s = n.GetIPStatistics(); received += s.BytesReceived; sent += s.BytesSent; } catch { }
        }
        return (received, sent);
    }

    /// <summary>The router's address (the gateway of the connection that's up).</summary>
    private static string? Gateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any))
                ?.ToString();
        }
        catch { return null; }
    }

    /// <summary>[count] pings, 200 ms apart: the average and worst time of the ones that came back, and how many were lost.</summary>
    private static (double average, double worst, int lost)? Ping(string host, int count)
    {
        try
        {
            using var ping = new Ping();
            var times = new List<long>();
            int lost = 0;
            for (int i = 0; i < count; i++)
            {
                var reply = ping.Send(host, 1000);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime); else lost++;
                Thread.Sleep(200);
            }
            return times.Count == 0 ? null : (times.Average(), times.Max(), lost);
        }
        catch { return null; }
    }

    private record GpuReading(string name, double util, double temp, double memUsed, double memTotal, double power, double powerLimit, string throttle);

    private static GpuReading? Gpu()
    {
        var f = NvidiaSmi("--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw,power.limit --format=csv,noheader,nounits");
        if (f == null || f.Length < 5) return null;
        double Num(int i) => i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        // Why it's slowing down (older and newer drivers call these fields differently: try both)
        var reasons = NvidiaSmi("--query-gpu=clocks_event_reasons.hw_thermal_slowdown,clocks_event_reasons.sw_thermal_slowdown,clocks_event_reasons.sw_power_cap --format=csv,noheader")
                   ?? NvidiaSmi("--query-gpu=clocks_throttle_reasons.hw_thermal_slowdown,clocks_throttle_reasons.sw_thermal_slowdown,clocks_throttle_reasons.sw_power_cap --format=csv,noheader");
        var throttle = new List<string>();
        if (reasons is { Length: >= 3 })
        {
            if (reasons[0] == "Active" || reasons[1] == "Active") throttle.Add("heat");
            if (reasons[2] == "Active") throttle.Add("its power limit");
        }
        return new GpuReading(f[0], Num(1), Num(2), Num(3), Num(4), Num(5), Num(6), string.Join(" and ", throttle));
    }

    private static string[]? NvidiaSmi(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var smi = Process.Start(info);
            if (smi == null) return null;
            var line = smi.StandardOutput.ReadLine();
            smi.WaitForExit(3000);
            if (smi.ExitCode != 0 || line == null) return null;
            return line.Split(',').Select(x => x.Trim()).ToArray();
        }
        catch { return null; } // no NVIDIA card or driver
    }

    /// <summary>The Wi-Fi signal in percent, or null when not on Wi-Fi.</summary>
    private static int? WifiSignal()
    {
        var output = Run("netsh", "wlan show interfaces");
        if (output == null || !output.Contains("connected", StringComparison.OrdinalIgnoreCase)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(output, @"(?:Signal|Signaal)\s*:\s*(\d+)%");
        var state = System.Text.RegularExpressions.Regex.Match(output, @"(?:State|Status)\s*:\s*(\w+)");
        if (state.Success && !state.Groups[1].Value.StartsWith("connected", StringComparison.OrdinalIgnoreCase)
                          && !state.Groups[1].Value.StartsWith("verbonden", StringComparison.OrdinalIgnoreCase)) return null;
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>The active power plan's name, e.g. "Balanced".</summary>
    private static string? PowerPlan()
    {
        var output = Run("powercfg", "/getactivescheme");
        var m = output != null ? System.Text.RegularExpressions.Regex.Match(output, @"\(([^)]+)\)") : null;
        return m is { Success: true } ? m.Groups[1].Value.Trim() : null;
    }

    private static string? Run(string file, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(info);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
