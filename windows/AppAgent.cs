using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace DaveWindows;

/// <summary>
/// Operator mode: does tasks inside apps step by step. Reads the window's buttons and fields through
/// Windows UI Automation (like a screen reader), then clicks, types and presses keys, checking after each step.
/// </summary>
public static class AppAgent
{
    private const int MaxSteps = 15;
    private const int MaxElements = 80;

    private static string System(string language) => $"""
        You operate the user's Windows PC to do a task for them, one step at a time, using the tools.
        Usually: read_window to see what's on screen, act (click, set_text, type_text, press_keys), then read_window again to check.
        If the right app isn't in front, use list_windows and focus_window, or open_app.
        Keyboard shortcuts are often the easiest way (ctrl+t new tab, ctrl+l address bar, ctrl+s save, ctrl+n new, space to pause a video).
        Look at the window title in read_window: it tells you which document or page is open.
        When the user wants to write something new, first make a new, empty document or tab (ctrl+n, or ctrl+t in a browser)
        and check with read_window that it's empty. Never type into a document that already contains the user's own content
        unless they asked you to change that document.
        Before anything that sends something to other people, posts, deletes, buys, or closes unsaved work, call ask_user and only continue on a clear yes.
        Only use the tools listed; to end, call finish.
        Never type passwords or payment details. Don't do more than the user asked.
        When the task is done, or it can't be done, call finish with one short spoken sentence in {language} saying what you did.
        """;

    private static JsonObject Tool(string name, string description, JsonObject? properties = null, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties ?? new JsonObject(),
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            },
        },
    };

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonArray Tools() => new()
    {
        Tool("list_windows", "List the open windows (title and program)."),
        Tool("focus_window", "Bring a window to the front.", new JsonObject { ["name"] = Str("Part of the window title or program name") }, "name"),
        Tool("open_app", "Open a program by name.", new JsonObject { ["name"] = Str("Program name, e.g. 'Notepad', 'Discord'") }, "name"),
        Tool("read_window", "Read the buttons, menus, fields and texts of the window in front. Each gets a number for click/set_text."),
        Tool("click", "Click an element from the last read_window.", new JsonObject { ["id"] = Int("Element number") }, "id"),
        Tool("set_text", "Replace the text in a text field from the last read_window.",
            new JsonObject { ["id"] = Int("Element number"), ["text"] = Str("New text") }, "id", "text"),
        Tool("type_text", "Type text into whatever has keyboard focus (use \\n for Enter).", new JsonObject { ["text"] = Str("Text to type") }, "text"),
        Tool("press_keys", "Press a key or shortcut, e.g. 'enter', 'ctrl+s', 'alt+tab', 'space', 'f5', 'ctrl+shift+n'.",
            new JsonObject { ["keys"] = Str("Keys joined with +") }, "keys"),
        Tool("scroll", "Scroll the window in front.", new JsonObject { ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("up", "down") } }, "direction"),
        Tool("wait", "Wait for something to load.", new JsonObject { ["seconds"] = Int("1 to 10") }, "seconds"),
        Tool("look_at_screen", "Look at a screenshot of the screen and answer a question about it. Use it when read_window shows little (games, 3D views, custom apps).",
            new JsonObject { ["question"] = Str("What you want to know, e.g. 'where is the Play button?'") }, "question"),
        Tool("ask_user", "Ask the user a yes/no question out loud and get their spoken answer (for confirmations).",
            new JsonObject { ["question"] = Str("Short question in the user's language") }, "question"),
        Tool("finish", "The task is done (or impossible). Say what you did.",
            new JsonObject { ["summary"] = Str("One short spoken sentence in the user's language") }, "summary"),
    };

    /// <summary>Run a task. [progress] shows steps in the bubble; [askUser] speaks a question and returns the spoken answer.</summary>
    public static async Task<string> RunAsync(Settings settings, string task, string language, Action<string> progress,
        Func<string, Task<string?>> askUser, CancellationToken cancel)
    {
        var languageName = CultureInfo.GetCultureInfo(language).EnglishName.Split(' ')[0];
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = System(languageName) +
                (settings.Browser.Length > 0 ? $"\nThe user's browser is {settings.Browser}: use it for websites unless they name another one." : "") },
            new JsonObject { ["role"] = "user", ["content"] = task },
        };
        var elements = new List<AutomationElement>();
        var sawWindow = false; // no typing or key presses until we've looked at what's in front

        for (int step = 0; step < MaxSteps; step++)
        {
            cancel.ThrowIfCancellationRequested();
            var body = new JsonObject
            {
                ["model"] = Groq.Model,
                ["reasoning_effort"] = "low",
                ["messages"] = messages.DeepClone(),
                ["tools"] = Tools(),
            };
            var message = await ChatWithRetryAsync(settings, body, cancel);
            var calls = message["tool_calls"] as JsonArray;
            if (calls == null || calls.Count == 0)
                return message["content"]?.ToString() is { Length: > 0 } text ? text : settings.Say("Done.", "Klaar.");

            message.Remove("reasoning"); // not accepted back as input
            messages.Add(message.DeepClone());

            foreach (var call in calls)
            {
                var name = call?["function"]?["name"]?.ToString() ?? "";
                var args = ParseArgs(call?["function"]?["arguments"]?.ToString());
                var id = call?["id"]?.ToString() ?? $"call_{step}";
                Log.Write($"Agent {name} {args.ToJsonString()}");

                string result;
                switch (name)
                {
                    case "finish":
                        return args["summary"]?.ToString() is { Length: > 0 } summary ? summary : settings.Say("Done.", "Klaar.");
                    case "ask_user":
                        var question = args["question"]?.ToString() ?? "";
                        progress("❓ " + question);
                        var answer = await askUser(question);
                        result = answer == null ? "The user didn't answer. Treat it as no." : $"The user answered: \"{answer}\"";
                        break;
                    case "read_window":
                        progress(settings.Say("👀 Looking at the window…", "👀 Ik kijk naar het venster…"));
                        (result, elements) = await Task.Run(ReadWindow, cancel);
                        sawWindow = true;
                        break;
                    case "type_text" or "press_keys" when !sawWindow:
                        // Safety: typing blindly could land in whatever the user is working in.
                        result = "Not done: first use read_window to check which window is in front.";
                        break;
                    case "focus_window" or "open_app":
                        progress(Describe(settings, name, args, elements));
                        result = await ExecuteAsync(name, args, elements, cancel);
                        sawWindow = false; // something else may be in front now
                        break;
                    default:
                        progress(Describe(settings, name, args, elements));
                        result = await ExecuteAsync(name, args, elements, cancel);
                        break;
                }
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
            }
            PruneOldWindowReads(messages);
        }
        return settings.Say("I stopped after too many steps; it's partly done.", "Ik ben gestopt na te veel stappen; het is deels gelukt.");
    }

    /// <summary>The free Groq tier allows ~8,000 tokens a minute, so wait and retry when a long task hits it.</summary>
    private static async Task<JsonObject> ChatWithRetryAsync(Settings settings, JsonObject body, CancellationToken cancel)
    {
        for (int attempt = 0; ; attempt++)
        {
            var (status, raw) = await Groq.ChatRawAsync(settings, body);
            if (status is >= 200 and < 300) return JsonNode.Parse(raw)!["choices"]![0]!["message"]!.AsObject();
            if (status == 400 && raw.Contains("tool_use_failed") && attempt < 2)
            {
                // The AI called a tool that doesn't exist or with broken arguments; just ask again.
                Log.Write("Agent: invalid tool call, retrying");
                continue;
            }
            if (status == 429 && attempt < 3)
            {
                var wait = Regex.Match(raw, @"try again in ([\d.]+)s") is { Success: true } m
                    ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 8;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait + 0.5, 30)), cancel);
                continue;
            }
            throw Groq.Failure(settings, status, raw);
        }
    }

    /// <summary>Only the latest window contents matter; older ones just use up tokens.</summary>
    private static void PruneOldWindowReads(JsonArray messages)
    {
        var reads = messages.OfType<JsonObject>()
            .Where(m => m["role"]?.ToString() == "tool" && m["content"]?.ToString().StartsWith("Window: ") == true)
            .ToList();
        foreach (var old in reads.Take(reads.Count - 1)) old["content"] = "(older window contents removed)";
    }

    private static JsonObject ParseArgs(string? json)
    {
        try { return JsonNode.Parse(json ?? "{}") as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static string Describe(Settings s, string name, JsonObject args, List<AutomationElement> elements)
    {
        string ElementName()
        {
            var i = args["id"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : -1;
            try { return i >= 0 && i < elements.Count ? elements[i].Current.Name : ""; } catch { return ""; }
        }
        return name switch
        {
            "click" => "🖱 " + s.Say("Click: ", "Klik: ") + ElementName(),
            "set_text" or "type_text" => "⌨ " + Short(args["text"]?.ToString() ?? ""),
            "press_keys" => "⌨ " + args["keys"],
            "focus_window" or "open_app" => "🪟 " + args["name"],
            "scroll" => "↕",
            "wait" => "⏳",
            _ => "…",
        };
    }

    private static string Short(string text) => text.Length > 60 ? text[..60] + "…" : text;

    // --- Carrying out the steps ---

    private static async Task<string> ExecuteAsync(string name, JsonObject args, List<AutomationElement> elements, CancellationToken cancel)
    {
        try
        {
            switch (name)
            {
                case "look_at_screen":
                    return await Vision.AskAboutScreenAsync(Settings.Load(), args["question"]?.ToString() ?? "What is on the screen?", "en-US");
                case "list_windows":
                    return string.Join("\n", WindowList.List().Select(w => $"{w.title} — {w.process}"));
                case "focus_window":
                    var window = WindowList.Find(args["name"]?.ToString() ?? "");
                    if (window == null) return "No such window. Use list_windows.";
                    WindowList.Focus(window.Value.handle);
                    await Task.Delay(400, cancel);
                    return $"Focused: {window.Value.title}";
                case "open_app":
                    var opened = PcActions.OpenApp(args["name"]?.ToString() ?? "");
                    if (opened == null) return "Program not found. Try the English name, or list_windows if it's already open.";
                    await Task.Delay(2500, cancel);
                    var appWindow = WindowList.Find(opened) ?? WindowList.Find(args["name"]?.ToString() ?? opened);
                    if (appWindow != null) WindowList.Focus(appWindow.Value.handle);
                    return $"Opened {opened}{(appWindow != null ? $" (window: {appWindow.Value.title})" : "")}. It may still be loading; read_window to check.";
                case "click":
                    var element = Pick(args, elements);
                    var how = await Task.Run(() => Click(element), cancel);
                    await Task.Delay(500, cancel);
                    return $"Clicked ({how}).";
                case "set_text":
                    var field = Pick(args, elements);
                    var text = args["text"]?.ToString() ?? "";
                    await Task.Run(() =>
                    {
                        if (field.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern value && !value.Current.IsReadOnly)
                            value.SetValue(text);
                        else
                        {
                            field.SetFocus();
                            Keyboard.Press("ctrl+a");
                            Keyboard.Type(text);
                        }
                    }, cancel);
                    return "Text set.";
                case "type_text":
                    await Task.Delay(250, cancel); // let the window finish coming to the front, or the first letters get lost
                    Keyboard.Type(args["text"]?.ToString() ?? "");
                    return "Typed.";
                case "press_keys":
                    Keyboard.Press(args["keys"]?.ToString() ?? "");
                    await Task.Delay(300, cancel);
                    return "Pressed.";
                case "scroll":
                    Mouse.Scroll(args["direction"]?.ToString() == "up" ? 5 : -5);
                    return "Scrolled.";
                case "wait":
                    var seconds = args["seconds"] is JsonValue sv && sv.TryGetValue<int>(out var sec) ? Math.Clamp(sec, 1, 10) : 2;
                    await Task.Delay(seconds * 1000, cancel);
                    return "Waited.";
                default:
                    return "Unknown tool.";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Log.Write($"Agent step {name} failed: {e.Message}");
            return $"That failed: {e.Message}. Try read_window and another way.";
        }
    }

    private static AutomationElement Pick(JsonObject args, List<AutomationElement> elements)
    {
        var id = args["id"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : -1;
        if (id < 0 || id >= elements.Count) throw new InvalidOperationException("That element number isn't in the last read_window");
        return elements[id];
    }

    /// <summary>Press the element the way it prefers; fall back to a real mouse click.</summary>
    private static string Click(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) { ((InvokePattern)invoke).Invoke(); return "invoke"; }
        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) { ((TogglePattern)toggle).Toggle(); return "toggle"; }
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) { ((SelectionItemPattern)select).Select(); return "select"; }
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)) { ((ExpandCollapsePattern)expand).Expand(); return "expand"; }
        var r = element.Current.BoundingRectangle;
        Mouse.Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        return "mouse";
    }

    // --- Reading the window ---

    private static readonly HashSet<ControlType> Interesting = new()
    {
        ControlType.Button, ControlType.Edit, ControlType.Document, ControlType.MenuItem, ControlType.Menu, ControlType.ListItem,
        ControlType.TabItem, ControlType.Hyperlink, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox,
        ControlType.TreeItem, ControlType.SplitButton, ControlType.Text, ControlType.DataItem,
    };

    /// <summary>A short numbered list of what's in the front window.</summary>
    private static (string, List<AutomationElement>) ReadWindow()
    {
        var handle = WindowList.Foreground();
        var root = AutomationElement.FromHandle(handle);
        var list = new List<AutomationElement>();
        var lines = new StringBuilder($"Window: {root.Current.Name}\n");
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        int visited = 0;

        while (queue.Count > 0 && list.Count < MaxElements && visited < 1500)
        {
            var current = queue.Dequeue();
            visited++;
            AutomationElement? child;
            try { child = walker.GetFirstChild(current); } catch { continue; }
            while (child != null)
            {
                queue.Enqueue(child);
                try { child = walker.GetNextSibling(child); } catch { child = null; }
            }
            if (current == root) continue;
            try
            {
                var info = current.Current;
                if (info.IsOffscreen || !Interesting.Contains(info.ControlType)) continue;
                var name = info.Name?.Trim() ?? "";
                var isField = info.ControlType == ControlType.Edit || info.ControlType == ControlType.Document;
                if (name.Length == 0 && !isField) continue;
                if (info.ControlType == ControlType.Text && name.Length > 80) name = name[..80] + "…";
                var value = "";
                if (isField && current.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
                    value = " = \"" + Short(((ValuePattern)p).Current.Value ?? "") + "\"";
                var type = info.ControlType.ProgrammaticName.Replace("ControlType.", "");
                lines.Append($"[{list.Count}] {type} \"{Short(name)}\"{value}{(info.IsEnabled ? "" : " (disabled)")}\n");
                list.Add(current);
            }
            catch (ElementNotAvailableException) { }
        }
        if (list.Count == 0) lines.Append("(Nothing readable. This app may not support it; use press_keys and type_text.)\n");
        return (lines.ToString(), list);
    }
}

/// <summary>Finding and focusing top-level windows.</summary>
public static class WindowList
{
    public static List<(IntPtr handle, string title, string process)> List()
    {
        var result = new List<(IntPtr, string, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var length = GetWindowTextLength(h);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(h, title, title.Capacity);
            GetWindowThreadProcessId(h, out var pid);
            string process;
            try { process = Process.GetProcessById((int)pid).ProcessName; } catch { process = "?"; }
            if (pid == Environment.ProcessId || process is "TextInputHost") return true; // skip Dave itself
            result.Add((h, title.ToString(), process));
            return result.Count < 80;
        }, IntPtr.Zero);
        return result;
    }

    public static (IntPtr handle, string title)? Find(string name)
    {
        var match = List().FirstOrDefault(w =>
            w.title.Contains(name, StringComparison.OrdinalIgnoreCase) || w.process.Contains(name, StringComparison.OrdinalIgnoreCase));
        return match.handle == IntPtr.Zero ? null : (match.handle, match.title);
    }

    public static IntPtr Foreground() => GetForegroundWindow();

    /// <summary>Windows only lets the app that got the last input change focus; a tap of Alt works around that.</summary>
    public static void Focus(IntPtr handle)
    {
        if (IsIconic(handle)) ShowWindow(handle, 9 /* SW_RESTORE */);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(handle);
    }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}

/// <summary>Real key presses and typing (works in almost every app).</summary>
public static class Keyboard
{
    private static readonly Dictionary<string, ushort> Named = new()
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["windows"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
    };

    /// <summary>Press a combination like "ctrl+shift+s" or "f5".</summary>
    public static void Press(string keys)
    {
        var codes = keys.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Code).Where(c => c != 0).ToList();
        // Held for a moment like a real finger: games check keys once per frame and miss an instant tap.
        foreach (var c in codes) { Send(c, up: false); Thread.Sleep(15); }
        Thread.Sleep(50);
        foreach (var c in Enumerable.Reverse(codes)) { Send(c, up: true); Thread.Sleep(15); }
    }

    private static ushort Code(string key)
    {
        if (Named.TryGetValue(key, out var code)) return code;
        if (key.Length > 1 && key[0] == 'f' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) return (ushort)(0x6F + f);
        if (key.Length == 1) return (ushort)(VkKeyScan(key[0]) & 0xFF);
        return 0;
    }

    /// <summary>Type text as Unicode characters, so accents and emoji arrive exactly.</summary>
    public static void Type(string text)
    {
        foreach (var ch in text.Replace("\r", ""))
        {
            if (ch == '\n') { Press("enter"); continue; }
            var inputs = new[] { Unicode(ch, up: false), Unicode(ch, up: true) };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            Thread.Sleep(4);
        }
    }

    /// <summary>
    /// Sent as the keyboard's hardware scan code, not just the key's name: games (Roblox and most others) read
    /// the raw keyboard and ignore key presses without one.
    /// </summary>
    private static void Send(ushort vk, bool up)
    {
        var scan = (ushort)MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */);
        uint flags = up ? 2u : 0u; // KEYEVENTF_KEYUP
        if (scan != 0) flags |= 0x0008; // KEYEVENTF_SCANCODE
        if (Extended.Contains(vk)) flags |= 0x0001; // KEYEVENTF_EXTENDEDKEY: arrows etc. share scan codes with the number pad
        var input = new Input { type = 1, u = new InputUnion { ki = new KeyboardInput { wVk = vk, wScan = scan, dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    private static readonly HashSet<ushort> Extended = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B };

    private static Input Unicode(char ch, bool up) =>
        new() { type = 1, u = new InputUnion { ki = new KeyboardInput { wScan = ch, dwFlags = 0x0004u | (up ? 2u : 0u) } } };

    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MouseInput mi; [FieldOffset(0)] public KeyboardInput ki; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short VkKeyScan(char ch);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}

public static class Mouse
{
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // left down
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // left up
    }

    public static void Scroll(int notches)
    {
        var handle = WindowList.Foreground();
        if (GetWindowRect(handle, out var r)) SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        mouse_event(0x0800, 0, 0, unchecked((uint)(notches * 120)), UIntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
}
