using System.Text;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DaveWindows;

/// <summary>
/// "What did I miss?": your Windows notifications (Discord, mail, WhatsApp…). Windows only keeps the ones you haven't
/// dismissed, so Dave also remembers what he's seen in the last day (only in memory, never saved or sent anywhere,
/// except to the AI when you ask about them).
/// </summary>
public static class Notifications
{
    public record Item(uint Id, DateTimeOffset At, string App, string Text);

    private static readonly Dictionary<uint, Item> Seen = new();
    private static readonly object Sync = new();

    /// <summary>Look every 30 seconds, so notifications you dismiss (or that disappear) can still be asked about.</summary>
    public static void Start() => _ = Task.Run(async () =>
    {
        while (true)
        {
            try { await CollectAsync(); }
            catch (Exception e) { Log.Write($"Notifications: {e.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
    });

    private static async Task<bool> CollectAsync()
    {
        var listener = UserNotificationListener.Current;
        var access = listener.GetAccessStatus();
        if (access == UserNotificationListenerAccessStatus.Unspecified) access = await listener.RequestAccessAsync();
        if (access != UserNotificationListenerAccessStatus.Allowed) return false; // turned off in Windows' privacy settings
        var current = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        lock (Sync)
        {
            foreach (var n in current)
            {
                if (Seen.ContainsKey(n.Id)) continue;
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()
                    .Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList() ?? new();
                if (texts.Count == 0) continue;
                string app;
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? "?"; } catch { app = "?"; }
                Seen[n.Id] = new Item(n.Id, n.CreationTime, app, string.Join(" — ", texts));
            }
            foreach (var old in Seen.Values.Where(i => DateTimeOffset.Now - i.At > TimeSpan.FromDays(1)).ToList()) Seen.Remove(old.Id);
        }
        return true;
    }

    /// <summary>The notifications of the last [hours] (newest first), as text for the AI; null when Windows doesn't allow reading them.</summary>
    public static async Task<string?> DescribeAsync(int hours)
    {
        if (!await CollectAsync()) return null;
        List<Item> items;
        lock (Sync) items = Seen.Values.Where(i => DateTimeOffset.Now - i.At <= TimeSpan.FromHours(Math.Max(1, hours)))
            .OrderByDescending(i => i.At).Take(60).ToList();
        if (items.Count == 0) return $"No notifications in the last {hours} hours.";
        var text = new StringBuilder($"Notifications of the last {hours} hours, newest first (now it's {DateTime.Now:HH:mm}):\n");
        foreach (var i in items)
            text.AppendLine($"- {i.At.LocalDateTime:ddd HH:mm} {i.App}: {(i.Text.Length > 400 ? i.Text[..400] + "…" : i.Text)}");
        return text.ToString();
    }
}
