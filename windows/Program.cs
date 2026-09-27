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
        Application.Run(new DaveApp(test));
    }
}

/// <summary>Tray icon, shortcut, wake word and reminders; runs the conversations.</summary>
public class DaveApp : ApplicationContext
{
    private readonly Settings settings = Settings.Load();
    private readonly Bubble bubble = new();
    private readonly NotifyIcon tray;
    private readonly HotkeyWindow hotkey;
    private readonly System.Windows.Forms.Timer reminderTimer = new() { Interval = 15_000 };
    private WakeWord? wakeWord;

    private CancellationTokenSource? session;          // the conversation or quiz that's running
    private CancellationTokenSource? snippetCut;       // quiz: shortcut during a snippet stops it early
    private bool quizRunning;

    private string T(string english, string dutch) => settings.Say(english, dutch);

    public DaveApp(string? testQuestion = null)
    {
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
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "Dave",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Trigger(); };

        hotkey = new HotkeyWindow(Trigger);
        ApplySettings();

        reminderTimer.Tick += (_, _) => AnnounceDueReminders();
        reminderTimer.Start();
        AnnounceDueReminders(); // ones that came due while the PC was off

        if (settings.GroqKey.Length == 0) OpenSettings();
        else tray.ShowBalloonTip(3000, "Dave is ready", T($"Press {settings.Hotkey} to talk to me.", $"Druk op {settings.Hotkey} om met me te praten."), ToolTipIcon.None);
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
        await Task.Delay(1600);
        ExitThread();
    }

    /// <summary>(Re)apply shortcut, wake word and autostart after the settings change.</summary>
    private void ApplySettings()
    {
        if (!hotkey.Register(settings.Hotkey))
            tray.ShowBalloonTip(4000, "Dave", $"Shortcut {settings.Hotkey} is already used by another program. Pick another in Settings.", ToolTipIcon.Warning);

        wakeWord?.Dispose();
        wakeWord = null;
        if (settings.WakeWord)
        {
            try { wakeWord = new WakeWord(() => bubble.BeginInvoke(Trigger)); }
            catch (Exception e) { Log.Write($"Wake word failed: {e}"); }
        }

        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (settings.StartWithWindows) run?.SetValue("Dave", $"\"{Application.ExecutablePath}\"");
        else run?.DeleteValue("Dave", throwOnMissingValue: false);
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(settings, AskTyped);
        if (form.ShowDialog() == DialogResult.OK) ApplySettings();
    }

    private void Quit()
    {
        Cancel();
        wakeWord?.Dispose();
        hotkey.Dispose();
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

    private async Task RunSessionAsync(string? typed, string? announcement = null)
    {
        if (settings.GroqKey.Length == 0) { OpenSettings(); return; }
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
                    var text = T("Reminder: ", "Herinnering: ") + announcement;
                    bubble.ShowText("⏰ " + text);
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

                bubble.ShowText($"“{text}”\n" + T("Thinking…", "Even denken…"));
                var result = await Assistant.AskAsync(settings, text, language, await locationTask);

                if (result is Assistant.Speak speak)
                {
                    Log.Write($"Says ({language}): {speak.Text}");
                    bubble.ShowText(speak.Text);
                    await Speaker.SpeakAsync(settings, speak.Text, language, cancel);
                    if (!speak.Text.TrimEnd().EndsWith('?')) return;
                    followUp = true; // Dave asked something back: keep listening
                    continue;
                }

                var command = (Assistant.Command)result;
                duck.Dispose(); // give the sound back before touching music or volume
                if (command.Name == "start_music_quiz")
                {
                    await QuizAsync(command.Args["theme"]?.ToString() ?? "mixed hits", cancel);
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
                    bubble.ShowText("✅ " + summary);
                    await Speaker.SpeakAsync(settings, summary, language, cancel);
                    return;
                }
                var outcome = await Commands.RunAsync(settings, command.Name, command.Args);
                Log.Write($"Outcome: {outcome.Text}");
                bubble.ShowText(outcome.Text);
                if (outcome.Speak) await Speaker.SpeakAsync(settings, outcome.Text, settings.Language, cancel);
                else Speaker.Beep(1320, 90);
                return;
            }
        }
    }

    /// <summary>Beep, record, transcribe. Null if nothing was understood.</summary>
    private async Task<(string text, string language)?> ListenAsync(CancellationToken cancel, bool followUp, int attempts = 2)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            bubble.ShowListening();
            Speaker.Beep();
            await Task.Delay(150, cancel);
            var wav = await Recorder.RecordAsync(cancel);
            if (wav == null)
            {
                if (followUp) return null; // no answer to Dave's question: just close
                bubble.ShowText(T("I didn't hear anything.", "Ik hoorde niets."));
                await Task.Delay(1200, cancel);
                return null;
            }
            bubble.ShowText("…");
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
        if (due.Count > 0) _ = RunSessionAsync(null, string.Join(". ", due.Select(r => r.Message)));
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
