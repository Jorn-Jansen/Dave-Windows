using NAudio.Wave;

namespace DaveWindows;

/// <summary>
/// Short, soft sounds in the theme's style instead of the beep: when Dave starts listening, when he heard you, when a
/// command is done, when a reminder goes off, and when a friend sends a message. A shimmering chime (Aurora), a crackle of fire (Ember), bubbles popping
/// (Toxic), water drops (Ocean), plucked strings (Sakura), crystal bells (Midnight). Made from scratch (no sound files)
/// the first time they're needed. Off in the settings (and in the Legacy theme): the old beeps.
/// </summary>
public static class ThemeSounds
{
    public enum Cue { Listen, Heard, Done, Alert, Message }

    private const int Rate = 44100;
    private static readonly Dictionary<(string, Cue), float[]> Made = new();
    private static readonly Random Noise = new(7);

    /// <summary>Plays the sound for [cue] (in [theme], or the one in use); returns how long it takes (so listening can start once it's over).</summary>
    public static TimeSpan Play(Settings s, Cue cue, string? theme = null)
    {
        if (Speaker.Silent) return TimeSpan.Zero;
        var id = theme ?? Palette.Id;
        if ((!s.ThemeSounds || id == "legacy") && cue == Cue.Message) id = "plain"; // a message always gets a proper notification sound
        else if (!s.ThemeSounds || id == "legacy")
        {
            // The old beeps
            var (frequency, ms) = cue switch { Cue.Done => (1320, 90), Cue.Heard => (660, 90), _ => (880, 120) };
            if (cue != Cue.Heard) Task.Run(() => Console.Beep(frequency, ms));
            return TimeSpan.FromMilliseconds(cue == Cue.Heard ? 0 : ms);
        }
        float[] samples;
        lock (Made)
        {
            if (!Made.TryGetValue((id, cue), out samples!)) Made[(id, cue)] = samples = Make(id, cue);
        }
        Task.Run(() => PlayAsync(samples));
        return TimeSpan.FromSeconds(samples.Length / (double)Rate);
    }

    private static async Task PlayAsync(float[] samples)
    {
        try
        {
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                var v = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                bytes[i * 2] = (byte)v;
                bytes[i * 2 + 1] = (byte)(v >> 8);
            }
            using var stream = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(Rate, 16, 1));
            using var output = new WaveOutEvent();
            var finished = new TaskCompletionSource<bool>();
            output.PlaybackStopped += (_, _) => finished.TrySetResult(true);
            output.Init(stream);
            output.Play();
            await Task.WhenAny(finished.Task, Task.Delay(3000));
        }
        catch (Exception e) { Log.Write($"Theme sound failed: {e.Message}"); }
    }

    // --- Making the sounds ---

    private static float[] Make(string theme, Cue cue)
    {
        var mix = new float[(int)(Rate * 0.9)];
        bool up = cue != Cue.Heard; // rising when he starts, falling when he's heard you
        switch (theme)
        {
            case "ember":
                // A short crackle of fire over a warm, low tone
                if (cue == Cue.Message)
                {
                    // A message: a little crackle and two warm notes, high then lower
                    Crackle(mix, 0, 0.15, 7);
                    Glide(mix, 0.0, 560, 600, 0.12, 0.16, harmonics: 0.35);
                    Glide(mix, 0.11, 420, 440, 0.18, 0.16, harmonics: 0.35);
                    break;
                }
                Crackle(mix, 0, cue == Cue.Alert ? 0.45 : cue == Cue.Done ? 0.12 : 0.2, cue == Cue.Alert ? 22 : 9);
                if (cue != Cue.Done) Glide(mix, 0.01, up ? 300 : 420, up ? 420 : 300, 0.2, 0.16, harmonics: 0.35);
                if (cue == Cue.Alert) Glide(mix, 0.2, 420, 560, 0.25, 0.14, harmonics: 0.35);
                break;
            case "toxic":
                // Bubbles popping
                foreach (var (at, from, to) in cue switch
                {
                    Cue.Listen => new[] { (0.0, 220.0, 620.0), (0.08, 300.0, 820.0) },
                    Cue.Heard => new[] { (0.0, 760.0, 260.0) },
                    Cue.Done => new[] { (0.0, 280.0, 760.0) },
                    Cue.Message => new[] { (0.0, 520.0, 1180.0), (0.1, 360.0, 820.0) },
                    _ => new[] { (0.0, 220.0, 620.0), (0.09, 300.0, 820.0), (0.18, 380.0, 1040.0) },
                })
                    Glide(mix, at, from, to, 0.07, 0.3, harmonics: 0.25, curve: 3);
                break;
            case "ocean":
                // Water drops: a quick "bloop" that rises (or falls) in pitch
                foreach (var (at, scale) in cue switch
                {
                    Cue.Listen => new[] { (0.0, 1.0), (0.09, 1.35) },
                    Cue.Heard => new[] { (0.0, 1.3), (0.09, 0.95) },
                    Cue.Done => new[] { (0.0, 1.2) },
                    Cue.Message => new[] { (0.0, 1.45), (0.1, 1.05) },
                    _ => new[] { (0.0, 1.0), (0.1, 1.25), (0.2, 1.55) },
                })
                    Glide(mix, at, (up ? 420 : 1250) * scale, (up ? 1250 : 420) * scale, 0.09, 0.32, curve: 2.2);
                break;
            case "sakura":
                // Plucked strings, on a gentle (pentatonic) scale
                foreach (var (at, note) in Notes(cue, new[] { 587.3, 740.0, 880.0 }, 0.07))
                    Pluck(mix, at, note, 0.32, cue == Cue.Listen ? 0.24 : 0.45);
                break;
            case "midnight":
                // Crystal bells: high, clear, with a long soft ring
                foreach (var (at, note) in Notes(cue, new[] { 1568.0, 2093.0, 2637.0 }, 0.07))
                    Bell(mix, at, note, cue == Cue.Listen ? 0.22 : 0.55, 0.17);
                break;
            case "plain":
                // Theme sounds off (or Legacy): a simple, friendly two-tone notification
                Glide(mix, 0.0, 880, 1100, 0.08, 0.28, curve: 3);
                Glide(mix, 0.09, 660, 830, 0.12, 0.28, curve: 3);
                break;
            default:
                // Aurora: a soft shimmering chime, two notes that drift a little
                foreach (var (at, note) in Notes(cue, new[] { 659.3, 987.8, 1318.5 }, 0.06))
                    Chime(mix, at, note, cue == Cue.Listen ? 0.2 : 0.45, 0.2);
                break;
        }
        return Trim(mix);
    }

    /// <summary>Which notes when: two rising (listen), two falling (heard), one (done), three rising (alert).</summary>
    private static (double at, double note)[] Notes(Cue cue, double[] scale, double gap) => cue switch
    {
        Cue.Listen => new[] { (0.0, scale[0]), (gap, scale[1]) },
        Cue.Heard => new[] { (0.0, scale[1]), (gap, scale[0]) },
        Cue.Done => new[] { (0.0, scale[1]) },
        Cue.Message => new[] { (0.0, scale[2]), (gap * 1.3, scale[1]) }, // a message: high, then lower
        _ => new[] { (0.0, scale[0]), (gap * 1.4, scale[1]), (gap * 2.8, scale[2]) },
    };

    /// <summary>A tone gliding from [from] to [to] Hz over [length] s, fading out; [curve] &gt; 1 glides fast at first.</summary>
    private static void Glide(float[] mix, double at, double from, double to, double length, double volume, double harmonics = 0, double curve = 1)
    {
        int start = (int)(at * Rate), n = (int)(length * Rate);
        double phase = 0;
        for (int i = 0; i < n && start + i < mix.Length; i++)
        {
            double x = i / (double)n, f = from + (to - from) * (1 - Math.Pow(1 - x, curve));
            phase += 2 * Math.PI * f / Rate;
            double envelope = Math.Min(1, i / (0.004 * Rate)) * Math.Pow(1 - x, 2);
            mix[start + i] += (float)(volume * envelope * (Math.Sin(phase) + harmonics * Math.Sin(3 * phase)));
        }
    }

    /// <summary>A chime: a sine with a soft overtone and a slight shimmer, ringing out over [length] s.</summary>
    private static void Chime(float[] mix, double at, double f, double length, double volume)
    {
        int start = (int)(at * Rate), n = (int)(length * Rate);
        for (int i = 0; i < n && start + i < mix.Length; i++)
        {
            double t = i / (double)Rate, shimmer = 1 + 0.004 * Math.Sin(2 * Math.PI * 5 * t);
            double envelope = Math.Min(1, i / (0.006 * Rate)) * Math.Exp(-t * 5.5 / length);
            mix[start + i] += (float)(volume * envelope * (Math.Sin(2 * Math.PI * f * shimmer * t) + 0.3 * Math.Sin(2 * Math.PI * 2 * f * t)));
        }
    }

    /// <summary>A bell: the uneven overtones of a real bell, the higher ones dying away faster.</summary>
    private static void Bell(float[] mix, double at, double f, double length, double volume)
    {
        int start = (int)(at * Rate), n = (int)(length * Rate);
        var partials = new[] { (1.0, 1.0, 1.0), (2.76, 0.45, 1.8), (5.4, 0.22, 3.2) };
        for (int i = 0; i < n && start + i < mix.Length; i++)
        {
            double t = i / (double)Rate, value = 0;
            foreach (var (ratio, gain, speed) in partials) value += gain * Math.Exp(-t * 4 * speed / length) * Math.Sin(2 * Math.PI * f * ratio * t);
            mix[start + i] += (float)(volume * Math.Min(1, i / (0.002 * Rate)) * value);
        }
    }

    /// <summary>A plucked string (Karplus-Strong): a burst of noise that settles into a note.</summary>
    private static void Pluck(float[] mix, double at, double f, double volume, double length)
    {
        int start = (int)(at * Rate), period = (int)(Rate / f), n = (int)(length * Rate);
        var ring = new double[period];
        for (int i = 0; i < period; i++) ring[i] = Noise.NextDouble() * 2 - 1;
        for (int i = 0; i < n && start + i < mix.Length; i++)
        {
            int k = i % period, next = (k + 1) % period;
            var sample = ring[k];
            ring[k] = 0.996 * 0.5 * (ring[k] + ring[next]);
            mix[start + i] += (float)(volume * sample * Math.Exp(-i / (0.25 * Rate)) * Math.Min(1, (n - i) / (0.03 * Rate))); // (a soft end when cut short)
        }
    }

    /// <summary>A crackle: [pops] tiny, sharp clicks scattered over [length] s, like wood in a fire.</summary>
    private static void Crackle(float[] mix, double at, double length, int pops)
    {
        for (int p = 0; p < pops; p++)
        {
            int start = (int)((at + Noise.NextDouble() * length) * Rate), n = (int)(Rate * (0.002 + Noise.NextDouble() * 0.006));
            double loud = 0.15 + Noise.NextDouble() * 0.3, last = 0;
            for (int i = 0; i < n && start + i < mix.Length; i++)
            {
                last = 0.55 * last + 0.45 * (Noise.NextDouble() * 2 - 1); // a little softer than pure noise
                mix[start + i] += (float)(loud * last * (1 - i / (double)n));
            }
        }
    }

    /// <summary>Cuts off the silence at the end, and keeps it from getting too loud.</summary>
    private static float[] Trim(float[] mix)
    {
        int end = mix.Length;
        while (end > 0 && Math.Abs(mix[end - 1]) < 0.0005f) end--;
        float peak = 0;
        for (int i = 0; i < end; i++) peak = Math.Max(peak, Math.Abs(mix[i]));
        float scale = peak > 0.42f ? 0.42f / peak : 1; // soft: never louder than this
        var result = new float[Math.Max(1, end)];
        for (int i = 0; i < end; i++) result[i] = mix[i] * scale;
        return result;
    }
}
