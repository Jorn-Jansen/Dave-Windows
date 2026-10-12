using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>
/// Dave-to-Dave messages: chat with friends who use Dave too ("tell Alex I'm online", or in the Social tab). Everyone has
/// a friend code (DAVE-K7QX-M2PA-9TRB-W4NE); adding someone's code sends them a friend request, and once they accept you
/// can message each other. Messages go through ntfy.sh (free, no account), to a channel named after your code, which only
/// people with your code know; they wait there for up to 12 hours, so messages sent while your PC was off still arrive.
/// The chats are kept in social.json, on this PC only.
/// </summary>
public static class Social
{
    private const string Server = "https://ntfy.sh/";
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O or 1/I: easy to type over
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly string ChatsPath = Path.Combine(Settings.Folder, "social.json");
    private static readonly object Sync = new();
    private static Dictionary<string, List<Line>>? chats;
    private static Settings? settings;
    private static CancellationTokenSource? listening;
    private static SynchronizationContext? ui; // messages are handled on the app's own thread, like everything else that changes the settings

    public sealed class Line
    {
        public DateTimeOffset At { get; set; }
        public bool Mine { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>A message from a friend: who, and what they said.</summary>
    public record Incoming(string Code, string Name, string Text);

    /// <summary>A friend sent a message (raised on the app's own thread).</summary>
    public static event Action<Incoming>? Received;
    /// <summary>Something changed (a request, an accept, a message): the window shows it.</summary>
    public static event Action? Changed;

    // --- Codes ---

    /// <summary>Your own friend code, made the first time it's needed.</summary>
    public static string MyCode(Settings s)
    {
        if (Valid(s.SocialCode)) return s.SocialCode;
        var chars = RandomNumberGenerator.GetItems(Alphabet.AsSpan(), 16);
        s.SocialCode = "DAVE-" + string.Join("-", Enumerable.Range(0, 4).Select(i => new string(chars, i * 4, 4)));
        s.Save();
        return s.SocialCode;
    }

    /// <summary>"dave k7qx m2pa 9trb w4ne", "K7QXM2PA9TRBW4NE"… → "DAVE-K7QX-M2PA-9TRB-W4NE", or null when it isn't a code.</summary>
    public static string? Normalise(string text)
    {
        var letters = new string(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (letters.StartsWith("DAVE")) letters = letters[4..];
        if (letters.Length != 16 || letters.Any(c => !Alphabet.Contains(c))) return null;
        return "DAVE-" + string.Join("-", Enumerable.Range(0, 4).Select(i => letters.Substring(i * 4, 4)));
    }

    private static bool Valid(string code) => Normalise(code) == code;

    private static string Topic(string code) => "dave-chat-" + code[5..].Replace("-", "").ToLowerInvariant();

    public static string MyName(Settings s) => s.SocialName.Trim() is { Length: > 0 } name ? name : Environment.UserName;

    // --- Listening for messages ---

    /// <summary>Starts (or restarts) listening for messages and requests sent to your code.</summary>
    public static void Start(Settings s)
    {
        settings = s;
        ui = SynchronizationContext.Current;
        listening?.Cancel();
        listening = new CancellationTokenSource();
        var token = listening.Token;
        var topic = Topic(MyCode(s));
        _ = Task.Run(() => ListenAsync(s, topic, token));
    }

    private static async Task ListenAsync(Settings s, string topic, CancellationToken token)
    {
        var wait = TimeSpan.FromSeconds(2);
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Everything since the last message we saw (or the last 12 hours, the first time), then new ones as they come
                var since = s.SocialSince.Length > 0 ? s.SocialSince : "12h";
                using var response = await Http.GetAsync($"{Server}{topic}/json?since={Uri.EscapeDataString(since)}", HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
                wait = TimeSpan.FromSeconds(2);
                while (!token.IsCancellationRequested && await reader.ReadLineAsync(token) is { } line)
                {
                    if (line.Length == 0) continue;
                    var e = JsonNode.Parse(line);
                    if (e?["event"]?.ToString() != "message") continue; // keepalives
                    var message = e["message"]?.ToString() ?? "";
                    var id = e["id"]?.ToString() ?? s.SocialSince;
                    void Take() { Handle(s, message); s.SocialSince = id; s.Save(); }
                    if (ui != null) ui.Post(_ => Take(), null); else Take();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Log.Write($"Social: connection lost ({e.Message}), trying again in {wait.TotalSeconds:F0} s");
            }
            try { await Task.Delay(wait, token); } catch (OperationCanceledException) { return; }
            wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2)); // not hammering the server while offline
        }
    }

    /// <summary>One message from the channel: a friend request, an accept, a chat message, or a goodbye.</summary>
    private static void Handle(Settings s, string raw)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(raw); } catch { return; } // not ours
        if (m?["dave"] == null) return;
        var from = Normalise(m["from"]?.ToString() ?? "");
        if (from == null || from == MyCode(s)) return;
        var name = Clean(m["name"]?.ToString() ?? "", 40) is { Length: > 0 } n ? n : "?";
        Settings.Friend? friend;
        lock (Sync)
        {
            friend = s.Friends.FirstOrDefault(f => f.Code == from);
            if (friend != null) friend.Name = name; // they may have changed their name
        }

        switch (m["type"]?.ToString())
        {
            case "hello":
                if (friend == null)
                {
                    lock (Sync) s.Friends.Add(new Settings.Friend { Code = from, Name = name, Status = "incoming", Since = DateTimeOffset.Now });
                    Log.Write($"Social: friend request from {name}");
                }
                else if (friend.Status == "outgoing" || friend.Status == "friend")
                {
                    // We asked them as well (or they re-added us): friends straight away
                    friend.Status = "friend";
                    _ = PublishAsync(s, from, "accept", "");
                }
                break;
            case "accept":
                if (friend is { Status: "outgoing" or "friend" }) friend.Status = "friend";
                break;
            case "bye":
                if (friend != null) lock (Sync) s.Friends.Remove(friend);
                break;
            case "msg":
                if (friend is not { Status: "friend" }) return; // only friends can message you
                var text = Clean(m["text"]?.ToString() ?? "", 1000);
                if (text.Length == 0) return;
                Add(friend.Code, new Line { At = DateTimeOffset.Now, Mine = false, Text = text });
                friend.Unread++;
                s.Save();
                Changed?.Invoke();
                Received?.Invoke(new Incoming(friend.Code, friend.Name, text));
                return;
            default:
                return;
        }
        s.Save();
        Changed?.Invoke();
    }

    private static string Clean(string text, int max)
    {
        var plain = new string(text.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        return plain.Length > max ? plain[..max] : plain;
    }

    // --- Doing things ---

    /// <summary>Add a friend by their code: sends them a friend request. Returns what to tell the user.</summary>
    public static async Task<string> AddAsync(Settings s, string codeText)
    {
        var code = Normalise(codeText);
        if (code == null) return s.Say("That's not a friend code. It looks like DAVE-K7QX-M2PA-9TRB-W4NE.", "Dat is geen vriendcode. Die ziet eruit als DAVE-K7QX-M2PA-9TRB-W4NE.");
        if (code == MyCode(s)) return s.Say("That's your own code. Send it to a friend, so they can add you.", "Dat is je eigen code. Stuur hem naar een vriend, dan kan die jou toevoegen.");
        var existing = s.Friends.FirstOrDefault(f => f.Code == code);
        if (existing is { Status: "friend" }) return s.Say($"You're already friends with {existing.Name}.", $"Je bent al bevriend met {existing.Name}.");
        if (existing is { Status: "incoming" }) { await AcceptAsync(s, code); return s.Say($"You're now friends with {existing.Name}!", $"Je bent nu bevriend met {existing.Name}!"); }
        if (existing == null) lock (Sync) s.Friends.Add(new Settings.Friend { Code = code, Name = s.Say("Waiting…", "Wachten…"), Status = "outgoing", Since = DateTimeOffset.Now });
        s.Save();
        Changed?.Invoke();
        var why = await PublishAsync(s, code, "hello", "");
        return why ?? s.Say("Friend request sent. Once they accept, you can chat.", "Vriendverzoek verstuurd. Zodra ze accepteren, kunnen jullie chatten.");
    }

    public static async Task AcceptAsync(Settings s, string code)
    {
        var friend = s.Friends.FirstOrDefault(f => f.Code == code && f.Status == "incoming");
        if (friend == null) return;
        friend.Status = "friend";
        s.Save();
        Changed?.Invoke();
        await PublishAsync(s, code, "accept", "");
    }

    /// <summary>Decline a request, or remove a friend (they're told, so the chat closes on their side too).</summary>
    public static async Task RemoveAsync(Settings s, string code)
    {
        Settings.Friend? friend;
        lock (Sync)
        {
            friend = s.Friends.FirstOrDefault(f => f.Code == code);
            if (friend == null) return;
            s.Friends.Remove(friend);
        }
        s.Save();
        Changed?.Invoke();
        if (friend.Status != "incoming") await PublishAsync(s, code, "bye", "");
    }

    /// <summary>Send a chat message to a friend. Null when it went, otherwise why not.</summary>
    public static async Task<string?> SendAsync(Settings s, string code, string text)
    {
        text = Clean(text, 1000);
        var friend = s.Friends.FirstOrDefault(f => f.Code == code);
        if (friend is not { Status: "friend" } || text.Length == 0) return s.Say("You can only message friends who accepted you.", "Je kunt alleen vrienden berichten die je hebben geaccepteerd.");
        var why = await PublishAsync(s, code, "msg", text);
        if (why != null) return why;
        Add(code, new Line { At = DateTimeOffset.Now, Mine = true, Text = text });
        Changed?.Invoke();
        return null;
    }

    public static void MarkRead(Settings s, string code)
    {
        if (s.Friends.FirstOrDefault(f => f.Code == code) is { Unread: > 0 } friend) { friend.Unread = 0; s.Save(); }
    }

    /// <summary>The friend [name] is about ("Alex", "alex", "Al"): exact first, then the start of a name. Null when none.</summary>
    public static Settings.Friend? Find(Settings s, string name)
    {
        var friends = s.Friends.Where(f => f.Status == "friend").ToList();
        name = name.Trim();
        if (name.Length == 0 || name is "last" or "laatste") return friends.OrderByDescending(f => LastAt(f.Code)).FirstOrDefault(); // "reply …"
        return friends.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? friends.FirstOrDefault(f => f.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase))
            ?? friends.FirstOrDefault(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Does [text] name one of your friends ("tell Alex…")? Then the message commands are offered.</summary>
    public static bool Mentioned(string text) =>
        settings?.Friends.Any(f => f.Status == "friend" && f.Name.Length > 1 &&
            System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(f.Name)}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) == true;

    /// <summary>For the AI: the friends, and the recent messages (unread ones marked). Marks them read.</summary>
    public static string Describe(Settings s, string from)
    {
        var friends = s.Friends.Where(f => f.Status == "friend").ToList();
        if (friends.Count == 0) return "The user has no friends added in Dave yet (Social tab in Dave's window, with a friend code).";
        var who = from.Trim().Length > 0 && Find(s, from) is { } one ? new List<Settings.Friend> { one } : friends;
        var report = new StringBuilder($"Friends: {string.Join(", ", friends.Select(f => f.Name))}.\n");
        foreach (var f in who)
        {
            var lines = Lines(f.Code).TakeLast(12).ToList();
            if (lines.Count == 0) continue;
            report.AppendLine($"Chat with {f.Name} ({f.Unread} unread):");
            foreach (var l in lines) report.AppendLine($"  {l.At:ddd HH:mm} {(l.Mine ? "the user" : f.Name)}: {l.Text}");
            f.Unread = 0;
        }
        s.Save();
        Changed?.Invoke();
        return report.ToString();
    }

    /// <summary>Your friend code, friends and requests, and the chats, for Dave's window.</summary>
    public static JsonObject StateJson(Settings s)
    {
        var friends = new JsonArray();
        foreach (var f in s.Friends.OrderBy(f => f.Status == "friend" ? 1 : 0).ThenByDescending(f => LastAt(f.Code)))
        {
            var lines = Lines(f.Code);
            friends.Add(new JsonObject
            {
                ["code"] = f.Code, ["name"] = f.Name, ["status"] = f.Status, ["unread"] = f.Unread,
                ["last"] = lines.Count > 0 ? (lines[^1].Mine ? s.Say("You: ", "Jij: ") : "") + lines[^1].Text : "",
                ["lines"] = new JsonArray(lines.TakeLast(100).Select(l => (JsonNode)new JsonObject
                {
                    ["mine"] = l.Mine, ["text"] = l.Text,
                    ["at"] = l.At.ToString(l.At.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm"),
                }).ToArray()),
            });
        }
        return new JsonObject { ["code"] = MyCode(s), ["name"] = MyName(s), ["friends"] = friends };
    }

    // --- Sending and keeping the chats ---

    /// <summary>Puts a message in [code]'s channel. Null when it went, otherwise why not.</summary>
    private static async Task<string?> PublishAsync(Settings s, string code, string type, string text)
    {
        var body = new JsonObject { ["dave"] = 1, ["type"] = type, ["from"] = MyCode(s), ["name"] = MyName(s), ["text"] = text }.ToJsonString();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await Http.PostAsync(Server + Topic(code), new StringContent(body, Encoding.UTF8), timeout.Token);
            if (response.IsSuccessStatusCode) return null;
            Log.Write($"Social: sending failed ({(int)response.StatusCode})");
            return (int)response.StatusCode == 429
                ? s.Say("Too many messages today; try again later.", "Te veel berichten vandaag; probeer het later nog eens.")
                : s.Say("The message didn't go through. Try again in a moment.", "Het bericht kwam niet aan. Probeer het zo nog eens.");
        }
        catch (Exception e)
        {
            Log.Write($"Social: sending failed ({e.Message})");
            return s.Say("I couldn't reach the internet to send it.", "Ik kon het internet niet bereiken om het te versturen.");
        }
    }

    private static Dictionary<string, List<Line>> Chats()
    {
        if (chats != null) return chats;
        try { chats = File.Exists(ChatsPath) ? JsonSerializer.Deserialize<Dictionary<string, List<Line>>>(File.ReadAllText(ChatsPath)) : null; }
        catch (Exception e) { Log.Write($"Social: couldn't read the chats: {e.Message}"); }
        return chats ??= new();
    }

    private static List<Line> Lines(string code)
    {
        lock (Sync) return Chats().TryGetValue(code, out var lines) ? lines.ToList() : new List<Line>();
    }

    private static DateTimeOffset LastAt(string code) => Lines(code) is { Count: > 0 } lines ? lines[^1].At : DateTimeOffset.MinValue;

    private static void Add(string code, Line line)
    {
        lock (Sync)
        {
            var all = Chats();
            if (!all.TryGetValue(code, out var lines)) all[code] = lines = new List<Line>();
            lines.Add(line);
            if (lines.Count > 300) lines.RemoveRange(0, lines.Count - 300);
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(ChatsPath, JsonSerializer.Serialize(all));
            }
            catch (Exception e) { Log.Write($"Social: couldn't save the chats: {e.Message}"); }
        }
    }
}
