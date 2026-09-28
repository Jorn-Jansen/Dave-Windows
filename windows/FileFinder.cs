using System.Data.OleDb;
using System.Diagnostics;

namespace DaveWindows;

/// <summary>
/// Finds files by (part of) their name and/or type. Uses the Windows Search index (fast, same as Explorer's
/// search box); falls back to walking your own folders if the index isn't available.
/// </summary>
public static class FileFinder
{
    public record Found(string Path, DateTime Modified);

    private static readonly Dictionary<string, string[]> KindExtensions = new()
    {
        ["image"] = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic" },
        ["video"] = new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".wmv" },
        ["audio"] = new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg" },
        ["document"] = new[] { ".pdf", ".docx", ".doc", ".txt", ".md", ".pptx", ".xlsx", ".odt", ".rtf" },
        ["code"] = new[] { ".js", ".ts", ".cs", ".lua", ".luau", ".py", ".kt", ".java", ".json", ".html", ".css", ".rbxl", ".rbxlx" },
    };

    // Windows Search's own names for these kinds.
    private static readonly Dictionary<string, string> SearchKinds = new()
    {
        ["image"] = "picture", ["video"] = "video", ["audio"] = "music", ["document"] = "document", ["folder"] = "folder",
    };

    private static readonly string[] SkipFolders = { "node_modules", ".git", "bin", "obj", "AppData", ".gradle", "build", "$Recycle.Bin" };

    /// <summary>Best matches first: exact name, then name starting with the words, then newest.</summary>
    public static async Task<List<Found>> FindAsync(string query, string kind, bool newest)
    {
        query = query.Trim();
        List<Found> results;
        try { results = await Task.Run(() => SearchIndex(query, kind)); }
        catch (Exception e)
        {
            Log.Write($"Windows Search unavailable ({e.Message}); searching folders instead");
            results = await Task.Run(() => SearchFolders(query, kind));
        }
        if (results.Count == 0 && query.Length > 0)
            results = await Task.Run(() => SearchFolders(query, kind)); // the index can miss folders it doesn't cover

        var q = query.ToLowerInvariant();
        return (newest
                ? results.OrderByDescending(f => f.Modified)
                : results.OrderByDescending(f => Score(Path.GetFileNameWithoutExtension(f.Path).ToLowerInvariant(), Path.GetFileName(f.Path).ToLowerInvariant(), q))
                         .ThenByDescending(f => f.Modified))
            .Take(10)
            .ToList();
    }

    private static int Score(string nameNoExtension, string name, string q) =>
        q.Length == 0 ? 0 : name == q || nameNoExtension == q ? 3 : name.StartsWith(q) ? 2 : 1;

    private static List<Found> SearchIndex(string query, string kind)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        var where = new List<string> { $"SCOPE='file:{Escape(profile)}'" };
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            where.Add($"System.FileName LIKE '%{Escape(word)}%'");
        if (SearchKinds.TryGetValue(kind, out var searchKind)) where.Add($"System.Kind = '{searchKind}'");
        else if (KindExtensions.TryGetValue(kind, out var extensions))
            where.Add("(" + string.Join(" OR ", extensions.Select(e => $"System.FileExtension = '{e}'")) + ")");

        var sql = $"SELECT TOP 200 System.ItemPathDisplay, System.DateModified FROM SystemIndex WHERE {string.Join(" AND ", where)} " +
                  "ORDER BY System.DateModified DESC";
        using var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        connection.Open();
        using var command = new OleDbCommand(sql, connection);
        using var reader = command.ExecuteReader();
        var found = new List<Found>();
        while (reader.Read())
        {
            var path = reader.GetValue(0) as string;
            if (string.IsNullOrEmpty(path) || SkipFolders.Any(s => path.Contains($"\\{s}\\", StringComparison.OrdinalIgnoreCase))) continue;
            found.Add(new Found(path, reader.GetValue(1) is DateTime modified ? modified : DateTime.MinValue));
        }
        return found;
    }

    private static string Escape(string s) => s.Replace("'", "''").Replace("%", "").Replace("[", "");

    /// <summary>Fallback: walk the usual folders, at most a few seconds.</summary>
    private static List<Found> SearchFolders(string query, string kind)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "OneDrive" }
            .Select(r => Path.Combine(profile, r)).Where(Directory.Exists).ToList();
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        KindExtensions.TryGetValue(kind, out var extensions);
        var found = new List<Found>();
        var clock = Stopwatch.StartNew();
        var stack = new Stack<string>(roots);
        while (stack.Count > 0 && clock.ElapsedMilliseconds < 4000 && found.Count < 500)
        {
            var folder = stack.Pop();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(folder))
                {
                    if (SkipFolders.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)) continue;
                    stack.Push(dir);
                    if (kind == "folder" && words.All(w => Path.GetFileName(dir).ToLowerInvariant().Contains(w)))
                        found.Add(new Found(dir, Directory.GetLastWriteTime(dir)));
                }
                if (kind == "folder") continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileName(file).ToLowerInvariant();
                    if (!words.All(name.Contains)) continue;
                    if (extensions != null && !extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                    found.Add(new Found(file, File.GetLastWriteTime(file)));
                }
            }
            catch { /* no access to this folder */ }
        }
        return found;
    }

    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static void ShowInFolder(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");

    /// <summary>"on your Desktop", "in Downloads\Roblox", … for speaking.</summary>
    public static string Where(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Path.GetDirectoryName(path) ?? "";
        return folder.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ? folder[(profile.Length + 1)..] : folder;
    }
}
