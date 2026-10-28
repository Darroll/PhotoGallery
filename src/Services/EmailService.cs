using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public class EmailSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "192.168.2.3";
    public int Port { get; set; } = 25;
    public string From { get; set; } = "gallery@darrollwalsh.com";
    /// <summary>Where alerts are sent (the photographer).</summary>
    public string To { get; set; } = "";
    /// <summary>Public address of the site, used for links inside emails.</summary>
    public string SiteUrl { get; set; } = "";
    public bool GuestUploads { get; set; } = true;
    public bool LinkAlerts { get; set; } = true;
    public bool GalleryExpiring { get; set; } = true;
    public int ExpiryDays { get; set; } = 3;
    public bool WeeklySummary { get; set; }
    public bool LoginAlerts { get; set; } = true;
    /// <summary>Readers may subscribe to emails about new public galleries and photos (double opt-in).</summary>
    public bool Subscribers { get; set; } = true;

    public EmailSettings Sanitized()
    {
        static string T(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }
        return new EmailSettings
        {
            Enabled = Enabled, Host = T(Host, 200), Port = Math.Clamp(Port, 1, 65535), From = T(From, 200), To = T(To, 200),
            SiteUrl = T(SiteUrl, 200).TrimEnd('/'), GuestUploads = GuestUploads, LinkAlerts = LinkAlerts,
            GalleryExpiring = GalleryExpiring, ExpiryDays = Math.Clamp(ExpiryDays, 1, 30), WeeklySummary = WeeklySummary, LoginAlerts = LoginAlerts, Subscribers = Subscribers
        };
    }

    public EmailSettings Clone() => Sanitized();
}

public class EmailOutbox
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string ToAddr { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime NextTry { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    /// <summary>Last error; set with Failed when the message was given up on.</summary>
    public string Error { get; set; } = "";
    public bool Failed { get; set; }
}

/// <summary>Remembers which alerts were already sent so each one goes out once.</summary>
public class AlertState
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Sends alert emails through the configured SMTP relay. Messages are saved in a table first and
/// sent by a background loop, so a relay outage only delays mail (retried with a growing pause).
/// </summary>
public class EmailService(IServiceScopeFactory scopes, SettingsService settings, ILogger<EmailService> log) : BackgroundService
{
    static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    public static async Task QueueAsync(AppDb db, EmailSettings s, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(s.To)) return;
        db.EmailOutbox.Add(new EmailOutbox { ToAddr = s.To, Subject = subject, Body = body });
        await db.SaveChangesAsync();
    }

    /// <summary>Sends one message right now and reports the error text (null on success). Used by the test button.</summary>
    public static async Task<string?> SendAsync(EmailSettings s, string to, string subject, string body, CancellationToken ct = default)
    {
        try
        {
            using var client = new SmtpClient(s.Host, s.Port) { EnableSsl = false, Timeout = 20000 };
            using var msg = new MailMessage(new MailAddress(s.From, "Photo gallery"), new MailAddress(to))
            {
                Subject = subject, Body = body, BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8
            };
            await client.SendMailAsync(msg, ct);
            return null;
        }
        catch (Exception ex) { return (ex.InnerException?.Message ?? ex.Message); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            var lastChecks = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                    var s = settings.Email;
                    if (s.Enabled && !string.IsNullOrWhiteSpace(s.To))
                    {
                        if (DateTime.UtcNow - lastChecks > TimeSpan.FromMinutes(2)) { await Alerts.RunAsync(db, s, ct); lastChecks = DateTime.UtcNow; }
                        await DeliverAsync(db, s, ct);
                    }
                    await Alerts.PurgeAsync(db, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Email cycle failed"); }
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task DeliverAsync(AppDb db, EmailSettings s, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.EmailOutbox.Where(m => m.SentAt == null && !m.Failed && m.NextTry <= now).OrderBy(m => m.Id).Take(20).ToListAsync(ct);
        foreach (var m in due)
        {
            var err = await SendAsync(s, m.ToAddr, m.Subject, m.Body, ct);
            m.Attempts++;
            if (err == null) { m.SentAt = DateTime.UtcNow; m.Error = ""; }
            else
            {
                m.Error = err.Length > 300 ? err[..300] : err;
                if (m.Attempts > Backoff.Length) m.Failed = true;
                else m.NextTry = DateTime.UtcNow + Backoff[m.Attempts - 1];
                log.LogWarning("Email '{Subject}' failed (attempt {N}): {Error}", m.Subject, m.Attempts, err);
            }
            await db.SaveChangesAsync(ct);
            if (err != null) break;   // relay is down: stop and try again next cycle
        }
    }
}

/// <summary>The rules that decide when an alert email is due.</summary>
static class Alerts
{
    static async Task<AlertState?> State(AppDb db, string key) => await db.AlertStates.FirstOrDefaultAsync(a => a.Key == key);

    static async Task Mark(AppDb db, string key, string value = "")
    {
        var a = await State(db, key);
        if (a == null) db.AlertStates.Add(new AlertState { Key = key, Value = value });
        else { a.Value = value; a.At = DateTime.UtcNow; }
        await db.SaveChangesAsync();
    }

    static string Link(EmailSettings s, string path) => s.SiteUrl.Length > 0 ? $"\n{s.SiteUrl}{path}" : "";

    public static async Task RunAsync(AppDb db, EmailSettings s, CancellationToken ct)
    {
        if (s.GuestUploads) await Uploads(db, s);
        if (s.LinkAlerts) await Links(db, s);
        if (s.GalleryExpiring) await Expiring(db, s);
        if (s.WeeklySummary) await Weekly(db, s);
    }

    static async Task Uploads(AppDb db, EmailSettings s)
    {
        var rows = await db.Photos.Where(p => !p.Approved && p.IsGuest).GroupBy(p => p.GalleryId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        var titles = await db.Galleries.ToDictionaryAsync(g => g.Id, g => g.Title);
        foreach (var r in rows)
        {
            var key = $"pending:{r.Key}";
            var st = await State(db, key);
            var notified = st != null && int.TryParse(st.Value, out var n) ? n : 0;
            if (r.N > notified)
            {
                var title = titles.GetValueOrDefault(r.Key, "a gallery");
                await EmailService.QueueAsync(db, s, $"{r.N} guest photo{(r.N == 1 ? "" : "s")} awaiting approval in {title}",
                    $"{r.N} uploaded photo{(r.N == 1 ? " is" : "s are")} waiting for your approval in \"{title}\"." + Link(s, "/admin"));
                await Mark(db, key, r.N.ToString());
            }
            else if (r.N < notified) await Mark(db, key, r.N.ToString());   // some were approved: only newer uploads alert again
        }
        // galleries with nothing pending anymore start over
        var live = rows.Select(r => $"pending:{r.Key}").ToHashSet();
        var stale = await db.AlertStates.Where(a => a.Key.StartsWith("pending:")).ToListAsync();
        var gone = stale.Where(a => !live.Contains(a.Key)).ToList();
        if (gone.Count > 0) { db.AlertStates.RemoveRange(gone); await db.SaveChangesAsync(); }
    }

    static async Task Links(AppDb db, EmailSettings s)
    {
        var now = DateTime.UtcNow;
        var links = await db.ShareLinks.Include(l => l.Gallery).Where(l => l.MaxDownloads != null && !l.Revoked).ToListAsync();
        foreach (var l in links)
        {
            if (l.ExpiresAt != null && l.ExpiresAt <= now) continue;
            var max = l.MaxDownloads!.Value;
            var left = max - l.DownloadCount;
            var name = string.IsNullOrWhiteSpace(l.Label) ? "a share link" : $"share link \"{l.Label}\"";
            var gal = l.Gallery?.Title ?? "a gallery";
            if (left <= 0)
            {
                if (await State(db, $"linkout:{l.Id}") != null) continue;
                await EmailService.QueueAsync(db, s, $"Download limit reached: {name} ({gal})",
                    $"The {max} downloads allowed for {name} in \"{gal}\" are used up. Visitors with this link can no longer download." + Link(s, "/admin"));
                await Mark(db, $"linkout:{l.Id}");
            }
            else if (left <= Math.Max(1, (int)Math.Ceiling(max * 0.2)))
            {
                if (await State(db, $"linklow:{l.Id}") != null) continue;
                await EmailService.QueueAsync(db, s, $"Almost used up: {name} ({gal})",
                    $"{name} in \"{gal}\" has {left} of {max} downloads left." + Link(s, "/admin"));
                await Mark(db, $"linklow:{l.Id}");
            }
        }
    }

    static async Task Expiring(AppDb db, EmailSettings s)
    {
        var now = DateTime.UtcNow;
        var soon = now.AddDays(s.ExpiryDays);
        var gals = await db.Galleries.Where(g => g.ExpiresAt != null && g.ExpiresAt > now && g.ExpiresAt <= soon).ToListAsync();
        foreach (var g in gals)
        {
            var key = $"expiring:{g.Id}:{g.ExpiresAt!.Value.Ticks}";
            if (await State(db, key) != null) continue;
            await EmailService.QueueAsync(db, s, $"Gallery expiring soon: {g.Title}",
                $"\"{g.Title}\" closes to visitors on {g.ExpiresAt:yyyy-MM-dd HH:mm} UTC. Clear or extend the date in the gallery settings to keep it open." + Link(s, $"/admin/galleries/{g.Id}"));
            await Mark(db, key);
        }
    }

    static async Task Weekly(AppDb db, EmailSettings s)
    {
        var now = DateTime.UtcNow;
        if (now.DayOfWeek != DayOfWeek.Monday || now.Hour < 8) return;
        var cal = System.Globalization.ISOWeek.GetWeekOfYear(now);
        var key = $"weekly:{System.Globalization.ISOWeek.GetYear(now)}-{cal}";
        if (await State(db, key) != null) return;
        var since = now.AddDays(-7);
        var rows = await db.Visits.Where(v => v.Time >= since).Select(v => new { v.Kind, v.IsBot, v.GalleryId, v.Ip, v.VisitorId }).ToListAsync();
        var people = rows.Where(r => !r.IsBot).ToList();
        var visitors = people.Select(r => r.VisitorId.Length > 0 ? r.VisitorId : r.Ip).Distinct().Count();
        var titles = await db.Galleries.ToDictionaryAsync(g => g.Id, g => g.Title);
        var top = people.Where(r => r.Kind == "view_gallery" && r.GalleryId != null).GroupBy(r => r.GalleryId!.Value)
            .OrderByDescending(g => g.Count()).Take(5).Select(g => $"  {titles.GetValueOrDefault(g.Key, "?")}: {g.Count()} views");
        var sb = new StringBuilder();
        sb.AppendLine("Last 7 days:");
        sb.AppendLine($"  Visitors: {visitors}");
        sb.AppendLine($"  Gallery views: {people.Count(r => r.Kind == "view_gallery")}");
        sb.AppendLine($"  Downloads: {people.Count(r => r.Kind is "download" or "zip")}");
        sb.AppendLine($"  Guest uploads: {people.Count(r => r.Kind == "upload")}");
        sb.AppendLine($"  Bot requests (not counted above): {rows.Count(r => r.IsBot)}");
        var topList = top.ToList();
        if (topList.Count > 0) { sb.AppendLine(); sb.AppendLine("Most viewed galleries:"); topList.ForEach(t => sb.AppendLine(t)); }
        sb.Append(Link(s, "/admin/stats"));
        await EmailService.QueueAsync(db, s, "Weekly gallery summary", sb.ToString());
        await Mark(db, key);
    }

    public static async Task PurgeAsync(AppDb db, CancellationToken ct)
    {
        if (DateTime.UtcNow.Minute != 0 && DateTime.UtcNow.Minute != 30) return;
        var cutoff = DateTime.UtcNow.AddDays(-30);
        await db.EmailOutbox.Where(m => (m.SentAt != null || m.Failed) && m.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.AlertStates.Where(a => a.At < DateTime.UtcNow.AddDays(-400)).ExecuteDeleteAsync(ct);
    }
}
