namespace DaveWindows;

/// <summary>Dave's settings: key, languages, voices, shortcut, wake word, Spotify.</summary>
public class SettingsForm : Form
{
    private readonly Settings settings;
    private readonly Action<string>? askTyped;
    private readonly TextBox groqKey = new() { UseSystemPasswordChar = true, Width = 420 };
    private readonly TextBox country = new() { Width = 420 };
    private readonly TextBox assistantName = new() { Width = 200, PlaceholderText = "Dave" };
    private readonly TextBox wakePhrase = new() { Width = 300 };
    private readonly TextBox language = new() { Width = 120 };
    private readonly TextBox secondLanguage = new() { Width = 120 };
    private readonly ComboBox voice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox secondVoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox hotkeyBox = new() { Width = 160 };
    private readonly TextBox windowHotkeyBox = new() { Width = 160 };
    private readonly TextBox browser = new() { Width = 160, PlaceholderText = "Windows default" };
    private readonly CheckBox wakeWord = new() { Text = "Listen for “Hey Dave” (offline)", AutoSize = true };
    private readonly CheckBox autostart = new() { Text = "Start Dave with Windows", AutoSize = true };
    private readonly CheckBox autoUpdate = new() { Text = $"Update Dave automatically (now version {Updater.Current.ToString(3)})", AutoSize = true };
    private readonly ComboBox provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox aiKey = new() { UseSystemPasswordChar = true, Width = 260, PlaceholderText = "API key" };
    private readonly TextBox aiModel = new() { Width = 200 };
    private readonly TextBox aiBaseUrl = new() { Width = 420, PlaceholderText = "e.g. http://localhost:11434/v1 (Ollama)" };
    private readonly TextBox calendarLinks = new() { Width = 420, PlaceholderText = "https://calendar.google.com/calendar/ical/…/basic.ics" };
    private static readonly (string id, string name)[] Providers =
        { ("groq", "Groq (free, fastest)"), ("openai", "OpenAI"), ("openrouter", "OpenRouter"), ("custom", "Custom (OpenAI-compatible)") };
    private readonly TextBox spotifyId = new() { Width = 420 };
    private readonly Label spotifyStatus = new() { AutoSize = true };
    private readonly TextBox question = new() { Width = 420, PlaceholderText = "Type a question to test Dave" };
    private readonly RadioButton engineWindows = new() { Text = "Windows voices (offline, free)", AutoSize = true };
    private readonly RadioButton engineAzure = new() { Text = "Azure voices (online, natural)", AutoSize = true };
    private readonly TextBox azureKey = new() { UseSystemPasswordChar = true, Width = 300 };
    private readonly TextBox azureRegion = new() { Width = 120 };
    private readonly ComboBox azureVoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly ComboBox azureSecondVoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly Label azureStatus = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 8, 0, 0) };

    private static readonly (double rate, string name)[] Speeds = { (0.85, "slow"), (1.0, "normal"), (1.15, "a bit faster"), (1.3, "fast") };

    public SettingsForm(Settings settings, Action<string>? askTyped = null)
    {
        this.settings = settings;
        this.askTyped = askTyped;
        Text = "Dave settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 10f);

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(16) };
        void Row(string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) });
            layout.Controls.Add(control);
        }
        FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            flow.Controls.AddRange(controls);
            return flow;
        }

        groqKey.Text = settings.GroqKey;
        foreach (var (_, name) in Providers) provider.Items.Add(name);
        provider.SelectedIndex = Math.Max(0, Array.FindIndex(Providers, p => p.id == settings.AiProvider));
        aiKey.Text = settings.AiKey;
        aiModel.Text = settings.AiModel;
        aiBaseUrl.Text = settings.AiBaseUrl;
        calendarLinks.Text = settings.CalendarLinks;
        void UpdateProvider()
        {
            var id = Providers[Math.Max(0, provider.SelectedIndex)].id;
            aiKey.Enabled = aiModel.Enabled = id != "groq";
            aiBaseUrl.Enabled = id == "custom";
            aiModel.PlaceholderText = id switch { "openai" => "gpt-4.1-mini", "openrouter" => "openai/gpt-oss-120b", "custom" => "model name", _ => "" };
        }
        UpdateProvider();
        provider.SelectedIndexChanged += (_, _) => UpdateProvider();
        country.Text = settings.Country;
        assistantName.Text = settings.AssistantName;
        wakePhrase.Text = settings.WakePhrase;
        void UpdateWakeHint() => wakePhrase.PlaceholderText = $"hey {(assistantName.Text.Trim().Length > 0 ? assistantName.Text.Trim() : "Dave")}";
        UpdateWakeHint();
        assistantName.TextChanged += (_, _) => UpdateWakeHint();
        language.Text = settings.Language;
        secondLanguage.Text = settings.SecondLanguage;
        hotkeyBox.Text = settings.Hotkey;
        windowHotkeyBox.Text = settings.WindowHotkey;
        browser.Text = settings.Browser;
        wakeWord.Checked = settings.WakeWord;
        autostart.Checked = settings.StartWithWindows;
        autoUpdate.Checked = settings.AutoUpdate;
        spotifyId.Text = settings.SpotifyClientId;
        engineAzure.Checked = settings.VoiceEngine == "azure";
        engineWindows.Checked = !engineAzure.Checked;
        azureKey.Text = settings.AzureKey;
        azureRegion.Text = settings.AzureRegion;
        // Until the voice list is loaded, show the saved choice so Save keeps it.
        if (settings.AzureVoice.Length > 0) { azureVoice.Items.Add(settings.AzureVoice); azureVoice.SelectedIndex = 0; }
        if (settings.AzureSecondVoice.Length > 0) { azureSecondVoice.Items.Add(settings.AzureSecondVoice); azureSecondVoice.SelectedIndex = 0; }
        foreach (var (_, name) in Speeds) speed.Items.Add(name);
        speed.SelectedIndex = Math.Max(0, Array.FindIndex(Speeds, s => Math.Abs(s.rate - settings.SpeechRate) < 0.01));
        FillVoices();
        language.Leave += (_, _) => FillVoices();
        secondLanguage.Leave += (_, _) => FillVoices();

        var preview = new Button { Text = "▶", Width = 40 };
        preview.Click += async (_, _) => await PreviewAsync("windows", settings.Language);
        var previewSecond = new Button { Text = "▶", Width = 40 };
        previewSecond.Click += async (_, _) => await PreviewAsync("windows", settings.SecondLanguage);
        var previewAzure = new Button { Text = "▶", Width = 40 };
        previewAzure.Click += async (_, _) => await PreviewAsync("azure", settings.Language);
        var previewAzureSecond = new Button { Text = "▶", Width = 40 };
        previewAzureSecond.Click += async (_, _) => await PreviewAsync("azure", settings.SecondLanguage);
        var loadAzure = new Button { Text = "Load Azure voices", AutoSize = true };
        loadAzure.Click += async (_, _) => await LoadAzureVoicesAsync(loadAzure);

        var connect = new Button { Text = "Connect Spotify", AutoSize = true };
        connect.Click += async (_, _) => await ConnectSpotifyAsync(connect);
        UpdateSpotifyStatus();

        var ask = new Button { Text = "Ask", AutoSize = true };
        ask.Click += (_, _) => { Collect(); settings.Save(); askTyped?.Invoke(question.Text); };

        Row("Groq API key (free, console.groq.com/keys)", groqKey);
        Row("AI provider", Flow(provider, new Label { Text = "listening always uses the Groq key (or OpenAI)", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 8, 0, 0) }));
        Row("Provider key and model", Flow(aiKey, aiModel));
        Row("Provider address (custom)", aiBaseUrl);
        Row("Country (units, currency)", country);
        Row("Name (empty = Dave)", assistantName);
        Row("Wake phrase (empty = hey + name)", Flow(wakePhrase, new Label { Text = "e.g. hey jarvis, whats up dave, yo computer", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 8, 0, 0) }));
        Row("Main language", Flow(language, new Label { Text = "e.g. nl-NL", AutoSize = true, Margin = new Padding(6, 8, 0, 0) }));
        Row("Voice engine", Flow(engineWindows, engineAzure));
        Row("Main-language voice (Windows)", Flow(voice, preview));
        Row("Second language (empty = none)", Flow(secondLanguage, new Label { Text = "e.g. en-US", AutoSize = true, Margin = new Padding(6, 8, 0, 0) }));
        Row("Second-language voice (Windows)", Flow(secondVoice, previewSecond));
        Row("Azure key and region", Flow(azureKey, azureRegion, loadAzure, azureStatus));
        Row("Main-language voice (Azure)", Flow(azureVoice, previewAzure));
        Row("Second-language voice (Azure)", Flow(azureSecondVoice, previewAzureSecond));
        Row("Speaking speed", speed);
        Row("Shortcut", Flow(hotkeyBox, new Label { Text = "e.g. Ctrl+Alt+D", AutoSize = true, Margin = new Padding(6, 8, 0, 0) }));
        Row("Window shortcut", Flow(windowHotkeyBox, new Label { Text = "opens Dave's window (chat, reminders, screen time)", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 8, 0, 0) }));
        Row("Browser for websites", Flow(browser, new Label { Text = "e.g. Brave (empty = Windows default)", AutoSize = true, Margin = new Padding(6, 8, 0, 0) }));
        Row("", wakeWord);
        Row("", autostart);
        Row("", autoUpdate);
        Row("Calendar link (iCal)", calendarLinks);
        Row("", new Label
        {
            Text = "Google Calendar: Settings → your calendar → \"Secret address in iCal format\".\n" +
                   "Outlook: Settings → Calendar → Shared calendars → Publish → ICS link. Several links: separate with spaces.",
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        Row("Spotify Client ID", spotifyId);
        Row("", new Label
        {
            Text = $"Add this Redirect URI to your app at developer.spotify.com/dashboard:\n{Spotify.RedirectUri}",
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        Row("", Flow(connect, spotifyStatus));
        Row("Test", Flow(question, ask));

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => { Collect(); settings.Save(); WarnAboutWakePhrase(); };
        Row("", Flow(save, cancel));
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    private void FillVoices()
    {
        void Fill(ComboBox box, string tag, string chosen)
        {
            box.Items.Clear();
            if (string.IsNullOrWhiteSpace(tag)) return;
            foreach (var v in Speaker.VoicesFor(tag)) box.Items.Add(v.DisplayName);
            if (box.Items.Count == 0) { box.Items.Add("(no voice installed for " + tag + ")"); box.SelectedIndex = 0; return; }
            var index = box.Items.IndexOf(chosen);
            box.SelectedIndex = index >= 0 ? index : 0;
        }
        Fill(voice, language.Text.Trim(), settings.Voice);
        Fill(secondVoice, secondLanguage.Text.Trim(), settings.SecondVoice);
    }

    private void Collect()
    {
        settings.GroqKey = groqKey.Text.Trim();
        settings.AiProvider = Providers[Math.Max(0, provider.SelectedIndex)].id;
        settings.AiKey = aiKey.Text.Trim();
        settings.AiModel = aiModel.Text.Trim();
        settings.AiBaseUrl = aiBaseUrl.Text.Trim();
        settings.CalendarLinks = calendarLinks.Text.Trim();
        settings.AssistantName = assistantName.Text.Trim();
        settings.WakePhrase = wakePhrase.Text.Trim();
        settings.Country = country.Text.Trim().Length > 0 ? country.Text.Trim() : "the Netherlands";
        settings.Language = language.Text.Trim().Length > 0 ? language.Text.Trim() : "nl-NL";
        settings.SecondLanguage = secondLanguage.Text.Trim();
        settings.Voice = voice.SelectedItem as string ?? "";
        settings.SecondVoice = secondVoice.SelectedItem as string ?? "";
        settings.SpeechRate = Speeds[Math.Max(0, speed.SelectedIndex)].rate;
        settings.VoiceEngine = engineAzure.Checked ? "azure" : "windows";
        settings.AzureKey = azureKey.Text.Trim();
        settings.AzureRegion = azureRegion.Text.Trim().ToLowerInvariant().Replace(" ", "");
        settings.AzureVoice = ShortName(azureVoice.SelectedItem);
        settings.AzureSecondVoice = ShortName(azureSecondVoice.SelectedItem);
        settings.Hotkey = hotkeyBox.Text.Trim().Length > 0 ? hotkeyBox.Text.Trim() : "Ctrl+Alt+D";
        settings.WindowHotkey = windowHotkeyBox.Text.Trim().Length > 0 ? windowHotkeyBox.Text.Trim() : "Ctrl+Alt+W";
        settings.WakeWord = wakeWord.Checked;
        settings.Browser = browser.Text.Trim();
        settings.StartWithWindows = autostart.Checked;
        settings.AutoUpdate = autoUpdate.Checked;
        if (spotifyId.Text.Trim() != settings.SpotifyClientId)
        {
            settings.SpotifyClientId = spotifyId.Text.Trim();
            settings.SpotifyRefresh = ""; // different app: log in again
        }
    }

    /// <summary>The offline listener only knows English words; say so instead of silently never waking up.</summary>
    private void WarnAboutWakePhrase()
    {
        List<string> unknown;
        try { unknown = WakeWord.UnknownWords(settings.EffectiveWakePhrase); }
        catch { return; } // model not available: nothing to check against
        if (unknown.Count == 0) return;
        var fallback = WakeWord.UnknownWords($"hey {settings.Name}").Count == 0 ? $"hey {settings.Name}" : "hey dave";
        MessageBox.Show(
            $"The wake-word listener doesn't know: {string.Join(", ", unknown)}\n\n" +
            $"It works offline with a fixed English vocabulary. Until you pick words it knows, it listens for \"{fallback}\".\n\n" +
            "Tip: write it the way it sounds in English, or pick another word (names like Jarvis, Friday, Alexa and Kyle work).",
            "Wake phrase", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string ShortName(object? item) => item switch
    {
        Azure.Voice v => v.ShortName,
        string saved => saved,
        _ => "",
    };

    /// <summary>Preview a voice with a given engine, without changing which engine Dave uses.</summary>
    private async Task PreviewAsync(string engine, string languageTag)
    {
        Collect();
        if (string.IsNullOrWhiteSpace(languageTag)) return;
        var chosen = settings.VoiceEngine;
        var quietUntil = settings.QuietUntil;
        settings.VoiceEngine = engine;
        settings.QuietUntil = DateTime.MinValue; // a preview is always heard, also in quiet mode
        try
        {
            var sample = languageTag.StartsWith("nl") ? "Hoi, ik ben Dave. Waar kan ik je mee helpen?" : "Hi, I'm Dave. How can I help?";
            await Speaker.SpeakAsync(settings, sample, languageTag);
        }
        finally { settings.VoiceEngine = chosen; settings.QuietUntil = quietUntil; }
    }

    private async Task LoadAzureVoicesAsync(Button button)
    {
        Collect();
        if (!Azure.IsConfigured(settings)) { azureStatus.Text = "Fill in the key and region first"; return; }
        button.Enabled = false;
        azureStatus.Text = "Loading…";
        try
        {
            async Task Fill(ComboBox box, string tag, string chosen)
            {
                box.Items.Clear();
                if (string.IsNullOrWhiteSpace(tag)) return;
                foreach (var v in await Azure.VoicesForAsync(settings, tag)) box.Items.Add(v);
                var match = box.Items.Cast<Azure.Voice>().ToList().FindIndex(v => v.ShortName == chosen);
                if (box.Items.Count > 0) box.SelectedIndex = Math.Max(0, match);
            }
            await Fill(azureVoice, settings.Language, settings.AzureVoice);
            await Fill(azureSecondVoice, settings.SecondLanguage, settings.AzureSecondVoice);
            azureStatus.Text = $"✅ {azureVoice.Items.Count} + {azureSecondVoice.Items.Count} voices";
        }
        catch (Exception e)
        {
            Log.Write($"Loading Azure voices failed: {e.Message}");
            azureStatus.Text = "❌ " + e.Message;
        }
        button.Enabled = true;
    }

    private async Task ConnectSpotifyAsync(Button button)
    {
        Collect();
        if (settings.SpotifyClientId.Length == 0) { MessageBox.Show("Paste the Spotify Client ID first."); return; }
        button.Enabled = false;
        spotifyStatus.Text = "Log in in your browser…";
        try
        {
            await Spotify.LoginAsync(settings);
            settings.Save();
        }
        catch (Exception e)
        {
            Log.Write($"Spotify login failed: {e}");
            MessageBox.Show($"Spotify login failed: {e.Message}");
        }
        button.Enabled = true;
        UpdateSpotifyStatus();
    }

    private void UpdateSpotifyStatus() =>
        spotifyStatus.Text = Spotify.IsConnected(settings) ? "✅ connected" : "not connected";
}
