using System.Drawing.Imaging;

namespace DaveWindows;

/// <summary>"Take a screenshot and copy it": of the main screen, the window you're in, or all screens. Dave's own bubble stays out of it.</summary>
public static class Screenshots
{
    /// <summary>Take it and copy and/or save it. Returns the bubble text, or a message to say when something's wrong.</summary>
    public static Commands.Outcome Take(Settings s, string what, string action, string where)
    {
        Rectangle bounds;
        if (what == "window")
        {
            var front = WindowList.Foreground();
            if (front == IntPtr.Zero) return new Commands.Outcome(s.Say("I don't see a window to take.", "Ik zie geen venster."), true);
            bounds = Rectangle.Intersect(WindowControl.VisibleBounds(front), SystemInformation.VirtualScreen);
        }
        else bounds = what == "all_screens" ? SystemInformation.VirtualScreen : Screen.PrimaryScreen!.Bounds;
        if (bounds.Width < 2 || bounds.Height < 2) return new Commands.Outcome(s.Say("That window isn't on the screen.", "Dat venster staat niet op het scherm."), true);

        using var image = Vision.Capture(bounds);
        var done = new List<string>();
        if (action is "copy" or "both")
        {
            ClipboardTasks.Copy(image);
            done.Add(s.Say("copied", "gekopieerd"));
        }
        if (action is "save" or "both")
        {
            var folder = where == "desktop"
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"Dave {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            image.Save(file, ImageFormat.Png);
            Log.Write($"Screenshot saved: {file}");
            done.Add(where == "desktop" ? s.Say("saved on your desktop", "op je bureaublad gezet") : s.Say("saved in Pictures › Screenshots", "opgeslagen in Afbeeldingen › Screenshots"));
        }
        return new Commands.Outcome("📸 " + s.Say("Screenshot ", "Screenshot ") + string.Join(s.Say(" and ", " en "), done));
    }
}
