using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>
/// Dave's longer memory: every exchange (what you said, what Dave answered or did) is kept for 30 days in
/// conversations.json, so "what did I ask you yesterday?" works, also after a restart.
/// Only stays on this PC, next to the settings.
/// </summary>
public static class ConversationLog
{
    public record Entry(DateTime At, string You, string Dave);

    private static readonly string FilePath = Path.Combine(Settings.Folder, "conversations.json");
    private static readonly object Sync = new();
    private static List<Entry>? entries;
    private const int KeepDays = 30, MaxEntries = 2000;

    /// <summary>Raised after an exchange is added (on whatever thread added it), e.g. to update Dave's window.</summary>
    public static event Action? Added;

    /// <summary>The last [count] exchanges, oldest first.</summary>
    public static List<Entry> Recent(int count)
    {
        lock (Sync) return Load().TakeLast(count).ToList();
    }

    public static void Add(string you, string dave)
    {
        try { AddEntry(you, dave); }
        finally { Added?.Invoke(); }
    }

    private static void AddEntry(string you, string dave)
    {
        if (string.IsNullOrWhiteSpace(you) || string.IsNullOrWhiteSpace(dave)) return;
        lock (Sync)
        {
            var list = Load();
            list.Add(new Entry(DateTime.Now, Trim(you, 500), Trim(dave, 800)));
            list.RemoveAll(e => e.At < DateTime.Now.AddDays(-KeepDays));
            if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(list)); }
            catch (Exception e) { Log.Write($"Couldn't save the conversation log: {e.Message}"); }
        }
    }

    /// <summary>The last few things you asked (short), for when the short-term memory is empty: lets Dave pick up where you left off.</summary>
    public static string? RecentTopics(int count = 6)
    {
        lock (Sync)
        {
            var recent = Load().TakeLast(count).ToList();
            if (recent.Count == 0) return null;
            return string.Join("; ", recent.Select(e => $"{When(e.At)}: \"{Trim(e.You, 80)}\""));
        }
    }

    /// <summary>
    /// Earlier exchanges about [about] (keywords, any language), between [fromDaysAgo] and [toDaysAgo] (0 = today).
    /// Without keywords, or when nothing matches, the most recent ones in that period.
    /// </summary>
    public static string Search(string about, int fromDaysAgo, int toDaysAgo)
    {
        lock (Sync)
        {
            var start = DateTime.Today.AddDays(-Math.Max(fromDaysAgo, toDaysAgo));
            var end = DateTime.Today.AddDays(1 - Math.Min(fromDaysAgo, toDaysAgo));
            var inRange = Load().Where(e => e.At >= start && e.At < end).ToList();
            if (inRange.Count == 0) return "Nothing was said in that period.";

            var words = Regex.Split(about.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 2).Distinct().ToList();
            var picked = words.Count == 0 ? new List<Entry>() : inRange
                .Select(e => (entry: e, score: words.Count(w => (e.You + " " + e.Dave).ToLowerInvariant().Contains(w))))
                .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenByDescending(x => x.entry.At)
                .Take(12).Select(x => x.entry).OrderBy(e => e.At).ToList();
            var note = "";
            if (picked.Count == 0)
            {
                if (words.Count > 0) note = $"(Nothing matched \"{about}\"; these are the most recent ones in that period.)\n";
                picked = inRange.TakeLast(15).ToList();
            }

            var text = new StringBuilder(note);
            foreach (var e in picked)
            {
                var line = $"[{e.At:dddd d MMMM HH:mm}] User: {e.You}\n  You answered: {e.Dave}\n";
                if (text.Length + line.Length > 4000) break; // stay within the free AI's limits
                text.Append(line);
            }
            return text.ToString();
        }
    }

    private static List<Entry> Load()
    {
        if (entries != null) return entries;
        try { entries = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) { Log.Write($"Couldn't read the conversation log: {e.Message}"); entries = new(); }
        return entries;
    }

    private static string When(DateTime at) =>
        at.Date == DateTime.Today ? $"today {at:HH:mm}" : at.Date == DateTime.Today.AddDays(-1) ? $"yesterday {at:HH:mm}" : at.ToString("d MMM HH:mm");

    private static string Trim(string s, int max)
    {
        s = Regex.Replace(s.Trim(), @"\s+", " ");
        return s.Length > max ? s[..max] + "…" : s;
    }
}
