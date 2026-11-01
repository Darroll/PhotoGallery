using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>What a new admin has finished or hidden: the setup guide and the checklist on the Overview.</summary>
public class SetupSettings
{
    /// <summary>The first-run guide was finished or skipped, so it is not offered again on its own.</summary>
    public bool WizardDone { get; set; }
    /// <summary>The admin dismissed the checklist.</summary>
    public bool ChecklistHidden { get; set; }
    public SetupSettings Sanitized() => new() { WizardDone = WizardDone, ChecklistHidden = ChecklistHidden };
}

/// <summary>The first-run setup guide, the Overview checklist, and the "does my address work" test behind both.</summary>
public static class OnboardingApi
{
    record StateInput(bool? WizardDone, bool? ChecklistHidden);
    record AddressInput(string? Url);

    /// <summary>One line of the checklist. Optional lines never keep the checklist on screen.</summary>
    record Step(string Id, string Label, string Detail, bool Done, bool Optional, string Link);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin/onboarding").RequireAuthorization("Admin");
        g.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Request.Method is "POST" or "PUT" && ctx.HttpContext.Request.Headers["X-Requested-With"] != "pg"
                ? Results.BadRequest("missing header") : await next(ctx));

        g.MapGet("", async (AppDb db, AppConfig cfg, SettingsService settings, SsoConfig sso, BackupService backups) =>
        {
            var gals = await db.Galleries.AsNoTracking().Select(x => new { x.Id, x.Title, x.Visibility, hasPassword = x.PasswordHash != null && x.PasswordHash != "" }).ToListAsync();
            var photos = await db.Photos.CountAsync(p => p.Approved);
            var shareLinks = await db.ShareLinks.CountAsync(l => !l.Revoked);
            var shared = gals.Any(x => x.Visibility != Visibility.Protected || x.hasPassword) || shareLinks > 0;
            var snapshots = backups.Enabled ? backups.List().Count : 0;
            var email = settings.Email;
            var setup = settings.Setup;

            var steps = new List<Step>
            {
                new("gallery", "Make your first gallery", photos > 0 ? $"{gals.Count} {(gals.Count == 1 ? "gallery" : "galleries")} with {photos} {(photos == 1 ? "photo" : "photos")}." : "Pick a folder of photos on your share and turn it into a gallery.", photos > 0, false, "/admin/setup"),
                new("share", "Share a gallery with someone", shared ? "At least one gallery can be opened by people you chose." : "Galleries start private. Open one, set who can see it, and send the link.", shared, false, "/admin/galleries"),
                new("backup", "Have a backup", !backups.Enabled ? "Automatic backups are switched off (BACKUP_HOURS is 0)." : snapshots > 0 ? "A snapshot of the database exists. Copy the backups folder off this server now and then." : "The first snapshot is made a few minutes after the app starts.", snapshots > 0, false, "/admin/stats"),
                new("address", "Set the address people use", email.SiteUrl.Length > 0 ? "Emails and the address test use " + email.SiteUrl + "." : "Needed for links in emails, and so the Overview can check that it works from outside.", email.SiteUrl.Length > 0, true, "/admin/setup"),
                new("email", "Turn on email alerts", email.Ready ? "Alerts are on." : "Get told about new comments, guest uploads and failed sign-ins.", email.Ready, true, "/admin/settings/email"),
                new("signin", "Let visitors sign in", sso.Enabled ? "Sign-in is on." : "Needed for likes, comments and galleries for specific people.", sso.Enabled, true, "/admin/settings/sso")
            };
            var requiredDone = steps.Where(s => !s.Optional).All(s => s.Done);
            return Results.Ok(new
            {
                wizardDone = setup.WizardDone,
                wizardPending = !setup.WizardDone && gals.Count == 0,
                checklistHidden = setup.ChecklistHidden,
                showChecklist = !setup.ChecklistHidden && !requiredDone,
                requiredDone, steps,
                siteName = settings.SiteName, siteUrl = email.SiteUrl,
                library = new { path = cfg.LibraryRoot, readable = FolderAccess.Probe(cfg).LibraryReadable },
                galleries = gals.Select(x => new { x.Id, x.Title })
            });
        });

        g.MapPut("", (StateInput input, SettingsService settings) =>
        {
            var s = settings.Setup;
            if (input.WizardDone is { } w) s.WizardDone = w;
            if (input.ChecklistHidden is { } c) s.ChecklistHidden = c;
            return Results.Ok(settings.SetSetup(s));
        });

        // Saves the public address into the email settings, where emails already read it from.
        g.MapPut("/address", (AddressInput input, SettingsService settings, Reachability reach) =>
        {
            var email = settings.Email;
            if (string.IsNullOrWhiteSpace(input.Url)) { email.SiteUrl = ""; settings.SetEmail(email); return Results.Ok(new { url = "" }); }
            if (Reachability.Normalize(input.Url) is not { } url) return Results.BadRequest("Enter a web address such as https://photos.example.com.");
            email.SiteUrl = url;
            settings.SetEmail(email);
            return Results.Ok(new { url });
        });

        // Settings to a file and back, for moving to another server. Secrets never leave.
        g.MapGet("/export", (SettingsService settings) =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(settings.Export()), "application/json", "photogallery-settings.json"));

        g.MapPost("/import", async (HttpRequest req, SettingsService settings) =>
        {
            if (req.ContentLength is > 200_000) return Results.BadRequest("That file is too big to be a settings export.");
            using var reader = new StreamReader(req.Body);
            var applied = settings.Import(await reader.ReadToEndAsync());
            return applied is null
                ? Results.BadRequest("That is not a PhotoGallery settings file. Use the file from \"Export settings\".")
                : Results.Ok(new { applied });
        });

        // Tests an address (the one in the request, or the saved one) from this server.
        g.MapPost("/test", async (AddressInput input, Reachability reach) =>
        {
            var url = string.IsNullOrWhiteSpace(input.Url) ? reach.Saved : Reachability.Normalize(input.Url);
            if (url == null) return Results.BadRequest("Enter a web address such as https://photos.example.com.");
            return Results.Ok(await reach.CheckAsync(url));
        });
    }
}
