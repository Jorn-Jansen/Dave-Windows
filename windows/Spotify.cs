using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>
/// Controls Spotify (Premium) through the Web API: on this PC, or wherever it's playing.
/// Login uses PKCE with a local redirect, so no client secret lives in the app.
/// </summary>
public static class Spotify
{
    public const string RedirectUri = "http://127.0.0.1:8765/callback";
    private const string Scopes = "user-modify-playback-state user-read-playback-state user-read-currently-playing " +
                                  "user-library-modify playlist-read-private playlist-modify-private playlist-modify-public";
    private const string Accounts = "https://accounts.spotify.com";
    private const string Api = "https://api.spotify.com/v1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public class SpotifyException(string message) : Exception(message);

    public record Track(string Name, string Artist, string Uri);

    public static bool IsConnected(Settings s) => s.SpotifyRefresh.Length > 0;

    // --- Login ---

    /// <summary>Opens the Spotify login in the browser and waits for it to come back to 127.0.0.1:8765.</summary>
    public static async Task LoginAsync(Settings settings)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = $"{Accounts}/authorize?response_type=code&client_id={Uri.EscapeDataString(settings.SpotifyClientId)}" +
                  $"&scope={Uri.EscapeDataString(Scopes)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                  $"&code_challenge_method=S256&code_challenge={challenge}";

        var listener = new TcpListener(IPAddress.Loopback, 8765);
        listener.Start();
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            var request = await new StreamReader(stream).ReadLineAsync() ?? ""; // "GET /callback?code=... HTTP/1.1"
            var query = request.Split(' ').ElementAtOrDefault(1)?.Split('?').ElementAtOrDefault(1) ?? "";
            var parts = query.Split('&').Select(p => p.Split('=')).Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

            var ok = parts.TryGetValue("code", out var code);
            var page = ok ? "<h2>Spotify connected ✅</h2><p>You can close this tab and go back to Dave.</p>"
                          : "<h2>Spotify login cancelled</h2>";
            var html = Encoding.UTF8.GetBytes($"<html><body style='font-family:sans-serif;text-align:center;margin-top:15%'>{page}</body></html>");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(html);
            if (!ok) throw new SpotifyException("Spotify login cancelled");

            await RequestTokenAsync(settings, new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = settings.SpotifyClientId,
                ["code_verifier"] = verifier,
            });
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> AccessTokenAsync(Settings s)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > s.SpotifyExpiry - 60_000)
        {
            await RequestTokenAsync(s, new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = s.SpotifyRefresh,
                ["client_id"] = s.SpotifyClientId,
            });
        }
        return s.SpotifyAccess;
    }

    private static async Task RequestTokenAsync(Settings s, Dictionary<string, string> form)
    {
        using var response = await Http.PostAsync($"{Accounts}/api/token", new FormUrlEncodedContent(form));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Log.Write($"Spotify token error {(int)response.StatusCode}: {text}");
            throw new SpotifyException(s.Say("I'm not logged in to Spotify anymore. Connect it again in Dave's settings.",
                "Ik ben niet meer ingelogd bij Spotify. Koppel het opnieuw in de instellingen van Dave."));
        }
        var json = JsonNode.Parse(text)!;
        s.SpotifyAccess = json["access_token"]!.GetValue<string>();
        s.SpotifyExpiry = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (json["expires_in"]?.GetValue<long>() ?? 3600) * 1000;
        if (json["refresh_token"]?.GetValue<string>() is { Length: > 0 } refresh) s.SpotifyRefresh = refresh;
        if (json["scope"]?.GetValue<string>() is { Length: > 0 } scope) s.SpotifyScopes = scope;
        s.Save();
    }

    // --- Playback ---

    /// <summary>A song plus its album. The Spotify desktop app ignores "play these tracks" (uris) but does play "this album, from this song".</summary>
    public record Song(string Uri, string AlbumUri);

    /// <summary>Search in the user's country, so we get the original release rather than a cover or a version that won't play.</summary>
    private static string Market(Settings s)
    {
        var c = s.Country.ToLowerInvariant();
        var code = c.Contains("nether") || c.Contains("nederland") ? "NL" : c.Contains("belg") ? "BE" : c.Contains("german") || c.Contains("duits") ? "DE"
            : c.Contains("kingdom") || c.Contains("england") ? "GB" : c.Contains("united states") || c == "usa" ? "US" : c.Contains("france") ? "FR" : "";
        return code.Length > 0 ? $"&market={code}" : "";
    }

    private static Song? ToSong(JsonNode? track) =>
        track?["uri"]?.GetValue<string>() is { } uri && track["album"]?["uri"]?.GetValue<string>() is { } album ? new Song(uri, album) : null;

    public static async Task<string> PlayAsync(Settings s, string query, string kind)
    {
        if (kind is "song" or "" && RandomRequest(query) is { } topic) return await PlayRandomAsync(s, topic);
        if (kind == "liked_songs")
        {
            await PlayLikedSongsAsync(s);
            return s.Say("Your liked songs, shuffled", "Je favoriete nummers, geshuffeld");
        }
        if (kind == "playlist" && s.SpotifyScopes.Contains("playlist-read-private") && await FindOwnPlaylistAsync(s, query) is { } own)
        {
            // The user's own playlists first: "my gaming playlist" is theirs, not a public one called "gaming"
            await PlayerCommandAsync(s, HttpMethod.Put, "/me/player/play", new JsonObject { ["context_uri"] = own.uri });
            return own.name;
        }
        var type = kind switch { "artist" => "artist", "album" => "album", "playlist" => "playlist", _ => "track" };
        var (_, text) = await ApiAsync(s, HttpMethod.Get, $"/search?type={type}&limit=5{Market(s)}&q={Uri.EscapeDataString(query)}");
        var items = JsonNode.Parse(text)?[$"{type}s"]?["items"]?.AsArray() ?? new JsonArray();
        var found = items.FirstOrDefault(i => i != null)
                    ?? throw new SpotifyException(s.Say($"I couldn't find {query} on Spotify.", $"Ik kon {query} niet vinden op Spotify."));
        if (type == "track" && ToSong(found) is { } song) await PlaySongAsync(s, song, 0);
        else await PlayerCommandAsync(s, HttpMethod.Put, "/me/player/play", new JsonObject { ["context_uri"] = found["uri"]!.GetValue<string>() });
        var artist = found["artists"]?[0]?["name"]?.GetValue<string>();
        return artist != null ? $"{found["name"]} – {artist}" : found["name"]!.GetValue<string>();
    }

    private static readonly HashSet<string> RandomWords = new() { "random", "any", "anything", "something", "whatever", "surprise", "willekeurig", "willekeurige", "iets", "random's" };
    private static readonly HashSet<string> FillerWords = new() { "a", "an", "some", "song", "songs", "track", "music", "me", "play", "een", "nummer", "nummertje", "liedje", "muziek", "maar", "wat" };

    /// <summary>
    /// For "random song" / "something" / "a random rock song": what to pick from ("" for anything), or null when it's a normal request.
    /// Searching for "random song" literally always gave the same result ("Ransom").
    /// </summary>
    private static string? RandomRequest(string query)
    {
        var words = Regex.Replace(query.ToLowerInvariant(), @"[^\p{L}\p{N}' ]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) return "";
        if (!words.Any(RandomWords.Contains)) return null;
        return string.Join(' ', words.Where(w => !RandomWords.Contains(w) && !FillerWords.Contains(w)));
    }

    /// <summary>A random song: from a random spot in the search results for [topic], or for a random letter when it's just "anything".</summary>
    private static async Task<string> PlayRandomAsync(Settings s, string topic)
    {
        var (found, song) = await FindRandomAsync(s, topic);
        await PlaySongAsync(s, song, 0);
        return Describe(found);
    }

    /// <summary>
    /// A random song: from a random spot in the search results for [topic], or for a random letter when it's just "anything".
    /// Never one you said you don't like.
    /// </summary>
    private static async Task<(JsonNode found, Song song)> FindRandomAsync(Settings s, string topic)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var q = topic.Length > 0 ? topic : ((char)('a' + Random.Shared.Next(26))).ToString();
            var offset = Random.Shared.Next(topic.Length > 0 ? 40 : 200); // popular songs: deeper for "anything", shallower for a topic
            var (_, text) = await ApiAsync(s, HttpMethod.Get, $"/search?type=track&limit=10&offset={offset}{Market(s)}&q={Uri.EscapeDataString(q)}");
            var items = (JsonNode.Parse(text)?["tracks"]?["items"]?.AsArray() ?? new JsonArray())
                .Where(i => i != null && !MusicWatcher.IsDisliked(s, i["name"]?.GetValue<string>() ?? "", i["artists"]?[0]?["name"]?.GetValue<string>() ?? ""))
                .ToList();
            if (items.Count == 0) continue;
            var found = items[Random.Shared.Next(items.Count)]!;
            if (ToSong(found) is not { } song) continue;
            Log.Write($"Random song (from '{q}', offset {offset}): {Describe(found)}");
            return (found, song);
        }
        throw new SpotifyException(s.Say("I couldn't find a random song right now.", "Ik kon nu geen willekeurig nummer vinden."));
    }

    private static string Describe(JsonNode track)
    {
        var artist = track["artists"]?[0]?["name"]?.GetValue<string>();
        return artist != null ? $"{track["name"]} – {artist}" : track["name"]!.GetValue<string>();
    }

    /// <summary>Play one song from [positionMs], as "its album, starting at this song" (works on the desktop app too).</summary>
    public static Task PlaySongAsync(Settings s, Song song, int positionMs) =>
        PlayerCommandAsync(s, HttpMethod.Put, "/me/player/play", new JsonObject
        {
            ["context_uri"] = song.AlbumUri,
            ["offset"] = new JsonObject { ["uri"] = song.Uri },
            ["position_ms"] = positionMs,
        });

    public static Task ControlAsync(Settings s, string action) => action switch
    {
        "play" => PlayerCommandAsync(s, HttpMethod.Put, "/me/player/play", null),
        "pause" => PlayerCommandAsync(s, HttpMethod.Put, "/me/player/pause", null),
        "next" => PlayerCommandAsync(s, HttpMethod.Post, "/me/player/next", null),
        _ => PlayerCommandAsync(s, HttpMethod.Post, "/me/player/previous", null),
    };

    public static async Task<Song?> FindTrackAsync(Settings s, string title, string artist)
    {
        foreach (var query in new[] { $"track:{title} artist:{artist}", $"{title} {artist}" })
        {
            var (_, text) = await ApiAsync(s, HttpMethod.Get, $"/search?type=track&limit=1{Market(s)}&q={Uri.EscapeDataString(query)}");
            if (ToSong(JsonNode.Parse(text)?["tracks"]?["items"]?[0]) is { } song) return song;
        }
        return null;
    }

    /// <summary>DJ mode: play the first song, queue the rest (the desktop app ignores a list of tracks).</summary>
    public static async Task PlaySongsAsync(Settings s, IReadOnlyList<Song> songs)
    {
        await PlaySongAsync(s, songs[0], 0);
        foreach (var song in songs.Skip(1))
        {
            try { await ApiAsync(s, HttpMethod.Post, $"/me/player/queue?uri={Uri.EscapeDataString(song.Uri)}"); }
            catch (SpotifyException e) { Log.Write($"Queueing failed: {e.Message}"); }
        }
    }

    public static async Task PauseAsync(Settings s)
    {
        try { await ControlAsync(s, "pause"); } catch (SpotifyException) { /* already paused */ }
    }

    /// <summary>"Play X next": the first matching song goes into the queue, after the current one.</summary>
    public static async Task<string> QueueAsync(Settings s, string query)
    {
        if (RandomRequest(query) is { } topic) // "queue a random song": not a search for the word "random"
        {
            var (random, song) = await FindRandomAsync(s, topic);
            await PlayerCommandAsync(s, HttpMethod.Post, $"/me/player/queue?uri={Uri.EscapeDataString(song.Uri)}", null);
            return Describe(random);
        }
        var (_, text) = await ApiAsync(s, HttpMethod.Get, $"/search?type=track&limit=1{Market(s)}&q={Uri.EscapeDataString(query)}");
        var found = JsonNode.Parse(text)?["tracks"]?["items"]?[0]
                    ?? throw new SpotifyException(s.Say($"I couldn't find {query} on Spotify.", $"Ik kon {query} niet vinden op Spotify."));
        await PlayerCommandAsync(s, HttpMethod.Post, $"/me/player/queue?uri={Uri.EscapeDataString(found["uri"]!.GetValue<string>())}", null);
        return $"{found["name"]} – {found["artists"]?[0]?["name"]}";
    }

    /// <summary>Songs after the current one, for "queue some songs like this".</summary>
    public static async Task QueueSongsAsync(Settings s, IEnumerable<Song> songs)
    {
        foreach (var song in songs)
        {
            try { await PlayerCommandAsync(s, HttpMethod.Post, $"/me/player/queue?uri={Uri.EscapeDataString(song.Uri)}", null); }
            catch (SpotifyException e) { Log.Write($"Queueing failed: {e.Message}"); }
        }
    }

    /// <summary>The user's Liked Songs, shuffled.</summary>
    public static async Task PlayLikedSongsAsync(Settings s)
    {
        var (_, me) = await ApiAsync(s, HttpMethod.Get, "/me");
        var id = JsonNode.Parse(me)?["id"]?.GetValue<string>() ?? throw new SpotifyException(s.Say("Spotify didn't tell me who you are.", "Spotify vertelde niet wie je bent."));
        await PlayerCommandAsync(s, HttpMethod.Put, "/me/player/play", new JsonObject { ["context_uri"] = $"spotify:user:{id}:collection" });
        try { await ApiAsync(s, HttpMethod.Put, "/me/player/shuffle?state=true"); } catch (SpotifyException) { /* shuffle is a bonus */ }
    }

    public static Task SetShuffleAsync(Settings s, bool on) => PlayerCommandAsync(s, HttpMethod.Put, $"/me/player/shuffle?state={(on ? "true" : "false")}", null);

    /// <summary>[mode]: "track" (this song), "context" (the album/playlist) or "off".</summary>
    public static Task SetRepeatAsync(Settings s, string mode) => PlayerCommandAsync(s, HttpMethod.Put, $"/me/player/repeat?state={mode}", null);

    /// <summary>Jump within the song: [seconds] forward (negative = back), or to [seconds] from the start when [absolute].</summary>
    public static async Task SeekAsync(Settings s, int seconds, bool absolute)
    {
        var position = 0L;
        if (!absolute)
        {
            var (status, text) = await ApiAsync(s, HttpMethod.Get, "/me/player");
            if (status == 204 || string.IsNullOrWhiteSpace(text)) throw new SpotifyException(s.Say("Nothing is playing right now.", "Er speelt nu niets."));
            position = JsonNode.Parse(text)?["progress_ms"]?.GetValue<long>() ?? 0;
        }
        var target = Math.Max(0, position + seconds * 1000L);
        await PlayerCommandAsync(s, HttpMethod.Put, $"/me/player/seek?position_ms={target}", null);
    }

    // --- What's playing, liking, playlists ---

    private static void RequireScope(Settings s, string scope)
    {
        if (!s.SpotifyScopes.Split(' ').Contains(scope))
            throw new SpotifyException(s.Say("For that, connect Spotify again in Dave's settings.", "Koppel Spotify opnieuw in de instellingen van Dave, dan kan ik dat ook."));
    }

    public static async Task<Track?> NowPlayingAsync(Settings s)
    {
        RequireScope(s, "user-read-currently-playing");
        var (status, text) = await ApiAsync(s, HttpMethod.Get, "/me/player/currently-playing");
        if (status == 204 || string.IsNullOrWhiteSpace(text)) return null;
        var item = JsonNode.Parse(text)?["item"];
        if (item == null) return null;
        return new Track(item["name"]!.GetValue<string>(), item["artists"]?[0]?["name"]?.GetValue<string>() ?? "", item["uri"]!.GetValue<string>());
    }

    private static async Task<Track> CurrentOrThrowAsync(Settings s) =>
        await NowPlayingAsync(s) ?? throw new SpotifyException(s.Say("Nothing is playing right now.", "Er speelt nu niets."));

    public static async Task<Track> LikeCurrentAsync(Settings s)
    {
        RequireScope(s, "user-library-modify");
        var track = await CurrentOrThrowAsync(s);
        await ApiAsync(s, HttpMethod.Put, $"/me/library?uris={Uri.EscapeDataString(track.Uri)}");
        return track;
    }

    public static async Task<Track> AddCurrentToPlaylistAsync(Settings s, string playlistName)
    {
        RequireScope(s, "playlist-modify-private");
        var track = await CurrentOrThrowAsync(s);
        var id = await FindPlaylistAsync(s, playlistName) ?? await CreatePlaylistAsync(s, playlistName);
        await ApiAsync(s, HttpMethod.Post, $"/playlists/{id}/items", new JsonObject { ["uris"] = new JsonArray(track.Uri) });
        return track;
    }

    /// <summary>
    /// One of the user's own playlists by name: an exact match first, otherwise the closest one
    /// ("gaming" finds "Gaming 🎮" or "My gaming mix"). Null when none comes close.
    /// </summary>
    private static async Task<string?> FindPlaylistAsync(Settings s, string name) => (await FindOwnPlaylistAsync(s, name))?.id;

    private static async Task<(string id, string name, string uri)?> FindOwnPlaylistAsync(Settings s, string name)
    {
        static string Simple(string x) => new(x.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var wanted = Simple(name.Replace("playlist", "", StringComparison.OrdinalIgnoreCase));
        if (wanted.Length == 0) return null;
        (string id, string name, string uri)? close = null;
        string? path = "/me/playlists?limit=50";
        for (int page = 0; page < 5 && path != null; page++)
        {
            var (_, text) = await ApiAsync(s, HttpMethod.Get, path);
            var json = JsonNode.Parse(text)!;
            foreach (var playlist in json["items"]?.AsArray() ?? new JsonArray())
            {
                var title = playlist?["name"]?.GetValue<string>() ?? "";
                var found = (playlist!["id"]!.GetValue<string>(), title, playlist["uri"]?.GetValue<string>() ?? "");
                if (Simple(title) == wanted) return found;
                if (close == null && Simple(title).Length > 0 && (Simple(title).Contains(wanted) || wanted.Contains(Simple(title)))) close = found;
            }
            var next = json["next"]?.GetValue<string>();
            path = next != null && next.StartsWith(Api) ? next[Api.Length..] : null;
        }
        return close;
    }

    private static async Task<string> CreatePlaylistAsync(Settings s, string name)
    {
        var (_, text) = await ApiAsync(s, HttpMethod.Post, "/me/playlists",
            new JsonObject { ["name"] = name, ["public"] = false, ["description"] = "Made by Dave" });
        return JsonNode.Parse(text)!["id"]!.GetValue<string>();
    }

    // --- Plumbing ---

    /// <summary>Send a player command; if nothing is active, aim it at this PC (starting Spotify if needed).</summary>
    private static async Task PlayerCommandAsync(Settings s, HttpMethod method, string path, JsonObject? body)
    {
        var (status, _) = await ApiAsync(s, method, path, body, allowNotFound: true);
        if (status != 404) return;

        var device = await PickDeviceAsync(s);
        if (device == null)
        {
            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); // open the Spotify app
            for (int i = 0; i < 8 && device == null; i++)
            {
                await Task.Delay(1500);
                device = await PickDeviceAsync(s);
            }
        }
        if (device == null)
            throw new SpotifyException(s.Say("Spotify isn't open anywhere. Open the Spotify app once.", "Spotify staat nergens open. Open de Spotify-app één keer."));
        var separator = path.Contains('?') ? '&' : '?';
        await ApiAsync(s, method, $"{path}{separator}device_id={device}", body);
    }

    private static async Task<string?> PickDeviceAsync(Settings s)
    {
        var (_, text) = await ApiAsync(s, HttpMethod.Get, "/me/player/devices");
        var devices = JsonNode.Parse(text)?["devices"]?.AsArray().Where(d => d != null).ToList() ?? new();
        var chosen = devices.FirstOrDefault(d => d!["is_active"]?.GetValue<bool>() == true)
                     ?? devices.FirstOrDefault(d => d!["type"]?.GetValue<string>() == "Computer")
                     ?? devices.FirstOrDefault();
        return chosen?["id"]?.GetValue<string>();
    }

    /// <summary>After "too many requests": no more asking until Spotify says it's fine again (asking anyway only makes it longer).</summary>
    private static DateTime restUntil = DateTime.MinValue;

    private static async Task<(int status, string text)> ApiAsync(Settings s, HttpMethod method, string path, JsonObject? body = null, bool allowNotFound = false)
    {
        if (DateTime.Now < restUntil)
            throw new SpotifyException(s.Say("Spotify is busy. Try again in a moment.", "Spotify heeft het druk. Probeer het zo nog eens."));
        using var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(s));
        if (body != null || method == HttpMethod.Put || method == HttpMethod.Post)
            request.Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request);
        var status = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync();
        if (status is >= 200 and < 300 || (allowNotFound && status == 404)) return (status, text);

        Log.Write($"Spotify {method} {path} -> {status}: {text}");
        if (status == 429)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            restUntil = DateTime.Now + TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 5, 600));
            Log.Write($"Spotify: too many requests; not asking until {restUntil:HH:mm:ss}");
        }
        throw new SpotifyException(status switch
        {
            401 => s.Say("I'm not logged in to Spotify anymore. Connect it again in Dave's settings.", "Ik ben niet meer ingelogd bij Spotify. Koppel het opnieuw in de instellingen van Dave."),
            403 when text.Contains("PREMIUM_REQUIRED") => s.Say("This needs Spotify Premium.", "Hiervoor is Spotify Premium nodig."),
            403 => s.Say("Spotify couldn't do that right now. Start some music first.", "Spotify kon dat nu niet doen. Zet eerst wat muziek aan."),
            404 => s.Say("Spotify isn't open anywhere. Open the Spotify app once.", "Spotify staat nergens open. Open de Spotify-app één keer."),
            429 => s.Say("Spotify is busy. Try again in a moment.", "Spotify heeft het druk. Probeer het zo nog eens."),
            _ => s.Say($"Spotify had a problem, error {status}.", $"Spotify had een probleem, foutcode {status}."),
        });
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
