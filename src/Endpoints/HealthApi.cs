using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>The admin overview: a short list of checks that say whether the app is healthy, each with a status colour.</summary>
public static class HealthApi
{
    // ok = green, warn = amber, bad = red, off = grey (switched off on purpose)
    record Check(string Id, string Group, string Label, string Status, string Value, string Detail, string? Link = null);

    static int Rank(string status) => status switch { "bad" => 3, "warn" => 2, "ok" => 1, _ => 0 };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin/health").RequireAuthorization("Admin");

        g.MapGet("", async (AppDb db, AppConfig cfg, SettingsService settings, CacheService cache, ThumbnailService thumbs, BackupService backups) =>
        {
            var checks = new List<Check>();
            var now = DateTime.UtcNow;

            // --- storage ---
            var libraryOk = Directory.Exists(cfg.LibraryRoot);
            checks.Add(libraryOk
                ? new Check("library", "Storage", "Photo library", "ok", "Reachable", cfg.LibraryRoot)
                : new Check("library", "Storage", "Photo library", "bad", "Not found", $"The folder {cfg.LibraryRoot} cannot be read. Is the share mounted?"));

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(cfg.DataDir) ?? cfg.DataDir);
                var free = drive.AvailableFreeSpace; var total = drive.TotalSize;
                var pct = total > 0 ? (double)free / total * 100 : 100;
                var status = free < 1L << 30 || pct < 3 ? "bad" : free < 5L << 30 || pct < 10 ? "warn" : "ok";
                checks.Add(new Check("disk", "Storage", "Disk space for data and thumbnails", status, $"{fmt(free)} free", $"{pct:0}% of {fmt(total)} is free where the database and thumbnail cache live."));
            }
            catch { checks.Add(new Check("disk", "Storage", "Disk space for data and thumbnails", "off", "Unknown", "The free space could not be read.")); }

            if (!backups.Enabled) checks.Add(new Check("backup", "Storage", "Database backup", "off", "Off", "BACKUP_HOURS is 0, so no snapshots are made.", "/admin/stats"));
            else
            {
                var newest = backups.List().FirstOrDefault();
                var limit = TimeSpan.FromHours(cfg.BackupHours * 1.5);
                if (newest == null)
                    checks.Add(backups.LastError != null
                        ? new Check("backup", "Storage", "Database backup", "bad", "Failed", "No backup exists and the last attempt failed: " + backups.LastError, "/admin/stats")
                        : new Check("backup", "Storage", "Database backup", "warn", "None yet", "The first backup is made a few minutes after the app starts.", "/admin/stats"));
                else
                {
                    var age = now - newest.Time;
                    var status = age <= limit ? "ok" : backups.LastError != null || age > limit * 2 ? "bad" : "warn";
                    var detail = $"Newest snapshot {newest.Time.ToLocalTime():g}, {fmt(newest.Bytes)}." + (status == "ok" ? "" : backups.LastError != null ? " The last attempt failed: " + backups.LastError : " Backups are overdue.");
                    checks.Add(new Check("backup", "Storage", "Database backup", status, Ago(age), detail, "/admin/stats"));
                }
            }

            int missing = 0;
            try { missing = await cache.MissingAsync(null); } catch { }
            var busy = thumbs.BusyGalleries;
            checks.Add(busy > 0
                ? new Check("thumbs", "Storage", "Thumbnails", "warn", "Building", $"{busy} {(busy == 1 ? "gallery is" : "galleries are")} building thumbnails now; {missing} photos still need them.", "/admin/settings/cache")
                : missing > 0
                    ? new Check("thumbs", "Storage", "Thumbnails", "warn", $"{missing} missing", "Some photos have no thumbnail yet and will load slowly the first time.", "/admin/settings/cache")
                    : new Check("thumbs", "Storage", "Thumbnails", "ok", "Complete", "Every approved photo has its thumbnails."));

            // --- galleries ---
            var galleries = await db.Galleries.AsNoTracking().Select(x => new { x.Id, x.Title, x.Folder, x.ExpiresAt }).ToListAsync();
            var expired = galleries.Count(x => x.ExpiresAt != null && x.ExpiresAt <= now);
            var soon = galleries.Count(x => x.ExpiresAt != null && x.ExpiresAt > now && x.ExpiresAt <= now.AddDays(7));
            var lost = libraryOk ? galleries.Where(x => !Directory.Exists(Path.Combine(cfg.LibraryRoot, x.Folder))).Select(x => x.Title).ToList() : new List<string>();
            checks.Add(lost.Count > 0
                ? new Check("folders", "Galleries", "Gallery folders", "bad", $"{lost.Count} missing", "No folder on disk for: " + string.Join(", ", lost.Take(5)) + (lost.Count > 5 ? ", ..." : ""), "/admin/galleries")
                : new Check("folders", "Galleries", "Gallery folders", "ok", "All found", $"{galleries.Count} {(galleries.Count == 1 ? "gallery" : "galleries")}, each with its folder on disk."));
            checks.Add(expired > 0 || soon > 0
                ? new Check("expiry", "Galleries", "Expiry", "warn", expired > 0 ? $"{expired} expired" : $"{soon} expiring", $"{expired} expired and {soon} expiring within 7 days.", "/admin/galleries")
                : new Check("expiry", "Galleries", "Expiry", "ok", "None due", "No gallery has expired or expires within 7 days."));

            var unprinted = await db.Photos.CountAsync(x => x.Fingerprint == "" && !x.IsGuest);
            var held = await db.PhotosRetired.CountAsync();
            checks.Add(unprinted > 0
                ? new Check("fileid", "Galleries", "Renamed and moved files", "warn", $"{unprinted} not tracked yet", "Some photos have not been scanned since file tracking began, so renaming them would lose their likes and tags. A rescan fixes this.", "/admin/galleries")
                : new Check("fileid", "Galleries", "Renamed and moved files", "ok", "Tracked", $"Renaming or moving a photo file keeps its likes, tags and caption. {held} removed {(held == 1 ? "photo's" : "photos'")} details are held for {FileIdentity.KeepDays} days in case the file comes back."));

            // --- links ---
            if (!settings.LinkPreviews.Enabled) checks.Add(new Check("links", "Galleries", "Links", "off", "Not checked", "Link previews and checks are switched off.", "/admin/settings"));
            else
            {
                var broken = new List<string>();
                var all = await db.Galleries.AsNoTracking().Where(x => x.ExpiresAt == null || x.ExpiresAt > now).ToListAsync();
                foreach (var gal in all)
                    foreach (var l in GalleryRules.Links(gal))
                        if (LinkHealth.Get(cfg, l.Url) is { Status: "broken" }) broken.Add($"{l.Label} ({gal.Title})");
                checks.Add(broken.Count > 0
                    ? new Check("links", "Galleries", "Links", "warn", $"{broken.Count} not opening", "These links did not open at the last check: " + string.Join(", ", broken.Take(5)) + (broken.Count > 5 ? ", ..." : ""), "/admin/galleries")
                    : new Check("links", "Galleries", "Links", "ok", "All open", "No saved link has failed its check. Links are checked about once a day."));
            }

            // --- waiting for you ---
            var pendingPhotos = await db.Photos.CountAsync(p => !p.Approved);
            checks.Add(pendingPhotos > 0
                ? new Check("uploads", "Waiting for you", "Guest uploads", "warn", $"{pendingPhotos} to approve", "Photos uploaded by guests wait for your approval before anyone sees them.", "/admin/galleries")
                : new Check("uploads", "Waiting for you", "Guest uploads", "ok", "None waiting", "No guest photos need approval."));
            var pendingCaptions = await db.CaptionEdits.CountAsync(e => e.Status == "pending");
            checks.Add(pendingCaptions > 0
                ? new Check("captions", "Waiting for you", "Caption suggestions", "warn", $"{pendingCaptions} to review", "Signed-in visitors suggested changes.", "/admin/settings/review")
                : new Check("captions", "Waiting for you", "Caption suggestions", "ok", "None waiting", "No suggestions need review."));

            // --- email ---
            var email = settings.Email;
            if (!email.Enabled) checks.Add(new Check("email", "Email", "Email alerts", "off", "Off", "Alerts are switched off.", "/admin/settings/email"));
            else
            {
                var failed = await db.EmailOutbox.CountAsync(m => m.Failed && m.CreatedAt >= now.AddDays(-7));
                var stuck = await db.EmailOutbox.CountAsync(m => !m.Failed && m.SentAt == null && m.Attempts > 0);
                checks.Add(failed > 0
                    ? new Check("email", "Email", "Email alerts", "bad", $"{failed} failed", "Alerts were given up on in the last 7 days. Check the mail relay.", "/admin/settings/email")
                    : stuck > 0
                        ? new Check("email", "Email", "Email alerts", "warn", $"{stuck} retrying", "The mail relay is not accepting mail right now; alerts are queued and retried.", "/admin/settings/email")
                        : new Check("email", "Email", "Email alerts", "ok", "Working", "No failed or stuck alerts."));
            }

            // --- security ---
            var stats = settings.Stats;
            if (!stats.Enabled) checks.Add(new Check("signin", "Security", "Wrong passwords (24 h)", "off", "Not recorded", "Visitor statistics are off, so failed attempts are not counted.", "/admin/settings/visitor-stats"));
            else
            {
                var since = now.AddHours(-24);
                var fails = await db.Visits.CountAsync(v => v.Kind == "unlock_fail" && v.Time >= since && !v.IsBot);
                checks.Add(new Check("signin", "Security", "Wrong passwords (24 h)", fails >= 50 ? "bad" : fails >= 10 ? "warn" : "ok", fails.ToString(),
                    fails >= 10 ? "Many wrong gallery passwords were entered. Someone may be guessing." : "Wrong gallery passwords entered in the last 24 hours.", "/admin/stats"));
            }
            checks.Add(cfg.AdminPasswordGenerated
                ? new Check("adminpw", "Security", "Admin password", "warn", "Generated", "No ADMIN_PASSWORD is set, so a random one is made on every start. Set ADMIN_PASSWORD in the container settings.")
                : new Check("adminpw", "Security", "Admin password", "ok", "Set", cfg.OidcEnabled ? "Single sign-on is also available." : "Set in the container settings."));

            // --- visitor activity ---
            if (!stats.Enabled) checks.Add(new Check("activity", "Activity", "Visitor activity (24 h)", "off", "Off", "Visitor statistics are switched off.", "/admin/settings/visitor-stats"));
            else
            {
                var since = now.AddHours(-24);
                var events = await db.Visits.CountAsync(v => v.Time >= since && !v.IsBot);
                checks.Add(new Check("activity", "Activity", "Visitor activity (24 h)", "ok", events.ToString(), events == 0 ? "No visits recorded in the last 24 hours." : "Visits, views and downloads recorded in the last 24 hours.", "/admin/stats"));
            }

            // --- automatic captions ---
            if (cfg.CaptionUrl == null) checks.Add(new Check("autocap", "Activity", "Automatic captions", "off", "Off", "No caption server is configured."));
            else
            {
                var gaveUp = await db.Photos.CountAsync(p => p.Approved && p.Caption == "" && p.AltText == "" && p.CaptionAttempts >= CaptionService.MaxAttempts);
                var queued = await db.Photos.CountAsync(p => p.Approved && p.Caption == "" && p.AltText == "" && p.CaptionAttempts < CaptionService.MaxAttempts);
                checks.Add(gaveUp > 0
                    ? new Check("autocap", "Activity", "Automatic captions", "warn", $"{gaveUp} failed", $"{gaveUp} photos could not be captioned after {CaptionService.MaxAttempts} tries. Is the caption server up?")
                    : new Check("autocap", "Activity", "Automatic captions", "ok", queued > 0 ? $"{queued} queued" : "Up to date", queued > 0 ? "Photos are waiting to be captioned." : "Every photo has a caption."));
            }

            var worst = checks.Max(c => Rank(c.Status));
            var overall = worst >= 3 ? "bad" : worst == 2 ? "warn" : "ok";
            var up = DateTime.Now - Process.GetCurrentProcess().StartTime;
            return Results.Ok(new
            {
                overall,
                version = cfg.Version, commit = cfg.Commit, built = cfg.BuildDate,
                uptimeSeconds = (long)up.TotalSeconds,
                counts = new
                {
                    galleries = galleries.Count,
                    photos = await db.Photos.CountAsync(p => p.Approved),
                    subscribers = await db.Subscribers.CountAsync()
                },
                checks
            });
        });
    }

    static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "Just now" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago" : t.TotalHours < 48 ? $"{(int)t.TotalHours} h ago" : $"{(int)t.TotalDays} days ago";

    static string fmt(long n) => n >= 1L << 40 ? $"{n / (double)(1L << 40):0.0} TB" : n >= 1L << 30 ? $"{n / (double)(1L << 30):0.0} GB" : $"{n / (double)(1L << 20):0} MB";
}
