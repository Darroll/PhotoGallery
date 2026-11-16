using System.Net.Mail;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>A reader who asked for emails about new galleries and photos. Only confirmed (double opt-in) readers get mail.</summary>
public class Subscriber
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    /// <summary>Secret in the confirm and unsubscribe links.</summary>
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAt { get; set; }
    public bool Unsubscribed { get; set; }
}

/// <summary>
/// Notices to subscribers about new public galleries and new photos in them. Unlisted and protected galleries are never announced,
/// and nothing is sent unless the site address is set (the links in every email need it).
/// </summary>
public static class Subscribers
{
    public static bool ValidAddress(string? address)
    {
        var a = (address ?? "").Trim();
        if (a.Length == 0 || a.Length > 200) return false;
        try { return new MailAddress(a).Address.Equals(a, StringComparison.Ordinal); }
        catch (FormatException) { return false; }
    }

    public static bool Available(SettingsService settings) =>
        settings.Email.Subscribers && settings.Email.Ready && settings.Email.SiteUrl.Length > 0;

    public static async Task QueueConfirmationAsync(AppDb db, SettingsService settings, Subscriber s)
    {
        var site = settings.SiteName;
        db.EmailOutbox.Add(new EmailOutbox
        {
            ToAddr = s.Email,
            Subject = $"Confirm your subscription to {site}",
            Body = $"Please confirm that you want emails about new galleries and photos from {site}:\n\n" +
                   $"{settings.Email.SiteUrl}/subscribe/confirm/{s.Token}\n\n" +
                   "If you did not ask for this, ignore this message and nothing more will be sent."
        });
        await db.SaveChangesAsync();
    }

    public static async Task NewGalleryAsync(AppDb db, SettingsService settings, Gallery g)
    {
        if (!Available(settings) || g.Visibility != Visibility.Public || GalleryRules.IsExpired(g)) return;
        if (settings.Email.SubscriberMail != "now") { await AddPendingAsync(db, "digest-gallery:" + g.Id, 1); return; }
        await BroadcastAsync(db, settings, $"New gallery: {g.Title}",
            $"A new gallery has been added to {settings.SiteName}: {g.Title}.\n\n{settings.Email.SiteUrl}/g/{g.Slug}");
    }

    public static async Task NewPhotosAsync(AppDb db, SettingsService settings, Gallery g, int added)
    {
        if (added <= 0 || !Available(settings) || g.Visibility != Visibility.Public || GalleryRules.IsExpired(g)) return;
        if (settings.Email.SubscriberMail != "now") { await AddPendingAsync(db, "digest-photos:" + g.Id, added); return; }
        var what = added == 1 ? "1 new photo" : $"{added} new photos";
        var verb = added == 1 ? "has" : "have";
        await BroadcastAsync(db, settings, $"{what} in {g.Title}",
            $"{what} {verb} been added to \"{g.Title}\" on {settings.SiteName}.\n\n{settings.Email.SiteUrl}/g/{g.Slug}");
    }

    /// <summary>Notes what is new so the next digest can mention it; photos for the same gallery add up.</summary>
    static async Task AddPendingAsync(AppDb db, string key, int count)
    {
        var row = await db.AlertStates.FirstOrDefaultAsync(a => a.Key == key);
        if (row == null) db.AlertStates.Add(new AlertState { Key = key, Value = count.ToString() });
        else row.Value = ((int.TryParse(row.Value, out var n) ? n : 0) + count).ToString();
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Sends the daily or weekly email: every new public gallery and every gallery with new photos since the last one, in a single message
    /// per reader. Nothing is sent when nothing is new, and not more often than the chosen interval.
    /// </summary>
    public static async Task SendDigestAsync(AppDb db, SettingsService settings)
    {
        var pending = await db.AlertStates.Where(a => a.Key.StartsWith("digest-photos:") || a.Key.StartsWith("digest-gallery:")).ToListAsync();
        if (pending.Count == 0) return;
        var mode = settings.Email.SubscriberMail;
        if (mode == "now" || !Available(settings)) { if (mode == "now") { db.AlertStates.RemoveRange(pending); await db.SaveChangesAsync(); } return; }

        var last = await db.AlertStates.FirstOrDefaultAsync(a => a.Key == "digest-sent");
        var gap = mode == "weekly" ? TimeSpan.FromDays(7) - TimeSpan.FromHours(1) : TimeSpan.FromHours(23);
        if (last != null && DateTime.UtcNow - last.At < gap) return;

        int IdOf(AlertState a) => int.Parse(a.Key[(a.Key.IndexOf(':') + 1)..]);
        var ids = pending.Select(IdOf).Distinct().ToList();
        var galleries = (await db.Galleries.AsNoTracking().Where(g => ids.Contains(g.Id)).ToListAsync())
            .Where(g => g.Visibility == Visibility.Public && !GalleryRules.IsExpired(g)).ToDictionary(g => g.Id);
        var site = settings.SiteName;
        var url = settings.Email.SiteUrl;
        var lines = new List<string>();
        var fresh = pending.Where(a => a.Key.StartsWith("digest-gallery:") && galleries.ContainsKey(IdOf(a))).Select(a => galleries[IdOf(a)]).ToList();
        if (fresh.Count > 0)
        {
            lines.Add(fresh.Count == 1 ? "New gallery:" : "New galleries:");
            lines.AddRange(fresh.Select(g => $"  {g.Title}\n  {url}/g/{g.Slug}"));
        }
        var newGalleryIds = fresh.Select(g => g.Id).ToHashSet();
        var more = pending.Where(a => a.Key.StartsWith("digest-photos:") && galleries.ContainsKey(IdOf(a)) && !newGalleryIds.Contains(IdOf(a)))
            .Select(a => (g: galleries[IdOf(a)], n: int.TryParse(a.Value, out var n) ? n : 0)).Where(x => x.n > 0).ToList();
        if (more.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("New photos:");
            lines.AddRange(more.Select(x => $"  {x.n} new photo{(x.n == 1 ? "" : "s")} in {x.g.Title}\n  {url}/g/{x.g.Slug}"));
        }
        db.AlertStates.RemoveRange(pending);
        if (lines.Count > 0)
        {
            await BroadcastAsync(db, settings, mode == "weekly" ? $"This week on {site}" : $"What's new on {site}", "Here is what has been added:\n\n" + string.Join("\n", lines));
            if (last == null) db.AlertStates.Add(new AlertState { Key = "digest-sent" }); else last.At = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    static async Task BroadcastAsync(AppDb db, SettingsService settings, string subject, string body)
    {
        var site = settings.SiteName;
        var readers = await db.Subscribers.Where(s => s.ConfirmedAt != null && !s.Unsubscribed).ToListAsync();
        foreach (var s in readers)
        {
            db.EmailOutbox.Add(new EmailOutbox
            {
                ToAddr = s.Email,
                Subject = subject,
                Body = body + $"\n\n---\nYou get this because you subscribed to {site}. To stop, open:\n{settings.Email.SiteUrl}/unsubscribe/{s.Token}"
            });
        }
        if (readers.Count > 0) await db.SaveChangesAsync();
    }
}
