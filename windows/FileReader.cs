using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>
/// "Read this file and rate it": gets what's in a file so the AI can answer about it. Text and code are read
/// directly, Word documents unpacked, PDFs and images looked at (as pictures), folders listed.
/// </summary>
public static class FileReader
{
    /// <summary>About 1700 words: enough to judge most files, and within the free AI's per-minute limit.</summary>
    private const int MaxText = 7000;

    private static readonly string[] ImageTypes = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>What's in the file: as text, or as pictures (base64 JPEGs) for PDFs and images. [Problem] says why not.</summary>
    public record Content(string Path, string? Text = null, List<string>? Images = null, string? Problem = null, bool Cut = false);

    public static async Task<Content> ReadAsync(string path)
    {
        if (Directory.Exists(path)) return ListFolder(path);
        if (!File.Exists(path)) return new Content(path, Problem: "not found");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ImageTypes.Contains(extension))
            {
                using var picture = Image.FromFile(path);
                return new Content(path, Images: new List<string> { Convert.ToBase64String(Vision.ToJpeg(picture)) });
            }
            if (extension == ".pdf") return await PdfPagesAsync(path);
            if (extension is ".docx") return Cut(path, WordText(path));
            if (LooksLikeText(path)) return Cut(path, await File.ReadAllTextAsync(path));
            return new Content(path, Problem: "not a text file");
        }
        catch (Exception e)
        {
            Log.Write($"Reading {path} failed: {e.Message}");
            return new Content(path, Problem: e is UnauthorizedAccessException or IOException ? "can't be opened (in use, or no access)" : e.Message);
        }
    }

    private static Content Cut(string path, string text)
    {
        text = text.Replace("\r\n", "\n");
        return text.Length > MaxText ? new Content(path, text[..MaxText], Cut: true) : new Content(path, text);
    }

    /// <summary>Text files have no zero bytes in their first few kilobytes; programs and other binary files do.</summary>
    private static bool LooksLikeText(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[4096];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read >= 2 && (buffer[0] == 0xFF && buffer[1] == 0xFE || buffer[0] == 0xFE && buffer[1] == 0xFF)) return true; // UTF-16
        return !buffer.Take(read).Contains((byte)0);
    }

    /// <summary>The text of a Word document: it's a zip with the text in word/document.xml.</summary>
    private static string WordText(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("not a Word document");
        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        xml = Regex.Replace(xml, @"</w:p>", "\n");
        xml = Regex.Replace(xml, @"<w:tab/>", "\t");
        var text = Regex.Replace(xml, @"<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text);
    }

    /// <summary>The first pages of a PDF as pictures (Windows' own PDF reader draws them), for the image AI to read.</summary>
    private static async Task<Content> PdfPagesAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        var images = new List<string>();
        for (uint i = 0; i < Math.Min(pdf.PageCount, 3u); i++)
        {
            using var page = pdf.GetPage(i);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1400 });
            using var picture = Image.FromStream(stream.AsStream());
            images.Add(Convert.ToBase64String(Vision.ToJpeg(picture)));
        }
        return new Content(path, Images: images, Cut: pdf.PageCount > 3);
    }

    private static Content ListFolder(string path)
    {
        var text = new StringBuilder();
        try
        {
            var folders = Directory.EnumerateDirectories(path).Select(d => new DirectoryInfo(d)).OrderBy(d => d.Name).Take(60).ToList();
            var files = Directory.EnumerateFiles(path).Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).Take(150).ToList();
            text.AppendLine($"Folder {path}: {folders.Count} folders and {files.Count} files shown.");
            foreach (var d in folders) text.AppendLine($"[folder] {d.Name}");
            foreach (var f in files) text.AppendLine($"{f.Name}  ({Size(f.Length)}, changed {f.LastWriteTime:d MMM yyyy})");
        }
        catch (Exception e) { return new Content(path, Problem: e.Message); }
        return Cut(path, text.ToString());
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / 1024.0 / 1024:F1} MB",
    };
}
