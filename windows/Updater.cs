using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

namespace DaveWindows;

/// <summary>
/// Updates Dave from the newest GitHub release: a release tagged like "v1.0.1" with DaveSetup.exe attached
/// (made by "Make Installer.bat"). The repo must be public, or people can't download from it.
/// </summary>
public static class Updater
{
    /// <summary>The GitHub repo whose Releases hold DaveSetup.exe ("owner/name").</summary>
    public const string Repo = "Jorn-Jansen/Dave-Windows";
    private const string AssetName = "DaveSetup.exe";

    private static readonly HttpClient Http = CreateClient();

    public record Release(Version Version, string DownloadUrl);

    /// <summary>The version of this Dave (the &lt;Version&gt; in DaveWindows.csproj).</summary>
    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>The full version as written in DaveWindows.csproj, e.g. "1.4.0" or "1.4.0-beta.1".</summary>
    public static string Display =>
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Current.ToString(3)).Split('+')[0];

    /// <summary>A beta ("1.4.0-beta.1"): the final 1.4.0 counts as newer. Betas are pre-releases, so they never reach anyone by themselves.</summary>
    public static bool IsBeta => Display.Contains('-');

    /// <summary>
    /// True when Dave was installed with DaveSetup.exe. A Dave built from the source (Build Dave.bat) doesn't
    /// update itself: the installer would put a second copy next to it.
    /// </summary>
    public static bool IsInstalled =>
        Application.ExecutablePath.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Dave"),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The newest release if it's newer than this Dave, otherwise null. Throws when GitHub can't be reached.</summary>
    public static async Task<Release?> CheckAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet (or the repo is private)
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var tag = json?["tag_name"]?.GetValue<string>()?.TrimStart('v', 'V') ?? "";
        if (!Version.TryParse(tag, out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        var url = json?["assets"]?.AsArray()
            .FirstOrDefault(a => string.Equals(a?["name"]?.GetValue<string>(), AssetName, StringComparison.OrdinalIgnoreCase))
            ?["browser_download_url"]?.GetValue<string>();
        return url != null && (version > Current || (version == Current && IsBeta)) ? new Release(version, url) : null;
    }

    /// <summary>Download the installer and start it silently; it closes this Dave and starts the new one when done.</summary>
    public static async Task InstallAsync(Release release)
    {
        var file = Path.Combine(Path.GetTempPath(), $"DaveSetup-{release.Version}.exe");
        Log.Write($"Downloading update {release.Version}");
        using (var download = await Http.GetStreamAsync(release.DownloadUrl))
        using (var output = File.Create(file))
            await download.CopyToAsync(output);
        Log.Write($"Installing update {release.Version}");
        Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Dave/{Current}"); // GitHub requires one
        return client;
    }
}
