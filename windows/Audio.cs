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
        var done = new TaskCompletionSource<bool>();
        var started = Stopwatch.StartNew();
        double noise = 0;
        int calibrationChunks = 0;
        bool speaking = false;
        var lastLoud = TimeSpan.Zero;

        using var input = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50 };
        input.DataAvailable += (_, e) =>
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
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
        input.RecordingStopped += (_, e) => { if (e.Exception != null) done.TrySetException(e.Exception); };

        using var registration = cancel.Register(() => done.TrySetCanceled());
        input.StartRecording();
        bool heard;
        try { heard = await done.Task; }
        finally { input.StopRecording(); Level = 0; }

        writer.Dispose(); // finishes the WAV header
        return heard ? buffer.ToArray() : null;
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

    public static IEnumerable<VoiceInformation> VoicesFor(string languageTag) =>
        SpeechSynthesizer.AllVoices.Where(v =>
            v.Language.Split('-')[0].Equals(languageTag.Split('-')[0], StringComparison.OrdinalIgnoreCase));

    public static async Task SpeakAsync(Settings settings, string text, string languageTag, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
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

    private static async Task PlayAsync(Stream wav, CancellationToken cancel)
    {
        using var reader = new WaveFileReader(wav);
        using var output = new WaveOutEvent();
        var finished = new TaskCompletionSource<bool>();
        output.PlaybackStopped += (_, _) => finished.TrySetResult(true);
        output.Init(reader);
        current = output;
        output.Play();
        Ducker.OwnVolumeFull(); // Windows remembers per-app volume; make sure Dave himself isn't stuck low
        using (cancel.Register(() => output.Stop())) await finished.Task;
        current = null;
    }

    public static void Stop() => current?.Stop();

    /// <summary>Short "I'm listening" beep.</summary>
    public static void Beep(int frequency = 880, int ms = 120) => Task.Run(() => Console.Beep(frequency, ms));
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
/// Whatever is playing right now (YouTube Music in the browser, Spotify, a video…), as Windows sees it —
/// the same thing the media pop-up next to the volume shows.
/// </summary>
public static class MediaSession
{
    public record Playing(string Title, string Artist, string App);

    private static async Task<Windows.Media.Control.GlobalSystemMediaTransportControlsSession?> CurrentAsync()
    {
        try
        {
            var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return manager.GetCurrentSession();
        }
        catch (Exception e)
        {
            Log.Write($"Media session unavailable: {e.Message}");
            return null;
        }
    }

    /// <summary>play, pause, next or previous on the app that's playing. False if there's no such app.</summary>
    public static async Task<bool> ControlAsync(string action)
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
    }

    public static async Task<Playing?> NowPlayingAsync()
    {
        var session = await CurrentAsync();
        if (session == null) return null;
        try
        {
            var info = await session.TryGetMediaPropertiesAsync();
            if (string.IsNullOrWhiteSpace(info?.Title)) return null;
            return new Playing(info.Title, info.Artist ?? "", session.SourceAppUserModelId ?? "");
        }
        catch { return null; }
    }

    /// <summary>True when Spotify is the app playing (or nothing is), so Spotify-only actions make sense.</summary>
    public static async Task<bool> IsSpotifyOrNothingAsync()
    {
        var session = await CurrentAsync();
        return session == null || (session.SourceAppUserModelId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase);
    }
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
