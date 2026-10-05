using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DaveWindows;

/// <summary>
/// Your calendar, through its private iCal link (Google, Outlook and iCloud all have one): Dave reads your events
/// from it, including repeating ones. Adding an event opens it pre-filled in your calendar, you click Save.
/// </summary>
public static class CalendarFeed
{
    public record Event(string Title, DateTime Start, DateTime End, bool AllDay, string Location);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, (DateTime fetched, string ics)> Cache = new();

    public static List<string> Links(Settings s) => s.CalendarLinks
        .Split(new[] { ' ', '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + l[9..] : l)
        .Where(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Events from [fromDays] to [toDays] (days from today: 0 = today, 1 = tomorrow, -1 = yesterday), as text for the AI.</summary>
    public static async Task<string> DescribeAsync(Settings s, int fromDays, int toDays)
    {
        if (toDays < fromDays) (fromDays, toDays) = (toDays, fromDays);
        DateTime from = DateTime.Today.AddDays(fromDays), to = DateTime.Today.AddDays(toDays + 1);
        var events = new List<Event>();
        foreach (var link in Links(s))
        {
            try { events.AddRange(Parse(await FetchAsync(link), from, to)); }
            catch (Exception e) { Log.Write($"Calendar failed ({Shorten(link)}): {e.Message}"); }
        }
        var period = $"Period: {from:dddd d MMMM yyyy} to {to.AddDays(-1):dddd d MMMM yyyy}.";
        if (events.Count == 0) return period + "\nNo events in this period.";
        var text = new StringBuilder(period + "\n");
        foreach (var e in events.OrderBy(e => e.Start).Take(60))
        {
            var when = e.AllDay ? $"{e.Start:dddd d MMMM}, all day" : $"{e.Start:dddd d MMMM HH:mm}–{e.End:HH:mm}";
            text.AppendLine($"- {when}: {e.Title}{(e.Location.Length > 0 ? $" (at {e.Location})" : "")}");
        }
        return text.ToString();
    }

    /// <summary>Open a new event, filled in, in the calendar the links come from (Google or Outlook), or the PC's calendar app.</summary>
    public static string Add(Settings s, string title, DateTime start, DateTime end, bool allDay, string location)
    {
        var links = string.Join(" ", Links(s)).ToLowerInvariant();
        string Q(string v) => Uri.EscapeDataString(v);
        string url;
        if (links.Contains("google.com"))
        {
            var dates = allDay ? $"{start:yyyyMMdd}/{end.Date.AddDays(end.Date <= start.Date ? 1 : 0):yyyyMMdd}" : $"{start:yyyyMMdd'T'HHmmss}/{end:yyyyMMdd'T'HHmmss}";
            var zone = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? $"&ctz={Q(iana)}" : "";
            url = $"https://calendar.google.com/calendar/render?action=TEMPLATE&text={Q(title)}&dates={dates}{zone}&location={Q(location)}";
        }
        else if (links.Contains("outlook.live.com") || links.Contains("outlook.office") || links.Contains("office365"))
        {
            var host = links.Contains("outlook.live.com") ? "outlook.live.com" : "outlook.office.com";
            url = $"https://{host}/calendar/0/deeplink/compose?subject={Q(title)}&startdt={start:yyyy-MM-ddTHH:mm:ss}&enddt={end:yyyy-MM-ddTHH:mm:ss}" +
                  $"{(allDay ? "&allday=true" : "")}&location={Q(location)}";
        }
        else
        {
            // iCloud or anything else: an .ics file opens in the PC's own calendar app (Outlook, Calendar...)
            var file = Path.Combine(Path.GetTempPath(), $"Dave event {DateTime.Now:yyyyMMddHHmmss}.ics");
            File.WriteAllText(file, ToIcs(title, start, end, allDay, location));
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return file;
        }
        PcActions.OpenWebsite(url, null, s.Browser);
        return url;
    }

    private static async Task<string> FetchAsync(string link)
    {
        lock (Cache)
            if (Cache.TryGetValue(link, out var hit) && DateTime.Now - hit.fetched < TimeSpan.FromMinutes(5)) return hit.ics;
        var ics = await Http.GetStringAsync(link);
        lock (Cache) Cache[link] = (DateTime.Now, ics);
        return ics;
    }

    // --- Reading iCal ---

    private sealed class Raw
    {
        public readonly Dictionary<string, (string value, Dictionary<string, string> par)> Props = new();
        public readonly List<(string value, Dictionary<string, string> par)> ExDates = new();
    }

    /// <summary>All events (repeats worked out) that overlap [from, to).</summary>
    public static List<Event> Parse(string ics, DateTime from, DateTime to)
    {
        var raws = new List<Raw>();
        Raw? current = null;
        int depth = 0; // inside a VEVENT: skip what's in its VALARM
        foreach (var line in Unfold(ics))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { current = new Raw(); depth = 0; continue; }
            if (current == null) continue;
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { depth++; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (depth > 0) { depth--; continue; }
                raws.Add(current);
                current = null;
                continue;
            }
            if (depth > 0) continue;
            var (name, par, value) = Split(line);
            if (name == "EXDATE") current.ExDates.Add((value, par));
            else current.Props.TryAdd(name, (value, par));
        }

        // Moved or changed single occurrences of a repeating event (RECURRENCE-ID) replace the original one.
        var replaced = new HashSet<(string, DateTime)>();
        foreach (var r in raws)
            if (r.Props.TryGetValue("RECURRENCE-ID", out var rid) && r.Props.TryGetValue("UID", out var uid))
                try { replaced.Add((uid.value, ParseTime(rid.value, rid.par).time)); } catch { /* unreadable date: ignore */ }

        var result = new List<Event>();
        foreach (var r in raws)
        {
            try { AddEvent(r); }
            catch (Exception e) { Log.Write($"Calendar: skipped an unreadable event ({e.Message})"); }
        }
        return result;

        void AddEvent(Raw r)
        {
            if (!r.Props.TryGetValue("DTSTART", out var dtStart)) return;
            if (r.Props.TryGetValue("STATUS", out var status) && status.value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return;
            var (start, allDay) = ParseTime(dtStart.value, dtStart.par);
            DateTime end;
            if (r.Props.TryGetValue("DTEND", out var dtEnd)) end = ParseTime(dtEnd.value, dtEnd.par).time;
            else if (r.Props.TryGetValue("DURATION", out var dur)) end = start + ParseDuration(dur.value);
            else end = allDay ? start.AddDays(1) : start;
            var length = end - start;
            var title = Text(r.Props.GetValueOrDefault("SUMMARY").value ?? "(no title)");
            var location = Text(r.Props.GetValueOrDefault("LOCATION").value ?? "");
            var uid = r.Props.GetValueOrDefault("UID").value ?? "";

            if (!r.Props.TryGetValue("RRULE", out var rule) || r.Props.ContainsKey("RECURRENCE-ID"))
            {
                if (start < to && (end > from || (end == start && start >= from))) result.Add(new Event(title, start, end, allDay, location));
                return;
            }
            var excluded = r.ExDates.SelectMany(x => x.value.Split(',').Select(v => ParseTime(v, x.par).time)).ToHashSet();
            foreach (var occurrence in Repeats(start, rule.value, to))
            {
                if (occurrence + length <= from && !(length == TimeSpan.Zero && occurrence >= from)) continue;
                if (excluded.Contains(occurrence) || replaced.Contains((uid, occurrence))) continue;
                result.Add(new Event(title, occurrence, occurrence + length, allDay, location));
            }
        }
    }

    /// <summary>The start times of a repeating event (RRULE), up to [until]. Covers daily, weekly, monthly and yearly repeats.</summary>
    private static IEnumerable<DateTime> Repeats(DateTime start, string rrule, DateTime until)
    {
        var rule = rrule.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        var freq = rule.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        int interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), out var i) && i > 0 ? i : 1;
        int? count = int.TryParse(rule.GetValueOrDefault("COUNT"), out var c) ? c : null;
        if (rule.TryGetValue("UNTIL", out var u)) { var last = ParseTime(u, new()).time; if (last < until) until = last.AddSeconds(1); }
        var days = (rule.GetValueOrDefault("BYDAY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var monthDays = (rule.GetValueOrDefault("BYMONTHDAY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => int.TryParse(d, out var n) ? n : 0).Where(n => n != 0).ToList();

        int produced = 0;
        IEnumerable<DateTime> Candidates()
        {
            for (int step = 0; step < 20000; step++)
            {
                switch (freq)
                {
                    case "DAILY":
                        yield return start.AddDays(step * interval);
                        break;
                    case "WEEKLY":
                        var monday = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7)).AddDays(7 * step * interval);
                        var weekdays = days.Length > 0 ? days.Select(d => Weekday(d[^2..])).Where(d => d != null).Select(d => d!.Value) : new[] { start.DayOfWeek };
                        foreach (var day in weekdays.Select(d => ((int)d + 6) % 7).Distinct().Order())
                            yield return monday.AddDays(day) + start.TimeOfDay;
                        break;
                    case "MONTHLY":
                        var month = new DateTime(start.Year, start.Month, 1).AddMonths(step * interval);
                        foreach (var date in MonthDates(month, start, days, monthDays).Order()) yield return date + start.TimeOfDay;
                        break;
                    case "YEARLY":
                        var year = start.Year + step * interval;
                        if (start.Month == 2 && start.Day == 29 && !DateTime.IsLeapYear(year)) continue;
                        yield return new DateTime(year, start.Month, start.Day) + start.TimeOfDay;
                        break;
                    default:
                        yield return start;
                        yield break;
                }
            }
        }

        foreach (var occurrence in Candidates())
        {
            if (occurrence < start) continue;
            if (occurrence >= until) yield break;
            if (count != null && produced >= count) yield break;
            produced++;
            yield return occurrence;
        }
    }

    /// <summary>The days in [month] a monthly repeat falls on: "2MO" (2nd Monday), "-1FR" (last Friday), month days, or the start's day.</summary>
    private static IEnumerable<DateTime> MonthDates(DateTime month, DateTime start, string[] days, List<int> monthDays)
    {
        int length = DateTime.DaysInMonth(month.Year, month.Month);
        if (days.Length > 0)
        {
            foreach (var d in days)
            {
                if (Weekday(d[^2..]) is not { } weekday) continue;
                var all = Enumerable.Range(1, length).Select(n => new DateTime(month.Year, month.Month, n)).Where(x => x.DayOfWeek == weekday).ToList();
                if (int.TryParse(d[..^2], out var nth) && nth != 0)
                {
                    var index = nth > 0 ? nth - 1 : all.Count + nth;
                    if (index >= 0 && index < all.Count) yield return all[index];
                }
                else foreach (var x in all) yield return x;
            }
            yield break;
        }
        foreach (var n in monthDays.Count > 0 ? monthDays : new List<int> { start.Day })
        {
            var day = n > 0 ? n : length + n + 1;
            if (day >= 1 && day <= length) yield return new DateTime(month.Year, month.Month, day);
        }
    }

    private static DayOfWeek? Weekday(string code) => code.ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
    };

    /// <summary>A date or date-time from iCal, in local time. All-day events are a plain date.</summary>
    private static (DateTime time, bool allDay) ParseTime(string value, Dictionary<string, string> par)
    {
        value = value.Trim();
        if (value.Length == 8 || par.GetValueOrDefault("VALUE")?.ToUpperInvariant() == "DATE")
            return (DateTime.ParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture), true);
        var utc = value.EndsWith('Z');
        var time = DateTime.ParseExact(value.TrimEnd('Z')[..15], "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (utc) return (DateTime.SpecifyKind(time, DateTimeKind.Utc).ToLocalTime(), false);
        if (par.TryGetValue("TZID", out var zoneId))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId.Trim('"'));
                return (TimeZoneInfo.ConvertTime(time, zone, TimeZoneInfo.Local), false);
            }
            catch { /* unknown zone: treat as local time */ }
        }
        return (time, false);
    }

    private static TimeSpan ParseDuration(string value)
    {
        var m = Regex.Match(value.ToUpperInvariant(), @"P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?");
        int N(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        return new TimeSpan(N(1) * 7 + N(2), N(3), N(4), N(5));
    }

    /// <summary>Long iCal lines are folded onto the next line (starting with a space); put them back together.</summary>
    private static IEnumerable<string> Unfold(string ics)
    {
        var current = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) { current.Append(raw, 1, raw.Length - 1); continue; }
            if (current.Length > 0) yield return current.ToString();
            current.Clear().Append(raw);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>"DTSTART;TZID=Europe/Amsterdam:20261010T140000" -> name, parameters, value.</summary>
    private static (string name, Dictionary<string, string> par, string value) Split(string line)
    {
        bool quoted = false;
        int colon = -1;
        for (int i = 0; i < line.Length && colon < 0; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) colon = i;
        }
        if (colon < 0) return (line.ToUpperInvariant(), new(), "");
        var head = line[..colon].Split(';');
        var par = head.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        return (head[0].ToUpperInvariant(), par, line[(colon + 1)..]);
    }

    private static string Text(string value) =>
        value.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    private static string ToIcs(string title, DateTime start, DateTime end, bool allDay, string location)
    {
        string Esc(string v) => v.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;");
        string Time(DateTime t) => allDay ? $";VALUE=DATE:{t:yyyyMMdd}" : $":{t.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}";
        return "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Dave//EN\r\nBEGIN:VEVENT\r\n" +
               $"UID:{Guid.NewGuid()}@dave\r\nDTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}\r\n" +
               $"DTSTART{Time(start)}\r\nDTEND{Time(allDay && end.Date <= start.Date ? start.AddDays(1) : end)}\r\n" +
               $"SUMMARY:{Esc(title)}\r\n{(location.Length > 0 ? $"LOCATION:{Esc(location)}\r\n" : "")}END:VEVENT\r\nEND:VCALENDAR\r\n";
    }

    private static string Shorten(string link) => link.Length > 40 ? link[..40] + "…" : link;
}
