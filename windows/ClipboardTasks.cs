namespace DaveWindows;

/// <summary>Reading and filling the clipboard ("read what I copied", "translate this", "copy that").</summary>
public static class ClipboardTasks
{
    private const int MaxText = 6000; // about 1500 words: keeps it within the free AI's limits

    /// <summary>Do [task] with what's on the clipboard. Returns what Dave should say.</summary>
    public static async Task<string> RunAsync(Settings s, string task, bool copyResult, string languageTag)
    {
        var (text, image) = OnSta(() =>
        {
            if (Clipboard.ContainsText()) return (Clipboard.GetText(), (byte[]?)null);
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } picture)
                using (picture) return ((string?)null, Vision.ToJpeg(picture));
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: 1 } files && IsImageFile(files[0]))
                using (var file = Image.FromFile(files[0]!)) return ((string?)null, Vision.ToJpeg(file));
            return ((string?)null, (byte[]?)null);
        });

        if (image != null)
        {
            Log.Write($"Clipboard image ({image.Length / 1024} KB)");
            return await Vision.AskAboutImageAsync(s, task, languageTag, Convert.ToBase64String(image), "an image the user copied to their clipboard");
        }
        if (string.IsNullOrWhiteSpace(text))
            return s.Say("There's nothing on your clipboard. Copy something first.", "Er staat niets op je klembord. Kopieer eerst iets.");

        var cut = text.Length > MaxText;
        if (cut) text = text[..MaxText];
        Log.Write($"Clipboard text ({text.Length} characters{(cut ? ", cut short" : "")})");
        var what = "text the user copied to their clipboard" + (cut ? " (only the first part: it was very long)" : "");
        if (!copyResult) return await Assistant.WorkOnAsync(s, task, text, what, languageTag, spoken: true);

        var result = await Assistant.WorkOnAsync(s, task, text, what, languageTag, spoken: false);
        if (result.Length == 0) return s.Say("That didn't work, sorry.", "Dat lukte niet, sorry.");
        Copy(result);
        return s.Say("Done, it's on your clipboard.", "Klaar, het staat op je klembord.");
    }

    public static void Copy(string text) => OnSta(() => { Clipboard.SetText(text); return true; });

    /// <summary>Put an image on the clipboard, ready to paste in Discord, a chat or a document.</summary>
    public static void Copy(Image image) => OnSta(() => { Clipboard.SetImage(image); return true; });

    private static bool IsImageFile(string? path) =>
        path != null && new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The clipboard only works on a thread set up for it (STA), so run there.</summary>
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() => { try { result = work(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result;
    }
}
