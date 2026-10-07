using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace DaveWindows;

/// <summary>Records one spoken question: starts when you talk, stops when you've been quiet for a moment.</summary>
public static class Recorder
{
    private const int SampleRate = 16000;

    /// <summary>How loud you're talking right now (0..1), for the glow. 0 when not recording.</summary>
    public static volatile float Level;

    /// <summary>WAV bytes of what was said, or null if nobody spoke within [waitMs].</summary>
    public static async Task<byte[]?> RecordAsync(CancellationToken cancel, int waitMs = 6000, int maxMs = 15000)
    {
        var buffer = new MemoryStream();
        var writer = new WaveFileWriter(new IgnoreDisposeStream(buffer), new WaveFormat(SampleRate, 16, 1));
        var writeLock = new object();
        var finished = false; // after this, nothing is written anymore: the file is being closed
        var done = new TaskCompletionSource<bool>();
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Stopwatch.StartNew();
        double noise = 0;
        int calibrationChunks = 0;
        bool speaking = false;
        var lastLoud = TimeSpan.Zero;

        using var input = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50 };
        input.DataAvailable += (_, e) =>
        {
            // The microphone can still deliver a last bit of sound while the recording is being finished.
            // Writing that into the file while it's being closed broke the file now and then ("invalid media file").
            lock (writeLock)
            {
                if (finished) return;
                writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
            double level = Rms(e.Buffer, e.BytesRecorded);
            Level = (float)Math.Min(1, level / 5000);

            if (calibrationChunks < 6) // first 300 ms: learn how loud the background is
            {
                noise = (noise * calibrationChunks + level) / ++calibrationChunks;
                return;
            }
            var threshold = Math.Max(noise * 3, 450);
            if (level > threshold)
            {
                speaking = true;
                lastLoud = started.Elapsed;
            }

            var elapsed = started.Elapsed;
            if (!speaking && elapsed.TotalMilliseconds > waitMs) done.TrySetResult(false);
            else if (speaking && (elapsed - lastLoud).TotalMilliseconds > 1100) done.TrySetResult(true);
            else if (elapsed.TotalMilliseconds > maxMs) done.TrySetResult(speaking);
        };
        input.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) done.TrySetException(e.Exception);
            stopped.TrySetResult(true);
        };

        using var registration = cancel.Register(() => done.TrySetCanceled());
        input.StartRecording();
        bool heard;
        try { heard = await done.Task; }
        finally
        {
            lock (writeLock) finished = true;
            input.StopRecording();
            await Task.WhenAny(stopped.Task, Task.Delay(1000)); // let the microphone really stop first
            Level = 0;
        }

        lock (writeLock) writer.Dispose(); // finishes the WAV header
        var wav = buffer.ToArray();
        // A quarter of a second or less isn't a question (and Whisper may refuse it)
        if (heard && wav.Length < 44 + SampleRate * 2 / 4) heard = false;
        return heard ? wav : null;
    }

    private static double Rms(byte[] data, int count)
    {
        double sum = 0;
        int samples = count / 2;
        for (int i = 0; i < samples * 2; i += 2)
        {
            short s = BitConverter.ToInt16(data, i);
            sum += s * (double)s;
        }
        return samples == 0 ? 0 : Math.Sqrt(sum / samples);
    }

    /// <summary>Lets WaveFileWriter finish without closing our MemoryStream.</summary>
    private class IgnoreDisposeStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
        public override void SetLength(long v) => inner.SetLength(v);
        public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
        protected override void Dispose(bool disposing) { /* keep inner open */ }
    }
}

/// <summary>Dave's voice, using the Windows voices (Frank for Dutch, Mark/David/Zira for English).</summary>
public static class Speaker
{
    private static WaveOutEvent? current;
    private static int playing;
    private static DateTime lastSound = DateTime.MinValue;

    /// <summary>True while Dave's voice is playing, and for a moment after (sound still leaving the speakers).</summary>
    public static bool IsBusy => playing > 0 || DateTime.Now - lastSound < TimeSpan.FromSeconds(1.5);

    public static IEnumerable<VoiceInformation> VoicesFor(string languageTag) =>
        SpeechSynthesizer.AllVoices.Where(v =>
            v.Language.Split('-')[0].Equals(languageTag.Split('-')[0], StringComparison.OrdinalIgnoreCase));

    /// <summary>True while answering the iPhone: the answer goes to the phone, so nothing is said (or beeped) here.</summary>
    public static volatile bool Silent;

    public static async Task SpeakAsync(Settings settings, string text, string languageTag, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(text) || Silent) return;
        if (settings.IsQuiet)
        {
            // Quiet mode: the answer only shows in the bubble; wait about as long as reading it takes, so it stays up.
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(text.Length / 15.0, 2.5, 15)), cancel);
            return;
        }
        if (settings.VoiceEngine == "azure" && Azure.IsConfigured(settings))
        {
            byte[]? azureWav = null;
            try { azureWav = await Azure.SynthesizeAsync(settings, text, languageTag, cancel); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception e) { Log.Write($"Azure voice failed, using the Windows voice instead: {e.Message}"); }
            if (azureWav != null)
            {
                using var azureStream = new MemoryStream(azureWav);
                await PlayAsync(azureStream, cancel);
                return;
            }
        }
        await SpeakWindowsAsync(settings, text, languageTag, cancel);
    }

    /// <summary>The built-in Windows voices (offline). Also the fallback when Azure isn't reachable.</summary>
    private static async Task SpeakWindowsAsync(Settings settings, string text, string languageTag, CancellationToken cancel)
    {
        using var synth = new SpeechSynthesizer();
        var wanted = languageTag == settings.Language ? settings.Voice : settings.SecondVoice;
        var voice = VoicesFor(languageTag).FirstOrDefault(v => v.DisplayName == wanted)
                    ?? VoicesFor(languageTag).FirstOrDefault();
        if (voice != null) synth.Voice = voice;
        synth.Options.SpeakingRate = Math.Clamp(settings.SpeechRate, 0.5, 3.0);

        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        using var wav = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(wav, cancel);
        wav.Position = 0;
        await PlayAsync(wav, cancel);
    }

    /// <summary>How loud Dave's voice is right now (0..1), so the bubble and glow can move with it. 0 when he's quiet.</summary>
    public static volatile float Level;

    /// <summary>Raised when Dave starts saying something, with how long it takes (the bubble shows the words along with it).</summary>
    public static event Action<TimeSpan>? Started;

    private static async Task PlayAsync(Stream wav, CancellationToken cancel)
    {
        using var reader = new WaveFileReader(wav);
        using var output = new WaveOutEvent();
        var finished = new TaskCompletionSource<bool>();
        output.PlaybackStopped += (_, _) => finished.TrySetResult(true);
        // A meter between the voice and the speakers: about 30 volume readings a second
        var meter = new NAudio.Wave.SampleProviders.MeteringSampleProvider(reader.ToSampleProvider(), reader.WaveFormat.SampleRate / 30);
        meter.StreamVolume += (_, e) => Level = Math.Min(1f, e.MaxSampleValues.DefaultIfEmpty(0).Max() * 1.6f);
        output.Init(meter);
        current = output;
        Interlocked.Increment(ref playing);
        try
        {
            output.Play();
            Started?.Invoke(reader.TotalTime);
            Ducker.OwnVolumeFull(); // Windows remembers per-app volume; make sure Dave himself isn't stuck low
            using (cancel.Register(() => output.Stop())) await finished.Task;
        }
        finally
        {
            Level = 0;
            lastSound = DateTime.Now;
            Interlocked.Decrement(ref playing);
            current = null;
        }
    }

    public static void Stop() => current?.Stop();

    /// <summary>Short "I'm listening" beep.</summary>
    public static void Beep(int frequency = 880, int ms = 120) { if (!Silent) Task.Run(() => Console.Beep(frequency, ms)); }
}

/// <summary>
/// Turns other apps down while Dave talks, and back up afterwards.
/// Safe against stacking (nested duckers share one ducking) and against Dave being closed halfway:
/// the original volumes are saved, and <see cref="RestoreAfterCrash"/> puts them back at the next start.
/// </summary>
public sealed class Ducker : IDisposable
{
    private static readonly object Sync = new();
    private static int depth;
    private static readonly List<(SimpleAudioVolume volume, float original)> Lowered = new();
    private static readonly string SavedPath = Path.Combine(Settings.Folder, "ducked.json");
    private bool disposed;

    public Ducker(double to)
    {
        lock (Sync)
        {
            if (depth++ > 0) return; // already turned down: don't do it twice (100 -> 30 -> 9 -> ...)
            var saved = new Dictionary<string, float>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.IsSystemSoundsSession) continue;
                    var app = AppName(session.GetProcessID);
                    if (app is null or "Dave") continue; // never turn Dave down, not even another Dave
                    var volume = session.SimpleAudioVolume;
                    Lowered.Add((volume, volume.Volume));
                    saved[app] = Math.Max(saved.GetValueOrDefault(app), volume.Volume);
                    volume.Volume = (float)(volume.Volume * to);
                }
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(SavedPath, System.Text.Json.JsonSerializer.Serialize(saved));
            }
            catch (Exception e) { Log.Write($"Ducking failed: {e.Message}"); }
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (disposed) return;
            disposed = true;
            if (--depth > 0) return; // an outer ducker still wants it quiet
            foreach (var (volume, original) in Lowered)
            {
                try { volume.Volume = original; } catch { /* app may have closed */ }
            }
            Lowered.Clear();
            try { File.Delete(SavedPath); } catch { }
        }
    }

    /// <summary>If Dave was closed while other sound was turned down, turn it back up. Also sets Dave himself to 100%.</summary>
    public static void RestoreAfterCrash()
    {
        try
        {
            var saved = File.Exists(SavedPath)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(SavedPath)) ?? new()
                : new Dictionary<string, float>();
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var app = AppName(sessions[i].GetProcessID);
                    if (app == null) continue;
                    var volume = sessions[i].SimpleAudioVolume;
                    if (app == "Dave") volume.Volume = 1f;
                    else if (saved.TryGetValue(app, out var original) && volume.Volume < original)
                    {
                        volume.Volume = original;
                        Log.Write($"Restored {app} to {original:P0} (Dave was closed while it was turned down)");
                    }
                }
            }
            if (File.Exists(SavedPath)) File.Delete(SavedPath);
        }
        catch (Exception e) { Log.Write($"Restoring volumes failed: {e.Message}"); }
    }

    /// <summary>Set this Dave's own sound to 100% in the Volume mixer.</summary>
    public static void OwnVolumeFull()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var me = (uint)Environment.ProcessId;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                    if (sessions[i].GetProcessID == me && sessions[i].SimpleAudioVolume.Volume < 0.99f) sessions[i].SimpleAudioVolume.Volume = 1f;
            }
        }
        catch (Exception e) { Log.Write($"Setting Dave's volume failed: {e.Message}"); }
    }

    private static string? AppName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return null; }
    }
}

/// <summary>
/// The volume of one app ("make Discord quieter", "mute Chrome"), like the Volume mixer in Windows.
/// Looks on every audio output, since apps can play on different ones (Voicemeeter, headphones…).
/// </summary>
public static class AppVolume
{
    public record Result(string App, int Level, bool Muted);

    /// <summary>
    /// Change [name]'s volume: to [level] %, [change] up/down (a quarter of the range each time), or (un)[mute].
    /// Null when that app isn't making sound right now.
    /// </summary>
    public static Result? Set(string name, int? level, string change, bool? mute)
    {
        var sessions = Find(name);
        if (sessions.Count == 0) return null;
        var current = sessions.Max(s => s.volume.Volume);
        float target = level is { } l ? l / 100f
            : change == "up" ? Math.Min(1f, current + 0.25f)
            : change == "down" ? Math.Max(0.05f, current - 0.25f) // "quieter" never quite mutes it
            : current;
        foreach (var (_, volume) in sessions)
        {
            try
            {
                if (mute is { } m) volume.Mute = m;
                else
                {
                    volume.Volume = Math.Clamp(target, 0f, 1f);
                    if (volume.Mute && target > 0) volume.Mute = false; // "turn it up" when it's muted: unmute too
                }
            }
            catch (Exception e) { Log.Write($"App volume: {e.Message}"); }
        }
        var first = sessions[0];
        return new Result(first.app, (int)Math.Round(first.volume.Volume * 100), first.volume.Mute);
    }

    /// <summary>The apps playing sound right now, with their volume (for "which apps are playing sound").</summary>
    public static List<Result> List() =>
        All(playingOnly: true).GroupBy(s => s.app).Select(g => new Result(g.Key, (int)Math.Round(g.Max(s => s.volume.Volume) * 100), g.All(s => s.volume.Mute))).ToList();

    private static List<(string app, SimpleAudioVolume volume)> Find(string name)
    {
        static string Simple(string x) => new(x.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var wanted = Simple(name);
        if (wanted.Length == 0) return new();
        return All().Where(s =>
        {
            var app = Simple(s.app);
            return app.Contains(wanted) || (app.Length > 3 && wanted.Contains(app));
        }).ToList();
    }

    /// <summary>
    /// Every app's sound on every active output, except Windows' own sounds and Dave. With [playingOnly], just the ones
    /// making sound right now (many apps keep a sound channel open without playing anything).
    /// </summary>
    private static List<(string app, SimpleAudioVolume volume)> All(bool playingOnly = false)
    {
        var result = new List<(string, SimpleAudioVolume)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.IsSystemSoundsSession || session.GetProcessID == Environment.ProcessId) continue;
                    if (playingOnly && session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                    if (FriendlyName(session.GetProcessID) is { } app && !app.StartsWith("audiodg")) result.Add((app, session.SimpleAudioVolume));
                }
            }
        }
        catch (Exception e) { Log.Write($"Reading app volumes failed: {e.Message}"); }
        return result;
    }

    private static readonly Dictionary<uint, string?> Names = new();

    /// <summary>"Roblox" for RobloxPlayerBeta, "Google Chrome" for chrome…: the name you'd say.</summary>
    private static string? FriendlyName(uint pid)
    {
        if (Names.TryGetValue(pid, out var cached)) return cached;
        string? name = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            if (name.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase)) name = "Roblox";
            else
            {
                try
                {
                    var described = p.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(described) && described.Length < 40) name = $"{described} ({p.ProcessName})";
                }
                catch { /* some processes can't be read: the process name will do */ }
            }
        }
        catch { /* gone */ }
        if (Names.Count > 300) Names.Clear();
        return Names[pid] = name;
    }
}

/// <summary>
/// Whatever is playing right now (YouTube Music in the browser, Spotify, a video…), as Windows sees it —
/// the same thing the media pop-up next to the volume shows.
/// </summary>
public static class MediaSession
{
    public record Playing(string Title, string Artist, string App);

    private static Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager? manager;

    /// <summary>
    /// The app that's playing. The connection to Windows' media controls is made once and kept: asking for a new one
    /// every few seconds is when Windows sometimes stopped answering altogether.
    /// </summary>
    private static async Task<Windows.Media.Control.GlobalSystemMediaTransportControlsSession?> CurrentAsync()
    {
        try
        {
            manager ??= await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return manager.GetCurrentSession();
        }
        catch (Exception e)
        {
            Log.Write($"Media session unavailable: {e.Message}");
            manager = null;
            return null;
        }
    }

    /// <summary>After Windows' media controls stopped answering, leave them alone until then (asking again only piles up stuck requests).</summary>
    private static DateTime restUntil = DateTime.MinValue;

    /// <summary>True when the last question went unanswered: then "nothing playing" really means "don't know".</summary>
    public static bool Unknown { get; private set; }

    /// <summary>[work] on a background thread, but give up after [ms]: Windows' media controls can hang without ever answering.</summary>
    private static async Task<T> WithinAsync<T>(Func<Task<T>> work, int ms, T fallback, string what)
    {
        if (DateTime.Now < restUntil) { Unknown = true; return fallback; } // they're stuck right now: don't add another request
        var task = Task.Run(work);
        if (manager == null) ms = Math.Max(ms, 5000); // making the connection the first time takes a few seconds
        if (await Task.WhenAny(task, Task.Delay(ms)) == task) { Unknown = false; return task.Result; }
        Log.Write($"Windows' media controls didn't answer ({what}); leaving them alone for a minute");
        manager = null; // try a fresh connection next time
        restUntil = DateTime.Now.AddMinutes(1);
        Unknown = true;
        return fallback;
    }

    /// <summary>play, pause, next or previous on the app that's playing. False if there's no such app (or Windows doesn't answer).</summary>
    public static Task<bool> ControlAsync(string action) => WithinAsync(async () =>
    {
        var session = await CurrentAsync();
        if (session == null) return false;
        try
        {
            return action switch
            {
                "play" => await session.TryPlayAsync(),
                "pause" => await session.TryPauseAsync(),
                "next" => await session.TrySkipNextAsync(),
                "previous" => await session.TrySkipPreviousAsync(),
                _ => false,
            };
        }
        catch { return false; }
    }, 1500, false, action);

    /// <summary>
    /// What's playing, or null. Never waits more than a second: Windows sometimes doesn't answer at all (seen with a
    /// game open), and that froze Dave's window and could hold up a question.
    /// </summary>
    public static Task<Playing?> NowPlayingAsync() => WithinAsync(async () =>
    {
        var session = await CurrentAsync();
        if (session == null) return null;
        try
        {
            var info = await session.TryGetMediaPropertiesAsync();
            if (string.IsNullOrWhiteSpace(info?.Title)) return null;
            return new Playing(info.Title, info.Artist ?? "", session.SourceAppUserModelId ?? "");
        }
        catch { return (Playing?)null; }
    }, 1000, null, "what's playing");

    /// <summary>
    /// What's playing: Spotify first, from Spotify itself (fast, and it keeps working when Windows' media controls get
    /// stuck), then any other app (YouTube, a video…) through Windows.
    /// </summary>
    public static async Task<Playing?> NowPlayingAsync(Settings s)
    {
        if (Spotify.IsConnected(s) && s.SpotifyScopes.Contains("user-read-currently-playing"))
        {
            try
            {
                if (await Spotify.NowPlayingAsync(s) is { } track) return new Playing(track.Name, track.Artist, "Spotify");
            }
            catch (Exception e) { Log.Write($"Spotify now playing: {e.Message}"); }
        }
        return await NowPlayingAsync();
    }

    /// <summary>True when Spotify is the app playing (or nothing is), so Spotify-only actions make sense.</summary>
    public static Task<bool> IsSpotifyOrNothingAsync() => WithinAsync(async () =>
    {
        var session = await CurrentAsync();
        return session == null || (session.SourceAppUserModelId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase);
    }, 1000, true, "which app is playing");
}

/// <summary>Windows master volume and the media keys.</summary>
public static class SystemAudio
{
    private static AudioEndpointVolume Endpoint()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioEndpointVolume;
    }

    public static int GetVolume() => (int)Math.Round(Endpoint().MasterVolumeLevelScalar * 100);

    public static int SetVolume(int percent)
    {
        var endpoint = Endpoint();
        endpoint.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        if (percent > 0) endpoint.Mute = false;
        return GetVolume();
    }

    public static void SetMute(bool mute) => Endpoint().Mute = mute;

    // Media keys work with Spotify, YouTube in the browser, and most players.
    private const byte PlayPause = 0xB3, Next = 0xB0, Previous = 0xB1;

    public static void MediaKey(string action)
    {
        byte key = action switch { "next" => Next, "previous" => Previous, _ => PlayPause };
        keybd_event(key, 0, 1, UIntPtr.Zero);
        keybd_event(key, 0, 3, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
