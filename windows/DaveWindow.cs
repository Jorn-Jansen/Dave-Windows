using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DaveWindows;

/// <summary>
/// Dave's own window (Ctrl+Alt+W by default, never on its own): the chat history you can scroll and type in,
/// reminders, memories, screen time, music and what Dave is watching. The page itself is DaveWindow.html.
/// </summary>
public sealed partial class DaveWindow : Form
{
    private readonly Settings settings;
    private readonly Func<string, bool> ask;
    private readonly Action listen, applySettings;
    private readonly Func<bool> busy;
    private readonly Func<string> status;
    private bool openSettingsWhenReady;

    /// <summary>True when the window can't work (no WebView2): then the old settings window is used instead.</summary>
    public bool Broken { get; private set; }
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 15, 23) };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private bool ready;

    /// <param name="ask">Ask a typed question; false when Dave is busy.</param>
    /// <param name="applySettings">Called after the settings were saved (shortcuts, wake word, autostart).</param>
    /// <param name="status">"listening", "thinking", "busy" (e.g. talking) or "" (idle).</param>
    public DaveWindow(Settings settings, Func<string, bool> ask, Action listen, Action applySettings, Func<bool> busy, Func<string> status)
    {
        this.status = status;
        this.settings = settings;
        this.ask = ask;
        this.listen = listen;
        this.applySettings = applySettings;
        this.busy = busy;
        Text = settings.Name;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1000, 700);
        MinimumSize = new Size(380, 340); // small is fine: the page rearranges itself
        BackColor = Color.FromArgb(15, 15, 23);
        Controls.Add(web);
        refresh.Tick += (_, _) => Push();
        ConversationLog.Added += () => { if (IsHandleCreated && Visible) BeginInvoke(Push); };
        Load += async (_, _) => await InitAsync();
    }

    /// <summary>The shortcut: open (and bring to the front), or close when it's already in front.</summary>
    public void Toggle()
    {
        if (Visible && ContainsFocus && WindowState != FormWindowState.Minimized) { Hide(); return; }
        BringUp();
    }

    /// <summary>Open the window with the settings panel on top (tray menu "Settings", or the first start without a key).</summary>
    public void ShowSettings()
    {
        BringUp();
        if (ready) Send(new JsonObject { ["type"] = "openSettings" });
        else openSettingsWhenReady = true;
    }

    private void BringUp()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        WindowList.Focus(Handle); // Windows only lets apps take the focus right after input; this works from a global shortcut too
        Push();
    }

    private async Task InitAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Folder, "WebView2"));
            await web.EnsureCoreWebView2Async(environment);
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
            // Photos, videos and voice messages from chats: shown and played straight from Dave's folder for them
            Directory.CreateDirectory(Social.FilesFolder);
            core.SetVirtualHostNameToFolderMapping("dave-files.example", Social.FilesFolder, CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationCompleted += (_, _) =>
            {
                ready = true;
                Push();
                if (openSettingsWhenReady) { openSettingsWhenReady = false; Send(new JsonObject { ["type"] = "openSettings" }); }
            };
            using var page = typeof(DaveWindow).Assembly.GetManifestResourceStream("DaveWindow.html")!;
            core.NavigateToString(await new StreamReader(page).ReadToEndAsync());
        }
        catch (Exception e)
        {
            Log.Write($"Dave's window couldn't start: {e}");
            Broken = true;
            Controls.Clear();
            Controls.Add(new Label
            {
                Text = "This window needs the Microsoft Edge WebView2 Runtime.\nGet it from go.microsoft.com/fwlink/p/?LinkId=2124703",
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Segoe UI", 12f),
            });
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        refresh.Enabled = Visible;
    }

    /// <summary>Closing only hides it (whichever way it's closed), so it opens instantly next time. Only Windows shutting down really closes it.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.ApplicationExitCall)) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int)); // dark title bar
    }

    // --- Page -> Dave ---

    private void OnMessage(string json)
    {
        try
        {
            var m = JsonNode.Parse(json)!.AsObject();
            string Str(string key) => m[key]?.ToString() ?? "";
            switch (Str("type"))
            {
                case "ready": ready = true; break;
                case "jsError": Log.Write($"Window page error: {Str("text")}"); return;
                case "ask":
                    if (Str("text").Trim().Length > 0 && !ask(Str("text").Trim()))
                        Send(new JsonObject { ["type"] = "toast", ["text"] = settings.Say("I'm still busy, one moment.", "Ik ben nog bezig, momentje.") });
                    break;
                case "listen": listen(); break;
                case var social when social.StartsWith("social"):
                    _ = OnSocialMessageAsync(Str("type"), Str("code"), Str("text"), m);
                    return;
                case "getSettings" or "voices" or "azureVoices" or "preview" or "spotifyConnect" or "saveSettings":
                    _ = OnSettingsMessageAsync(Str("type"), m);
                    return;
                default: ApplyAction(settings, m); break;
            }
            Push();
        }
        catch (Exception e) { Log.Write($"Window message failed: {e.Message}"); }
    }

    /// <summary>
    /// Changes made from the window's tabs, or from the iPhone app's: delete a reminder or memory, add a memory, allow a
    /// song again, stop watching, quiet mode. False when [m] isn't one of these.
    /// </summary>
    public static bool ApplyAction(Settings settings, JsonObject m)
    {
        string Str(string key) => m[key]?.ToString() ?? "";
        switch (Str("type"))
        {
            case "deleteReminder":
                if (long.TryParse(Str("id"), out var id)) { settings.Reminders.RemoveAll(r => r.Id == id); settings.Save(); }
                return true;
            case "deleteMemory":
                settings.Memories.Remove(Str("text"));
                settings.Save();
                return true;
            case "addMemory":
                if (Str("text").Trim().Length > 0) { settings.Memories.Add(Str("text").Trim()); settings.Save(); }
                return true;
            case "undislike":
                settings.DislikedSongs.RemoveAll(d => d.Title == Str("title") && d.Artist == Str("artist"));
                settings.Save();
                return true;
            case "stopWatching": Watchers.CancelAll(); return true;
            case "quiet":
                settings.QuietUntil = m["on"]?.GetValue<bool>() == true ? DateTime.MaxValue : DateTime.MinValue;
                settings.Save();
                return true;
            default: return false;
        }
    }

    /// <summary>
    /// The Social panel: add a friend, accept or decline, remove, send a message, mark a chat read, copy your code, send a
    /// file (picked or dropped) or a voice message, react, "typing", open a file.
    /// </summary>
    private async Task OnSocialMessageAsync(string type, string code, string text, JsonObject m)
    {
        string Str(string key) => m[key]?.ToString() ?? "";
        try
        {
            string? note = null;
            switch (type)
            {
                case "socialCopy":
                    Clipboard.SetText(Social.MyCode(settings));
                    note = settings.Say("Your friend code is copied. Send it to a friend!", "Je vriendcode is gekopieerd. Stuur hem naar een vriend!");
                    break;
                case "socialAttach":
                    using (var pick = new OpenFileDialog { Multiselect = true, Title = settings.Say("Send to your friend", "Sturen naar je vriend") })
                    {
                        if (pick.ShowDialog(this) != DialogResult.OK) return;
                        foreach (var file in pick.FileNames)
                        {
                            if (new FileInfo(file).Length > 200_000_000) { note = settings.Say("That's too big to send (200 MB at most).", "Dat is te groot om te versturen (hooguit 200 MB)."); continue; }
                            Send(new JsonObject { ["type"] = "socialNote", ["text"] = "⏳ " + settings.Say("Sending ", "Versturen: ") + Path.GetFileName(file) });
                            note = await Social.SendFileAsync(settings, code, await File.ReadAllBytesAsync(file), Path.GetFileName(file), reply: Str("reply")) ?? note;
                        }
                    }
                    break;
                case "socialDrop": // dragged into the chat
                    Send(new JsonObject { ["type"] = "socialNote", ["text"] = "⏳ " + settings.Say("Sending ", "Versturen: ") + text });
                    note = await Social.SendFileAsync(settings, code, Convert.FromBase64String(m["data"]?.ToString() ?? ""), text, reply: Str("reply"));
                    break;
                case "socialRecord":
                    note = await RecordVoiceMessageAsync(code, Str("reply"));
                    break;
                case "socialReact": await Social.ReactAsync(settings, code, m["id"]?.ToString() ?? "", text); break;
                case "socialEdit": note = await Social.EditAsync(settings, code, Str("id"), text); break;
                case "socialUnsend": note = await Social.UnsendAsync(settings, code, Str("id")); break;
                case "socialTranscribe": await Social.TranscribeAsync(settings, code, Str("id")); return;
                case "socialTranslate": await Social.TranslateAsync(settings, code, Str("id")); return;
                case "socialSchedule":
                    if (DateTimeOffset.TryParse(Str("at"), out var at)) note = Social.Schedule(settings, code, text, at, Str("reply"));
                    break;
                case "socialUnschedule": Social.Unschedule(settings, Str("id")); break;
                case "socialTyping": await Social.TypingAsync(settings, code); return;
                case "socialOpenFile":
                    // Only Dave's own copies of chat files (from the address the page shows them at)
                    var path = Path.Combine(Social.FilesFolder, Path.GetFileName(Uri.UnescapeDataString(text)));
                    if (File.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                case "socialOpenLink":
                    // A link preview: only web pages, in the browser
                    if (Uri.TryCreate(text, UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
                    return;
                default:
                    note = await SocialActionAsync(settings, type, code, text, Str("reply"));
                    break;
            }
            if (note != null) Send(new JsonObject { ["type"] = "socialNote", ["text"] = note });
            Push();
        }
        catch (Exception e) { Log.Write($"Social action failed: {e.Message}"); }
    }

    /// <summary>The 🎤 button in a chat: records until you stop talking (up to a minute), then sends it as a voice message.</summary>
    private async Task<string?> RecordVoiceMessageAsync(string code, string reply)
    {
        if (busy()) return settings.Say("I'm busy right now; try again in a moment.", "Ik ben nu bezig; probeer het zo nog eens.");
        Send(new JsonObject { ["type"] = "socialRecording", ["on"] = true });
        try
        {
            var sound = ThemeSounds.Play(settings, ThemeSounds.Cue.Listen);
            await Task.Delay(Math.Max(150, (int)sound.TotalMilliseconds)); // so the sound isn't recorded
            var wav = await Recorder.RecordAsync(CancellationToken.None, waitMs: 6000, maxMs: 60_000);
            ThemeSounds.Play(settings, ThemeSounds.Cue.Heard);
            if (wav == null) return settings.Say("I didn't hear anything.", "Ik hoorde niets.");
            return await Social.SendFileAsync(settings, code, wav, "Voice message.wav", "voice", reply);
        }
        finally { Send(new JsonObject { ["type"] = "socialRecording", ["on"] = false }); }
    }

    /// <summary>A change in the Social tab (here or in the iPhone app's): add, accept, remove, send, mark read. Returns what to tell the user, if anything.</summary>
    public static async Task<string?> SocialActionAsync(Settings settings, string type, string code, string text, string reply = "")
    {
        switch (type)
        {
            case "socialAdd": return await Social.AddAsync(settings, text);
            case "socialAccept": await Social.AcceptAsync(settings, code); return null;
            case "socialRemove": await Social.RemoveAsync(settings, code); return null;
            case "socialSend": return await Social.SendAsync(settings, code, text, reply);
            case "socialRead": Social.MarkRead(settings, code); return null;
            default: return null;
        }
    }

    // --- Dave -> page ---

    /// <summary>A short note at the bottom of the window (a friend's message while you're in it), and the tabs updated.</summary>
    public void Notify(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Notify(text)); return; }
        Send(new JsonObject { ["type"] = "toast", ["text"] = text });
        Push();
    }

    private void Send(JsonObject message)
    {
        if (ready && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(message.ToJsonString());
    }

    /// <summary>Everything the page shows, sent as one message.</summary>
    public async void Push()
    {
        if (!ready || !Visible) return;
        try { Send(await StateAsync(settings, busy(), status())); }
        catch (Exception e) { Log.Write($"Window update failed: {e}"); }
    }

    /// <summary>Everything the window's tabs show (also what the iPhone app shows in its tabs).</summary>
    public static async Task<JsonObject> StateAsync(Settings settings, bool busy, string status)
    {
        var culture = CultureInfo.GetCultureInfo(settings.IsDutch ? "nl-NL" : "en-GB");
        string When(DateTimeOffset at) =>
            at.Date == DateTime.Today ? settings.Say("today ", "vandaag ") + at.ToString("HH:mm")
            : at.Date == DateTime.Today.AddDays(1) ? settings.Say("tomorrow ", "morgen ") + at.ToString("HH:mm")
            : at.ToString("ddd d MMM HH:mm", culture);
        JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonNode> map) => new(items.Select(map).ToArray());

        var playing = await MediaSession.NowPlayingAsync(settings);
        var weekStart = DateTime.Today.AddDays(-6);
        return new JsonObject
        {
            ["type"] = "state",
            ["name"] = settings.Name,
            ["version"] = Updater.Display,
            ["dutch"] = settings.IsDutch,
            ["busy"] = busy,
            ["status"] = status,
            ["quiet"] = settings.IsQuiet,
            ["hotkey"] = settings.Hotkey,
            ["addresses"] = new JsonArray(PhoneLink.Addresses().Select(a => (JsonNode)a).ToArray()), // for the iPhone app (home and Tailscale)
            ["ntfyTopic"] = settings.PhoneNotify ? PhoneNotify.TopicFor(settings) : "", // the phone's notifications channel (ntfy)
            ["theme"] = ThemeJson(Themes.Get(settings.Theme)),
            ["social"] = Social.StateJson(settings), // friends, requests and chats
            ["chat"] = Array(ConversationLog.Recent(150), e => new JsonObject
            {
                ["at"] = e.At.ToString(e.At.Date == DateTime.Today ? "HH:mm" : "ddd d MMM HH:mm", culture),
                ["you"] = e.You,
                ["dave"] = e.Dave,
            }),
            ["reminders"] = Array(settings.Reminders.OrderBy(r => r.At), r => new JsonObject
            {
                ["id"] = r.Id.ToString(),
                ["message"] = (r.Action.Length > 0 ? "⚡ " : "") + r.Message, // ⚡ = something Dave will do, not say
                ["repeat"] = r.Repeat.Length > 0 ? Reminders.DescribeRepeat(r, settings.IsDutch) : settings.Say("once", "eenmalig"),
                ["next"] = When(r.At),
            }),
            ["memories"] = Array(settings.Memories, m => JsonValue.Create(m)!),
            ["watching"] = Array(Watchers.List(), w => JsonValue.Create(w)!),
            ["screen"] = new JsonObject
            {
                ["today"] = Array(ScreenTime.AppTotals(DateTime.Today, DateTime.Today).Take(12), a => new JsonObject { ["app"] = a.app, ["seconds"] = a.seconds }),
                ["week"] = Array(ScreenTime.AppTotals(weekStart, DateTime.Today).Take(12), a => new JsonObject { ["app"] = a.app, ["seconds"] = a.seconds }),
                ["days"] = Array(ScreenTime.DayTotals(weekStart, DateTime.Today), d => new JsonObject
                {
                    ["day"] = d.day.ToString("ddd", culture),
                    ["seconds"] = d.seconds,
                }),
            },
            ["music"] = new JsonObject
            {
                ["now"] = playing == null ? null : new JsonObject { ["title"] = playing.Title, ["artist"] = playing.Artist },
                ["history"] = Array(MusicWatcher.Recent(30), p => new JsonObject { ["title"] = p.Title, ["artist"] = p.Artist, ["at"] = p.At.ToString("HH:mm") }),
                ["disliked"] = Array(settings.DislikedSongs, d => new JsonObject { ["title"] = d.Title, ["artist"] = d.Artist }),
            },
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refresh.Dispose(); web.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
