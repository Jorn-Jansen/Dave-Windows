using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>
/// Lets Dave on your iPhone talk to Dave on this PC (Settings → iPhone app). A tiny web server on your network: the
/// phone sends a question ("pause the music", "lock my PC"), this Dave carries it out and answers in text. Every
/// request needs the pairing code, so nobody else on the network can use it. Off unless you turn it on.
/// </summary>
public sealed class PhoneLink : IDisposable
{
    public const int Port = 47800;

    private readonly Settings settings;
    private readonly Func<string, Task<string>> ask;
    private readonly TcpListener listener = new(IPAddress.Any, Port);
    private readonly CancellationTokenSource stop = new();
    private DateTime lastWrongCode = DateTime.MinValue;

    /// <param name="ask">Ask this Dave something as if it was typed; returns his answer (without speaking it here).</param>
    public PhoneLink(Settings settings, Func<string, Task<string>> ask)
    {
        this.settings = settings;
        this.ask = ask;
        listener.Start();
        Log.Write($"iPhone link listening on {string.Join(", ", Addresses())} port {Port}");
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The pairing code, made the first time (8 digits, shown in the settings).</summary>
    public static string CodeFor(Settings s)
    {
        if (s.PhoneCode.Length == 0)
        {
            s.PhoneCode = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString();
            s.Save();
        }
        return s.PhoneCode;
    }

    /// <summary>
    /// This PC's addresses the phone can use: the real home network (the one with a router), and Tailscale's 100.x when
    /// it's installed. Not the virtual networks of VirtualBox, VMware, WSL or Hyper-V: the phone can't reach those.
    /// </summary>
    public static List<string> Addresses()
    {
        var result = new List<(string address, int order)>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var properties = n.GetIPProperties();
            var tailscale = n.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
            var hasRouter = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            if (!hasRouter && !tailscale) continue;
            foreach (var a in properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var address = a.Address.ToString();
                if (address.StartsWith("169.254.")) continue;
                result.Add((address, tailscale ? 2 : n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1));
            }
        }
        return result.OrderBy(r => r.order).Select(r => r.address).Distinct().ToList();
    }

    private const string FirewallRule = "Dave iPhone link";

    /// <summary>
    /// Make sure Windows' firewall lets the phone in, on every kind of network: Windows often calls home Wi-Fi "public",
    /// and its first question only allows private networks (or blocks Dave altogether after Cancel). Asks Windows once
    /// (a Yes/No question) to replace Dave's own firewall rules with one that allows the link. False when that was refused.
    /// </summary>
    public static bool AllowThroughFirewall()
    {
        var exe = Application.ExecutablePath;
        try
        {
            using var check = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRule}\" verbose")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            var rules = check.StandardOutput.ReadToEnd();
            check.WaitForExit(5000);
            if (rules.Contains(exe, StringComparison.OrdinalIgnoreCase)) return true; // done before (for this Dave.exe)
        }
        catch (Exception e) { Log.Write($"Couldn't read the firewall rules: {e.Message}"); }

        Log.Write("Asking Windows to let the iPhone link through the firewall");
        try
        {
            using var fix = Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c netsh advfirewall firewall delete rule name=all dir=in program=\"{exe}\" & " +
                $"netsh advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow program=\"{exe}\" protocol=TCP localport={Port} profile=any")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            fix.WaitForExit(15000);
            return fix.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { Log.Write("Firewall question was answered with No"); return false; }
        catch (Exception e) { Log.Write($"Couldn't change the firewall: {e.Message}"); return false; }
    }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception e) { Log.Write($"iPhone link: {e.Message}"); }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 10_000;
            var stream = client.GetStream();
            var (method, path, headers, body) = await ReadRequestAsync(stream);
            int status;
            JsonObject reply;
            if (path == "/hello") (status, reply) = (200, new JsonObject { ["name"] = settings.Name, ["version"] = Updater.Display });
            else if (!CodeMatches(headers.GetValueOrDefault("x-dave-code")))
            {
                lastWrongCode = DateTime.Now;
                await Task.Delay(1000); // makes guessing the code hopeless
                (status, reply) = (401, new JsonObject { ["error"] = "wrong code" });
            }
            else if (method == "POST" && path == "/pair")
            {
                Log.Write("iPhone app paired");
                (status, reply) = (200, new JsonObject
                {
                    ["name"] = settings.Name,
                    ["language"] = settings.Language,
                    ["secondLanguage"] = settings.SecondLanguage,
                    ["country"] = settings.Country,
                    ["groqKey"] = settings.GroqKey,
                    ["memories"] = new JsonArray(settings.Memories.Select(m => (JsonNode)m).ToArray()),
                });
            }
            else if (method == "POST" && path == "/ask")
            {
                var question = JsonNode.Parse(body)?["text"]?.ToString()?.Trim() ?? "";
                if (question.Length == 0) (status, reply) = (400, new JsonObject { ["error"] = "no text" });
                else
                {
                    Log.Write($"From the iPhone: {question}");
                    (status, reply) = (200, new JsonObject { ["answer"] = await ask(question) });
                }
            }
            else (status, reply) = (404, new JsonObject { ["error"] = "unknown" });
            await WriteResponseAsync(stream, status, reply.ToJsonString());
        }
        catch (Exception e) { Log.Write($"iPhone link request failed: {e.Message}"); }
    }

    private bool CodeMatches(string? code)
    {
        if (string.IsNullOrEmpty(code) || settings.PhoneCode.Length == 0) return false;
        if (DateTime.Now - lastWrongCode < TimeSpan.FromSeconds(1)) return false; // one guess per second at most
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code.Trim()), Encoding.UTF8.GetBytes(settings.PhoneCode));
    }

    /// <summary>Just enough HTTP for the app: the request line, headers and a JSON body.</summary>
    private static async Task<(string method, string path, Dictionary<string, string> headers, string body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOfBlankLine(buffer)) < 0)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0 || buffer.Count > 64 * 1024) throw new IOException("incomplete request");
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }
        var head = Encoding.UTF8.GetString(buffer.GetRange(0, headerEnd).ToArray()).Split("\r\n");
        var first = head[0].Split(' ');
        var headers = head.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
        var length = int.TryParse(headers.GetValueOrDefault("content-length"), out var l) ? Math.Clamp(l, 0, 64 * 1024) : 0;
        var bodyBytes = buffer.Skip(headerEnd + 4).ToList();
        while (bodyBytes.Count < length)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0) break;
            bodyBytes.AddRange(chunk.AsSpan(0, read).ToArray());
        }
        return (first[0].ToUpperInvariant(), first.Length > 1 ? first[1].Split('?')[0] : "/", headers, Encoding.UTF8.GetString(bodyBytes.ToArray()));
    }

    private static int IndexOfBlankLine(List<byte> b)
    {
        for (int i = 0; i + 3 < b.Count; i++)
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                   $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}
