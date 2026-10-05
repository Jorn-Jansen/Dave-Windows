namespace DaveWindows;

/// <summary>
/// Keeps track of the music: which songs played (for "what was the last song?"), and skips songs you said you
/// don't like as soon as they come on in Spotify. Reads what Windows' media controls show, so it's cheap and works offline.
/// </summary>
public static class MusicWatcher
{
    public record Played(DateTime At, string Title, string Artist, string App);

    private static readonly List<Played> History = new();
    private static readonly object Sync = new();
    private static string lastKey = "";

    public static void Start(Settings s) => _ = Task.Run(async () =>
    {
        while (true)
        {
            try { await CheckAsync(s); }
            catch (Exception e) { Log.Write($"Music watcher: {e.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
    });

    private static async Task CheckAsync(Settings s)
    {
        var playing = await MediaSession.NowPlayingAsync();
        if (playing == null) return;
        var key = Key(playing.Title, playing.Artist);
        if (key == lastKey) return;
        lastKey = key;

        if (playing.App.Contains("spotify", StringComparison.OrdinalIgnoreCase) && IsDisliked(s, playing.Title, playing.Artist))
        {
            Log.Write($"Skipping a song you don't like: {playing.Title} – {playing.Artist}");
            await MediaSession.ControlAsync("next");
            return; // not counted as played
        }
        lock (Sync)
        {
            History.Add(new Played(DateTime.Now, playing.Title, playing.Artist, playing.App));
            if (History.Count > 50) History.RemoveAt(0);
        }
    }

    /// <summary>For the AI: the last few songs before [current] (the one playing now), newest first.</summary>
    public static string? EarlierSongs(MediaSession.Playing? current)
    {
        lock (Sync)
        {
            var key = current == null ? "" : Key(current.Title, current.Artist);
            var earlier = History.Where(p => Key(p.Title, p.Artist) != key).TakeLast(5).Reverse()
                .Select(p => $"\"{p.Title}\" by {p.Artist} ({p.At:HH:mm})").ToList();
            return earlier.Count == 0 ? null : string.Join(", ", earlier);
        }
    }

    /// <summary>"I don't like this song": remember it and skip it. Returns the song, or null if nothing is playing.</summary>
    public static async Task<MediaSession.Playing?> DislikeCurrentAsync(Settings s)
    {
        var playing = await MediaSession.NowPlayingAsync();
        if (playing == null) return null;
        if (!IsDisliked(s, playing.Title, playing.Artist))
        {
            s.DislikedSongs.Add(new Settings.Song { Title = playing.Title, Artist = playing.Artist });
            s.Save();
        }
        lastKey = Key(playing.Title, playing.Artist);
        await MediaSession.ControlAsync("next");
        return playing;
    }

    /// <summary>"Actually, that song is fine": take it off the list again. Returns how many were removed.</summary>
    public static int Undislike(Settings s, string song)
    {
        var wanted = Simple(song);
        var removed = s.DislikedSongs.RemoveAll(d => wanted.Length > 0 && (Simple(d.Title).Contains(wanted) || wanted.Contains(Simple(d.Title))));
        if (removed > 0) s.Save();
        return removed;
    }

    /// <summary>Same song? Titles can differ a bit ("- Remastered 2011"), and the artist may be just the first of several.</summary>
    public static bool IsDisliked(Settings s, string title, string artist)
    {
        var t = Simple(CleanTitle(title));
        var a = Simple(artist);
        return s.DislikedSongs.Any(d =>
        {
            var dt = Simple(CleanTitle(d.Title));
            var da = Simple(d.Artist);
            return dt.Length > 0 && dt == t && (da.Length == 0 || a.Length == 0 || a.Contains(da) || da.Contains(a)
                   || Simple(d.Artist.Split(',')[0]) == Simple(artist.Split(',')[0]));
        });
    }

    private static string CleanTitle(string title) =>
        System.Text.RegularExpressions.Regex.Replace(title, @"\s*[-–(\[].*(remaster|version|edit|mix|live|feat|with).*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string Key(string title, string artist) => Simple(title) + "|" + Simple(artist);

    private static string Simple(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
