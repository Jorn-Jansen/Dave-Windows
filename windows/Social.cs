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
        /// <summary>The message; for a file a short label ("🎤 Voice message"), which older Daves show.</summary>
        public string Text { get; set; } = "";
        /// <summary>The message's id (for reactions).</summary>
        public string Id { get; set; } = "";
        /// <summary>"" (text), "voice", "photo", "video" or "file".</summary>
        public string Kind { get; set; } = "";
        /// <summary>A file: its name, and where it's kept on this PC ("" while it's still coming in).</summary>
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public bool Failed { get; set; }
        /// <summary>Emoji reactions to it: yours and your friend's ("" = none).</summary>
        public string MyReaction { get; set; } = "";
        public string TheirReaction { get; set; } = "";
        /// <summary>The id of the message this one replies to ("" = none).</summary>
        public string ReplyTo { get; set; } = "";
        public bool Edited { get; set; }
        /// <summary>A voice message's words (made the first time you ask for them), and a friend's message in your language ("" = not yet, "=" = it already was).</summary>
        public string Transcript { get; set; } = "";
        public string Translation { get; set; } = "";
        /// <summary>A link in it: the page's title, description and picture (looked up once; Url "" = none, "-" = no preview).</summary>
        public string PreviewUrl { get; set; } = "";
        public string PreviewTitle { get; set; } = "";
        public string PreviewText { get; set; } = "";
        public string PreviewImage { get; set; } = "";
    }

    /// <summary>A message from a friend: who, and what they said.</summary>
    /// <summary>A message from a friend: who, what they said, and for a file what kind and where it is ("voice", a path).</summary>
    public record Incoming(string Code, string Name, string Text, string Kind = "", string File = "");

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
        // Friends from before end-to-end encryption: tell them our key, so their (updated) Dave answers with theirs
        foreach (var f in s.Friends.Where(f => f.Status == "friend" && f.PublicKey.Length == 0).ToList()) _ = PublishAsync(s, f.Code, "hello");
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

    /// <summary>
    /// One message from the channel. Friend requests and accepts are open (they carry the sender's public key: that's how
    /// two Daves learn each other's lock); everything after that (chat messages, goodbyes) is sealed with a key only the
    /// two friends can make, so nobody else can read it, fake it, or unfriend you, not even someone who has your code.
    /// </summary>
    private static void Handle(Settings s, string raw)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(raw); } catch { return; } // not ours
        if (m?["dave"] == null) return;
        var from = Normalise(m["from"]?.ToString() ?? "");
        if (from == null) return;
        if (from == MyCode(s))
        {
            // What you did on your iPhone (sealed with your own key): a message you sent, edited or deleted there
            if (m["c"] == null || Open(s, Self(s), m) is not { } echo || echo["by"]?.ToString() == "pc") return;
            var echoId = echo["id"]?.ToString() ?? "";
            if (echoId.Length == 0 || !Seen.Add(echoId) || Normalise(echo["to"]?.ToString() ?? "") is not { } to || s.Friends.All(f => f.Code != to)) return;
            var target = echo["target"]?.ToString() ?? "";
            switch (echo["type"]?.ToString())
            {
                case "sent":
                    var sent = Clean(echo["text"]?.ToString() ?? "", 1000);
                    if (sent.Length == 0) return;
                    Add(to, new Line { At = DateTimeOffset.Now, Mine = true, Text = sent, Id = echo["msgid"]?.ToString() ?? "", ReplyTo = ReplyOf(echo) });
                    break;
                case "sentfile": // a photo, video, voice message or file you sent from your iPhone: fetched here too
                    var myId = echo["msgid"]?.ToString() ?? "";
                    if (myId.Length == 0 || myId.Length > 64) return;
                    var myKind = echo["kind"]?.ToString() is "voice" or "photo" or "video" ? echo["kind"]!.ToString() : "file";
                    var myName = SafeName(echo["filename"]?.ToString() ?? "file");
                    var mine = new Line { At = DateTimeOffset.Now, Mine = true, Id = myId, Kind = myKind, Name = myName, Text = Label(myKind, myName), ReplyTo = ReplyOf(echo) };
                    Add(to, mine);
                    _ = DownloadAsync(s.Friends.First(f => f.Code == to), mine, echo, announce: false);
                    break;
                case "edit" when Lines(to).LastOrDefault(l => l.Mine && l.Id.Length > 0 && l.Id == target) is { } mineEdited:
                    (mineEdited.Text, mineEdited.Edited, mineEdited.PreviewUrl) = (Clean(echo["text"]?.ToString() ?? "", 1000), true, "");
                    SaveChats();
                    break;
                case "unsend":
                    RemoveLine(to, target, mine: true);
                    break;
                default:
                    return;
            }
            Changed?.Invoke();
            return;
        }
        Settings.Friend? friend;
        lock (Sync) friend = s.Friends.FirstOrDefault(f => f.Code == from);

        if (m["c"] != null) // sealed: a chat message or a goodbye from a friend
        {
            if (friend is not { Status: "friend", PublicKey.Length: > 0 } || Open(s, friend, m) is not { } inner) return; // not from them
            var at = DateTimeOffset.FromUnixTimeSeconds(inner["at"]?.GetValue<long>() ?? 0);
            var id = inner["id"]?.ToString() ?? "";
            if (DateTimeOffset.Now - at > TimeSpan.FromHours(13) || id.Length == 0 || !Seen.Add(id)) return; // an old one sent again
            if (Clean(inner["name"]?.ToString() ?? "", 40) is { Length: > 0 } newName) friend.Name = newName; // they may have changed their name
            switch (inner["type"]?.ToString())
            {
                case "msg":
                    var text = Clean(inner["text"]?.ToString() ?? "", 1000);
                    if (text.Length == 0) return;
                    Typing.Remove(friend.Code);
                    var received = new Line { At = DateTimeOffset.Now, Mine = false, Text = text, Id = id, ReplyTo = ReplyOf(inner) };
                    Add(friend.Code, received);
                    _ = PreviewAsync(friend.Code, received);
                    if (s.Translate && s.TranslateAutomatically) _ = TranslateAsync(s, friend.Code, id);
                    friend.Unread++;
                    s.Save();
                    Changed?.Invoke();
                    Received?.Invoke(new Incoming(friend.Code, friend.Name, text));
                    return;
                case "file":
                    // A voice message, photo, video or file: the message says where the (locked) file is and how to unlock it
                    Typing.Remove(friend.Code);
                    var kind = inner["kind"]?.ToString() is "voice" or "photo" or "video" ? inner["kind"]!.ToString() : "file";
                    var fileName = SafeName(inner["filename"]?.ToString() ?? "file");
                    var incoming = new Line { At = DateTimeOffset.Now, Mine = false, Id = id, Kind = kind, Name = fileName, Text = Label(kind, fileName), ReplyTo = ReplyOf(inner) };
                    Add(friend.Code, incoming);
                    friend.Unread++;
                    s.Save();
                    Changed?.Invoke();
                    _ = DownloadAsync(friend, incoming, inner);
                    return;
                case "react":
                    var target = Lines(friend.Code).LastOrDefault(l => l.Id == inner["target"]?.ToString() && l.Id.Length > 0);
                    if (target == null) return;
                    target.TheirReaction = Clean(inner["emoji"]?.ToString() ?? "", 8);
                    SaveChats();
                    Changed?.Invoke();
                    return;
                case "edit": // they changed a message they sent
                    var edited = Lines(friend.Code).LastOrDefault(l => !l.Mine && l.Id.Length > 0 && l.Id == inner["target"]?.ToString());
                    if (edited == null || edited.Kind.Length > 0) return;
                    edited.Text = Clean(inner["text"]?.ToString() ?? "", 1000);
                    (edited.Edited, edited.Translation, edited.PreviewUrl) = (true, "", "");
                    SaveChats();
                    Changed?.Invoke();
                    return;
                case "unsend": // they deleted a message they sent
                    if (RemoveLine(friend.Code, inner["target"]?.ToString() ?? "", mine: false)) Changed?.Invoke();
                    return;
                case "seen": // they've read your messages up to this one
                    if (!s.ReadReceipts || inner["target"]?.ToString() is not { Length: > 0 } seenId) return;
                    (friend.SeenId, friend.SeenAt) = (seenId, DateTimeOffset.Now);
                    s.Save();
                    Changed?.Invoke();
                    return;
                case "typing":
                    Typing[friend.Code] = DateTime.Now;
                    Changed?.Invoke();
                    return;
                case "bye":
                    lock (Sync) s.Friends.Remove(friend);
                    break;
                default:
                    return;
            }
            s.Save();
            Changed?.Invoke();
            return;
        }

        var name = Clean(m["name"]?.ToString() ?? "", 40) is { Length: > 0 } n ? n : "?";
        var key = ValidKey(m["key"]?.ToString());
        switch (m["type"]?.ToString())
        {
            case "hello":
                if (friend == null)
                {
                    lock (Sync) s.Friends.Add(new Settings.Friend { Code = from, Name = name, Status = "incoming", Since = DateTimeOffset.Now, PublicKey = key ?? "" });
                    Log.Write($"Social: friend request from {name}");
                }
                else if (friend.PublicKey.Length > 0 && friend.PublicKey != key)
                {
                    // A different lock for a friend we have: someone pretending to be them (or they reinstalled Dave: then
                    // remove them and add them again). Ignored, so nobody can take over a friend's chat.
                    Log.Write($"Social: a friend request in {friend.Name}'s name with a different key: ignored");
                    return;
                }
                else if (friend.Status is "outgoing" or "friend")
                {
                    // We asked them as well, they re-added us, or they're telling us their key (after updating): friends right away
                    friend.Name = name;
                    if (key != null) friend.PublicKey = key;
                    friend.Status = "friend";
                    _ = PublishAsync(s, from, "accept");
                }
                break;
            case "accept":
                if (friend is not { Status: "outgoing" or "friend" }) return;
                if (key != null && friend.PublicKey.Length > 0 && friend.PublicKey != key) return; // not the lock we know
                friend.Name = name;
                if (key != null) friend.PublicKey = key;
                friend.Status = "friend";
                break;
            default:
                return; // (old, unsealed messages from before end-to-end encryption aren't trusted)
        }
        s.Save();
        Changed?.Invoke();
    }

    /// <summary>The id of the message a sealed one replies to ("" for none).</summary>
    private static string ReplyOf(JsonNode inner) => inner["reply"]?.ToString() is { Length: > 0 and <= 64 } reply ? reply : "";

    /// <summary>Ids of sealed messages handled lately, so one sent again (replayed) isn't shown twice.</summary>
    private static readonly HashSet<string> Seen = new();

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
        var why = await PublishAsync(s, code, "hello");
        return why ?? s.Say("Friend request sent. Once they accept, you can chat.", "Vriendverzoek verstuurd. Zodra ze accepteren, kunnen jullie chatten.");
    }

    public static async Task AcceptAsync(Settings s, string code)
    {
        var friend = s.Friends.FirstOrDefault(f => f.Code == code && f.Status == "incoming");
        if (friend == null) return;
        friend.Status = "friend";
        s.Save();
        Changed?.Invoke();
        await PublishAsync(s, code, "accept");
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
        if (friend is { Status: "friend", PublicKey.Length: > 0 }) await SealAsync(s, friend, new JsonObject { ["type"] = "bye" });
    }

    /// <summary>Send a chat message to a friend. Null when it went, otherwise why not.</summary>
    public static async Task<string?> SendAsync(Settings s, string code, string text, string reply = "")
    {
        text = Clean(text, 1000);
        var friend = s.Friends.FirstOrDefault(f => f.Code == code);
        if (friend is not { Status: "friend" } || text.Length == 0) return s.Say("You can only message friends who accepted you.", "Je kunt alleen vrienden berichten die je hebben geaccepteerd.");
        if (friend.PublicKey.Length == 0) // their Dave is from before end-to-end encryption: ask it for its key again
        {
            _ = PublishAsync(s, code, "hello");
            return s.Say($"{friend.Name}'s Dave needs the latest update first, so your messages can be encrypted. Ask them to update Dave.",
                          $"De Dave van {friend.Name} heeft eerst de nieuwste update nodig, zodat je berichten versleuteld kunnen worden. Vraag ze Dave te updaten.");
        }
        var id = Guid.NewGuid().ToString("N");
        var why = await SealAsync(s, friend, new JsonObject { ["type"] = "msg", ["text"] = text, ["id"] = id, ["reply"] = reply });
        if (why != null) return why;
        var sentLine = new Line { At = DateTimeOffset.Now, Mine = true, Text = text, Id = id, ReplyTo = reply };
        Add(code, sentLine);
        _ = PreviewAsync(code, sentLine);
        TypingSent.Remove(code); // the next "typing…" goes right away
        // A copy, sealed with your own key, so the iPhone app shows it in the chat too
        if (s.PhoneEnabled) _ = SealAsync(s, Self(s), new JsonObject { ["type"] = "sent", ["to"] = code, ["text"] = text, ["by"] = "pc", ["msgid"] = id, ["reply"] = reply }); // (ntfy allows 250 a day)
        Changed?.Invoke();
        return null;
    }

    // --- Voice messages, photos, videos and files ---

    /// <summary>Where files from and to friends are kept on this PC (Dave's window shows them from here).</summary>
    public static string FilesFolder => Path.Combine(Settings.Folder, "social-files");

    private const long MaxFile = 200_000_000; // 200 MB
    private static readonly HttpClient Upload = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>"voice", "photo", "video" or "file", by the file's extension.</summary>
    public static string KindOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" => "photo",
        ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" or ".m4v" => "video",
        _ => "file",
    };

    /// <summary>What a file message says in places that only show text (the bubble, older Daves).</summary>
    private static string Label(string kind, string name) => kind switch
    {
        "voice" => "🎤 Voice message",
        "photo" => "📷 Photo",
        "video" => "🎬 Video",
        _ => "📎 " + name,
    };

    private static string SafeName(string name)
    {
        var clean = new string(Path.GetFileName(name).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return clean.Length is 0 or > 120 ? "file" + Path.GetExtension(clean) : clean;
    }

    /// <summary>
    /// Sends a voice message, photo, video or other file. It's locked first with its own random key (AES-GCM) and put on
    /// a free file host (litterbox.catbox.moe, kept 3 days), which only ever sees scrambled data; the end-to-end
    /// encrypted message tells your friend where it is and how to unlock it. Null when it went, otherwise why not.
    /// </summary>
    public static async Task<string?> SendFileAsync(Settings s, string code, byte[] data, string name, string kind = "", string reply = "")
    {
        var friend = s.Friends.FirstOrDefault(f => f.Code == code);
        if (friend is not { Status: "friend" }) return s.Say("You can only send things to friends who accepted you.", "Je kunt alleen dingen sturen naar vrienden die je hebben geaccepteerd.");
        if (friend.PublicKey.Length == 0) return s.Say($"{friend.Name}'s Dave needs the latest update first.", $"De Dave van {friend.Name} heeft eerst de nieuwste update nodig.");
        if (data.Length == 0) return s.Say("That file is empty.", "Dat bestand is leeg.");
        if (data.Length > MaxFile) return s.Say("That's too big to send (200 MB at most).", "Dat is te groot om te versturen (hooguit 200 MB).");
        name = SafeName(name);
        if (kind.Length == 0) kind = KindOf(name);

        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var locked = new byte[data.Length + 16]; // the scrambled file, then its 16-byte check
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, data, locked.AsSpan(0, data.Length), locked.AsSpan(data.Length, 16));

        string url;
        try
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent("fileupload"), "reqtype" },
                { new StringContent("72h"), "time" },
                { new ByteArrayContent(locked), "fileToUpload", "dave.bin" },
            };
            using var response = await Upload.PostAsync("https://litterbox.catbox.moe/resources/internals/api.php", form);
            url = (await response.Content.ReadAsStringAsync()).Trim();
            if (!response.IsSuccessStatusCode || !url.StartsWith("https://")) throw new InvalidOperationException($"{(int)response.StatusCode} {url}");
        }
        catch (Exception e)
        {
            Log.Write($"Social: uploading the file failed ({e.Message})");
            return s.Say("The file didn't upload. Check your internet and try again.", "Het bestand kon niet worden geüpload. Check je internet en probeer het opnieuw.");
        }

        var id = Guid.NewGuid().ToString("N");
        var why = await SealAsync(s, friend, new JsonObject
        {
            ["type"] = "file", ["kind"] = kind, ["filename"] = name, ["size"] = data.Length, ["url"] = url, ["id"] = id,
            ["k"] = Convert.ToBase64String(key), ["fn"] = Convert.ToBase64String(nonce),
            ["reply"] = reply,
        });
        if (why != null) return why;
        var kept = Path.Combine(FilesFolder, id + Path.GetExtension(name));
        try { Directory.CreateDirectory(FilesFolder); await File.WriteAllBytesAsync(kept, data); } catch (Exception e) { Log.Write($"Social: couldn't keep the file: {e.Message}"); kept = ""; }
        Add(code, new Line { At = DateTimeOffset.Now, Mine = true, Id = id, Kind = kind, Name = name, File = kept, Text = Label(kind, name), ReplyTo = reply });
        Changed?.Invoke();
        // A copy for your iPhone (sealed with your own key): where the file is and how to unlock it, so it's in the chat there too
        if (s.PhoneEnabled) _ = SealAsync(s, Self(s), new JsonObject
        {
            ["type"] = "sentfile", ["to"] = code, ["by"] = "pc", ["msgid"] = id, ["kind"] = kind, ["filename"] = name, ["size"] = data.Length,
            ["url"] = url, ["k"] = Convert.ToBase64String(key), ["fn"] = Convert.ToBase64String(nonce), ["reply"] = reply,
        });
        return null;
    }

    /// <summary>Fetches and unlocks a file a friend sent (or you sent from your iPhone); then it's in the chat, and a friend's
    /// is announced like a message.</summary>
    private static async Task DownloadAsync(Settings.Friend friend, Line line, JsonNode inner, bool announce = true)
    {
        try
        {
            var url = inner["url"]?.ToString() ?? "";
            if (!url.StartsWith("https://litter.catbox.moe/")) throw new InvalidOperationException("not where Dave puts files");
            var key = Convert.FromBase64String(inner["k"]?.ToString() ?? "");
            var nonce = Convert.FromBase64String(inner["fn"]?.ToString() ?? "");
            var locked = await Upload.GetByteArrayAsync(url);
            if (locked.Length < 17 || locked.Length > MaxFile + 16) throw new InvalidOperationException("wrong size");
            var data = new byte[locked.Length - 16];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, locked.AsSpan(0, data.Length), locked.AsSpan(data.Length, 16), data);
            var kept = Path.Combine(FilesFolder, line.Id + Path.GetExtension(line.Name));
            Directory.CreateDirectory(FilesFolder);
            await File.WriteAllBytesAsync(kept, data);
            void Done()
            {
                line.File = kept;
                SaveChats();
                Changed?.Invoke();
                if (announce) Received?.Invoke(new Incoming(friend.Code, friend.Name, line.Text, line.Kind, kept));
            }
            if (ui != null) ui.Post(_ => Done(), null); else Done();
        }
        catch (Exception e)
        {
            Log.Write($"Social: a file from {friend.Name} couldn't be fetched ({e.Message})");
            void Failed() { line.Failed = true; SaveChats(); Changed?.Invoke(); }
            if (ui != null) ui.Post(_ => Failed(), null); else Failed();
        }
    }

    // --- Editing and deleting your messages ---

    /// <summary>Change a message you sent (text only); it changes on your friend's side too, marked "(edited)".</summary>
    public static async Task<string?> EditAsync(Settings s, string code, string id, string text)
    {
        text = Clean(text, 1000);
        var line = Lines(code).LastOrDefault(l => l.Mine && l.Id.Length > 0 && l.Id == id);
        var friend = s.Friends.FirstOrDefault(f => f.Code == code);
        if (line == null || line.Kind.Length > 0 || text.Length == 0 || friend is not { Status: "friend", PublicKey.Length: > 0 }) return null;
        var why = await SealAsync(s, friend, new JsonObject { ["type"] = "edit", ["target"] = id, ["text"] = text });
        if (why != null) return why;
        (line.Text, line.Edited, line.PreviewUrl) = (text, true, "");
        SaveChats();
        Changed?.Invoke();
        if (s.PhoneEnabled) _ = SealAsync(s, Self(s), new JsonObject { ["type"] = "edit", ["to"] = code, ["target"] = id, ["text"] = text, ["by"] = "pc" });
        _ = PreviewAsync(code, line);
        return null;
    }

    /// <summary>Delete a message you sent: gone on your friend's side too.</summary>
    public static async Task<string?> UnsendAsync(Settings s, string code, string id)
    {
        var friend = s.Friends.FirstOrDefault(f => f.Code == code);
        if (friend is not { Status: "friend", PublicKey.Length: > 0 } || Lines(code).All(l => !(l.Mine && l.Id == id && id.Length > 0))) return null;
        var why = await SealAsync(s, friend, new JsonObject { ["type"] = "unsend", ["target"] = id });
        if (why != null) return why;
        RemoveLine(code, id, mine: true);
        Changed?.Invoke();
        if (s.PhoneEnabled) _ = SealAsync(s, Self(s), new JsonObject { ["type"] = "unsend", ["to"] = code, ["target"] = id, ["by"] = "pc" });
        return null;
    }

    /// <summary>Takes a message out of a chat (and its file off this PC). True when there was one.</summary>
    private static bool RemoveLine(string code, string id, bool mine)
    {
        Line? gone = null;
        lock (Sync)
        {
            if (id.Length == 0 || !Chats().TryGetValue(code, out var lines)) return false;
            gone = lines.LastOrDefault(l => l.Id == id && l.Mine == mine);
            if (gone != null) lines.Remove(gone);
        }
        if (gone == null) return false;
        if (gone.File.Length > 0) try { File.Delete(gone.File); } catch { /* in use: it goes next time */ }
        SaveChats();
        return true;
    }

    // --- "Seen" ---

    private static readonly Dictionary<string, string> SeenSent = new();

    /// <summary>You're looking at the chat: tells your friend you've seen their messages (unless read receipts are off).</summary>
    private static void SendSeen(Settings s, Settings.Friend friend)
    {
        if (!s.ReadReceipts || friend.PublicKey.Length == 0) return;
        var last = Lines(friend.Code).LastOrDefault(l => !l.Mine && l.Id.Length > 0);
        if (last == null || SeenSent.GetValueOrDefault(friend.Code) == last.Id) return;
        SeenSent[friend.Code] = last.Id;
        _ = SealAsync(s, friend, new JsonObject { ["type"] = "seen", ["target"] = last.Id });
    }

    // --- Voice message text, translations and link previews (worked out when needed) ---

    /// <summary>The words of a voice message (Groq's speech-to-text), made once and kept.</summary>
    public static async Task TranscribeAsync(Settings s, string code, string id)
    {
        var line = Lines(code).LastOrDefault(l => l.Id == id && l.Kind == "voice");
        if (line == null || line.Transcript.Length > 0 || !File.Exists(line.File)) return;
        line.Transcript = "…";
        Changed?.Invoke();
        try
        {
            var (text, _) = await Groq.TranscribeAsync(s, await File.ReadAllBytesAsync(line.File), Path.GetFileName(line.File), remember: false);
            line.Transcript = text.Length > 0 ? text : s.Say("(nothing I could make out)", "(niets wat ik kon verstaan)");
        }
        catch (Exception e)
        {
            Log.Write($"Social: transcribing a voice message failed ({e.Message})");
            line.Transcript = "";
        }
        SaveChats();
        Changed?.Invoke();
    }

    /// <summary>A friend's message in your language (Groq), made once and kept; "=" when it already was.</summary>
    public static async Task TranslateAsync(Settings s, string code, string id)
    {
        var line = Lines(code).LastOrDefault(l => l.Id == id && !l.Mine && l.Kind.Length == 0);
        if (line == null || line.Translation.Length > 0 || !s.Translate) return;
        line.Translation = "…";
        Changed?.Invoke();
        try
        {
            var language = System.Globalization.CultureInfo.GetCultureInfo(s.Language).EnglishName.Split(' ')[0];
            var body = new JsonObject
            {
                ["model"] = Groq.Model,
                ["reasoning_effort"] = "low",
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = $"Translate the user's chat message into {language}, keeping its tone, slang and emojis. " +
                        $"If it's already in {language} (or has no words to translate), reply with exactly =. Reply with only the translation." },
                    new JsonObject { ["role"] = "user", ["content"] = line.Text },
                },
            };
            var reply = (await Groq.ChatAsync(s, body))["content"]?.GetValue<string>()?.Trim() ?? "";
            line.Translation = reply.Length == 0 || reply == "=" || reply.Equals(line.Text, StringComparison.OrdinalIgnoreCase) ? "=" : reply;
        }
        catch (Exception e)
        {
            Log.Write($"Social: translating failed ({e.Message})");
            line.Translation = "";
        }
        SaveChats();
        Changed?.Invoke();
    }

    private static readonly HttpClient Pages = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>The first link in a message: its page's title, description and picture, for a preview under it.</summary>
    private static async Task PreviewAsync(string code, Line line)
    {
        if (line.Kind.Length > 0 || line.PreviewUrl.Length > 0) return;
        var match = System.Text.RegularExpressions.Regex.Match(line.Text, @"https?://[^\s<>""]+");
        if (!match.Success) return;
        var url = match.Value.TrimEnd('.', ',', ')', '!', '?');
        line.PreviewUrl = "-"; // (looking it up, or none)
        try
        {
            string title = "", text = "", image = "";
            if (System.Text.RegularExpressions.Regex.IsMatch(url, @"^https?://(www\.|m\.)?(youtube\.com|youtu\.be)/"))
            {
                // YouTube asks for cookie consent instead of showing the page: its own little info service works
                var info = JsonNode.Parse(await Pages.GetStringAsync("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(url)));
                (title, text, image) = (info?["title"]?.ToString() ?? "", info?["author_name"]?.ToString() ?? "", info?["thumbnail_url"]?.ToString() ?? "");
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Dave link preview)");
                using var response = await Pages.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/html")
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    var buffer = new char[200_000];
                    var read = await new StreamReader(stream).ReadBlockAsync(buffer, 0, buffer.Length);
                    var html = new string(buffer, 0, read);
                    string Meta(string name) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html,
                        $@"<meta[^>]+(?:property|name)=[""']{name}[""'][^>]+content=[""']([^""']*)|<meta[^>]+content=[""']([^""']*)[""'][^>]+(?:property|name)=[""']{name}[""']",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m ? (m.Groups[1].Value + m.Groups[2].Value) : "").Trim();
                    title = Meta("og:title") is { Length: > 0 } t ? t : System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>([^<]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value).Trim();
                    text = Meta("og:description") is { Length: > 0 } d ? d : Meta("description");
                    image = Meta("og:image");
                    if (image.StartsWith("/")) image = new Uri(new Uri(url), image).ToString();
                }
            }
            if (title.Length > 0)
            {
                (line.PreviewUrl, line.PreviewTitle, line.PreviewText) = (url, Clean(title, 120), Clean(text, 200));
                line.PreviewImage = image.StartsWith("https://") ? image : "";
            }
        }
        catch (Exception e) { Log.Write($"Social: no preview for a link ({e.GetType().Name})"); }
        void Done() { SaveChats(); Changed?.Invoke(); }
        if (ui != null) ui.Post(_ => Done(), null); else Done();
    }

    // --- Scheduled messages ---

    /// <summary>"Send Alex happy birthday tomorrow at 9": kept, and sent then (see <see cref="SendDueAsync"/>).</summary>
    public static string Schedule(Settings s, string code, string text, DateTimeOffset at, string reply = "")
    {
        var friend = s.Friends.FirstOrDefault(f => f.Code == code && f.Status == "friend");
        text = Clean(text, 1000);
        if (friend == null || text.Length == 0) return s.Say("I couldn't schedule that.", "Dat kon ik niet inplannen.");
        if (at <= DateTimeOffset.Now.AddSeconds(5)) at = DateTimeOffset.Now.AddSeconds(5);
        s.ScheduledMessages.Add(new Settings.ScheduledMessage { Id = Guid.NewGuid().ToString("N"), Code = code, Text = text, Reply = reply, At = at });
        s.Save();
        Changed?.Invoke();
        var when = at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.GetCultureInfo(s.IsDutch ? "nl-NL" : "en-GB"));
        return "⏰ " + s.Say($"I'll send it to {friend.Name} at {when}.", $"Ik stuur het om {when} naar {friend.Name}.");
    }

    public static void Unschedule(Settings s, string id)
    {
        if (s.ScheduledMessages.RemoveAll(m => m.Id == id) > 0) { s.Save(); Changed?.Invoke(); }
    }

    /// <summary>Every second (with the reminders): sends the scheduled messages whose time has come.</summary>
    public static async Task SendDueAsync(Settings s)
    {
        var due = s.ScheduledMessages.Where(m => m.At <= DateTimeOffset.Now).ToList();
        if (due.Count == 0) return;
        s.ScheduledMessages.RemoveAll(due.Contains);
        s.Save();
        foreach (var m in due)
        {
            var why = await SendAsync(s, m.Code, m.Text, m.Reply);
            if (why != null) Log.Write($"Social: a scheduled message couldn't be sent ({why})");
        }
    }

    // --- Reactions and "typing…" ---

    /// <summary>When each friend last said they're typing (shown for a few seconds).</summary>
    private static readonly Dictionary<string, DateTime> Typing = new();
    private static readonly Dictionary<string, DateTime> TypingSent = new();

    /// <summary>React to a message with an emoji (the same one again takes it away).</summary>
    public static async Task ReactAsync(Settings s, string code, string id, string emoji)
    {
        var line = Lines(code).LastOrDefault(l => l.Id == id && id.Length > 0);
        var friend = s.Friends.FirstOrDefault(f => f.Code == code);
        if (line == null || friend is not { Status: "friend", PublicKey.Length: > 0 }) return;
        line.MyReaction = line.MyReaction == emoji ? "" : Clean(emoji, 8);
        SaveChats();
        Changed?.Invoke();
        await SealAsync(s, friend, new JsonObject { ["type"] = "react", ["target"] = id, ["emoji"] = line.MyReaction });
    }

    /// <summary>"You're typing": at most every 8 seconds, and not kept on the server (it only matters right now).</summary>
    public static async Task TypingAsync(Settings s, string code)
    {
        var friend = s.Friends.FirstOrDefault(f => f.Code == code);
        if (friend is not { Status: "friend", PublicKey.Length: > 0 }) return;
        if (TypingSent.TryGetValue(code, out var last) && DateTime.Now - last < TimeSpan.FromSeconds(8)) return;
        TypingSent[code] = DateTime.Now;
        await SealAsync(s, friend, new JsonObject { ["type"] = "typing" }, keep: false);
    }

    public static void MarkRead(Settings s, string code)
    {
        if (s.Friends.FirstOrDefault(f => f.Code == code) is not { } friend) return;
        if (friend.Unread > 0) { friend.Unread = 0; s.Save(); }
        SendSeen(s, friend); // "Seen" on their side
    }

    /// <summary>The friend [name] is about ("Alex", "alex", "Al"): exact first, then the start of a name. Null when none.</summary>
    public static Settings.Friend? Find(Settings s, string name)
    {
        var friends = s.Friends.Where(f => f.Status == "friend").ToList();
        name = name.Trim().Trim('.', '!', '?');
        // "reply …", "send it to him": whoever you chatted with last (or your only friend)
        if (name.Length == 0 || name.ToLowerInvariant() is "last" or "laatste" or "him" or "her" or "them" or "hem" or "haar" or "hen" or "ze" or "zij")
            return friends.OrderByDescending(f => LastAt(f.Code)).FirstOrDefault();
        return friends.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? friends.FirstOrDefault(f => f.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase))
            ?? friends.FirstOrDefault(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            // nearly the name ("Alec" for Alex: heard or "corrected" wrong): at most a letter or two off
            ?? friends.Select(f => (f, off: Distance(f.Name.ToLowerInvariant(), name.ToLowerInvariant())))
                      .Where(x => x.off <= Math.Max(1, Math.Min(2, x.f.Name.Length / 3))).OrderBy(x => x.off).Select(x => x.f).FirstOrDefault();
    }

    /// <summary>The friend named in [text] ("send that to alex"), whatever the AI made of the name; null when none is.</summary>
    public static Settings.Friend? NamedIn(Settings s, string text) =>
        s.Friends.Where(f => f.Status == "friend" && f.Name.Length > 1)
            .FirstOrDefault(f => System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(f.Name)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    /// <summary>How many letters to change, add or remove to get from [a] to [b].</summary>
    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int previous = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int was = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = was;
            }
        }
        return row[b.Length];
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
                ["code"] = f.Code, ["name"] = f.Name, ["status"] = f.Status, ["unread"] = f.Unread, ["secure"] = f.PublicKey.Length > 0,
                ["typing"] = Typing.TryGetValue(f.Code, out var typed) && DateTime.Now - typed < TimeSpan.FromSeconds(10),
                ["last"] = lines.Count > 0 ? (lines[^1].Mine ? s.Say("You: ", "Jij: ") : "") + lines[^1].Text : "",
                ["seenId"] = s.ReadReceipts ? f.SeenId : "", ["seenAt"] = f.SeenAt.ToString("HH:mm"),
                ["scheduled"] = new JsonArray(s.ScheduledMessages.Where(m => m.Code == f.Code).OrderBy(m => m.At).Select(m => (JsonNode)new JsonObject
                {
                    ["id"] = m.Id, ["text"] = m.Text,
                    ["at"] = m.At.ToString(m.At.Date == DateTime.Today ? "HH:mm" : "ddd d MMM HH:mm", System.Globalization.CultureInfo.GetCultureInfo(s.IsDutch ? "nl-NL" : "en-GB")),
                }).ToArray()),
                ["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject
                {
                    ["mine"] = l.Mine, ["text"] = l.Text, ["id"] = l.Id, ["kind"] = l.Kind, ["name"] = l.Name, ["failed"] = l.Failed,
                    // the file, through the window's own address for Dave's files (see DaveWindow); "" while it's coming in
                    ["url"] = l.File.Length > 0 ? "https://dave-files.example/" + Uri.EscapeDataString(Path.GetFileName(l.File)) : "",
                    ["mine_reaction"] = l.MyReaction, ["their_reaction"] = l.TheirReaction,
                    ["reply"] = l.ReplyTo,
                    ["edited"] = l.Edited, ["transcript"] = l.Transcript, ["translation"] = l.Translation,
                    ["preview"] = l.PreviewUrl.Length > 1 ? new JsonObject { ["url"] = l.PreviewUrl, ["title"] = l.PreviewTitle, ["text"] = l.PreviewText, ["image"] = l.PreviewImage } : null,
                    ["ts"] = l.At.ToString("yyyy-MM-dd"), // (for searching by date)
                    ["at"] = l.At.ToString(l.At.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm"),
                }).ToArray()),
            });
        }
        return new JsonObject
        {
            ["code"] = MyCode(s), ["name"] = MyName(s), ["friends"] = friends,
            ["doubleClick"] = s.DoubleClickAction, ["doubleClickEmoji"] = s.DoubleClickEmoji, // what double-clicking a message does
            ["translate"] = s.Translate, ["translateAuto"] = s.TranslateAutomatically,
        };
    }

    // --- Sending and keeping the chats ---

    /// <summary>A friend request or an accept in [code]'s channel, with your name and public key. Null when it went, otherwise why not.</summary>
    private static Task<string?> PublishAsync(Settings s, string code, string type) =>
        PostAsync(s, code, new JsonObject { ["dave"] = 2, ["type"] = type, ["from"] = MyCode(s), ["name"] = MyName(s), ["key"] = MyPublicKey(s) });

    /// <summary>
    /// A message only [friend] can read: [inner] (with your name, the time and an id added) locked with AES-GCM, using a key
    /// made from your private key and their public one (the same key comes out on their side: ECDH). Also proves it's from you.
    /// </summary>
    private static Task<string?> SealAsync(Settings s, Settings.Friend friend, JsonObject inner, bool keep = true)
    {
        inner["name"] = MyName(s);
        inner["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        inner["id"] ??= Guid.NewGuid().ToString("N");
        var key = SharedKey(s, friend);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(inner.ToJsonString());
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Direction(MyCode(s), friend.Code));
        return PostAsync(s, friend.Code, new JsonObject
        {
            ["dave"] = 2, ["from"] = MyCode(s),
            ["n"] = Convert.ToBase64String(nonce), ["c"] = Convert.ToBase64String(cipher), ["t"] = Convert.ToBase64String(tag),
        }, keep);
    }

    /// <summary>Unlocks a sealed message from [friend]; null when it isn't really from them (or was changed on the way).</summary>
    private static JsonNode? Open(Settings s, Settings.Friend friend, JsonNode m)
    {
        try
        {
            var nonce = Convert.FromBase64String(m["n"]?.ToString() ?? "");
            var cipher = Convert.FromBase64String(m["c"]?.ToString() ?? "");
            var tag = Convert.FromBase64String(m["t"]?.ToString() ?? "");
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(SharedKey(s, friend), 16)) aes.Decrypt(nonce, cipher, tag, plain, Direction(friend.Code, MyCode(s)));
            return JsonNode.Parse(plain);
        }
        catch (Exception e)
        {
            Log.Write($"Social: a sealed message in {friend.Name}'s name couldn't be opened ({e.GetType().Name}): ignored");
            return null;
        }
    }

    /// <summary>Who sent it to whom: part of what's checked, so a message can't be passed off the other way round.</summary>
    private static byte[] Direction(string from, string to) => Encoding.UTF8.GetBytes($"dave-chat {from} -> {to}");

    // --- Keys ---

    /// <summary>Your private key (made the first time, kept in the settings on this PC).</summary>
    private static ECDiffieHellman MyKey(Settings s)
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        if (s.SocialKey.Length > 0)
        {
            try { key.ImportPkcs8PrivateKey(Convert.FromBase64String(s.SocialKey), out _); return key; }
            catch (Exception e) { Log.Write($"Social: the private key couldn't be read ({e.Message}), making a new one"); }
        }
        s.SocialKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        s.Save();
        return key;
    }

    /// <summary>You, as a "friend": for copies of your own messages (sealed with your own key, for your other devices).</summary>
    private static Settings.Friend Self(Settings s) => new() { Code = MyCode(s), Name = MyName(s), Status = "friend", PublicKey = MyPublicKey(s) };

    /// <summary>
    /// For the iPhone app, so it can chat on its own (also when the PC is off): your friend code, name and private key,
    /// and your friends with their public keys. Only over the phone link, which needs the pairing code.
    /// </summary>
    public static JsonObject Identity(Settings s)
    {
        using var key = MyKey(s);
        var d = key.ExportParameters(true).D!;
        return new JsonObject
        {
            ["code"] = MyCode(s), ["name"] = MyName(s), ["d"] = Convert.ToBase64String(d), ["key"] = MyPublicKey(s),
            ["friends"] = new JsonArray(s.Friends.Select(f => (JsonNode)new JsonObject
            {
                ["code"] = f.Code, ["name"] = f.Name, ["status"] = f.Status, ["key"] = f.PublicKey,
            }).ToArray()),
        };
    }

    /// <summary>Your public key, for friends (sent along with requests and accepts).</summary>
    private static string MyPublicKey(Settings s)
    {
        using var key = MyKey(s);
        return Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo());
    }

    /// <summary>[text] when it's a usable public key, otherwise null.</summary>
    private static string? ValidKey(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 400) return null;
        try
        {
            using var key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(text), out _);
            return key.KeySize == 256 ? text : null;
        }
        catch { return null; }
    }

    /// <summary>The key you and [friend] share: the same on both sides (ECDH), and nobody else can work it out.</summary>
    private static byte[] SharedKey(Settings s, Settings.Friend friend)
    {
        using var mine = MyKey(s);
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(Convert.FromBase64String(friend.PublicKey), out _);
        var secret = mine.DeriveKeyFromHash(theirs.PublicKey, HashAlgorithmName.SHA256);
        var codes = string.CompareOrdinal(MyCode(s), friend.Code) < 0 ? MyCode(s) + friend.Code : friend.Code + MyCode(s);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Encoding.UTF8.GetBytes("dave-social-v2"), Encoding.UTF8.GetBytes(codes));
    }

    /// <summary>Puts [message] in [code]'s channel. Null when it went, otherwise why not.</summary>
    /// <param name="keep">False for things that only matter right now ("typing…"): not kept for whoever comes online later.</param>
    private static async Task<string?> PostAsync(Settings s, string code, JsonObject message, bool keep = true)
    {
        var body = message.ToJsonString();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Post, Server + Topic(code)) { Content = new StringContent(body, Encoding.UTF8) };
            if (!keep) request.Headers.Add("Cache", "no");
            using var response = await Http.SendAsync(request, timeout.Token);
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
        }
        SaveChats();
    }

    private static void SaveChats()
    {
        lock (Sync)
        {
            var all = Chats();
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(ChatsPath, JsonSerializer.Serialize(all));
            }
            catch (Exception e) { Log.Write($"Social: couldn't save the chats: {e.Message}"); }
        }
    }
}
