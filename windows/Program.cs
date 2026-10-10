using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DaveWindows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Explorer → Send to → My iPhone (Dave): send the files to the phone, show a short "sent" and quit (also while Dave runs)
        if (args.Length >= 1 && args[0] == "--send-to-phone")
        {
            SendToPhone(args.Skip(1).ToArray());
            return;
        }
        // Testing: Dave.exe --ask "question" answers one typed question and quits.
        var test = args.Length == 2 && args[0] == "--ask" ? args[1] : null;
        using var single = new Mutex(true, test == null ? "DaveWindows-single-instance" : "DaveWindows-test", out var first);
        if (!first) return; // Dave is already running (tray icon)
        ApplicationConfiguration.Initialize();

        // Log anything that goes wrong instead of vanishing silently.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"ERROR (screen thread) while '{Watchdog.Step}': {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"CRASH while '{Watchdog.Step}': {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write($"Background error: {e.Exception}"); e.SetObserved(); };

        var app = new DaveApp(test);
        Watchdog.Start();
        Application.Run(app);
    }

    private static void SendToPhone(string[] files)
    {
        ApplicationConfiguration.Initialize();
        var settings = Settings.Load();
        var problems = new List<string>();
        var sent = 0;
        foreach (var file in files.Where(File.Exists))
        {
            var why = PhoneNotify.SendFileAsync(settings, File.ReadAllBytes(file), Path.GetFileName(file)).GetAwaiter().GetResult();
            if (why == null) sent++; else problems.Add(why);
        }
        using var tray = new NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath), Visible = true };
        tray.ShowBalloonTip(4000, settings.Name, problems.Count > 0 ? problems[0]
            : settings.Say($"Sent {sent} {(sent == 1 ? "file" : "files")} to your phone.", $"{sent} {(sent == 1 ? "bestand" : "bestanden")} naar je telefoon gestuurd."),
            problems.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        Thread.Sleep(4500);
    }
}

/// <summary>
/// Notices when Dave's screen thread gets stuck (Windows would call him "not responding")
/// and writes down what he was doing at that moment.
/// </summary>
public static class Watchdog
{
    /// <summary>What Dave is doing right now, e.g. "listening" or "asking the AI".</summary>
    public static volatile string Step = "idle";
    private static Control? ui;

    public static void Start()
    {
        ui = new Control();
        _ = ui.Handle; // bound to the screen thread
        new Thread(Watch) { IsBackground = true, Name = "Dave watchdog" }.Start();
    }

    private static void Watch()
    {
        while (true)
        {
            Thread.Sleep(1000);
            var answered = new ManualResetEventSlim();
            try { ui!.BeginInvoke(() => answered.Set()); } catch { return; }
            if (answered.Wait(3000)) continue;
            var stuckSince = DateTime.Now.AddSeconds(-3);
            Log.Write($"FROZEN: the screen thread stopped responding while '{Step}'");
            answered.Wait();
            Log.Write($"Unfrozen after {(DateTime.Now - stuckSince).TotalSeconds:F0} s");
        }
    }
}

/// <summary>Tray icon, shortcut, wake word and reminders; runs the conversations.</summary>
public class DaveApp : ApplicationContext
{
    private readonly Settings settings = Settings.Load();
    private readonly Bubble bubble = new();
    private readonly NotifyIcon tray;
    private readonly HotkeyWindow hotkey;
    private HotkeyWindow? windowHotkey;
    private DaveWindow? window; // made the first time you open it
    private readonly System.Windows.Forms.Timer reminderTimer = new() { Interval = 1_000 }; // every second, so timers go off on time
    private WakeWord? wakeWord;
    private PhoneLink? phoneLink; // Dave on the iPhone talking to this Dave (when turned on)
    private bool remote; // answering the iPhone: no speaking, ducking or listening here
    private bool firewallChecked;
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 6 * 60 * 60 * 1000 }; // look for a new version every 6 hours
    private Updater.Release? pendingInstall; // asked for by voice: installed once Dave has finished talking
    private readonly Queue<string> headsUps = new(); // things Dave was watching for that happened; said once he's free
    private readonly Queue<string> dueActions = new(); // "lock my PC in 10 minutes" when it's time; done once he's free

    private CancellationTokenSource? session;          // the conversation or quiz that's running
    private CancellationTokenSource? snippetCut;       // quiz: shortcut during a snippet stops it early
    private bool quizRunning;

    private string T(string english, string dutch) => settings.Say(english, dutch);

    public DaveApp(string? testQuestion = null)
    {
        Ducker.RestoreAfterCrash(); // in case Dave was closed while other sound was turned down
        Palette.Use(settings.Theme);
        Watchers.Triggered += message =>
        {
            bubble.BeginInvoke(() => headsUps.Enqueue(message)); // said at the next tick, when Dave is free
            PhoneNotify.Send(settings, "👀 " + settings.Name, message); // and on the phone, when you're not at the PC
        };
        ConversationLog.Added += () => { thinking = false; bubble.Thinking = false; }; // the answer is there: no more "thinking" dots or comets
        bubble.Suppressed = WindowOpen;
        if (testQuestion == null)
        {
            MusicWatcher.Start(settings); // song history, and skipping songs you don't like
            Notifications.Start(); // "what did I miss?" also covers notifications you already dismissed
            ScreenTime.Start();
        }
        if (testQuestion != null)
        {
            _ = bubble.Handle;
            tray = new NotifyIcon();
            hotkey = new HotkeyWindow(() => { });
            _ = TestAsync(testQuestion);
            return;
        }

        _ = bubble.Handle; // create the window on this (UI) thread
        bubble.Clicked += Cancel;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ask Dave", null, (_, _) => Trigger());
        menu.Items.Add($"Open {settings.Name} ({settings.WindowHotkey})", null, (_, _) => ToggleWindow());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Information, // Dave's own icon, built into Dave.exe
            Text = settings.Name,
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Trigger(); };

        hotkey = new HotkeyWindow(Trigger);
        windowHotkey = new HotkeyWindow(ToggleWindow);
        ApplySettings();

        reminderTimer.Tick += (_, _) => AnnounceDueReminders();
        reminderTimer.Start();
        AnnounceDueReminders(); // ones that came due while the PC was off

        updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
        updateTimer.Start();
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => CheckForUpdatesAsync(manual: false),
            TaskScheduler.FromCurrentSynchronizationContext()); // first look a minute after starting, when the PC has settled

        if (!settings.HasAiKey) OpenSettings();
        else tray.ShowBalloonTip(3000, $"{settings.Name} is ready", T($"Press {settings.Hotkey} to talk to me.", $"Druk op {settings.Hotkey} om met me te praten."), ToolTipIcon.None);
    }

    private async Task TestAsync(string question)
    {
        if (question == "demo") // shows listening, thinking and talking, without talking to anyone (for checking the look)
        {
            if (Environment.GetEnvironmentVariable("DAVE_DEMO_THEME") is { Length: > 0 } demoTheme) Palette.Use(demoTheme); // try a theme without changing the settings
            bubble.ShowListening();
            for (int i = 0; i < 50; i++) { Recorder.Level = (float)Math.Abs(Math.Sin(i / 3.0)) * 0.85f; await Task.Delay(100); }
            Recorder.Level = 0;
            bubble.Thinking = true;
            bubble.ShowText("“What's the weather tomorrow?”\nThinking…");
            await Task.Delay(3500);
            bubble.Thinking = false;
            bubble.ShowText("Tomorrow it'll be sunny and 21 degrees in Utrecht, with a light breeze in the afternoon.");
            for (int i = 0; i < 50; i++) { Speaker.Level = (float)(0.35 + 0.35 * Math.Sin(i / 1.7)); await Task.Delay(100); }
            Speaker.Level = 0;
            bubble.HideAfter(1);
            await Task.Delay(1500);
            ExitThread();
            return;
        }
        Log.Write($"TEST ask: {question}");
        await RunSessionAsync(question);
        // For testing things that happen later (watchers): DAVE_TEST_LINGER=seconds keeps the test running that long.
        if (int.TryParse(Environment.GetEnvironmentVariable("DAVE_TEST_LINGER"), out var linger))
            for (int i = 0; i < linger; i++) { await Task.Delay(1000); AnnounceDueReminders(); }
        await Task.Delay(1600);
        ExitThread();
    }

    /// <summary>(Re)apply shortcut, wake word and autostart after the settings change.</summary>
    private void ApplySettings()
    {
        Palette.Use(settings.Theme);
        if (!hotkey.Register(settings.Hotkey))
            tray.ShowBalloonTip(4000, "Dave", $"Shortcut {settings.Hotkey} is already used by another program. Pick another in Settings.", ToolTipIcon.Warning);
        if (windowHotkey != null && settings.WindowHotkey.Trim().Length > 0 && !windowHotkey.Register(settings.WindowHotkey))
            tray.ShowBalloonTip(4000, "Dave", $"Shortcut {settings.WindowHotkey} (Dave's window) is already used by another program. Pick another in Settings.", ToolTipIcon.Warning);

        wakeWord?.Dispose();
        wakeWord = null;
        if (settings.WakeWord)
        {
            try { wakeWord = new WakeWord(settings, () => bubble.BeginInvoke(Trigger)); }
            catch (Exception e) { Log.Write($"Wake word failed: {e}"); }
        }

        PhoneNotify.UpdateSendToShortcut(settings); // Explorer → Send to → My iPhone (Dave), while notifications on the phone are on

        phoneLink?.Dispose();
        phoneLink = null;
        if (settings.PhoneEnabled)
        {
            PhoneLink.CodeFor(settings);
            if (!firewallChecked)
            {
                firewallChecked = true; // once per start: not a Windows question every time the settings are saved
                _ = Task.Run(() =>
                {
                    if (!PhoneLink.AllowThroughFirewall())
                        bubble.BeginInvoke(() => tray.ShowBalloonTip(6000, "Dave", T(
                            "Windows' firewall may block the iPhone app. Allow Dave in Windows Security → Firewall → Allow an app.",
                            "De firewall van Windows blokkeert de iPhone-app misschien. Sta Dave toe in Windows-beveiliging → Firewall → App toestaan."),
                            ToolTipIcon.Warning));
                });
            }
            try
            {
                phoneLink = new PhoneLink(settings, question => (Task<string>)bubble.Invoke(() => AskFromPhoneAsync(question)),
                    (name, args) => (Task<string>)bubble.Invoke(() => CommandFromPhoneAsync(name, args)),
                    change => (Task<System.Text.Json.Nodes.JsonObject>)bubble.Invoke(() => StateForPhoneAsync(change)),
                    item => (Task<string>)bubble.Invoke(() => ReceiveFromPhoneAsync(item)));
            }
            catch (Exception e)
            {
                Log.Write($"iPhone link couldn't start: {e.Message}");
                tray.ShowBalloonTip(4000, "Dave", T($"The iPhone link couldn't start: port {PhoneLink.Port} is in use.", $"De iPhone-koppeling kon niet starten: poort {PhoneLink.Port} is bezet."), ToolTipIcon.Warning);
            }
        }

        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (settings.StartWithWindows) run?.SetValue("Dave", $"\"{Application.ExecutablePath}\"");
        else run?.DeleteValue("Dave", throwOnMissingValue: false);
    }

    /// <summary>The settings: as a panel in Dave's window; the old settings window only if that window can't run.</summary>
    private void OpenSettings()
    {
        if (window is not { Broken: true })
        {
            Window().ShowSettings();
            return;
        }
        using var form = new SettingsForm(settings, AskTyped);
        if (form.ShowDialog() == DialogResult.OK) ApplySettings();
    }

    /// <summary>Dave's window (chat history, reminders, screen time…): only opens with its shortcut or from the tray menu.</summary>
    private void ToggleWindow() => Window().Toggle();

    private DaveWindow Window() => window is { IsDisposed: false } ? window : window = new DaveWindow(settings, // a new one if it was closed for real
        ask: question => { if (session != null) return false; AskTyped(question); return true; },
        listen: () => { if (session == null) Trigger(); },
        applySettings: ApplySettings,
        busy: () => session != null,
        status: Status);

    /// <summary>What Dave is doing, for his window and the iPhone app: "listening", "thinking", "busy" (e.g. talking) or "" (idle).</summary>
    private string Status() => session == null ? "" : Watchdog.Step == "listening" ? "listening" : thinking ? "thinking" : "busy";

    /// <summary>Dave is working out an answer (not listening, not talking yet): the window shows its three dots only then.</summary>
    private bool thinking;

    private void SetThinking(bool on)
    {
        thinking = on;
        bubble.Thinking = on; // the comets and the sheen on the bubble
        window?.Push();
    }

    /// <summary>
    /// While you're looking at Dave's window (it's the window in front), the bubble and glow stay away: the window shows it all.
    /// Minimised, behind other windows (a game) or under "Show desktop", the bubble shows as usual.
    /// </summary>
    private bool WindowOpen() => window is { Visible: true, IsHandleCreated: true } w && w.WindowState != FormWindowState.Minimized
        && WindowList.Foreground() == w.Handle;

    // --- Updates ---

    /// <summary>
    /// Look for a new version; install it right away (automatic) or after asking (manual, from the tray menu).
    /// Only for a Dave installed with DaveSetup.exe.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (!manual && !settings.AutoUpdate) return;
        if (!Updater.IsInstalled)
        {
            if (manual) tray.ShowBalloonTip(5000, settings.Name, T("This Dave was built from the source code. To update him, run Build Dave.bat.",
                "Deze Dave is zelf gebouwd. Update hem met Build Dave.bat."), ToolTipIcon.Info);
            return;
        }
        Updater.Release? release;
        try { release = await Updater.CheckAsync(); }
        catch (Exception e)
        {
            Log.Write($"Update check failed: {e.Message}");
            if (manual) tray.ShowBalloonTip(4000, settings.Name, T("I couldn't check for updates right now.", "Ik kon nu niet checken op updates."), ToolTipIcon.Warning);
            return;
        }
        if (release == null)
        {
            if (manual) tray.ShowBalloonTip(3000, settings.Name, T($"You have the newest version ({Updater.Display}).",
                $"Je hebt de nieuwste versie ({Updater.Display})."), ToolTipIcon.None);
            return;
        }
        Log.Write($"Update available: {release.Version}");
        if (manual && MessageBox.Show(T($"Version {release.Version} is available (you have {Updater.Display}). Update now?",
                $"Versie {release.Version} is beschikbaar (je hebt {Updater.Display}). Nu updaten?"),
                settings.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        while (session != null) await Task.Delay(TimeSpan.FromSeconds(20)); // never in the middle of a conversation
        await InstallUpdateAsync(release);
    }

    /// <summary>"Update yourself": what to say. When there's a new version, it's installed after Dave has said so.</summary>
    private async Task<string> UpdateOnRequestAsync()
    {
        var current = Updater.Display;
        if (!Updater.IsInstalled)
            return T($"I'm version {current}, built from the source code. To update me, run Build Dave dot bat.",
                     $"Ik ben versie {current}, zelf gebouwd van de broncode. Update me met Build Dave punt bat.");
        try
        {
            var release = await Updater.CheckAsync();
            if (release == null) return T($"I'm up to date: version {current}.", $"Ik ben up-to-date: versie {current}.");
            pendingInstall = release;
            return T($"Version {release.Version} is out. I'm updating now, I'll be right back.",
                     $"Versie {release.Version} is uit. Ik update nu, ik ben zo terug.");
        }
        catch (Exception e)
        {
            Log.Write($"Update check failed: {e.Message}");
            return T("I couldn't check for updates right now.", "Ik kon nu niet checken op updates.");
        }
    }

    private async Task InstallUpdateAsync(Updater.Release release)
    {
        tray.ShowBalloonTip(4000, settings.Name, T($"Updating to version {release.Version}… I'll be back in a moment.",
            $"Updaten naar versie {release.Version}… Ik ben zo terug."), ToolTipIcon.None);
        try
        {
            await Updater.InstallAsync(release);
            Quit(); // the installer replaces the files and starts the new Dave
        }
        catch (Exception e)
        {
            Log.Write($"Update failed: {e}");
            tray.ShowBalloonTip(4000, settings.Name, T("The update didn't work. I'll try again later.", "De update lukte niet. Ik probeer het later opnieuw."), ToolTipIcon.Warning);
        }
    }

    private void Quit()
    {
        Cancel();
        ScreenTime.Flush();
        wakeWord?.Dispose();
        phoneLink?.Dispose();
        hotkey.Dispose();
        windowHotkey?.Dispose();
        window?.Dispose();
        tray.Visible = false;
        ExitThread();
    }

    // --- Starting and stopping ---

    /// <summary>Shortcut / "Hey Dave" / tray click: start talking, or cut a quiz snippet short, or cancel.</summary>
    public void Trigger()
    {
        if (quizRunning)
        {
            snippetCut?.Cancel(); // "I know it!": stop the snippet and answer now
            return;
        }
        if (session != null) { Cancel(); return; }
        _ = RunSessionAsync(typed: null);
    }

    /// <summary>
    /// A question from Dave on the iPhone ("pause the music", "lock my PC"): done here like a typed question, but nothing
    /// is said here; the answer (what would have been said) goes back to the phone.
    /// </summary>
    private async Task<string> AskFromPhoneAsync(string question)
    {
        if (session != null) return T("I'm busy on the PC right now. Try again in a moment.", "Ik ben nu bezig op de pc. Probeer het zo nog eens.");
        string? answer = null;
        void Capture() => answer = ConversationLog.Recent(1).LastOrDefault()?.Dave;
        ConversationLog.Added += Capture;
        remote = true;
        Speaker.Silent = true;
        try { await RunSessionAsync(question); }
        finally
        {
            remote = false;
            Speaker.Silent = false;
            ConversationLog.Added -= Capture;
        }
        // Commands are logged as "(media_control) ⏸ Paused": the phone gets just the outcome
        answer = System.Text.RegularExpressions.Regex.Replace(answer ?? "", @"^\([a-z_ ]+\)\s*", "").Trim();
        return answer.Length > 0 ? answer : T("Done.", "Klaar.");
    }

    /// <summary>
    /// A command the iPhone's AI already chose ("media_control pause", "lock_pc"): carried out right away, without asking
    /// the AI here too. Quietly: only the bubble shows it, for a moment.
    /// </summary>
    private async Task<string> CommandFromPhoneAsync(string name, System.Text.Json.Nodes.JsonObject args)
    {
        if (session != null && name != "media_control") return T("I'm busy on the PC right now. Try again in a moment.", "Ik ben nu bezig op de pc. Probeer het zo nog eens.");
        try
        {
            var outcome = await Commands.RunAsync(settings, name, args);
            ConversationLog.Add($"📱 ({name}) {args.ToJsonString()}", outcome.Text);
            bubble.ShowText("📱 " + outcome.Text);
            bubble.HideAfter(2500);
            return outcome.Text;
        }
        catch (Exception e)
        {
            Log.Write($"iPhone command {name} failed: {e.Message}");
            return e is Groq.GroqException or Spotify.SpotifyException ? e.Message : T("That didn't work on the PC.", "Dat lukte niet op de pc.");
        }
    }

    /// <summary>
    /// "Send this to my PC" from the iPhone: a link opens in the browser, text goes on the clipboard, a photo or file is
    /// saved in Downloads\From iPhone. Only the bubble shows it, so nothing pops up over a game.
    /// </summary>
    private async Task<string> ReceiveFromPhoneAsync(System.Text.Json.Nodes.JsonObject item)
    {
        var kind = item["kind"]?.ToString() ?? "";
        try
        {
            string done;
            switch (kind)
            {
                case "url":
                    var url = item["text"]?.ToString() ?? "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        return T("That's not a web address.", "Dat is geen webadres.");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
                    done = T($"Opened {uri.Host} on your PC.", $"{uri.Host} geopend op je pc.");
                    break;
                case "text":
                    Clipboard.SetText(item["text"]?.ToString() ?? "");
                    done = T("It's on your PC's clipboard: paste it with Ctrl+V.", "Het staat op het klembord van je pc: plak het met Ctrl+V.");
                    break;
                case "file":
                    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "From iPhone");
                    Directory.CreateDirectory(folder);
                    var name = string.Concat((item["name"]?.ToString() is { Length: > 0 } n ? Path.GetFileName(n) : "file").Split(Path.GetInvalidFileNameChars()));
                    var file = Path.Combine(folder, name);
                    for (int i = 2; File.Exists(file); i++) file = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
                    await File.WriteAllBytesAsync(file, Convert.FromBase64String(item["data"]?.ToString() ?? ""));
                    done = T($"Saved {Path.GetFileName(file)} on your PC, in Downloads\\From iPhone.", $"{Path.GetFileName(file)} opgeslagen op je pc, in Downloads\\From iPhone.");
                    break;
                default:
                    return T("I don't know what to do with that.", "Ik weet niet wat ik daarmee moet.");
            }
            bubble.ShowText("📱 " + done);
            bubble.HideAfter(3000);
            ConversationLog.Add($"📱 (sent from the iPhone: {kind})", done);
            await Task.CompletedTask;
            return done;
        }
        catch (Exception e)
        {
            Log.Write($"Receiving from the iPhone failed: {e.Message}");
            return T("That didn't arrive properly on the PC.", "Dat kwam niet goed aan op de pc.");
        }
    }

    /// <summary>The iPhone app's tabs: apply its change (if any, e.g. delete a reminder), then what the tabs show, like Dave's window.</summary>
    private async Task<System.Text.Json.Nodes.JsonObject> StateForPhoneAsync(System.Text.Json.Nodes.JsonObject? change)
    {
        if (change != null && DaveWindow.ApplyAction(settings, change)) window?.Push();
        return await DaveWindow.StateAsync(settings, session != null, Status());
    }

    /// <summary>Ask a typed question (from the settings window).</summary>
    public void AskTyped(string question)
    {
        if (session == null && !string.IsNullOrWhiteSpace(question)) _ = RunSessionAsync(question);
    }

    private void Cancel()
    {
        session?.Cancel();
        Speaker.Stop();
    }

    private async Task RunSessionAsync(string? typed, string? announcement = null, bool isReminder = true)
    {
        if (!settings.HasAiKey) { OpenSettings(); return; }
        session = new CancellationTokenSource();
        var cancel = session.Token;
        if (wakeWord != null) wakeWord.Paused = true;
        try
        {
            if (announcement != null)
            {
                using (new Ducker(settings.DuckTo))
                {
                    Speaker.Beep();
                    var text = isReminder ? T("Reminder: ", "Herinnering: ") + announcement : announcement;
                    bubble.ShowText((isReminder ? "⏰ " : "👀 ") + text, spoken: true);
                    ConversationLog.Add(isReminder ? "(reminder went off)" : "(something you asked me to watch for happened)", text);
                    await Speaker.SpeakAsync(settings, text, settings.Language, cancel);
                }
                return;
            }
            await ConversationAsync(typed, cancel);
        }
        catch (OperationCanceledException) { /* cancelled by the user */ }
        catch (Exception e)
        {
            Log.Write($"Session failed: {e}");
            var message = e is Groq.GroqException or Spotify.SpotifyException ? e.Message : T("Something went wrong.", "Er ging iets mis.");
            bubble.ShowText(message, spoken: true);
            try { await Speaker.SpeakAsync(settings, message, settings.Language); } catch { }
        }
        finally
        {
            bubble.HideAfter(1500);
            session = null;
            Watchdog.Step = "idle";
            SetThinking(false);
            if (wakeWord != null) wakeWord.Paused = false;
        }
    }

    // --- Conversation ---

    private async Task ConversationAsync(string? typed, CancellationToken cancel)
    {
        var followUp = false;
        while (true)
        {
            var locationTask = PcLocation.DescribeAsync(); // look it up while you talk
            string text, language;
            Watchdog.Step = "turning other sound down";
            using (var duck = new Ducker(remote ? 1 : settings.DuckTo)) // (from the iPhone: nothing to turn down for)
            {
                if (typed != null)
                {
                    text = typed;
                    language = LanguageOf(null, typed);
                    typed = null;
                }
                else
                {
                    var heard = await ListenAsync(cancel, followUp);
                    if (heard == null) return; // nothing said
                    (text, language) = heard.Value;
                }

                // Leaving quiet mode never depends on the AI understanding it: these phrases always work.
                if (settings.IsQuiet && System.Text.RegularExpressions.Regex.IsMatch(text.ToLowerInvariant(),
                        @"(talk|speak) again|you can (talk|speak)|stop being quiet|quiet mode off|unmute|(weer|terug) (praten|spreken)|mag weer|stille modus uit|niet meer stil"))
                {
                    settings.QuietUntil = DateTime.MinValue;
                    settings.Save();
                    var back = T("Okay, I'll talk again.", "Oké, ik praat weer.");
                    Log.Write("Quiet mode off");
                    ConversationLog.Add(text, back);
                    bubble.ShowText(back, spoken: true);
                    await Speaker.SpeakAsync(settings, back, language, cancel);
                    return;
                }

                bubble.ShowText($"“{text}”\n" + T("Thinking…", "Even denken…"));
                Watchdog.Step = "asking the AI";
                SetThinking(true); // until the answer is in the chat (ConversationLog.Added)
                var result = await Assistant.AskAsync(settings, text, language, await locationTask);

                if (result is Assistant.Speak speak)
                {
                    Log.Write($"Says ({language}): {speak.Text}");
                    ConversationLog.Add(text, speak.Text);
                    bubble.ShowText(speak.Text, spoken: true);
                    Watchdog.Step = "speaking";
                    await Speaker.SpeakAsync(settings, speak.Text, language, cancel);
                    if (remote || !speak.Text.TrimEnd().EndsWith('?')) return; // (the iPhone asks its own follow-ups)
                    followUp = true; // Dave asked something back: keep listening
                    continue;
                }

                var command = (Assistant.Command)result;
                // "I copied a picture, extract the text and copy it": the AI sometimes picks "copy this text" with a made-up
                // placeholder ("<extracted text>") instead of looking at what was copied. Then do the clipboard task properly.
                if (command.Name == "copy_to_clipboard")
                {
                    var toCopy = command.Args["text"]?.ToString()?.Trim() ?? "";
                    var placeholder = toCopy.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(toCopy, @"^[<\[{(].*[>\]})]$");
                    var aboutWhatTheyCopied = System.Text.RegularExpressions.Regex.IsMatch(text.ToLowerInvariant(), @"\b(i|what i|i've|ive) copied\b|gekopieerd");
                    if (placeholder || aboutWhatTheyCopied)
                    {
                        Log.Write($"copy_to_clipboard \"{toCopy}\" is about what the user copied: doing use_clipboard instead");
                        command = command with { Name = "use_clipboard", Args = new System.Text.Json.Nodes.JsonObject { ["task"] = text, ["output"] = "copy" } };
                    }
                }
                // "Pause the music at 00:23": smaller AI models sometimes do it right away. A time for later in what you
                // said turns it into something Dave does then (the AI is asked again at that time, without the time).
                if (command.Name is "media_control" or "set_volume" or "play_music" or "music_settings" or "play_mix" or "lock_pc" or "open_app"
                        or "close_app" or "close_all_apps" or "open_website" or "quiet_mode" or "window_control" or "screenshot"
                    && Reminders.TryFindLater(text, out var laterMinutes, out var laterTime, out var request))
                {
                    Log.Write($"{command.Name} was asked for later: scheduling \"{request}\" ({(laterTime ?? $"in {laterMinutes} min")})");
                    var later = new System.Text.Json.Nodes.JsonObject { ["action"] = request, ["message"] = request };
                    if (laterMinutes != null) later["minutes"] = laterMinutes; else later["time"] = laterTime;
                    command = command with { Name = "set_reminder", Args = later };
                }
                // The AI can also get the time itself wrong ("0025" became 12:25): a clear time in what you said wins.
                else if (command.Name == "set_reminder" && Reminders.TryFindLater(text, out laterMinutes, out laterTime, out _))
                {
                    var args = command.Args;
                    if (laterTime != null && args["time"]?.ToString() != laterTime)
                    {
                        Log.Write($"set_reminder: the AI made {args["time"]?.ToString() ?? $"{args["minutes"]} min"} of it, you said {laterTime}");
                        args["time"] = laterTime;
                        if ((args["repeat"]?.ToString() ?? "none") is "none" or "") args.Remove("minutes");
                    }
                    else if (laterMinutes != null && (args["repeat"]?.ToString() ?? "none") is "none" or ""
                             && !(args["minutes"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var aiMinutes) && Math.Abs(aiMinutes - laterMinutes.Value) < 0.01))
                    {
                        Log.Write($"set_reminder: the AI made {args["minutes"]?.ToString() ?? args["time"]?.ToString()} of it, you said in {laterMinutes} min");
                        args.Remove("time");
                        args["minutes"] = laterMinutes;
                    }
                }
                duck.Dispose(); // give the sound back before touching music or volume
                if (command.Name == "start_music_quiz")
                {
                    ConversationLog.Add(text, $"(started a music quiz: {command.Args["theme"]})");
                    await QuizAsync(command.Args["theme"]?.ToString() ?? "mixed hits", cancel);
                    return;
                }
                if (command.Name == "look_at_screen")
                {
                    Watchdog.Step = "looking at the screen";
                    bubble.ShowText("👁 " + T("Looking at your screen…", "Ik kijk naar je scherm…"));
                    var seen = await Vision.AskAboutScreenAsync(settings, command.Args["question"]?.ToString() ?? text, language);
                    Log.Write($"Says (screen): {seen}");
                    ConversationLog.Add(text, $"(looked at the screen) {seen}");
                    bubble.ShowText(seen, spoken: true);
                    Watchdog.Step = "speaking";
                    await Speaker.SpeakAsync(settings, seen, language, cancel);
                    return;
                }
                if (command.Name is "use_clipboard" or "pc_stats" or "update_dave" or "recall_conversation" or "screen_time" or "internet_speed" or "read_file"
                        or "read_page" or "read_notifications" or "game_help" or "where_left_off" or "send_to_phone"
                    ||(command.Name == "calendar" && command.Args["action"]?.ToString() != "add" && CalendarFeed.Links(settings).Count > 0))
                {
                    Log.Write($"Command {command.Name} {command.Args.ToJsonString()}");
                    string answer;
                    if (command.Name == "use_clipboard")
                    {
                        Watchdog.Step = "working on the clipboard";
                        bubble.ShowText("📋 " + T("Looking at what you copied…", "Ik kijk naar wat je kopieerde…"));
                        answer = await ClipboardTasks.RunAsync(settings, command.Args["task"]?.ToString() ?? text,
                            command.Args["output"]?.ToString() == "copy", language);
                    }
                    else if (command.Name == "pc_stats")
                    {
                        Watchdog.Step = "checking the PC";
                        bubble.ShowText("📊 " + T("Checking your PC…", "Ik check je pc…"));
                        var stats = await Task.Run(PcStats.Collect);
                        Log.Write("PC stats:\n" + stats.TrimEnd());
                        answer = await Assistant.WorkOnAsync(settings, command.Args["question"]?.ToString() ?? text, stats,
                            "a report of the user's PC, measured just now", language, spoken: true);
                    }
                    else if (command.Name == "recall_conversation")
                    {
                        Watchdog.Step = "remembering";
                        bubble.ShowText("💭 " + T("Thinking back…", "Even terugdenken…"));
                        int Days(string key) => command.Args[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var d) ? Math.Max(0, d) : 0;
                        var earlier = ConversationLog.Search(command.Args["about"]?.ToString() ?? "", Days("from_days_ago"), Days("to_days_ago"));
                        answer = await Assistant.WorkOnAsync(settings, text, earlier,
                            "what the user and you (the assistant) said in earlier conversations, with dates and times", language, spoken: true);
                    }
                    else if (command.Name == "read_file")
                    {
                        Watchdog.Step = "reading a file";
                        answer = await ReadFileAsync(command.Args["path"]?.ToString() ?? "", command.Args["name"]?.ToString() ?? "",
                            command.Args["question"]?.ToString() is { Length: > 0 } q ? q : text, language);
                    }
                    else if (command.Name == "read_page")
                    {
                        Watchdog.Step = "reading the page";
                        answer = await ReadPageAsync(command.Args["question"]?.ToString() is { Length: > 0 } q ? q : text, language);
                    }
                    else if (command.Name == "send_to_phone")
                    {
                        Watchdog.Step = "sending to the phone";
                        answer = await SendToPhoneAsync(command.Args);
                    }
                    else if (command.Name == "where_left_off")
                    {
                        Watchdog.Step = "looking at what you were doing";
                        answer = await Assistant.WorkOnAsync(settings, command.Args["question"]?.ToString() is { Length: > 0 } wq ? wq : text,
                            WhereLeftOff(), "what the user was doing on their PC (measured by Dave); mention the apps, pages and song that matter, " +
                            "and offer to reopen them if they're closed", language, spoken: true);
                    }
                    else if (command.Name == "game_help")
                    {
                        Watchdog.Step = "helping with the game";
                        bubble.ShowText("🎮 " + T("Looking at your game…", "Ik kijk naar je game…"));
                        answer = await Assistant.GameHelpAsync(settings, command.Args["question"]?.ToString() is { Length: > 0 } gq ? gq : text, language);
                    }
                    else if (command.Name == "read_notifications")
                    {
                        Watchdog.Step = "reading notifications";
                        bubble.ShowText("🔔 " + T("Checking your notifications…", "Ik kijk naar je meldingen…"));
                        var hours = command.Args["hours"] is System.Text.Json.Nodes.JsonValue h && h.TryGetValue<int>(out var n) && n > 0 ? n : 24;
                        var list = await Notifications.DescribeAsync(hours);
                        answer = list == null
                            ? T("Windows doesn't let me read your notifications. Turn on notification access for apps in Windows' privacy settings.",
                                "Windows laat me je meldingen niet lezen. Zet meldingstoegang voor apps aan in de privacy-instellingen van Windows.")
                            : await Assistant.WorkOnAsync(settings, command.Args["question"]?.ToString() is { Length: > 0 } nq ? nq : text, list,
                                "the user's recent Windows notifications (only ones Windows still showed, or that Dave saw come in while running)", language, spoken: true);
                    }
                    else if (command.Name == "screen_time")
                    {
                        Watchdog.Step = "checking screen time";
                        DateTime Date(string key) => DateTime.TryParse(command.Args[key]?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var d) ? d.Date : DateTime.Today;
                        var report = ScreenTime.Report(Date("from_date"), Date("to_date"));
                        Log.Write("Screen time:\n" + report.TrimEnd());
                        answer = await Assistant.WorkOnAsync(settings, command.Args["question"]?.ToString() ?? text, report,
                            "the user's screen time per app (time the app was in front while they were at the PC)", language, spoken: true);
                    }
                    else if (command.Name == "internet_speed")
                    {
                        Watchdog.Step = "testing the internet";
                        var what = command.Args["what"]?.ToString() ?? "speed";
                        bubble.ShowText("📶 " + (what == "ping" ? T("Checking your ping…", "Ik check je ping…") : T("Testing your internet, about 15 seconds…", "Ik test je internet, zo'n 15 seconden…")));
                        answer = await SpeedTest.RunAsync(settings, what);
                    }
                    else if (command.Name == "calendar")
                    {
                        Watchdog.Step = "reading the calendar";
                        bubble.ShowText("📅 " + T("Checking your calendar…", "Ik kijk in je agenda…"));
                        DateTime? Date(string key) => DateTime.TryParse(command.Args[key]?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var d) ? d.Date : null;
                        var first = Date("from_date") ?? DateTime.Today;
                        var last = Date("to_date") ?? first;
                        var events = await CalendarFeed.DescribeAsync(settings, (first - DateTime.Today).Days, (last - DateTime.Today).Days);
                        Log.Write("Calendar:\n" + events.TrimEnd());
                        answer = await Assistant.WorkOnAsync(settings, text, events, "the user's calendar for the period they asked about", language, spoken: true);
                    }
                    else answer = await UpdateOnRequestAsync();
                    Log.Write($"Says: {answer}");
                    ConversationLog.Add(text, answer);
                    bubble.ShowText(answer, spoken: true);
                    Watchdog.Step = "speaking";
                    await Speaker.SpeakAsync(settings, answer, language, cancel);
                    if (pendingInstall != null) { await InstallUpdateAsync(pendingInstall); pendingInstall = null; }
                    return;
                }
                if (command.Name == "control_apps")
                {
                    var summary = await AppAgent.RunAsync(settings, command.Args["task"]?.ToString() ?? text, language,
                        progress: step => bubble.ShowText(step),
                        askUser: async question =>
                        {
                            await Speaker.SpeakAsync(settings, question, language, cancel);
                            return (await ListenAsync(cancel, followUp: true, attempts: 1))?.text;
                        },
                        cancel);
                    Log.Write($"Agent done: {summary}");
                    ConversationLog.Add(text, $"(did it in the apps) {summary}");
                    bubble.ShowText("✅ " + summary, spoken: true);
                    await Speaker.SpeakAsync(settings, summary, language, cancel);
                    return;
                }
                Watchdog.Step = $"command {command.Name}";
                var outcome = await Commands.RunAsync(settings, command.Name, command.Args, text);
                Log.Write($"Outcome: {outcome.Text}");
                ConversationLog.Add(text, $"({command.Name}) {outcome.Text}");
                bubble.ShowText(outcome.Text, spoken: outcome.Speak);
                if (outcome.Speak) await Speaker.SpeakAsync(settings, outcome.Text, settings.Language, cancel);
                else Speaker.Beep(1320, 90);
                return;
            }
        }
    }

    /// <summary>"Send this to my phone": what's copied (link, text, picture or a copied file), a screenshot, a file, or text.</summary>
    private async Task<string> SendToPhoneAsync(System.Text.Json.Nodes.JsonObject args)
    {
        if (PhoneNotify.NotReady(settings) is { } notReady) return notReady;
        static bool IsLink(string t) => !t.Contains(' ') && Uri.TryCreate(t.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
        string? why;
        string sent;
        switch (args["what"]?.ToString())
        {
            case "screenshot":
                bubble.ShowText("📸 " + T("Sending a screenshot to your phone…", "Ik stuur een screenshot naar je telefoon…"));
                why = await PhoneNotify.SendFileAsync(settings, Convert.FromBase64String(Vision.CaptureMainScreen()), $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm}.jpg");
                sent = T("the screenshot", "de screenshot");
                break;
            case "file":
                var target = FileFinder.DirectPath(args["path"]?.ToString() is { Length: > 0 } p ? p : args["name"]?.ToString() ?? "", out _);
                if (target == null || !File.Exists(target))
                {
                    var found = await FileFinder.FindAsync(args["name"]?.ToString() ?? "", "any", newest: false);
                    target = found.Select(f => f.Path).FirstOrDefault(File.Exists);
                }
                if (target == null) return T("I couldn't find that file.", "Ik kon dat bestand niet vinden.");
                bubble.ShowText("📎 " + T($"Sending {Path.GetFileName(target)} to your phone…", $"Ik stuur {Path.GetFileName(target)} naar je telefoon…"));
                why = await PhoneNotify.SendFileAsync(settings, await File.ReadAllBytesAsync(target), Path.GetFileName(target));
                sent = Path.GetFileName(target);
                break;
            case "text":
                var text = args["text"]?.ToString()?.Trim() ?? "";
                if (text.Length == 0) return T("I didn't get what to send.", "Ik snapte niet wat ik moest sturen.");
                why = IsLink(text) ? await PhoneNotify.SendLinkAsync(settings, text.Trim()) : await PhoneNotify.SendTextAsync(settings, text);
                sent = IsLink(text) ? T("the link", "de link") : T("it", "het");
                break;
            default: // what's copied
                if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files && File.Exists(files[0]))
                {
                    why = await PhoneNotify.SendFileAsync(settings, await File.ReadAllBytesAsync(files[0]!), Path.GetFileName(files[0]!));
                    sent = Path.GetFileName(files[0]!);
                }
                else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                {
                    using (image)
                        why = await PhoneNotify.SendFileAsync(settings, Vision.ToJpeg(image), "Copied picture.jpg");
                    sent = T("the picture", "de afbeelding");
                }
                else if (Clipboard.ContainsText() && Clipboard.GetText().Trim() is { Length: > 0 } copied)
                {
                    why = IsLink(copied) ? await PhoneNotify.SendLinkAsync(settings, copied) : await PhoneNotify.SendTextAsync(settings, copied);
                    sent = IsLink(copied) ? T("the link", "de link") : T("what you copied", "wat je kopieerde");
                }
                else return T("Nothing is copied right now.", "Er is nu niets gekopieerd.");
                break;
        }
        return why ?? T($"Sent {sent} to your phone.", $"{char.ToUpper(sent[0])}{sent[1..]} staat op je telefoon.");
    }

    /// <summary>"Where did I leave off?": the apps used last (with times), what's open now, the browser pages, the music, what was asked.</summary>
    private static string WhereLeftOff()
    {
        var text = new System.Text.StringBuilder($"Now it's {DateTime.Now:ddd HH:mm}.\n");
        var sessions = ScreenTime.RecentSessions(TimeSpan.FromHours(12)).Where(s => (s.to - s.from).TotalSeconds >= 20).Take(12).ToList();
        text.AppendLine("Apps the user used last (newest first): " + (sessions.Count == 0 ? "none measured since Dave started"
            : string.Join("; ", sessions.Select(s => $"{s.app} {s.from:HH:mm}-{s.to:HH:mm}"))));
        var windows = WindowList.List().Where(w => w.title.Length > 1 && w.process is not ("explorer" or "TextInputHost" or "SystemSettings")).Take(15).ToList();
        text.AppendLine("Windows open now (front first): " + (windows.Count == 0 ? "none" : string.Join("; ", windows.Select(w => $"{w.title} ({w.process})"))));
        if (MusicWatcher.Recent(4) is { Count: > 0 } songs)
            text.AppendLine("Songs that played last: " + string.Join("; ", songs.Select(p => $"\"{p.Title}\" by {p.Artist} at {p.At:HH:mm}")));
        if (ConversationLog.RecentTopics() is { } asked) text.AppendLine("What the user asked Dave last: " + asked);
        return text.ToString();
    }

    /// <summary>
    /// "Summarise this page": the page in the browser you were just in. Pages that can't be downloaded readably (you need
    /// to be logged in, or scripts build them) are looked at on screen instead, when the browser is in front.
    /// </summary>
    private async Task<string> ReadPageAsync(string question, string language)
    {
        bubble.ShowText("🌐 " + T("Reading the page…", "Ik lees de pagina…"));
        var page = await WebPage.ReadAsync();
        if (page == null) return T("I don't see a browser open.", "Ik zie geen browser openstaan.");
        if (page.Text != null)
            return await Assistant.WorkOnAsync(settings, question, page.Text,
                $"the web page \"{page.Title}\" ({page.Url}) the user has open" + (page.Cut ? " (only the first part: it's long)" : ""), language, spoken: true);
        if (WindowList.Foreground() != page.Window)
            return T("I can't read that page by myself. Put it in front and ask again, then I'll look at your screen.",
                "Die pagina kan ik zelf niet lezen. Zet hem vooraan en vraag het opnieuw, dan kijk ik op je scherm.");
        bubble.ShowText("👁 " + T("Looking at the page…", "Ik kijk naar de pagina…"));
        return await Vision.AskAboutScreenAsync(settings, $"{question} (about the web page \"{page.Title}\" on the screen)", language);
    }

    /// <summary>"Look at this file and rate it": find it (a path, a known folder, or by name), read it, and let the AI answer.</summary>
    private async Task<string> ReadFileAsync(string path, string name, string question, string language)
    {
        var target = FileFinder.DirectPath(path.Length > 0 ? path : name, out _);
        if (target == null || !(File.Exists(target) || Directory.Exists(target)))
        {
            if (path.Length > 0) return T($"I can't find {path} on this PC.", $"Ik kan {path} niet vinden op deze pc.");
            var found = await FileFinder.FindAsync(name, "any", newest: false);
            if (found.Count == 0) return T($"I couldn't find {name} on this PC.", $"Ik kon {name} niet vinden op deze pc.");
            target = found[0].Path;
        }
        var fileName = Path.GetFileName(target.TrimEnd('\\')) is { Length: > 0 } n ? n : target;
        bubble.ShowText("📄 " + T($"Reading {fileName}…", $"Ik lees {fileName}…"));
        var content = await FileReader.ReadAsync(target);
        Log.Write($"Read {target}: {(content.Problem ?? (content.Images != null ? $"{content.Images.Count} picture(s)" : $"{content.Text!.Length} characters"))}{(content.Cut ? ", cut short" : "")}");
        if (content.Problem != null)
            return T($"I can't read {fileName}: it's {content.Problem}.", $"Ik kan {fileName} niet lezen: {content.Problem}.");

        var what = $"the file {target}" + (content.Cut ? " (only the first part: it's long)" : "");
        if (content.Images != null)
            return await Vision.AskAboutImagesAsync(settings, question, language, content.Images,
                content.Images.Count > 1 ? $"the first {content.Images.Count} pages of {what}, one image per page" : what);
        return await Assistant.WorkOnAsync(settings, question, content.Text!, what, language, spoken: true);
    }

    /// <summary>Beep, record, transcribe. Null if nothing was understood.</summary>
    private async Task<(string text, string language)?> ListenAsync(CancellationToken cancel, bool followUp, int attempts = 2)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            bubble.ShowListening();
            Speaker.Beep();
            await Task.Delay(150, cancel);
            Watchdog.Step = "listening";
            window?.Push(); // the window's microphone lights up
            var wav = await Recorder.RecordAsync(cancel);
            Watchdog.Step = "transcribing";
            window?.Push();
            if (wav == null)
            {
                if (followUp) return null; // no answer to Dave's question: just close
                bubble.ShowText(T("I didn't hear anything.", "Ik hoorde niets."));
                await Task.Delay(1200, cancel);
                return null;
            }
            bubble.ShowText("…");
            Watchdog.Step = "transcribing";
            var (text, whisperLanguage) = await Groq.TranscribeAsync(settings, wav);
            if (text.Length > 0) return (text, LanguageOf(whisperLanguage, text));
            if (attempt == 0 && !followUp)
                await Speaker.SpeakAsync(settings, T("I didn't catch that.", "Dat verstond ik niet."), settings.Language, cancel);
        }
        return null;
    }

    /// <summary>Which of the two configured languages to answer in.</summary>
    private string LanguageOf(string? whisperLanguage, string text)
    {
        if (settings.SecondLanguage.Length == 0) return settings.Language;
        string Short(string tag) => tag.Split('-')[0].ToLowerInvariant();
        var names = new Dictionary<string, string> { ["dutch"] = "nl", ["english"] = "en", ["german"] = "de", ["french"] = "fr" };
        if (whisperLanguage != null && names.TryGetValue(whisperLanguage.ToLowerInvariant(), out var code))
        {
            if (code == Short(settings.SecondLanguage)) return settings.SecondLanguage;
            if (code == Short(settings.Language)) return settings.Language;
        }
        // Typed text: count common words.
        var words = text.ToLowerInvariant().Split(' ', ',', '.', '?', '!');
        string[] english = { "the", "a", "and", "you", "what", "how", "where", "when", "who", "why", "is", "are", "my", "can", "play", "tell", "turn" };
        string[] dutch = { "de", "het", "een", "en", "je", "wat", "hoe", "waar", "wanneer", "wie", "waarom", "is", "mijn", "kun", "speel", "vertel", "zet" };
        var en = words.Count(english.Contains);
        var nl = words.Count(dutch.Contains);
        var guess = en > nl ? "en" : "nl";
        return guess == Short(settings.SecondLanguage) ? settings.SecondLanguage : settings.Language;
    }

    // --- Music quiz ---

    private static readonly int[] SnippetMs = { 1500, 3000, 6000, 12000, 20000, 35000 };

    private async Task QuizAsync(string theme, CancellationToken cancel)
    {
        if (!Spotify.IsConnected(settings))
        {
            var need = T("The music quiz needs Spotify. Connect it in Dave's settings first.", "Voor de muziekquiz heb ik Spotify nodig. Koppel het eerst in de instellingen van Dave.");
            bubble.ShowText(need, spoken: true);
            await Speaker.SpeakAsync(settings, need, settings.Language, cancel);
            return;
        }
        quizRunning = true;
        try
        {
            bubble.ShowText(T("🎵 Music quiz\nFinding songs…", "🎵 Muziekquiz\nNummers zoeken…"));
            var picked = await Assistant.QuizSongsAsync(settings, theme);
            var found = await Task.WhenAll(picked.Select(async song =>
            {
                var title = song?["title"]?.ToString() ?? "";
                var artist = song?["artist"]?.ToString() ?? "";
                try { return (title, artist, uri: await Spotify.FindTrackAsync(settings, title, artist)); }
                catch { return (title, artist, uri: (Spotify.Song?)null); }
            }));
            var songs = found.Where(s => s.uri != null).OrderBy(_ => Random.Shared.Next()).ToList();
            if (songs.Count == 0)
            {
                await Speaker.SpeakAsync(settings, T("I couldn't find any songs for the quiz.", "Ik kon geen nummers voor de quiz vinden."), settings.Language, cancel);
                return;
            }

            await Speaker.SpeakAsync(settings, T(
                "Music quiz! I'll play a tiny bit of a song. Say the title, say you don't know it to hear more, say again to replay it, or say you give up.",
                "Muziekquiz! Ik speel steeds een klein stukje van een nummer. Zeg de titel, zeg 'weet ik niet' voor een langer stukje, 'nog een keer' om het opnieuw te horen, of zeg dat je het opgeeft."),
                settings.Language, cancel);

            int score = 0, played = 0;
            foreach (var (title, artist, uri) in songs)
            {
                int level = 0;
                var reveal = T($"It was {title} by {artist}.", $"Het was {title} van {artist}.");
                while (true)
                {
                    // Snippet
                    bubble.ShowText(T($"🎵 Song {played + 1}/{songs.Count} · score {score}\n▶ listen…", $"🎵 Nummer {played + 1}/{songs.Count} · score {score}\n▶ luister…"));
                    await Spotify.PlaySongAsync(settings, uri!, 0);
                    snippetCut = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    try { await Task.Delay(SnippetMs[Math.Min(level, SnippetMs.Length - 1)], snippetCut.Token); }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { /* shortcut: answer now */ }
                    snippetCut = null;
                    await Spotify.PauseAsync(settings);

                    // Guess
                    await Speaker.SpeakAsync(settings, level == 0 ? T("What song is this?", "Welk nummer is dit?") : T("And now?", "En nu?"), settings.Language, cancel);
                    var heard = await ListenAsync(cancel, followUp: true, attempts: 1);
                    var (verdict, reaction) = heard == null
                        ? ("dont_know", T("Let's hear a bit more.", "Dan een stukje langer."))
                        : await Assistant.JudgeQuizAsync(settings, title, artist, heard.Value.text);
                    if (heard != null) bubble.ShowText($"“{heard.Value.text}”");

                    if (verdict == "stop") { await EndQuizAsync(score, played, cancel); return; }
                    if (verdict == "replay") { await Speaker.SpeakAsync(settings, reaction, settings.Language, cancel); continue; }
                    if (verdict is "correct" or "give_up" or "skip")
                    {
                        if (verdict == "correct") score++;
                        bubble.ShowText($"{(verdict == "correct" ? "✅" : "🏳")} {title} – {artist}");
                        await Speaker.SpeakAsync(settings, $"{reaction} {reveal}", settings.Language, cancel);
                        await Spotify.PlaySongAsync(settings, uri!, 45_000); // a bit of the chorus as a reward
                        await Task.Delay(8000, cancel);
                        break;
                    }
                    level++; // wrong, close, don't know: longer snippet
                    await Speaker.SpeakAsync(settings, reaction, settings.Language, cancel);
                }
                played++;
            }
            await EndQuizAsync(score, played, cancel);
        }
        finally
        {
            quizRunning = false;
            snippetCut = null;
        }
    }

    private async Task EndQuizAsync(int score, int played, CancellationToken cancel)
    {
        await Spotify.PauseAsync(settings);
        bubble.ShowText($"🏁 Score: {score} / {played}");
        await Speaker.SpeakAsync(settings, T($"That's the end of the quiz! You got {score} out of {played}.", $"Einde van de quiz! Je had er {score} van de {played} goed."), settings.Language, cancel);
    }

    // --- Reminders ---

    private void AnnounceDueReminders()
    {
        if (session != null) return; // try again at the next tick
        var due = Reminders.TakeDue(settings);
        foreach (var r in due.Where(r => r.Action.Length > 0)) dueActions.Enqueue(r.Action);
        due.RemoveAll(r => r.Action.Length > 0);
        if (due.Count > 0)
        {
            foreach (var r in due) PhoneNotify.Send(settings, "⏰ " + settings.Name, r.Message); // also on the phone when you're away
            _ = RunSessionAsync(null, string.Join(". ", due.Select(r => r.Message)));
            return;
        }
        if (dueActions.Count > 0)
        {
            // "Lock my PC in 10 minutes": now it's time, so it's asked as if you said it just now (one per tick)
            var action = dueActions.Dequeue();
            Log.Write($"Doing what was asked for now: {action}");
            _ = RunSessionAsync(action);
            return;
        }
        if (headsUps.Count > 0)
        {
            var messages = new List<string>();
            while (headsUps.Count > 0) messages.Add(headsUps.Dequeue());
            _ = RunSessionAsync(null, string.Join(" ", messages), isReminder: false);
        }
    }
}

/// <summary>A hidden window that receives the global shortcut (works in every program, also games).</summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312, Id = 1;
    private readonly Action onPress;
    private bool registered;

    public HotkeyWindow(Action onPress)
    {
        this.onPress = onPress;
        CreateHandle(new CreateParams());
    }

    public bool Register(string shortcut)
    {
        if (registered) UnregisterHotKey(Handle, Id);
        registered = false;
        uint modifiers = 0x4000; // MOD_NOREPEAT
        Keys key = Keys.None;
        foreach (var part in shortcut.Split('+').Select(p => p.Trim().ToLowerInvariant()))
        {
            switch (part)
            {
                case "ctrl" or "control": modifiers |= 0x2; break;
                case "alt": modifiers |= 0x1; break;
                case "shift": modifiers |= 0x4; break;
                case "win": modifiers |= 0x8; break;
                default:
                    if (Enum.TryParse(part, true, out Keys k)) key = k;
                    break;
            }
        }
        if (key == Keys.None) return false;
        registered = RegisterHotKey(Handle, Id, modifiers, (uint)key);
        return registered;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey) onPress();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (registered) UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
