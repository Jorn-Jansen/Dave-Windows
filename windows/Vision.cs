using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>
/// Lets Dave look at your screen: a screenshot of the main monitor goes to Groq's image model together with
/// your question. Only used when a question is about the screen, so normal questions stay just as fast.
/// </summary>
public static class Vision
{
    public const string Model = "qwen/qwen3.8-27b";
    private const int MaxWidth = 1600; // smaller is faster to send; text stays readable

    /// <summary>Windows to keep out of the screenshot (Dave's own bubble and glow).</summary>
    public static readonly List<Form> HiddenFromScreenshots = new();

    /// <summary>Take a screenshot and answer [question] about it, in [languageTag], as a short spoken answer.</summary>
    public static Task<string> AskAboutScreenAsync(Settings settings, string question, string languageTag) =>
        AskAboutImageAsync(settings, question, languageTag, CaptureMainScreen(), "a screenshot of the user's main monitor, taken just now");

    /// <summary>Answer [question] about a JPEG ([image], base64), described to the AI as [whatItIs].</summary>
    public static async Task<string> AskAboutImageAsync(Settings settings, string question, string languageTag, string image, string whatItIs)
    {
        var language = CultureInfo.GetCultureInfo(languageTag).EnglishName.Split(' ')[0];
        var system = $"""
            You are {settings.Name}, a voice assistant. The image is {whatItIs}.
            You CAN see it: it is attached to the message. Never say you can't see it; describe what is in the image.
            Answer the user's question about it in {language}, in one to three short spoken sentences (more only if they ask to read or explain something longer).
            Plain speech only: no markdown, lists, emojis or symbols. Don't describe the screen in general unless that's what they asked.
            """;
        var body = new JsonObject
        {
            ["model"] = Model,
            ["reasoning_format"] = "hidden", // keep the model's thinking out of the answer
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = question },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + image } },
                    },
                },
            },
        };

        var (status, text) = await Groq.ChatRawAsync(settings, body);
        if (status == 400 && text.Contains("reasoning_format"))
        {
            body.Remove("reasoning_format");
            (status, text) = await Groq.ChatRawAsync(settings, body);
        }
        if (status is < 200 or >= 300) throw Groq.Failure(settings, status, text);
        var answer = JsonNode.Parse(text)!["choices"]![0]!["message"]!["content"]?.ToString() ?? "";
        answer = Regex.Replace(answer, @"<think>[\s\S]*?</think>", "").Trim(); // in case thinking comes along anyway
        return Assistant.Speakable(answer);
    }

    /// <summary>Screenshot of the main monitor as a JPEG (base64), without Dave's own overlays.</summary>
    public static string CaptureMainScreen()
    {
        using var full = Capture(Screen.PrimaryScreen!.Bounds);
        var jpeg = ToJpeg(full);
        Log.Write($"Screenshot taken ({jpeg.Length / 1024} KB)");
        // Keep the last one, so you can check what Dave saw.
        try { File.WriteAllBytes(Path.Combine(Settings.Folder, "last-screenshot.jpg"), jpeg); } catch { }
        return Convert.ToBase64String(jpeg);
    }

    /// <summary>What's on the screen in [bounds] (any monitor, or several), full size, without Dave's own overlays.</summary>
    public static Bitmap Capture(Rectangle bounds)
    {
        foreach (var form in HiddenFromScreenshots) SetWindowDisplayAffinity(form.Handle, ExcludeFromCapture);
        try
        {
            var image = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(image)) g.CopyFromScreen(bounds.Location, Point.Empty, image.Size);
            return image;
        }
        finally
        {
            foreach (var form in HiddenFromScreenshots) SetWindowDisplayAffinity(form.Handle, 0);
        }
    }

    /// <summary>[image] as a JPEG, at most 1600 px wide: small enough to send fast, text stays readable.</summary>
    public static byte[] ToJpeg(Image image)
    {
        var scale = Math.Min(1.0, MaxWidth / (double)image.Width);
        using var small = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.Clear(Color.White); // transparent parts (copied images often have them) would turn black
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, 0, 0, small.Width, small.Height);
        }
        using var stream = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 80L) } };
        small.Save(stream, jpeg, quality);
        return stream.ToArray();
    }

    private const uint ExcludeFromCapture = 0x11; // WDA_EXCLUDEFROMCAPTURE (Windows 10 2004 and newer)

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
