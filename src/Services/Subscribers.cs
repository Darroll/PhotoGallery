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
        settings.Email.Subscribers && settings.Email.Enabled && settings.Email.SiteUrl.Length > 0;

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

    public static Task NewGalleryAsync(AppDb db, SettingsService settings, Gallery g)
    {
        if (!Available(settings) || g.Visibility != Visibility.Public || GalleryRules.IsExpired(g)) return Task.CompletedTask;
        return BroadcastAsync(db, settings, $"New gallery: {g.Title}",
            $"A new gallery has been added to {settings.SiteName}: {g.Title}.\n\n{settings.Email.SiteUrl}/g/{g.Slug}");
    }

    public static Task NewPhotosAsync(AppDb db, SettingsService settings, Gallery g, int added)
    {
        if (added <= 0 || !Available(settings) || g.Visibility != Visibility.Public || GalleryRules.IsExpired(g)) return Task.CompletedTask;
        var what = added == 1 ? "1 new photo" : $"{added} new photos";
        var verb = added == 1 ? "has" : "have";
        return BroadcastAsync(db, settings, $"{what} in {g.Title}",
            $"{what} {verb} been added to \"{g.Title}\" on {settings.SiteName}.\n\n{settings.Email.SiteUrl}/g/{g.Slug}");
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
