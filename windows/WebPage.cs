using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace DaveWindows;

/// <summary>
/// "Summarise this page": finds the page open in your browser (its address, read from the address bar), downloads it
/// and keeps the readable text. Pages that need you to be logged in (or are built by scripts) come out (nearly) empty:
/// then the caller looks at the screen instead.
/// </summary>
public static class WebPage
{
    private const int MaxText = 7000; // about what fits in one request to the free AI
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "librewolf", "waterfox" };

    /// <param name="Window">The browser window the page is in.</param>
    /// <param name="Title">The page title (the window title without the browser's name).</param>
    /// <param name="Text">The readable text, or null when the page couldn't be read.</param>
    public record Page(IntPtr Window, string Title, string Url, string? Text, bool Cut);

    /// <summary>The page in the browser window nearest the front (where you just were), or null when no browser is open.</summary>
    public static async Task<Page?> ReadAsync()
    {
        var browser = WindowList.List().FirstOrDefault(w => Browsers.Contains(w.process.ToLowerInvariant()));
        if (browser.handle == IntPtr.Zero) return null;
        var title = Regex.Replace(browser.title, @"\s+[-–—]\s+(Google Chrome|Microsoft\s*Edge|Mozilla Firefox|Brave|Opera|Vivaldi)$", "", RegexOptions.IgnoreCase).Trim();
        var url = await Task.Run(() => AddressOf(browser.handle));
        if (url == null) return new Page(browser.handle, title, "", null, false);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            request.Headers.AcceptLanguage.ParseAdd("en,nl;q=0.8");
            using var response = await Http.SendAsync(request);
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!response.IsSuccessStatusCode || !(type.Contains("html") || type.StartsWith("text/")))
            {
                Log.Write($"Page {url}: {(int)response.StatusCode} {type}");
                return new Page(browser.handle, title, url, null, false);
            }
            var html = await response.Content.ReadAsStringAsync();
            var text = type.Contains("html") ? Readable(html) : html.Trim();
            Log.Write($"Page {url}: {text.Length} characters");
            if (text.Length < 300) return new Page(browser.handle, title, url, null, false); // a login page, or built by scripts
            var cut = text.Length > MaxText;
            return new Page(browser.handle, title, url, cut ? text[..MaxText] : text, cut);
        }
        catch (Exception e)
        {
            Log.Write($"Page {url} couldn't be downloaded: {e.Message}");
            return new Page(browser.handle, title, url, null, false);
        }
    }

    /// <summary>The address in the browser's address bar (Chrome and Edge leave out "https://"), or null.</summary>
    private static string? AddressOf(IntPtr window)
    {
        try
        {
            var edits = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            foreach (AutomationElement edit in edits)
            {
                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) continue;
                var value = ((ValuePattern)pattern).Current.Value.Trim();
                if (value.Length == 0 || value.Contains(' ') || !value.Contains('.')) continue; // a search box, or being typed in
                if (!value.Contains("://")) value = "https://" + value;
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.ToString();
            }
        }
        catch (Exception e) { Log.Write($"Couldn't read the browser's address bar: {e.Message}"); }
        return null;
    }

    /// <summary>The text a person would read: the article (or main part) if the page marks one, without menus, scripts and ads.</summary>
    private static string Readable(string html)
    {
        html = Regex.Replace(html, @"<!--.*?-->", " ", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<(script|style|noscript|svg|template|iframe|head|nav|footer|aside|form|button|select)\b.*?</\1\s*>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var main = Regex.Match(html, @"<(article|main)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (main.Success && main.Length > 2000) html = main.Value; // big enough to be the actual content
        html = Regex.Replace(html, @"<(header)\b.*?</\1\s*>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article|/blockquote)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<li\b(?:[^>""']|""[^""]*""|'[^']*')*>", "\n- ", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(?:[^>""']|""[^""]*""|'[^']*')*>", " "); // tags, also with a ">" inside a quoted attribute
        var text = WebUtility.HtmlDecode(html);
        text = Regex.Replace(text, @"[ \t ]+", " ");
        text = Regex.Replace(text, @"\s*\n\s*", "\n");
        return text.Trim();
    }
}
