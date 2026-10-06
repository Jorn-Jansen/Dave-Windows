using System.Globalization;
using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>Carries out the commands the AI picks.</summary>
public static class Commands
{
    /// <summary>[Text] is shown in the bubble; [Speak] means say it out loud (answers, problems, confirmations).</summary>
    public record Outcome(string Text, bool Speak = false);

    /// <param name="said">What the user said, word for word (for choices that shouldn't depend on the AI, like play vs. queue).</param>
    public static async Task<Outcome> RunAsync(Settings s, string name, JsonObject args, string said = "")
    {
        Log.Write($"Command {name} {args.ToJsonString()}");
        var spotify = Spotify.IsConnected(s);
        try
        {
            return name switch
            {
                "media_control" => await MediaControlAsync(s, Str(args, "action"), spotify),
                "set_volume" => SetVolume(s, args),
                "play_music" => !spotify ? NeedSpotify(s)
                    : WantsQueue(said) && Str(args, "kind") is "song" or ""
                        ? new Outcome("⏭ " + await Spotify.QueueAsync(s, Str(args, "query")))
                        : new Outcome("🎵 " + await Spotify.PlayAsync(s, Str(args, "query"), Str(args, "kind"))),
                "dislike_song" => await DislikeAsync(s),
                "undislike_song" => new Outcome(MusicWatcher.Undislike(s, Str(args, "song")) > 0
                    ? s.Say("Okay, I'll play that one again.", "Oké, die mag weer.")
                    : s.Say("That song wasn't on your skip list.", "Dat nummer stond niet op je overslaan-lijst."), true),
                // "next song" sometimes comes in as music settings (smaller AI models): it's still just next
                "music_settings" when Str(args, "action") is "next" or "previous" or "play" or "pause" => await MediaControlAsync(s, Str(args, "action"), spotify),
                "music_settings" => spotify ? await MusicSettingsAsync(s, args) : NeedSpotify(s),
                "now_playing" => await NowPlayingAsync(s),
                "like_song" => !spotify ? NeedSpotify(s) : !await MediaSession.IsSpotifyOrNothingAsync() ? OnlySpotify(s)
                    : new Outcome("❤ " + (await Spotify.LikeCurrentAsync(s)).Name),
                "add_to_playlist" => !spotify ? NeedSpotify(s) : !await MediaSession.IsSpotifyOrNothingAsync() ? OnlySpotify(s)
                    : await AddToPlaylistAsync(s, Str(args, "playlist")),
                "play_mix" => spotify ? await PlayMixAsync(s, Str(args, "description"), Int(args, "minutes") ?? 30, WantsQueue(said)) : NeedSpotify(s),
                "remember" => Remember(s, Str(args, "fact")),
                "forget" => Forget(s, Str(args, "fact")),
                "set_reminder" => SetReminder(s, args),
                "cancel_reminders" => CancelReminders(s, Str(args, "which")),
                "open_app" => OpenApp(s, Str(args, "name")),
                "open_website" => OpenWebsite(s, args),
                "lock_pc" => LockPc(),
                "find_file" => await FindFileAsync(s, args),
                "close_app" => CloseApp(s, Str(args, "name")),
                "close_all_apps" => CloseAll(s, Str(args, "keep")),
                "type_text" => await TypeAsync(s, Str(args, "text"), args["press_enter"] is JsonValue e && e.TryGetValue<bool>(out var enter) && enter),
                "copy_to_clipboard" => CopyToClipboard(s, Str(args, "text")),
                "window_control" => WindowControl.Run(s, Str(args, "action"), Str(args, "app"), Str(args, "app2"), Int(args, "screen") ?? 0) is { } problem
                    ? new Outcome(problem, true) : new Outcome("🪟 " + Str(args, "action").Replace('_', ' ')),
                "screenshot" => Screenshots.Take(s, Str(args, "what"), Str(args, "action") is { Length: > 0 } a ? a : "copy", Str(args, "where")),
                "calendar" => AddToCalendar(s, args),
                "quiet_mode" => QuietMode(s, Bool(args, "on"), Int(args, "minutes") ?? 0),
                "watch_for" => new Outcome(Watchers.Add(s, Str(args, "what"), Str(args, "program"),
                    args["number"] is JsonValue n && n.TryGetValue<double>(out var limit) ? limit : 0, Str(args, "message")), true),
                "stop_watching" => new Outcome(Watchers.CancelAll() is var stopped and > 0
                    ? s.Say($"Okay, I stopped watching {stopped} thing{(stopped == 1 ? "" : "s")}.", $"Oké, ik let er niet meer op ({stopped}).")
                    : s.Say("I wasn't keeping an eye on anything.", "Ik lette nergens op."), true),
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
    /// <summary>
    /// Queue instead of play only when the user said so in their own words ("queue", "next", "after this", "wachtrij",
    /// "hierna", "daarna"). Decided here, not by the AI: "play X" must always just play it.
    /// </summary>
    private static bool WantsQueue(string said) =>
        System.Text.RegularExpressions.Regex.IsMatch(said.ToLowerInvariant(),
            @"\bqueue|\bnext\b|after th(is|at|e current)|wachtrij|\bhierna\b|\bdaarna\b|als volgende|na dit|erachter");

    private static bool Bool(JsonObject args, string key) => args[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

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
    /// <remarks>
    /// Spotify (when connected and it's what's playing) is controlled directly through its own service: that never
    /// touches Windows' media controls, which can get stuck (and then even the media keys freeze the taskbar).
    /// </remarks>
    private static async Task<Outcome> MediaControlAsync(Settings s, string action, bool spotify)
    {
        if (spotify && await MediaSession.IsSpotifyOrNothingAsync())
        {
            try
            {
                await Spotify.ControlAsync(s, action);
                return new Outcome(Label(s, action));
            }
            catch (Spotify.SpotifyException e) { Log.Write($"Spotify {action} failed ({e.Message}); trying Windows' media controls"); }
        }
        if (!await MediaSession.ControlAsync(action)) SystemAudio.MediaKey(action);
        return new Outcome(Label(s, action));
    }

    private static Outcome SetVolume(Settings s, JsonObject args)
    {
        if (Str(args, "app") is { Length: > 0 } app) return SetAppVolume(s, app, args);
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

    /// <summary>One app's volume ("make Discord quieter", "mute Chrome", "Roblox to 30 percent").</summary>
    private static Outcome SetAppVolume(Settings s, string app, JsonObject args)
    {
        bool? mute = args["mute"] is JsonValue m && m.TryGetValue<bool>(out var x) ? x : null;
        var result = AppVolume.Set(app, Int(args, "level"), Str(args, "change"), mute);
        if (result == null)
        {
            var playing = AppVolume.List().Select(a => a.App.Split(" (")[0]).Distinct().Take(6).ToList();
            return new Outcome(s.Say(
                $"{app} isn't making any sound right now." + (playing.Count > 0 ? $" Apps with sound: {string.Join(", ", playing)}." : ""),
                $"{app} maakt nu geen geluid." + (playing.Count > 0 ? $" Apps met geluid: {string.Join(", ", playing)}." : "")), true);
        }
        var name = result.App.Split(" (")[0];
        return new Outcome(result.Muted ? $"🔇 {name}" : $"🔊 {name} {result.Level}%");
    }

    private static async Task<Outcome> NowPlayingAsync(Settings s)
    {
        // Windows knows what any app is playing (YouTube Music too); Spotify's own info is the fallback.
        if (await MediaSession.NowPlayingAsync(s) is { } playing)
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
    private static async Task<Outcome> PlayMixAsync(Settings s, string description, int minutes, bool queue)
    {
        if (string.IsNullOrWhiteSpace(description)) description = "mixed hits";
        var count = Math.Clamp((int)Math.Round(minutes / 3.5), 5, 20);
        var songs = (await Assistant.MixSongsAsync(s, description, count))
            .Where(song => !MusicWatcher.IsDisliked(s, song?["title"]?.ToString() ?? "", song?["artist"]?.ToString() ?? "")) // in case the AI picked one anyway
            .ToList();
        var lookups = songs.Select(song => Spotify.FindTrackAsync(s, song?["title"]?.ToString() ?? "", song?["artist"]?.ToString() ?? ""));
        var found = (await Task.WhenAll(lookups.Select(async t => { try { return await t; } catch { return null; } })))
            .Where(u => u != null).Cast<Spotify.Song>().ToList();
        if (found.Count == 0) return new Outcome(s.Say("I couldn't put that mix together.", "Ik kon die mix niet samenstellen."), true);
        if (queue) await Spotify.QueueSongsAsync(s, found);
        else await Spotify.PlaySongsAsync(s, found);
        return new Outcome($"🎧 {description} · {found.Count}{(queue ? s.Say(" queued", " in de wachtrij") : "")}");
    }

    private static async Task<Outcome> DislikeAsync(Settings s)
    {
        var song = await MusicWatcher.DislikeCurrentAsync(s);
        if (song == null) return new Outcome(s.Say("Nothing is playing right now.", "Er speelt nu niets."), true);
        return new Outcome(s.Say($"Skipped. I won't play {song.Title} again.", $"Overgeslagen. {song.Title} hoor je niet meer."), true);
    }

    /// <summary>Shuffle, repeat, and jumping within the song.</summary>
    private static async Task<Outcome> MusicSettingsAsync(Settings s, JsonObject args)
    {
        var done = new List<string>();
        if (args["shuffle"] is JsonValue shuffle && shuffle.TryGetValue<bool>(out var on))
        {
            await Spotify.SetShuffleAsync(s, on);
            done.Add(on ? "🔀 shuffle on" : "shuffle off");
        }
        if (Str(args, "repeat") is { Length: > 0 } repeat)
        {
            await Spotify.SetRepeatAsync(s, repeat);
            done.Add(repeat switch { "track" => "🔂 repeat song", "context" => "🔁 repeat", _ => "repeat off" });
        }
        if (Int(args, "seek_to") is int to)
        {
            await Spotify.SeekAsync(s, to, absolute: true);
            done.Add(to == 0 ? "⏮ from the start" : $"⏩ {to / 60}:{to % 60:00}");
        }
        else if (Int(args, "seek_seconds") is int by and not 0)
        {
            await Spotify.SeekAsync(s, by, absolute: false);
            done.Add(by > 0 ? $"⏩ +{by} s" : $"⏪ {by} s");
        }
        return done.Count > 0 ? new Outcome(string.Join(" · ", done)) : new Outcome(s.Say("I didn't get what to change.", "Ik snapte niet wat ik moest veranderen."), true);
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
        var repeat = Str(args, "repeat") is "none" ? "" : Str(args, "repeat");
        if (repeat.Length == 0 && Int(args, "every_minutes") is > 0) repeat = "every"; // "every 2 hours" sometimes comes without the repeat
        var reminder = Reminders.Schedule(s, minutes, Str(args, "time"), message, repeat, Str(args, "days"), Int(args, "every_minutes") ?? 0, Int(args, "month_day") ?? 0);
        if (reminder == null) return new Outcome(s.Say("I didn't get when to remind you.", "Ik snapte niet wanneer ik je moet herinneren."), true);
        var at = reminder.At;
        if (reminder.Repeat.Length > 0)
        {
            string Start(bool dutch)
            {
                if (at.Date == DateTime.Today) return dutch ? "vandaag" : "today";
                if (at.Date == DateTime.Today.AddDays(1)) return dutch ? "morgen" : "tomorrow";
                var culture = CultureInfo.GetCultureInfo(dutch ? "nl-NL" : "en-GB");
                return at.ToString(at.Date < DateTime.Today.AddDays(7) ? "dddd" : "d MMMM", culture); // a weekday this week, otherwise the date
            }
            return new Outcome(s.Say($"Okay, I'll remind you {Reminders.DescribeRepeat(reminder, false)}, starting {Start(false)}.",
                $"Oké, ik herinner je {Reminders.DescribeRepeat(reminder, true)}, vanaf {Start(true)}."), true);
        }
        if (minutes is > 0 and < 60)
        {
            // A timer: say how long, that's what you asked for (and it's exact, down to the second).
            var span = TimeSpan.FromSeconds(Math.Round(minutes.Value * 60));
            return new Outcome(s.Say($"Okay, timer set for {Reminders.Duration(span, false)}.", $"Oké, timer gezet voor {Reminders.Duration(span, true)}."), true);
        }
        return new Outcome(s.Say($"Okay, I'll remind you at {at:HH:mm}.", $"Oké, ik herinner je om {at:HH:mm}."), true);
    }

    /// <summary>Dictation: types into the window you're in (Dave's bubble never takes the focus away from it).</summary>
    private static async Task<Outcome> TypeAsync(Settings s, string text, bool pressEnter)
    {
        if (text.Length == 0) return new Outcome(s.Say("I didn't get what to type.", "Ik snapte niet wat ik moest typen."), true);
        await Task.Delay(300); // let the sound and bubble settle; the first letters can get lost otherwise
        await Task.Run(() =>
        {
            Keyboard.Type(text);
            if (pressEnter) { Thread.Sleep(80); Keyboard.Press("enter"); }
        });
        return new Outcome("⌨ " + text);
    }

    /// <summary>Quiet mode on (for [minutes], or until turned off) or off. While on, answers only show in the bubble.</summary>
    private static Outcome QuietMode(Settings s, bool on, int minutes)
    {
        s.QuietUntil = !on ? DateTime.MinValue : minutes > 0 ? DateTime.Now.AddMinutes(minutes) : DateTime.MaxValue;
        s.Save();
        if (!on) return new Outcome(s.Say("Okay, I'll talk again.", "Oké, ik praat weer."), true);
        return new Outcome("🤫 " + (minutes > 0
            ? s.Say($"Quiet until {s.QuietUntil:HH:mm}. I'll show my answers here.", $"Stil tot {s.QuietUntil:HH:mm}. Ik laat mijn antwoorden hier zien.")
            : s.Say("Quiet mode on. I'll show my answers here until you say I can talk again.", "Stille modus aan. Ik laat mijn antwoorden hier zien tot je zegt dat ik weer mag praten.")), true);
    }

    /// <summary>Opens the event, filled in, in your calendar; you click Save (reading happens in Program, with the AI).</summary>
    private static Outcome AddToCalendar(Settings s, JsonObject args)
    {
        if (CalendarFeed.Links(s).Count == 0) return NoCalendar(s);
        var title = Str(args, "title");
        if (title.Length == 0 || !DateTime.TryParse(Str(args, "start"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var start))
            return new Outcome(s.Say("I didn't get what or when.", "Ik snapte niet wat of wanneer."), true);
        var allDay = args["all_day"] is JsonValue v && v.TryGetValue<bool>(out var ad) && ad || Str(args, "start").Length <= 10;
        var end = allDay ? start.Date.AddDays(1) : start.AddMinutes(Int(args, "minutes") is int m and > 0 ? m : 60);
        CalendarFeed.Add(s, title, allDay ? start.Date : start, end, allDay, Str(args, "location"));
        return new Outcome(s.Say($"I've opened {title} in your calendar, just click Save.", $"Ik heb {title} in je agenda geopend, klik nog even op Opslaan."), true);
    }

    public static Outcome NoCalendar(Settings s) => new(s.Say(
        "I don't know your calendar yet. Paste its private iCal link in my settings.",
        "Ik ken je agenda nog niet. Plak de privé iCal-link in mijn instellingen."), true);

    private static Outcome CopyToClipboard(Settings s, string text)
    {
        if (text.Length == 0) return new Outcome(s.Say("I didn't get what to copy.", "Ik snapte niet wat ik moest kopiëren."), true);
        ClipboardTasks.Copy(text);
        return new Outcome("📋 " + (text.Length > 80 ? text[..80] + "…" : text));
    }

    private static Outcome CancelReminders(Settings s, string which)
    {
        var count = Reminders.Cancel(s, which);
        return new Outcome(count switch
        {
            0 => which.Length > 0 ? s.Say($"I don't have a reminder about {which}.", $"Ik heb geen herinnering over {which}.") : s.Say("There were no reminders.", "Er waren geen herinneringen."),
            1 => s.Say("Okay, I cancelled that reminder.", "Oké, die herinnering heb ik geannuleerd."),
            _ => s.Say($"Okay, I cancelled {count} reminders.", $"Oké, ik heb {count} herinneringen geannuleerd."),
        }, true);
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

        // A path ("C:\Users\me\Downloads") or a known folder ("Downloads", "bureaublad"): open it directly, no searching
        var asked = Str(args, "path") is { Length: > 0 } p ? p : query;
        if (FileFinder.DirectPath(asked, out var isPath) is { } direct)
        {
            var exists = Directory.Exists(direct) || File.Exists(direct);
            if (!exists && isPath) return new Outcome(s.Say($"I can't find {direct} on this PC.", $"Ik kan {direct} niet vinden op deze pc."), true);
            if (exists)
            {
                Log.Write($"Opening {direct} directly");
                if (action == "tell") return new Outcome(s.Say($"That's {direct}.", $"Dat is {direct}."), true);
                if (action == "show_in_folder" && File.Exists(direct)) FileFinder.ShowInFolder(direct);
                else FileFinder.Open(direct); // a folder opens in Explorer
                return new Outcome("📁 " + direct);
            }
        }

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
