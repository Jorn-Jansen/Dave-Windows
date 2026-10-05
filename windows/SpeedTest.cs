using System.Diagnostics;
using System.Net.NetworkInformation;

namespace DaveWindows;

/// <summary>"How fast is my internet?": download, upload and ping, measured with Cloudflare's speed test (speed.cloudflare.com).</summary>
public static class SpeedTest
{
    private const string Server = "https://speed.cloudflare.com";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>[what]: "speed" (everything, about 15 seconds) or "ping" (just the delay, quick). Returns what Dave should say.</summary>
    public static async Task<string> RunAsync(Settings s, string what)
    {
        try
        {
            var ping = await PingAsync();
            if (what == "ping")
                return s.Say($"Your ping is {ping} milliseconds{Verdict(ping, false)}.", $"Je ping is {ping} milliseconden{Verdict(ping, true)}.");
            var down = await MeasureAsync(upload: false);
            var up = await MeasureAsync(upload: true);
            Log.Write($"Speed test: {down:F0} down, {up:F0} up (Mbit/s), ping {ping} ms");
            return s.Say($"Download {Round(down)} megabit per second, upload {Round(up)}, and a ping of {ping} milliseconds.",
                         $"Download {Round(down)} megabit per seconde, upload {Round(up)}, en een ping van {ping} milliseconden.");
        }
        catch (Exception e)
        {
            Log.Write($"Speed test failed: {e.Message}");
            return s.Say("I couldn't test the internet. Are you connected?", "Ik kon het internet niet testen. Ben je verbonden?");
        }
    }

    private static string Verdict(int ping, bool dutch) => ping switch
    {
        < 30 => dutch ? ", dat is prima voor gamen" : ", that's great for gaming",
        < 80 => dutch ? ", dat is oké" : ", that's okay",
        _ => dutch ? ", dat is vrij hoog" : ", that's quite high",
    };

    private static double Round(double mbit) => mbit >= 100 ? Math.Round(mbit / 10) * 10 : Math.Round(mbit);

    /// <summary>The middle of a few pings (ICMP), or of a few tiny web requests when ping is blocked.</summary>
    private static async Task<int> PingAsync()
    {
        var times = new List<long>();
        using (var ping = new Ping())
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync("1.1.1.1", 1000);
                    if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
                }
                catch { /* blocked: use web requests below */ }
            }
        if (times.Count < 3)
        {
            times.Clear();
            for (int i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                using var response = await Http.GetAsync($"{Server}/__down?bytes=0");
                times.Add(watch.ElapsedMilliseconds);
            }
            times.RemoveAt(0); // the first one includes setting up the connection
        }
        times.Sort();
        return (int)times[times.Count / 2];
    }

    /// <summary>Mbit/s with a few connections at once (one alone rarely fills a fast line), for at most about 6 seconds.</summary>
    private static async Task<double> MeasureAsync(bool upload)
    {
        long bytes = 0;
        var watch = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var chunk = upload ? new byte[1_000_000] : null; // small pieces, so a slow upload still counts before the time is up
        async Task Worker()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (upload)
                    {
                        using var response = await Http.PostAsync($"{Server}/__up", new ByteArrayContent(chunk!), stop.Token);
                        Interlocked.Add(ref bytes, chunk!.Length);
                    }
                    else
                    {
                        using var response = await Http.GetAsync($"{Server}/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, stop.Token);
                        await using var stream = await response.Content.ReadAsStreamAsync(stop.Token);
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, stop.Token)) > 0) Interlocked.Add(ref bytes, read);
                    }
                }
                catch (OperationCanceledException) { break; }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, upload ? 3 : 4).Select(_ => Worker()));
        return bytes * 8 / 1e6 / Math.Max(0.5, watch.Elapsed.TotalSeconds);
    }
}
