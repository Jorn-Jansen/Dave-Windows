using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DaveWindows;

/// <summary>
/// Heads-ups on your phone (Settings → iPhone app → Notifications on my phone): when a reminder or something Dave was
/// watching for goes off while you're not at the PC, it's sent to your own channel on ntfy (ntfy.sh, a free notification
/// service). The free ntfy app on the phone, subscribed to that channel, shows it as a real notification. The channel's
/// name is long and random, so only you know it.
/// </summary>
public static class PhoneNotify
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string Server = "https://ntfy.sh";

    /// <summary>The channel, made the first time: "dave-" and 20 random letters and digits.</summary>
    public static string TopicFor(Settings s)
    {
        if (s.PhoneNotifyTopic.Length == 0)
        {
            const string chars = "abcdefghijkmnpqrstuvwxyz23456789";
            s.PhoneNotifyTopic = "dave-" + new string(Enumerable.Range(0, 20).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
            s.Save();
        }
        return s.PhoneNotifyTopic;
    }

    /// <summary>Send [message] to the phone, when it's turned on and you're away from the PC (or always, with [evenAtPc]).</summary>
    public static void Send(Settings s, string title, string message, bool evenAtPc = false)
    {
        if (!s.PhoneNotify || (!evenAtPc && ScreenTime.Idle < TimeSpan.FromMinutes(2))) return;
        var topic = TopicFor(s);
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{Server}/{topic}") { Content = new StringContent(message, Encoding.UTF8, "text/plain") };
                request.Headers.Add("Title", Encode(title));
                request.Headers.Add("Tags", "bell");
                using var response = await Http.SendAsync(request);
                Log.Write($"Phone notification: {(int)response.StatusCode} ({title})");
            }
            catch (Exception e) { Log.Write($"Phone notification failed: {e.Message}"); }
        });
    }

    // --- Sending things from the PC to the phone ("send this to my phone") ---

    /// <summary>What ntfy.sh takes as an attachment (and keeps for 3 hours).</summary>
    public const int MaxFileBytes = 15 * 1024 * 1024;

    /// <summary>Why it can't be sent right now, or null when it can.</summary>
    public static string? NotReady(Settings s) => s.PhoneNotify ? null : s.Say(
        "First turn on Notifications on my phone in my settings (under iPhone app), and add the channel in the free ntfy app on your phone.",
        "Zet eerst Meldingen op mijn telefoon aan in mijn instellingen (bij iPhone-app), en voeg het kanaal toe in de gratis ntfy-app op je telefoon.");

    /// <summary>A link: tapping the notification opens it. Returns null when sent, otherwise why not.</summary>
    public static Task<string?> SendLinkAsync(Settings s, string url) =>
        PublishAsync(s, new StringContent(url, Encoding.UTF8, "text/plain"),
            $"title={Uri.EscapeDataString("🔗 " + s.Say("From your PC", "Van je pc"))}&click={Uri.EscapeDataString(url)}&tags=link");

    /// <summary>Text: shown in the notification, and copyable in ntfy.</summary>
    public static Task<string?> SendTextAsync(Settings s, string text) =>
        PublishAsync(s, new StringContent(text, Encoding.UTF8, "text/plain"),
            $"title={Uri.EscapeDataString("📋 " + s.Say("From your PC", "Van je pc"))}&tags=clipboard");

    /// <summary>A photo or file: tapping the notification shows it, and it can be saved from there (for 3 hours).</summary>
    public static Task<string?> SendFileAsync(Settings s, byte[] data, string fileName)
    {
        if (data.Length > MaxFileBytes)
            return Task.FromResult<string?>(s.Say($"{fileName} is too big for your phone's notifications (the limit is 15 MB).",
                                                  $"{fileName} is te groot voor de meldingen op je telefoon (de grens is 15 MB)."));
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return PublishAsync(s, content,
            $"filename={Uri.EscapeDataString(fileName)}&title={Uri.EscapeDataString("📎 " + s.Say("From your PC", "Van je pc"))}" +
            $"&message={Uri.EscapeDataString(fileName)}&tags=paperclip", put: true);
    }

    private static async Task<string?> PublishAsync(Settings s, HttpContent content, string query, bool put = false)
    {
        if (NotReady(s) is { } why) return why;
        try
        {
            using var request = new HttpRequestMessage(put ? HttpMethod.Put : HttpMethod.Post, $"{Server}/{TopicFor(s)}?{query}") { Content = content };
            using var response = await new HttpClient { Timeout = TimeSpan.FromSeconds(120) }.SendAsync(request);
            Log.Write($"Sent to the phone: {(int)response.StatusCode}");
            if (response.IsSuccessStatusCode) return null;
            return (int)response.StatusCode == 413
                ? s.Say("That's too big for your phone's notifications.", "Dat is te groot voor de meldingen op je telefoon.")
                : s.Say($"ntfy didn't take it ({(int)response.StatusCode}).", $"ntfy nam het niet aan ({(int)response.StatusCode}).");
        }
        catch (Exception e)
        {
            Log.Write($"Sending to the phone failed: {e.Message}");
            return s.Say("I couldn't reach ntfy (is the internet on?).", "Ik kon ntfy niet bereiken (is het internet aan?).");
        }
    }

    /// <summary>"Send to → My iPhone (Dave)" in Explorer's right-click menu, while notifications on the phone are on.</summary>
    public static void UpdateSendToShortcut(Settings s)
    {
        try
        {
            var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "My iPhone (Dave).lnk");
            if (!s.PhoneNotify) { if (File.Exists(link)) File.Delete(link); return; }
            if (File.Exists(link)) return;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = Application.ExecutablePath;
            shortcut.Arguments = "--send-to-phone";
            shortcut.IconLocation = Application.ExecutablePath + ",0";
            shortcut.Description = "Send this file to your phone, through Dave";
            shortcut.Save();
        }
        catch (Exception e) { Log.Write($"Send to shortcut: {e.Message}"); }
    }

    /// <summary>Headers are ASCII: ntfy reads "=?UTF-8?B?…?=" for anything else (emoji, é).</summary>
    private static string Encode(string text) =>
        text.All(c => c < 128) ? text : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";
}
