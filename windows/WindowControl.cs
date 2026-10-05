using System.Runtime.InteropServices;

namespace DaveWindows;

/// <summary>
/// Arranging windows by voice: move one to the other screen, snap it left or right, put two side by side,
/// maximise, minimise, or minimise everything.
/// </summary>
public static class WindowControl
{
    /// <summary>Do [action] with the window of [app] (empty = the window you're working in). Returns what Dave should say, or null when it went fine.</summary>
    public static string? Run(Settings s, string action, string app, string app2, int screen)
    {
        if (action == "minimize_all") { Shell("MinimizeAll"); return null; }
        if (action == "restore_all") { Shell("UndoMinimizeALL"); return null; }

        var window = Find(app);
        if (window == IntPtr.Zero)
            return app.Length > 0 ? s.Say($"I don't see a {app} window.", $"Ik zie geen venster van {app}.")
                                  : s.Say("I don't know which window you mean.", "Ik weet niet welk venster je bedoelt.");
        var area = Screen.FromHandle(window).WorkingArea;
        switch (action)
        {
            case "other_screen" or "move_to_screen":
                var screens = Screen.AllScreens.OrderBy(x => x.Bounds.X).ThenBy(x => x.Bounds.Y).ToList();
                if (screens.Count < 2) return s.Say("You only have one screen.", "Je hebt maar één scherm.");
                var current = screens.FindIndex(x => x.DeviceName == Screen.FromHandle(window).DeviceName);
                // A screen number only when it's another screen than the current one ("my other screen" is sometimes sent as a number)
                var target = action == "move_to_screen" && screen >= 1 && screen <= screens.Count && screen - 1 != current
                    ? screens[screen - 1] : screens[(current + 1) % screens.Count];
                MoveToScreen(window, Screen.FromHandle(window).WorkingArea, target.WorkingArea);
                break;
            case "left_half":
                Place(window, new Rectangle(area.X, area.Y, area.Width / 2, area.Height));
                break;
            case "right_half":
                Place(window, new Rectangle(area.X + area.Width / 2, area.Y, area.Width - area.Width / 2, area.Height));
                break;
            case "side_by_side":
                var second = Find(app2);
                if (second == IntPtr.Zero || second == window)
                    return s.Say($"I don't see a {app2} window to put next to it.", $"Ik zie geen venster van {app2} om ernaast te zetten.");
                Place(window, new Rectangle(area.X, area.Y, area.Width / 2, area.Height));
                Place(second, new Rectangle(area.X + area.Width / 2, area.Y, area.Width - area.Width / 2, area.Height));
                WindowList.Focus(second);
                break;
            case "maximize": Show(window, Maximize); break;
            case "minimize": Show(window, Minimize); return null;
            case "restore": Show(window, Restore); break;
            case "center":
                Show(window, Restore);
                GetWindowRect(window, out var r);
                int w = Math.Min(r.Right - r.Left, area.Width), h = Math.Min(r.Bottom - r.Top, area.Height);
                Place(window, new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
                break;
            case "focus": break;
            default: return s.Say("I can't do that with windows.", "Dat kan ik niet met vensters.");
        }
        WindowList.Focus(window);
        return null;
    }

    /// <summary>The window of [app] (by program or title), or the one in front when [app] is empty.</summary>
    private static IntPtr Find(string app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            var front = WindowList.Foreground();
            GetWindowThreadProcessId(front, out var pid);
            return pid == Environment.ProcessId ? IntPtr.Zero : front;
        }
        string Simple(string x) => new(x.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var wanted = Simple(app);
        // Not "cloaked" ones: Store apps leave invisible windows around that only look open
        var windows = WindowList.List().Where(w => w.title != "Program Manager" && !IsCloaked(w.handle)).ToList();
        // The program's name first (e.g. "chrome" for Chrome), then the window title (e.g. a YouTube tab)
        var match = windows.FirstOrDefault(w => Simple(w.process).Contains(wanted) || (Simple(w.process).Length > 3 && wanted.Contains(Simple(w.process))));
        if (match.handle == IntPtr.Zero) match = windows.FirstOrDefault(w => Simple(w.title).Contains(wanted));
        return match.handle;
    }

    /// <summary>Same place and size relative to the screen, on the other screen; maximised stays maximised.</summary>
    /// <remarks>Restore, move, maximise again: setting the window's "placement" directly is ignored by apps that remember a snapped position.</remarks>
    private static void MoveToScreen(IntPtr window, Rectangle from, Rectangle to)
    {
        var maximized = IsZoomed(window);
        Show(window, Restore);
        var bounds = VisibleBounds(window);
        if (!from.IntersectsWith(bounds)) from = Screen.FromRectangle(bounds).WorkingArea; // restored onto another screen
        double sx = to.Width / (double)from.Width, sy = to.Height / (double)from.Height;
        var moved = new Rectangle(
            to.X + (int)((bounds.X - from.X) * sx), to.Y + (int)((bounds.Y - from.Y) * sy),
            Math.Min(to.Width, (int)(bounds.Width * sx)), Math.Min(to.Height, (int)(bounds.Height * sy)));
        moved.X = Math.Clamp(moved.X, to.X, to.Right - moved.Width);
        moved.Y = Math.Clamp(moved.Y, to.Y, to.Bottom - moved.Height);
        Place(window, moved);
        if (maximized) Show(window, Maximize);
    }

    /// <summary>A window that's "open" but invisible (Store apps keep these around).</summary>
    public static bool IsCloaked(IntPtr window) =>
        DwmGetCloaked(window, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetCloaked(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>Put the visible part of the window exactly on [target] (Windows 10/11 windows have invisible borders around them).</summary>
    private static void Place(IntPtr window, Rectangle target)
    {
        if (IsZoomed(window) || IsIconic(window)) Show(window, Restore);
        GetWindowRect(window, out var outer);
        var visible = VisibleBounds(window);
        int left = visible.Left - outer.Left, top = visible.Top - outer.Top, right = outer.Right - visible.Right, bottom = outer.Bottom - visible.Bottom;
        SetWindowPos(window, IntPtr.Zero, target.X - left, target.Y - top, target.Width + left + right, target.Height + top + bottom,
            0x0004 | 0x0010 /* SWP_NOZORDER | SWP_NOACTIVATE */);
    }

    /// <summary>The window as you see it, without the invisible resize borders.</summary>
    public static Rectangle VisibleBounds(IntPtr window)
    {
        if (DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<Rect>()) != 0)
            GetWindowRect(window, out r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static void Shell(string method)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return;
        var shell = Activator.CreateInstance(type);
        type.InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
    }

    private const int Maximize = 0xF030, Minimize = 0xF020, Restore = 0xF120; // SC_MAXIMIZE, SC_MINIMIZE, SC_RESTORE

    /// <summary>
    /// Maximise / minimise / restore, as if you clicked the title bar button: asks the window itself, which works for every
    /// kind of app (calling ShowWindow from here can be ignored, e.g. the first time a program uses it).
    /// </summary>
    private static void Show(IntPtr window, int command) =>
        SendMessageTimeout(window, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)command, IntPtr.Zero, 0x0002 /* SMTO_ABORTIFHUNG */, 1000, out _);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out Rect value, int size);
}
