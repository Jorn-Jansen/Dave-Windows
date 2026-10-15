using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>
/// Connecting Dave made easy: for the AI (a free Groq key) and Spotify. Opens the page where you make the key, says how,
/// and then watches the clipboard: as soon as you copy the key, it's checked, filled in and saved (Spotify then logs in
/// by itself). The clipboard is only looked at during such a setup (at most 20 minutes), never otherwise.
/// </summary>
public sealed partial class ConnectHelper : IDisposable
{
    public const string GroqPage = "https://console.groq.com/keys";
    public const string SpotifyPage = "https://developer.spotify.com/dashboard/create";

    /// <summary>What to tell the user (shown and said): a step, or that it worked.</summary>
    public event Action<string>? Say;
    /// <summary>A key was filled in: the setting's name and its value (so an open settings panel shows it too).</summary>
    public event Action<string, string>? Filled;
    /// <summary>Where a setup is: "groq"/"spotify", and a short status for the settings panel.</summary>
    public event Action<string, string>? Status;

    private readonly Settings settings;
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 800 };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string waiting = ""; // "groq", "spotify" or ""
    private DateTime until;
    private string lastSeen = "";
    private bool checking;

    [GeneratedRegex(@"^gsk_[A-Za-z0-9]{40,64}$")] private static partial Regex GroqKey();
    [GeneratedRegex(@"^[0-9a-f]{32}$")] private static partial Regex SpotifyId();

    /// <summary>The one Dave uses (for the voice command and the buttons in the settings).</summary>
    public static ConnectHelper? Current { get; private set; }
    private readonly SynchronizationContext? ui;

    public ConnectHelper(Settings settings)
    {
        this.settings = settings;
        ui = SynchronizationContext.Current; // (made on the UI thread: the clipboard and the timer need it)
        Current = this;
        poll.Tick += async (_, _) => await CheckClipboardAsync();
    }

    public bool Waiting => waiting.Length > 0;

    /// <summary>The AI: opens Groq's key page and waits for the key. Returns the steps.</summary>
    public string StartGroq(bool announce = false)
    {
        if (ui != null && SynchronizationContext.Current != ui) { var r = ""; ui.Send(_ => r = StartGroq(announce), null); return r; }
        Begin("groq", GroqPage);
        Status?.Invoke("groq", settings.Say("Waiting for you to copy the key…", "Wacht tot je de sleutel kopieert…"));
        var steps = settings.Say(
            "Let's get you a free AI key. I opened Groq's website: log in (Google works), click \"Create API Key\", give it any name, and click Copy. " +
            "That's it: as soon as you copy it, I'll fill it in myself.",
            "Laten we een gratis AI-sleutel regelen. Ik heb de website van Groq geopend: log in (met Google kan ook), klik op \"Create API Key\", geef hem een naam en klik op Copy. " +
            "Meer niet: zodra je hem kopieert, vul ik hem zelf in.");
        if (announce) Say?.Invoke(steps);
        return steps;
    }

    /// <summary>Spotify: opens the page to make an app, puts the Redirect URI on the clipboard, and waits for the Client ID. Returns the steps.</summary>
    public string StartSpotify(bool announce = false)
    {
        if (ui != null && SynchronizationContext.Current != ui) { var r = ""; ui.Send(_ => r = StartSpotify(announce), null); return r; }
        Begin("spotify", SpotifyPage);
        try { Clipboard.SetText(Spotify.RedirectUri); lastSeen = Spotify.RedirectUri; } catch { /* busy: it's in the steps too */ }
        Status?.Invoke("spotify", settings.Say("Waiting for you to copy the Client ID…", "Wacht tot je de Client ID kopieert…"));
        var steps = settings.Say(
            "I opened Spotify's developer page (log in if it asks). Make an app: any name and description, and in \"Redirect URIs\" paste what I just copied for you " +
            $"({Spotify.RedirectUri}) and click Add. Tick \"Web API\", agree, and Save. Then copy the Client ID at the top: I'll fill it in and connect myself. (Spotify Premium is needed.)",
            "Ik heb de ontwikkelaarspagina van Spotify geopend (log in als hij erom vraagt). Maak een app: naam en beschrijving maakt niet uit, en bij \"Redirect URIs\" plak je wat ik net voor je heb gekopieerd " +
            $"({Spotify.RedirectUri}) en klik je op Add. Vink \"Web API\" aan, ga akkoord en klik op Save. Kopieer daarna de Client ID bovenaan: ik vul hem in en verbind zelf. (Spotify Premium is nodig.)");
        if (announce) Say?.Invoke(steps);
        return steps;
    }

    public void Cancel() { waiting = ""; poll.Stop(); }

    private void Begin(string what, string page)
    {
        waiting = what;
        until = DateTime.Now.AddMinutes(20);
        try { lastSeen = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; } catch { lastSeen = ""; } // (what was there before doesn't count)
        try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"Couldn't open {page}: {e.Message}"); }
        poll.Start();
    }

    private async Task CheckClipboardAsync()
    {
        if (checking) return;
        if (DateTime.Now > until) { Cancel(); return; }
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; } catch { return; } // another app has it right now
        if (text.Length == 0 || text == lastSeen) return;
        lastSeen = text;
        checking = true;
        try
        {
            if (waiting == "groq" && GroqKey().IsMatch(text)) await UseGroqKeyAsync(text);
            else if (waiting == "spotify" && SpotifyId().IsMatch(text)) await UseSpotifyIdAsync(text);
        }
        finally { checking = false; }
    }

    private async Task UseGroqKeyAsync(string key)
    {
        Status?.Invoke("groq", settings.Say("Checking the key…", "Sleutel controleren…"));
        if (!await GroqKeyWorksAsync(key))
        {
            Status?.Invoke("groq", settings.Say("❌ That key doesn't work. Copy it again, or make a new one.", "❌ Die sleutel werkt niet. Kopieer hem opnieuw, of maak een nieuwe."));
            Say?.Invoke(settings.Say("Hmm, that key doesn't work. Try copying it again.", "Hmm, die sleutel werkt niet. Probeer hem nog eens te kopiëren."));
            return;
        }
        Cancel();
        settings.GroqKey = key;
        settings.Save();
        Log.Write("Connect: a Groq key was copied and filled in");
        Filled?.Invoke("GroqKey", key);
        Status?.Invoke("groq", settings.Say("✅ Connected! The key is filled in and saved.", "✅ Verbonden! De sleutel is ingevuld en opgeslagen."));
        Say?.Invoke(settings.Say($"Got it, I'm connected! Press {settings.Hotkey} or say \"hey {settings.Name}\" to talk to me.",
                                 $"Hebbes, ik ben verbonden! Druk op {settings.Hotkey} of zeg \"hé {settings.Name}\" om met me te praten."));
    }

    private static async Task<bool> GroqKeyWorksAsync(string key)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.groq.com/openai/v1/models");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var response = await Http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch { return true; } // no internet right now: take it, it looked right
    }

    private async Task UseSpotifyIdAsync(string id)
    {
        Cancel();
        settings.SpotifyClientId = id;
        settings.SpotifyRefresh = "";
        settings.Save();
        Log.Write("Connect: a Spotify Client ID was copied and filled in");
        Filled?.Invoke("SpotifyClientId", id);
        Status?.Invoke("spotify", settings.Say("Log in in your browser…", "Log in in je browser…"));
        Say?.Invoke(settings.Say("Got the Client ID! Now log in to Spotify in the page that opens and click Agree.",
                                 "Client ID binnen! Log nu in bij Spotify op de pagina die opent en klik op Agree."));
        try
        {
            await Spotify.LoginAsync(settings);
            settings.Save();
            Status?.Invoke("spotify", "✅ " + settings.Say("connected", "verbonden"));
            Say?.Invoke(settings.Say("Spotify is connected! Try \"play some lofi\".", "Spotify is verbonden! Probeer \"speel wat lofi\"."));
        }
        catch (Exception e)
        {
            Log.Write($"Spotify login failed: {e.Message}");
            Status?.Invoke("spotify", "❌ " + e.Message);
            Say?.Invoke(settings.Say("The Spotify login didn't finish. Click \"Connect Spotify\" in my settings to try again.",
                                     "De Spotify-login is niet afgemaakt. Klik op \"Connect Spotify\" in mijn instellingen om het opnieuw te proberen."));
        }
    }

    public void Dispose() => poll.Dispose();
}
