using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>Carries out the commands the AI picks.</summary>
public static class Commands
{
    /// <summary>[Text] is shown in the bubble; [Speak] means say it out loud (answers, problems, confirmations).</summary>
    public record Outcome(string Text, bool Speak = false);

    public static async Task<Outcome> RunAsync(Settings s, string name, JsonObject args)
    {
        Log.Write($"Command {name} {args.ToJsonString()}");
        var spotify = Spotify.IsConnected(s);
        try
        {
            return name switch
            {
                "media_control" => await MediaControlAsync(s, Str(args, "action"), spotify),
                "set_volume" => SetVolume(s, args),
                "play_music" => spotify
                    ? new Outcome("🎵 " + await Spotify.PlayAsync(s, Str(args, "query"), Str(args, "kind")))
                    : NeedSpotify(s),
                "now_playing" => await NowPlayingAsync(s),
                "like_song" => !spotify ? NeedSpotify(s) : !await MediaSession.IsSpotifyOrNothingAsync() ? OnlySpotify(s)
                    : new Outcome("❤ " + (await Spotify.LikeCurrentAsync(s)).Name),
                "add_to_playlist" => !spotify ? NeedSpotify(s) : !await MediaSession.IsSpotifyOrNothingAsync() ? OnlySpotify(s)
                    : await AddToPlaylistAsync(s, Str(args, "playlist")),
                "play_mix" => spotify ? await PlayMixAsync(s, Str(args, "description"), Int(args, "minutes") ?? 30) : NeedSpotify(s),
                "remember" => Remember(s, Str(args, "fact")),
                "forget" => Forget(s, Str(args, "fact")),
                "set_reminder" => SetReminder(s, args),
                "cancel_reminders" => CancelReminders(s),
                "open_app" => OpenApp(s, Str(args, "name")),
                "open_website" => OpenWebsite(s, args),
                "lock_pc" => LockPc(),
                "find_file" => await FindFileAsync(s, args),
                "close_app" => CloseApp(s, Str(args, "name")),
                "close_all_apps" => CloseAll(s, Str(args, "keep")),
                _ => new Outcome(s.Say("I can't do that yet.", "Dat kan ik nog niet."), true),
            };
        }
        catch (Spotify.SpotifyException e)
        {
            return new Outcome(e.Message, true);
        }
    }

    private static string Str(JsonObject args, string key) => args[key]?.ToString() ?? "";
    private static int? Int(JsonObject args, string key) => args[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static Outcome NeedSpotify(Settings s) => new(s.Say(
        "For that I need Spotify. Connect it in Dave's settings first.",
        "Daarvoor heb ik Spotify nodig. Koppel het eerst in de instellingen van Dave."), true);

    private static Outcome OnlySpotify(Settings s) => new(s.Say(
        "I can only do that for songs playing in Spotify.",
        "Dat kan ik alleen doen voor nummers die in Spotify spelen."), true);

    private static string Label(Settings s, string action) => action switch
    {
        "play" => s.Say("▶ Play", "▶ Afspelen"),
        "pause" => s.Say("⏸ Paused", "⏸ Gepauzeerd"),
        "next" => s.Say("⏭ Next song", "⏭ Volgend nummer"),
        _ => s.Say("⏮ Previous song", "⏮ Vorig nummer"),
    };

    /// <summary>Control whatever is playing (YouTube Music, Spotify, a video…); media keys if Windows doesn't know.</summary>
    private static async Task<Outcome> MediaControlAsync(Settings s, string action, bool spotify)
    {
        if (!await MediaSession.ControlAsync(action)) SystemAudio.MediaKey(action);
        return new Outcome(Label(s, action));
    }

    private static Outcome SetVolume(Settings s, JsonObject args)
    {
        if (args["mute"] is JsonValue m && m.TryGetValue<bool>(out var mute))
        {
            SystemAudio.SetMute(mute);
            return new Outcome(mute ? "🔇" : "🔊 " + SystemAudio.GetVolume() + "%");
        }
        var current = SystemAudio.GetVolume();
        var target = Int(args, "level") ?? Str(args, "change") switch
        {
            "up" => current + 15,
            "down" => current - 15,
            _ => current,
        };
        return new Outcome($"🔊 {SystemAudio.SetVolume(target)}%");
    }

    private static async Task<Outcome> NowPlayingAsync(Settings s)
    {
        // Windows knows what any app is playing (YouTube Music too); Spotify's own info is the fallback.
        if (await MediaSession.NowPlayingAsync() is { } playing)
            return new Outcome(playing.Artist.Length > 0
                ? s.Say($"This is {playing.Title} by {playing.Artist}.", $"Dit is {playing.Title} van {playing.Artist}.")
                : s.Say($"This is {playing.Title}.", $"Dit is {playing.Title}."), true);
        if (!Spotify.IsConnected(s)) return new Outcome(s.Say("Nothing is playing right now.", "Er speelt nu niets."), true);
        var track = await Spotify.NowPlayingAsync(s);
        return new Outcome(track == null
            ? s.Say("Nothing is playing right now.", "Er speelt nu niets.")
            : s.Say($"This is {track.Name} by {track.Artist}.", $"Dit is {track.Name} van {track.Artist}."), true);
    }

    private static async Task<Outcome> AddToPlaylistAsync(Settings s, string playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist)) playlist = "Dave";
        var track = await Spotify.AddCurrentToPlaylistAsync(s, playlist);
        return new Outcome($"➕ {track.Name} → {playlist}");
    }

    /// <summary>DJ mode: the AI picks songs, Spotify plays them in order.</summary>
    private static async Task<Outcome> PlayMixAsync(Settings s, string description, int minutes)
    {
        if (string.IsNullOrWhiteSpace(description)) description = "mixed hits";
        var count = Math.Clamp((int)Math.Round(minutes / 3.5), 5, 20);
        var songs = await Assistant.MixSongsAsync(s, description, count);
        var lookups = songs.Select(song => Spotify.FindTrackAsync(s, song?["title"]?.ToString() ?? "", song?["artist"]?.ToString() ?? ""));
        var found = (await Task.WhenAll(lookups.Select(async t => { try { return await t; } catch { return null; } })))
            .Where(u => u != null).Cast<Spotify.Song>().ToList();
        if (found.Count == 0) return new Outcome(s.Say("I couldn't put that mix together.", "Ik kon die mix niet samenstellen."), true);
        await Spotify.PlaySongsAsync(s, found);
        return new Outcome($"🎧 {description} · {found.Count}");
    }

    private static Outcome Remember(Settings s, string fact)
    {
        if (!string.IsNullOrWhiteSpace(fact))
        {
            s.Memories.Add(fact.Trim());
            while (s.Memories.Count > 50) s.Memories.RemoveAt(0);
            s.Save();
        }
        return new Outcome(s.Say("Got it, I'll remember that.", "Oké, dat onthoud ik."), true);
    }

    /// <summary>Forget the remembered fact that shares the most words with [fact], or everything.</summary>
    private static Outcome Forget(Settings s, string fact)
    {
        var wanted = fact.ToLowerInvariant();
        if (wanted.Length == 0 || wanted.Contains("everything") || wanted.Contains("alles"))
        {
            s.Memories.Clear();
        }
        else
        {
            var words = wanted.Split(' ', ',', '.').Where(w => w.Length > 2).ToHashSet();
            var best = s.Memories.OrderByDescending(m => m.ToLowerInvariant().Split(' ', ',', '.').Count(words.Contains)).FirstOrDefault();
            if (best != null) s.Memories.Remove(best);
        }
        s.Save();
        return new Outcome(s.Say("Okay, I've forgotten that.", "Oké, dat ben ik vergeten."), true);
    }

    private static Outcome SetReminder(Settings s, JsonObject args)
    {
        var message = Str(args, "message");
        if (message.Length == 0) message = s.Say("your reminder", "je herinnering");
        var minutes = args["minutes"] is JsonValue v && v.TryGetValue<double>(out var m) ? m : (double?)null;
        var at = Reminders.Schedule(s, minutes, Str(args, "time"), message);
        if (at == null) return new Outcome(s.Say("I didn't get when to remind you.", "Ik snapte niet wanneer ik je moet herinneren."), true);
        if (minutes is > 0 and < 60)
        {
            // A timer: say how long, that's what you asked for (and it's exact, down to the second).
            var span = TimeSpan.FromSeconds(Math.Round(minutes.Value * 60));
            return new Outcome(s.Say($"Okay, timer set for {Reminders.Duration(span, false)}.", $"Oké, timer gezet voor {Reminders.Duration(span, true)}."), true);
        }
        return new Outcome(s.Say($"Okay, I'll remind you at {at:HH:mm}.", $"Oké, ik herinner je om {at:HH:mm}."), true);
    }

    private static Outcome CancelReminders(Settings s)
    {
        var count = Reminders.CancelAll(s);
        return new Outcome(s.Say($"Okay, I cancelled {count} reminders.", $"Oké, ik heb {count} herinneringen geannuleerd."), true);
    }

    private static Outcome OpenApp(Settings s, string name)
    {
        var opened = PcActions.OpenApp(name);
        return opened != null
            ? new Outcome("🚀 " + opened)
            : new Outcome(s.Say($"I couldn't find {name} on this PC.", $"Ik kon {name} niet vinden op deze pc."), true);
    }

    private static Outcome OpenWebsite(Settings s, JsonObject args)
    {
        var browser = Str(args, "browser");
        if (browser.Length == 0) browser = s.Browser; // Dave's default browser from the settings
        return new Outcome("🌐 " + PcActions.OpenWebsite(Str(args, "url"), Str(args, "search"), browser));
    }

    private static async Task<Outcome> FindFileAsync(Settings s, JsonObject args)
    {
        var query = Str(args, "query");
        var kind = Str(args, "kind") is { Length: > 0 } k ? k : "any";
        var newest = args["newest"] is JsonValue v && v.TryGetValue<bool>(out var n) && n;
        var action = Str(args, "action") is { Length: > 0 } a ? a : "open";

        var found = await FileFinder.FindAsync(query, kind, newest);
        if (found.Count == 0)
        {
            var what = query.Length > 0 ? query : s.Say("that", "dat");
            return new Outcome(s.Say($"I couldn't find {what} on this PC.", $"Ik kon {what} niet vinden op deze pc."), true);
        }
        var best = found[0];
        var name = Path.GetFileName(best.Path);
        var where = FileFinder.Where(best.Path);
        var more = found.Count > 1 ? s.Say($" There are {found.Count - 1} more like it.", $" Er zijn er nog {found.Count - 1} die erop lijken.") : "";
        Log.Write($"Found {best.Path} ({found.Count} matches)");
        switch (action)
        {
            case "show_in_folder":
                FileFinder.ShowInFolder(best.Path);
                return new Outcome("📁 " + name);
            case "tell":
                return new Outcome(s.Say($"{name} is in {where}.{more}", $"{name} staat in {where}.{more}"), true);
            default:
                FileFinder.Open(best.Path);
                return new Outcome("📄 " + name);
        }
    }

    private static Outcome CloseApp(Settings s, string name)
    {
        var closed = PcActions.CloseApp(name);
        return closed.Count > 0
            ? new Outcome("✖ " + string.Join(", ", closed))
            : new Outcome(s.Say($"I couldn't find {name} open.", $"Ik zie {name} niet openstaan."), true);
    }

    private static Outcome CloseAll(Settings s, string keep)
    {
        var closed = PcActions.CloseAllExcept(keep);
        return closed.Count > 0
            ? new Outcome("✖ " + string.Join(", ", closed))
            : new Outcome(s.Say("There was nothing to close.", "Er was niets om te sluiten."), true);
    }

    private static Outcome LockPc()
    {
        PcActions.LockPc();
        return new Outcome("🔒");
    }
}
