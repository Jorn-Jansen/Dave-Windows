using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DaveWindows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 6 * 60 * 60 * 1000 }; // look for a new version every 6 hours
    private Updater.Release? pendingInstall; // asked for by voice: installed once Dave has finished talking
    private readonly Queue<string> headsUps = new(); // things Dave was watching for that happened; said once he's free

    private CancellationTokenSource? session;          // the conversation or quiz that's running
    private CancellationTokenSource? snippetCut;       // quiz: shortcut during a snippet stops it early
    private bool quizRunning;

    private string T(string english, string dutch) => settings.Say(english, dutch);

    public DaveApp(string? testQuestion = null)
    {
        Ducker.RestoreAfterCrash(); // in case Dave was closed while other sound was turned down
        Watchers.Triggered += message => bubble.BeginInvoke(() => headsUps.Enqueue(message)); // said at the next tick, when Dave is free
        if (testQuestion == null)
        {
            MusicWatcher.Start(settings); // song history, and skipping songs you don't like
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
        if (question == "demo") // shows the listening circle, then an answer pill, without talking to anyone
        {
            bubble.ShowListening();
            for (int i = 0; i < 30; i++) { Recorder.Level = (float)Math.Abs(Math.Sin(i / 3.0)) * 0.8f; await Task.Delay(100); }
            Recorder.Level = 0;
            bubble.ShowText("Dit is de nieuwe look van Dave: een cirkel als ik luister, en een pill voor het antwoord.");
            await Task.Delay(4000);
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

    private DaveWindow Window() => window ??= new DaveWindow(settings,
        ask: question => { if (session != null) return false; AskTyped(question); return true; },
        listen: () => { if (session == null) Trigger(); },
        applySettings: ApplySettings,
        busy: () => session != null);

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
            if (manual) tray.ShowBalloonTip(3000, settings.Name, T($"You have the newest version ({Updater.Current.ToString(3)}).",
                $"Je hebt de nieuwste versie ({Updater.Current.ToString(3)})."), ToolTipIcon.None);
            return;
        }
        Log.Write($"Update available: {release.Version}");
        if (manual && MessageBox.Show(T($"Version {release.Version} is available (you have {Updater.Current.ToString(3)}). Update now?",
                $"Versie {release.Version} is beschikbaar (je hebt {Updater.Current.ToString(3)}). Nu updaten?"),
                settings.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        while (session != null) await Task.Delay(TimeSpan.FromSeconds(20)); // never in the middle of a conversation
        await InstallUpdateAsync(release);
    }

    /// <summary>"Update yourself": what to say. When there's a new version, it's installed after Dave has said so.</summary>
    private async Task<string> UpdateOnRequestAsync()
    {
        var current = Updater.Current.ToString(3);
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
                    bubble.ShowText((isReminder ? "⏰ " : "👀 ") + text);
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
            bubble.ShowText(message);
            try { await Speaker.SpeakAsync(settings, message, settings.Language); } catch { }
        }
        finally
        {
            bubble.HideAfter(1500);
            session = null;
            Watchdog.Step = "idle";
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
            using (var duck = new Ducker(settings.DuckTo))
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
                    bubble.ShowText(back);
                    await Speaker.SpeakAsync(settings, back, language, cancel);
                    return;
                }

                bubble.ShowText($"“{text}”\n" + T("Thinking…", "Even denken…"));
                Watchdog.Step = "asking the AI";
                var result = await Assistant.AskAsync(settings, text, language, await locationTask);

                if (result is Assistant.Speak speak)
                {
                    Log.Write($"Says ({language}): {speak.Text}");
                    ConversationLog.Add(text, speak.Text);
                    bubble.ShowText(speak.Text);
                    Watchdog.Step = "speaking";
                    await Speaker.SpeakAsync(settings, speak.Text, language, cancel);
                    if (!speak.Text.TrimEnd().EndsWith('?')) return;
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
                duck.Dispose(); // give the sound back before touching music or volume
                if (command.Name == "start_music_quiz")
                {
                    ConversationLog.Add(text, $"(started a music quiz: {command.Args["theme"]})");
                    return;
                }
                if (command.Name == "look_at_screen")
                {
                    Watchdog.Step = "looking at the screen";
                    bubble.ShowText("👁 " + T("Looking at your screen…", "Ik kijk naar je scherm…"));
                    var seen = await Vision.AskAboutScreenAsync(settings, command.Args["question"]?.ToString() ?? text, language);
                    Log.Write($"Says (screen): {seen}");
                    ConversationLog.Add(text, $"(looked at the screen) {seen}");
                    bubble.ShowText(seen);
                    Watchdog.Step = "speaking";
                    await Speaker.SpeakAsync(settings, seen, language, cancel);
                    return;
                }
                if (command.Name is "use_clipboard" or "pc_stats" or "update_dave" or "recall_conversation" or "screen_time" or "internet_speed" or "read_file"
                    || (command.Name == "calendar" && command.Args["action"]?.ToString() != "add" && CalendarFeed.Links(settings).Count > 0))
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
                    bubble.ShowText(answer);
                    Watchdog.Step = "speaking";
                    await Speaker.SpeakAsync(settings, answer, language, cancel);
                    if (pendingInstall != null) { await InstallUpdateAsync(pendingInstall); pendingInstall = null; }
                    return;
                }
                if (command.Name == "control_apps")
                {
                    var summary = await AppAgent.RunAsync(settings, command.Args["task"]?.ToString() ?? text, language,
                        progress: bubble.ShowText,
                        askUser: async question =>
                        {
                            await Speaker.SpeakAsync(settings, question, language, cancel);
                            return (await ListenAsync(cancel, followUp: true, attempts: 1))?.text;
                        },
                        cancel);
                    Log.Write($"Agent done: {summary}");
                    ConversationLog.Add(text, $"(did it in the apps) {summary}");
                    bubble.ShowText("✅ " + summary);
                    await Speaker.SpeakAsync(settings, summary, language, cancel);
                    return;
                }
                Watchdog.Step = $"command {command.Name}";
                var outcome = await Commands.RunAsync(settings, command.Name, command.Args, text);
                Log.Write($"Outcome: {outcome.Text}");
                ConversationLog.Add(text, $"({command.Name}) {outcome.Text}");
                bubble.ShowText(outcome.Text);
                if (outcome.Speak) await Speaker.SpeakAsync(settings, outcome.Text, settings.Language, cancel);
                else Speaker.Beep(1320, 90);
                return;
            }
        }
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
            var wav = await Recorder.RecordAsync(cancel);
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
            bubble.ShowText(need);
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
        if (due.Count > 0) { _ = RunSessionAsync(null, string.Join(". ", due.Select(r => r.Message))); return; }
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
