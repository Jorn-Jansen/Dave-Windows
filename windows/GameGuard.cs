using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DaveWindows;

/// <summary>
/// Which monitor Dave shows himself on (the bubble, the glow along the edges, the timer cards): the main one. With
/// "Keep Dave off games" on, never over a game (or a film) that's full screen, or over Roblox: then on another monitor
/// if there is one, or out of sight (he still talks). Asked many times a second (also from the glow's own thread), so
/// the answer is kept for a moment.
/// </summary>
public static class GameGuard
{
    /// <summary>The setting, set by the app when the settings change.</summary>
    public static volatile bool KeepOffGames;

    private static readonly object Sync = new();
    private static Screen? cached;
    private static long checkedAt = -1_000_000; // (not long.MinValue: "now - checkedAt" would overflow)

    /// <summary>The monitor to show Dave on, or null when there's no free one.</summary>
    public static Screen? ScreenForDave()
    {
        if (!KeepOffGames) return Screen.PrimaryScreen;
        lock (Sync)
        {
            if (Environment.TickCount64 - checkedAt < 250) return cached;
            var taken = TakenScreen();
            cached = taken == null ? Screen.PrimaryScreen
                   : Screen.PrimaryScreen is { } main && main.DeviceName != taken.DeviceName ? main
                   : Screen.AllScreens.FirstOrDefault(s => s.DeviceName != taken.DeviceName);
            checkedAt = Environment.TickCount64;
            return cached;
        }
    }

    /// <summary>The monitor with something full screen in front (a game, a video), or with Roblox in front; null when none.</summary>
    private static Screen? TakenScreen()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        var name = new System.Text.StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return null; // the desktop and taskbar
        var screen = Screen.FromHandle(window);
        if (GetWindowThreadProcessId(window, out var pid) != 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.ProcessName.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase)) return screen;
                if (p.Id == Environment.ProcessId) return null; // Dave's own window
            }
            catch { /* gone */ }
        }
        if (!GetWindowRect(window, out var r)) return null;
        var b = screen.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom ? screen : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int count);
}
