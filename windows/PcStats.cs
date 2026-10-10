using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DaveWindows;

/// <summary>What the PC is doing right now: processor, memory, graphics card, drives, battery. Read out by Dave on request.</summary>
public static class PcStats
{
    /// <summary>Bytes received and sent so far on all network connections that are up.</summary>
    private static (long received, long sent) NetworkBytes()
    {
        long received = 0, sent = 0;
        foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            try { var s = n.GetIPStatistics(); received += s.BytesReceived; sent += s.BytesSent; } catch { }
        }
        return (received, sent);
    }

    /// <summary>A short plain-text report for the AI to answer from. Takes about half a second (to measure processor use).</summary>
    public static string Collect()
    {
        var report = new StringBuilder();

        // Processor: total use and the busiest programs, measured over half a second
        var before = ProcessorTimes();
        var (received1, sent1) = NetworkBytes();
        GetSystemTimes(out var idle1, out var kernel1, out var user1);
        Thread.Sleep(500);
        GetSystemTimes(out var idle2, out var kernel2, out var user2);
        var after = ProcessorTimes();
        var (received2, sent2) = NetworkBytes();
        double total = (kernel2 - kernel1) + (user2 - user1), busy = total - (idle2 - idle1);
        var cpuName = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        report.AppendLine($"Processor: {cpuName?.Trim() ?? "unknown"}, {Environment.ProcessorCount} threads, {(total > 0 ? busy / total * 100 : 0):F0}% in use.");
        var cores = Environment.ProcessorCount * 0.5; // the 500 ms window, in processor-seconds per core
        var busiest = after.Select(p => (name: p.Key, percent: (p.Value - before.GetValueOrDefault(p.Key)).TotalSeconds / cores * 100))
            .Where(p => p.percent >= 1).OrderByDescending(p => p.percent).Take(5);
        report.AppendLine("Busiest programs (processor): " + Join(busiest.Select(p => $"{p.name} {p.percent:F0}%")));
        report.AppendLine("Processor temperature: Windows doesn't show it without extra software.");
        // Network right now ("is my game still downloading?"), measured over the same half second
        double downMb = (received2 - received1) * 2 / 1e6, upMb = (sent2 - sent1) * 2 / 1e6;
        report.AppendLine($"Network right now: downloading {downMb:F1} MB/s, uploading {upMb:F1} MB/s" +
                          (downMb > 1 ? " (something is downloading)" : downMb < 0.05 ? " (nothing is downloading)" : "") + ".");

        // Memory
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            double totalGb = memory.TotalPhys / 1e9, usedGb = (memory.TotalPhys - memory.AvailPhys) / 1e9;
            report.AppendLine($"Memory (RAM): {usedGb:F1} of {totalGb:F1} GB in use ({memory.MemoryLoad}%).");
        }
        var hungriest = Process.GetProcesses()
            .GroupBy(p => p.ProcessName)
            .Select(g => (name: g.Key, gb: g.Sum(p => { try { return p.PrivateMemorySize64; } catch { return 0L; } }) / 1e9))
            .OrderByDescending(p => p.gb).Take(5);
        report.AppendLine("Most memory: " + Join(hungriest.Select(p => $"{p.name} {p.gb:F1} GB")));

        // Graphics card (NVIDIA cards come with nvidia-smi)
        report.AppendLine(NvidiaGpu() ?? "Graphics card: no details available (only NVIDIA cards can be read).");

        // Drives
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            report.AppendLine($"Drive {drive.Name.TrimEnd('\\')} {(drive.VolumeLabel.Length > 0 ? $"({drive.VolumeLabel}) " : "")}" +
                              $"{drive.AvailableFreeSpace / 1e9:F0} GB free of {drive.TotalSize / 1e9:F0} GB.");

        // Battery (laptops) and uptime
        var power = SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
            report.AppendLine($"Battery: {power.BatteryLifePercent * 100:F0}%{(power.PowerLineStatus == PowerLineStatus.Online ? ", charging" : "")}.");
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        report.AppendLine($"PC on for: {(int)up.TotalHours} hours {up.Minutes} minutes.");
        return report.ToString();
    }

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count > 0 ? string.Join(", ", list) + "." : "nothing notable.";
    }

    private static Dictionary<string, TimeSpan> ProcessorTimes()
    {
        var times = new Dictionary<string, TimeSpan>();
        foreach (var p in Process.GetProcesses())
        {
            try { times[p.ProcessName] = times.GetValueOrDefault(p.ProcessName) + p.TotalProcessorTime; }
            catch { /* system processes can't be read */ }
            finally { p.Dispose(); }
        }
        return times;
    }

    private static string? NvidiaGpu()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,power.draw,fan.speed --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var smi = Process.Start(info);
            if (smi == null) return null;
            var line = smi.StandardOutput.ReadLine();
            smi.WaitForExit(3000);
            var f = line?.Split(',').Select(x => x.Trim()).ToArray();
            if (f == null || f.Length < 5) return null;
            var extra = (f.Length > 5 && f[5] != "[N/A]" ? $", using {f[5]} watts" : "") + (f.Length > 6 && f[6] != "[N/A]" ? $", fans at {f[6]}%" : "");
            return $"Graphics card: {f[0]}, {f[1]} Ã‚Â°C, {f[2]}% in use, video memory {double.Parse(f[3], CultureInfo.InvariantCulture) / 1024:F1} of {double.Parse(f[4], CultureInfo.InvariantCulture) / 1024:F1} GB{extra}.";
        }
        catch { return null; } // no NVIDIA card or driver
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
