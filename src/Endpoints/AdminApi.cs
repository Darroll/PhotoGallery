using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public static class AdminApi
{
    record LoginRequest(string? Password, string? Username = null);

    static string Clip(string? s) => new string((s ?? "").Where(c => !char.IsControl(c)).Take(40).ToArray());
    record GalleryInput(string? Title, string? Description, string? Folder, string? Slug, Visibility? Visibility,
        string? Password, bool? AllowDownload, bool? AllowRaw, bool? AllowZip, bool? AllowGuestUpload,
        bool? WatermarkView, bool? WatermarkDownload, RawMatch? RawMatchMode,
        bool? HideRawOnly, GallerySort? SortMode, bool? SortDesc, string? EventDate, string? Location,
        string? AllowedSizes, string? AllowedFormats, string? ExpiresAt, List<LinkItem>? FootageLinks);
    record LinkRawInput(int RawPhotoId);
    record MoveInput(string? Direction);
    record LinkItem(string? Label, string? Url);
    record SiteInput(string? Name);
    record LinkInput(string? Label, int? ExpiresInDays, int? MaxDownloads);
    record CoverInput(int PhotoId);

    static string Slugify(string s)
    {
        var slug = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "gallery" : slug;
    }

    static async Task<string?> YouTubeTitle(string vid)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync($"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString("https://www.youtube.com/watch?v=" + vid)}");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    static object Dto(Gallery g, int count, int pending, int rawOnly = 0) => new
    {
        g.Id, g.Slug, g.Title, g.Description, g.Folder, g.Visibility,
        hasPassword = !string.IsNullOrEmpty(g.PasswordHash),
        g.AllowDownload, g.AllowRaw, g.AllowZip, g.AllowGuestUpload, g.WatermarkView, g.WatermarkDownload,
        rawMatchMode = g.RawMatchMode, hideRawOnly = g.HideRawOnly, sortMode = g.SortMode, sortDesc = g.SortDesc,
        eventDate = g.EventDate, g.Location, g.AllowedSizes, g.AllowedFormats, expiresAt = g.ExpiresAt,
        footageLinks = GalleryRules.Links(g), cover = g.CoverPhotoId, count, pending, rawOnly
    };

    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/api/admin");

        // CSRF defence in depth on top of SameSite=Strict: state-changing calls must carry a custom header.
        admin.AddEndpointFilter(async (ctx, next) =>
        {
            var m = ctx.HttpContext.Request.Method;
            if (m is "POST" or "PUT" or "DELETE" && ctx.HttpContext.Request.Headers["X-Requested-With"] != "pg")
                return Results.BadRequest("missing header");
            return await next(ctx);
        });

        admin.MapPost("/login", async (LoginRequest req, HttpContext ctx, AppConfig cfg, AppDb db, SettingsService settings) =>
        {
            var ip = ActivityLog.ClientIp(ctx);
            // both checks always run so the response time does not reveal which one was wrong
            var userOk = cfg.AdminUsername == null || Passwords.FixedEquals(req.Username?.Trim() ?? "", cfg.AdminUsername);
            var passOk = Passwords.FixedEquals(req.Password ?? "", cfg.AdminPassword);
            if (!(userOk && passOk))
            {
                var recent = await LoginGuard.FailedAsync(ip);
                var mail = settings.Email;
                if (mail.Enabled && mail.LoginAlerts)
                {
                    var who = cfg.AdminUsername == null ? "" : $" Username tried: \"{Clip(req.Username)}\".";
                    var country = ctx.Request.Headers["CF-IPCountry"].ToString();
                    await EmailService.QueueAsync(db, mail, "Admin login: wrong " + (cfg.AdminUsername != null && !userOk ? "username or password" : "password"),
                        $"A failed admin sign-in from {ip}{(country.Length > 0 ? " (" + country + ")" : "")} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC.{who} {recent} failed attempt(s) in the last 15 minutes. If this was not you, consider limiting /admin to your own network.");
                }
                return Results.StatusCode(401);
            }
            LoginGuard.Succeeded(ip);
            var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));
            return Results.NoContent();
        }).RequireRateLimiting("login");

        admin.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        admin.MapGet("/me", (HttpContext ctx, AppConfig cfg) => new { admin = AccessService.IsAdmin(ctx), userRequired = cfg.AdminUsername != null });

        var secured = admin.MapGroup("").RequireAuthorization();

        secured.MapGet("/folders", async (AppConfig cfg, AppDb db) =>
        {
            var taken = (await db.Galleries.Select(x => new { x.Id, x.Title, x.Folder }).ToListAsync())
                .GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var result = new List<object>();
            if (!Directory.Exists(cfg.LibraryRoot)) return Results.Ok(result);
            void Walk(string dir, int depth)
            {
                foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x))
                {
                    if (Scanner.IsHidden(Path.GetFileName(d))) continue;
                    var rel = cfg.Rel(d);
                    taken.TryGetValue(rel, out var owner);
                    result.Add(new { path = rel, galleryId = owner?.Id, galleryTitle = owner?.Title });
                    if (depth < 2) Walk(d, depth + 1);
                }
            }
            Walk(cfg.LibraryRoot, 0);
            return Results.Ok(result);
        });

        secured.MapGet("/galleries", async (AppDb db, SettingsService settings) =>
        {
            var rows = await db.Galleries.Select(g => new
            {
                g,
                count = g.Photos.Count(p => p.Approved),
                pending = g.Photos.Count(p => !p.Approved),
                rawOnly = g.Photos.Count(p => p.Approved && p.RawRelPath != null && p.RawRelPath == p.RelPath)
            }).ToListAsync();
            var byId = rows.ToDictionary(r => r.g.Id);
            return Results.Ok(GalleryRules.OrderGalleries(rows.Select(r => r.g), settings.Home.Order)
                .Select(g => Dto(g, byId[g.Id].count, byId[g.Id].pending, byId[g.Id].rawOnly)));
        });

        // manual gallery order: swap a gallery with its neighbour
        secured.MapPost("/galleries/{id:int}/move", async (int id, MoveInput input, AppDb db, SettingsService settings) =>
        {
            var all = GalleryRules.OrderGalleries(await db.Galleries.ToListAsync(), "manual");
            var idx = all.FindIndex(x => x.Id == id);
            if (idx < 0) return Results.NotFound();
            var to = input.Direction == "up" ? idx - 1 : idx + 1;
            if (to < 0 || to >= all.Count) return Results.NoContent();
            (all[idx], all[to]) = (all[to], all[idx]);
            for (int i = 0; i < all.Count; i++) all[i].SortOrder = i;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // full-size photo shown in the browser (not counted as a download); RAW-only photos show their embedded preview
        secured.MapGet("/photos/{id:int}/full", async (int id, AppDb db, ImageService images) =>
        {
            var p = await db.Photos.FirstOrDefaultAsync(x => x.Id == id);
            if (p == null) return Results.NotFound();
            var path = await images.GetSourceAsync(p);
            if (path == null || !File.Exists(path)) return Results.NotFound();
            return Results.File(path, ImageService.ContentType(Path.GetExtension(path).TrimStart('.')));
        });
        secured.MapGet("/settings/site", (SettingsService settings) => Results.Ok(new { name = settings.SiteName }));
        secured.MapPut("/settings/site", (SiteInput input, SettingsService settings) => Results.Ok(new { name = settings.SetSiteName(input.Name) }));
        secured.MapGet("/settings/email", (SettingsService settings) => Results.Ok(settings.Email));
        secured.MapPut("/settings/email", (EmailSettings input, SettingsService settings) =>
        {
            var s = input.Sanitized();
            if (s.To.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(s.To, out _)) return Results.BadRequest("That recipient address does not look right.");
            if (!System.Net.Mail.MailAddress.TryCreate(s.From, out _)) return Results.BadRequest("That sender address does not look right.");
            return Results.Ok(settings.SetEmail(s));
        });
        secured.MapPost("/settings/email/test", async (EmailSettings input, SettingsService settings) =>
        {
            var s = input.Sanitized();
            if (!System.Net.Mail.MailAddress.TryCreate(s.To, out _)) return Results.BadRequest("Enter the address that should receive alerts first.");
            var err = await EmailService.SendAsync(s, s.To, "Test email from your photo gallery", "If you can read this, the mail relay settings work.");
            return err == null ? Results.Ok(new { sent = true }) : Results.BadRequest("Could not send: " + err);
        });
        secured.MapGet("/settings/email/outbox", async (AppDb db) =>
        {
            var rows = await db.EmailOutbox.OrderByDescending(m => m.Id).Take(15).ToListAsync();
            return Results.Ok(rows.Select(m => new { m.Id, m.CreatedAt, m.Subject, m.Attempts, m.SentAt, m.Failed, m.Error }));
        });
        secured.MapGet("/settings/home",(SettingsService settings) => Results.Ok(settings.Home));
        secured.MapPut("/settings/home", (HomeSettings input, SettingsService settings) => Results.Ok(settings.SetHome(input)));

        secured.MapPost("/galleries", async (GalleryInput input, AppDb db, AppConfig cfg, Scanner scanner) =>
        {
            if (string.IsNullOrWhiteSpace(input.Title)) return Results.BadRequest("Title required");
            var wanted = input.Title.Trim().ToLower();
            if (await db.Galleries.AnyAsync(x => x.Title.ToLower() == wanted)) return Results.BadRequest("A gallery with that name already exists");
            var dir = cfg.Resolve(input.Folder ?? "");
            if (dir == null || !Directory.Exists(dir)) return Results.BadRequest("Folder not found in library");
            var newFolder = cfg.Rel(dir);
            var owner = await db.Galleries.Where(x => x.Folder.ToLower() == newFolder.ToLower()).Select(x => x.Title).FirstOrDefaultAsync();
            if (owner != null) return Results.BadRequest($"That folder already belongs to the gallery \"{owner}\"");

            var baseSlug = Slugify(string.IsNullOrWhiteSpace(input.Slug) ? input.Title : input.Slug);
            var slug = baseSlug;
            for (int i = 2; await db.Galleries.AnyAsync(g => g.Slug == slug); i++) slug = $"{baseSlug}-{i}";

            var maxOrder = await db.Galleries.AnyAsync() ? await db.Galleries.MaxAsync(x => x.SortOrder) : -1;
            var g = new Gallery
            {
                SortOrder = maxOrder + 1,
                Title = input.Title.Trim(), Slug = slug, Folder = cfg.Rel(dir),
                Description = input.Description ?? "",
                Visibility = input.Visibility ?? Visibility.Protected,
                AllowDownload = input.AllowDownload ?? true, AllowRaw = input.AllowRaw ?? false,
                AllowZip = input.AllowZip ?? true, AllowGuestUpload = input.AllowGuestUpload ?? false,
                WatermarkView = input.WatermarkView ?? false, WatermarkDownload = input.WatermarkDownload ?? false,
                RawMatchMode = input.RawMatchMode ?? RawMatch.Anywhere
            };
            if (!string.IsNullOrEmpty(input.Password)) g.PasswordHash = Passwords.Hash(input.Password);
            db.Galleries.Add(g);
            await db.SaveChangesAsync();
            var scan = await scanner.ScanAsync(db, g);
            return Results.Ok(new { gallery = Dto(g, scan.Total, 0, scan.RawOnly), scan });
        });

        secured.MapPut("/galleries/{id:int}", async (int id, GalleryInput input, AppDb db, AppConfig cfg, Scanner scanner, ThumbnailService thumbs) =>
        {
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            var folderChanged = false;
            if (!string.IsNullOrWhiteSpace(input.Folder))
            {
                var dir = cfg.Resolve(input.Folder);
                if (dir == null || !Directory.Exists(dir)) return Results.BadRequest("Folder not found in library");
                var rel = cfg.Rel(dir);
                if (!string.Equals(rel, g.Folder, StringComparison.OrdinalIgnoreCase))
                {
                    var owner = await db.Galleries.Where(x => x.Id != g.Id && x.Folder.ToLower() == rel.ToLower()).Select(x => x.Title).FirstOrDefaultAsync();
                    if (owner != null) return Results.BadRequest($"That folder already belongs to the gallery \"{owner}\"");
                    g.Folder = rel;
                    folderChanged = true;
                }
            }
            if (input.Title != null)
            {
                var newTitle = input.Title.Trim();
                if (newTitle.Length == 0) return Results.BadRequest("Title required");
                var lowered = newTitle.ToLower();
                if (await db.Galleries.AnyAsync(x => x.Id != g.Id && x.Title.ToLower() == lowered)) return Results.BadRequest("A gallery with that name already exists");
                g.Title = newTitle;
            }
            if (input.Description != null) g.Description = input.Description;
            if (!string.IsNullOrWhiteSpace(input.Slug))
            {
                var s = Slugify(input.Slug);
                if (s != g.Slug && await db.Galleries.AnyAsync(x => x.Slug == s)) return Results.BadRequest("Slug in use");
                g.Slug = s;
            }
            if (input.Visibility != null && input.Visibility != g.Visibility) { g.Visibility = input.Visibility.Value; g.AccessStamp = Guid.NewGuid().ToString("N"); }
            if (input.Password != null)
            {
                g.PasswordHash = input.Password == "" ? null : Passwords.Hash(input.Password);
                g.AccessStamp = Guid.NewGuid().ToString("N");
            }
            if (input.AllowDownload != null) g.AllowDownload = input.AllowDownload.Value;
            if (input.AllowRaw != null) g.AllowRaw = input.AllowRaw.Value;
            if (input.AllowZip != null) g.AllowZip = input.AllowZip.Value;
            if (input.AllowGuestUpload != null) g.AllowGuestUpload = input.AllowGuestUpload.Value;
            if (input.WatermarkView != null) g.WatermarkView = input.WatermarkView.Value;
            if (input.WatermarkDownload != null) g.WatermarkDownload = input.WatermarkDownload.Value;
            if (input.RawMatchMode != null) g.RawMatchMode = input.RawMatchMode.Value;
            if (input.HideRawOnly != null) g.HideRawOnly = input.HideRawOnly.Value;
            if (input.SortMode != null) g.SortMode = input.SortMode.Value;
            if (input.SortDesc != null) g.SortDesc = input.SortDesc.Value;
            if (input.Location != null) g.Location = input.Location.Trim();
            if (input.FootageLinks != null)
            {
                var items = new List<GalleryRules.FootageLink>();
                foreach (var l in input.FootageLinks)
                {
                    var label = l.Label ?? "";
                    // an empty label on a YouTube link is filled with the video's own title
                    if (label.Trim().Length == 0 && GalleryRules.YouTubeId(l.Url) is { } vid) label = await YouTubeTitle(vid) ?? "";
                    items.Add(new GalleryRules.FootageLink(label, l.Url ?? ""));
                }
                g.FootageLinks = GalleryRules.CleanLinks(items);
            }
            if (input.EventDate != null) g.EventDate = DateTime.TryParse(input.EventDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ed) ? ed : null;
            if (input.ExpiresAt != null) g.ExpiresAt = DateTime.TryParse(input.ExpiresAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ex) ? ex : null;
            if (input.AllowedSizes != null) g.AllowedSizes = string.Join(',', input.AllowedSizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => int.TryParse(s, out _)));
            if (input.AllowedFormats != null) g.AllowedFormats = string.Join(',', input.AllowedFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).Where(s => ImageService.LosslessFormats.Contains(s)));
            await db.SaveChangesAsync();
            if (input.WatermarkView == true) thumbs.Enqueue(g.Id);
            if (folderChanged)
            {
                g.CoverPhotoId = null; // old cover belonged to the previous folder
                await db.SaveChangesAsync();
                await scanner.ScanAsync(db, g);
            }
            return Results.Ok(Dto(g, await db.Photos.CountAsync(p => p.GalleryId == id && p.Approved), await db.Photos.CountAsync(p => p.GalleryId == id && !p.Approved), await db.Photos.CountAsync(p => p.GalleryId == id && p.Approved && p.RawRelPath != null && p.RawRelPath == p.RelPath)));
        });

        // Removes the gallery and cached derivatives. Original files in the library are never touched.
        secured.MapDelete("/galleries/{id:int}", async (int id, AppDb db, AppConfig cfg) =>
        {
            var g = await db.Galleries.Include(x => x.Photos).FirstOrDefaultAsync(x => x.Id == id);
            if (g == null) return Results.NotFound();
            foreach (var p in g.Photos)
            {
                var d = Path.Combine(cfg.CacheDir, "variants", p.Id.ToString());
                if (Directory.Exists(d)) Directory.Delete(d, true);
            }
            await db.Favorites.Where(f => f.GalleryId == id).ExecuteDeleteAsync();
            await db.FavoriteOwners.Where(o => o.GalleryId == id).ExecuteDeleteAsync();
            db.Galleries.Remove(g);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        secured.MapPost("/galleries/{id:int}/scan", async (int id, AppDb db, Scanner scanner) =>
        {
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            try { return Results.Ok(await scanner.ScanAsync(db, g)); }
            catch (DirectoryNotFoundException e) { return Results.BadRequest(e.Message); }
        });

        secured.MapPut("/galleries/{id:int}/cover", async (int id, CoverInput input, AppDb db) =>
        {
            var g = await db.Galleries.FindAsync(id);
            if (g == null || !await db.Photos.AnyAsync(p => p.Id == input.PhotoId && p.GalleryId == id && p.Approved)) return Results.NotFound();
            g.CoverPhotoId = input.PhotoId;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // --- share links ---
        secured.MapGet("/galleries/{id:int}/links", async (int id, AppDb db) =>
            Results.Ok(await db.ShareLinks.Where(l => l.GalleryId == id).OrderByDescending(l => l.CreatedAt)
                .Select(l => new { l.Id, l.Token, l.Label, l.ExpiresAt, l.MaxDownloads, l.DownloadCount, l.Revoked, l.CreatedAt }).ToListAsync()));

        secured.MapPost("/galleries/{id:int}/links", async (int id, LinkInput input, AppDb db) =>
        {
            if (!await db.Galleries.AnyAsync(g => g.Id == id)) return Results.NotFound();
            var link = new ShareLink
            {
                GalleryId = id, Token = Passwords.NewToken(), Label = input.Label ?? "",
                ExpiresAt = input.ExpiresInDays is > 0 ? DateTime.UtcNow.AddDays(input.ExpiresInDays.Value) : null,
                MaxDownloads = input.MaxDownloads is > 0 ? input.MaxDownloads : null
            };
            db.ShareLinks.Add(link);
            await db.SaveChangesAsync();
            return Results.Ok(new { link.Id, link.Token, link.Label, link.ExpiresAt, link.MaxDownloads, link.DownloadCount, link.Revoked, link.CreatedAt });
        });

        secured.MapDelete("/links/{id:int}", async (int id, AppDb db) =>
        {
            var l = await db.ShareLinks.FindAsync(id);
            if (l == null) return Results.NotFound();
            l.Revoked = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // --- favorites chosen by visitors ---
        secured.MapGet("/galleries/{id:int}/favorites", async (int id, AppDb db) =>
        {
            var favs = await db.Favorites.AsNoTracking().Where(f => f.GalleryId == id).ToListAsync();
            var photoIds = favs.Select(f => f.PhotoId).Distinct().ToList();
            var photos = await db.Photos.AsNoTracking().Where(p => photoIds.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.RawRelPath }).ToDictionaryAsync(p => p.Id);
            var owners = await db.FavoriteOwners.AsNoTracking().Where(o => o.GalleryId == id).ToDictionaryAsync(o => o.VisitorId, o => o.Name);
            var vids = favs.Select(f => f.VisitorId).Distinct().ToList();
            var seen = (await db.Visits.AsNoTracking().Where(v => v.GalleryId == id && vids.Contains(v.VisitorId)).Select(v => new { v.VisitorId, v.Ip, v.Country, v.UserAgent, v.Time }).ToListAsync())
                .GroupBy(v => v.VisitorId).ToDictionary(grp => grp.Key, grp => grp.OrderByDescending(v => v.Time).First());
            DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

            var lists = favs.GroupBy(f => f.VisitorId).Select(grp =>
            {
                seen.TryGetValue(grp.Key, out var s);
                return new
                {
                    visitorId = grp.Key, name = owners.TryGetValue(grp.Key, out var n) ? n : "",
                    count = grp.Count(), updated = Utc(grp.Max(f => f.CreatedAt)),
                    ip = s?.Ip ?? "", country = s?.Country ?? "", userAgent = s?.UserAgent ?? "",
                    photos = grp.OrderBy(f => f.CreatedAt).Where(f => photos.ContainsKey(f.PhotoId)).Select(f => new
                    {
                        id = f.PhotoId, name = photos[f.PhotoId].Name,
                        raw = photos[f.PhotoId].RawRelPath != null ? Path.GetFileName(photos[f.PhotoId].RawRelPath!) : ""
                    }).ToList()
                };
            }).OrderByDescending(x => x.updated).ToList();

            var top = favs.GroupBy(f => f.PhotoId).Where(grp => photos.ContainsKey(grp.Key))
                .Select(grp => new { photoId = grp.Key, name = photos[grp.Key].Name, count = grp.Count() })
                .OrderByDescending(x => x.count).ThenBy(x => x.name).Take(30).ToList();
            return Results.Ok(new { lists, top });
        });

        // --- guest upload moderation ---
        secured.MapPost("/photos/{id:int}/approve", async (int id, AppDb db) =>
        {
            var p = await db.Photos.FindAsync(id);
            if (p == null) return Results.NotFound();
            p.Approved = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        secured.MapDelete("/photos/{id:int}", async (int id, AppDb db, AppConfig cfg) =>
        {
            var p = await db.Photos.FindAsync(id);
            if (p == null) return Results.NotFound();
            if (!p.IsGuest) return Results.BadRequest("Only guest uploads can be deleted here; originals are never removed.");
            var path = cfg.ResolvePhoto(p, p.RelPath);
            if (path != null && File.Exists(path)) File.Delete(path);
            var d = Path.Combine(cfg.CacheDir, "variants", p.Id.ToString());
            if (Directory.Exists(d)) Directory.Delete(d, true);
            db.Photos.Remove(p);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // --- RAW linking (for RAW files whose name does not match automatically) ---
        secured.MapPost("/photos/{id:int}/link-raw", async (int id, LinkRawInput input, AppDb db, AppConfig cfg) =>
        {
            var target = await db.Photos.FindAsync(id);
            var raw = await db.Photos.FindAsync(input.RawPhotoId);
            if (target == null || raw == null || target.GalleryId != raw.GalleryId || target.IsGuest) return Results.NotFound();
            if (!raw.RawOnly) return Results.BadRequest("Selected item is not a RAW-only photo");
            if (target.RawOnly) return Results.BadRequest("Pick a processed image (PNG/JPEG) as the target");
            target.RawRelPath = raw.RelPath;
            target.RawLocked = true;
            var d = Path.Combine(cfg.CacheDir, "variants", raw.Id.ToString());
            if (Directory.Exists(d)) try { Directory.Delete(d, true); } catch { }
            db.Photos.Remove(raw);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        secured.MapDelete("/photos/{id:int}/link-raw", async (int id, AppDb db, Scanner scanner) =>
        {
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id);
            if (p?.Gallery == null || p.RawRelPath == null || p.RawOnly) return Results.NotFound();
            p.RawRelPath = null;
            p.RawLocked = true; // stays unlinked on later scans
            await db.SaveChangesAsync();
            await scanner.ScanAsync(db, p.Gallery); // brings the freed RAW back as a RAW-only photo
            return Results.NoContent();
        });

        // --- settings ---
        secured.MapGet("/settings", (SettingsService settings, ImageService images) =>
            Results.Ok(new { watermark = settings.Watermark, hasPng = images.HasWatermarkPng }));

        secured.MapPut("/settings/watermark", async (WatermarkSettings input, SettingsService settings, AppDb db, ThumbnailService thumbs) =>
        {
            var saved = settings.SetWatermark(input);
            foreach (var gid in await db.Galleries.Where(g => g.WatermarkView).Select(g => g.Id).ToListAsync()) thumbs.Enqueue(gid);
            return Results.Ok(saved);
        });

        // --- thumbnail cache ---
        secured.MapGet("/cache", async (CacheService cache, ThumbnailService thumbs) =>
        {
            var s = await cache.StatsAsync();
            return Results.Ok(new { bytes = s.Bytes, files = s.Files, staleFiles = s.StaleFiles, staleBytes = s.StaleBytes, busyGalleries = thumbs.BusyGalleries, missing = s.Missing });
        });

        secured.MapPost("/cache/clean", async (CacheService cache) => Results.Ok(await cache.CleanAsync()));

        secured.MapPost("/cache/rebuild-all", async (AppDb db, ThumbnailService thumbs) =>
        {
            var ids = await db.Galleries.Select(g => g.Id).ToListAsync();
            foreach (var gid in ids) thumbs.Enqueue(gid, force: true);
            return Results.Ok(new { galleries = ids.Count });
        });

        secured.MapGet("/galleries/{id:int}/thumbnails", async (int id, ThumbnailService thumbs, CacheService cache) =>
        {
            var s = thumbs.Status(id);
            return Results.Ok(new { s.Total, s.Done, s.Running, s.Queued, missing = await cache.MissingAsync(id) });
        });

        // builds only what is not cached yet (rebuild deletes first)
        secured.MapPost("/galleries/{id:int}/thumbnails/generate", async (int id, AppDb db, ThumbnailService thumbs) =>
        {
            if (!await db.Galleries.AnyAsync(g => g.Id == id)) return Results.NotFound();
            thumbs.Enqueue(id);
            return Results.Accepted();
        });

        secured.MapPost("/cache/generate-all", async (AppDb db, ThumbnailService thumbs) =>
        {
            var ids = await db.Galleries.Select(g => g.Id).ToListAsync();
            foreach (var gid in ids) thumbs.Enqueue(gid);
            return Results.Ok(new { galleries = ids.Count });
        });

        secured.MapPost("/galleries/{id:int}/thumbnails/rebuild", async (int id, AppDb db, ThumbnailService thumbs) =>
        {
            if (!await db.Galleries.AnyAsync(g => g.Id == id)) return Results.NotFound();
            thumbs.Enqueue(id, force: true);
            return Results.Accepted();
        });

        // renders a sample photo with unsaved settings so the admin can tune them
        secured.MapPost("/settings/watermark/preview", async (WatermarkSettings input, AppDb db, ImageService images) =>
        {
            var p = await db.Photos.Where(x => x.Approved && !x.IsGuest && (x.RawRelPath == null || x.RawRelPath != x.RelPath))
                .OrderBy(x => x.Id).FirstOrDefaultAsync();
            p ??= await db.Photos.OrderBy(x => x.Id).FirstOrDefaultAsync();
            if (p == null) return Results.NotFound("Add a gallery with photos first");
            var bytes = await images.PreviewWatermarkAsync(p, input);
            return bytes == null ? Results.NotFound() : Results.File(bytes, "image/webp");
        });

        secured.MapPost("/settings/watermark/png", async (HttpContext ctx, AppConfig cfg) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var f = form.Files.FirstOrDefault();
            if (f == null || f.Length > 2_000_000) return Results.BadRequest("PNG up to 2 MB required");
            await using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            var b = ms.ToArray();
            if (b.Length < 8 || b[0] != 0x89 || b[1] != 0x50 || b[2] != 0x4E || b[3] != 0x47) return Results.BadRequest("Not a PNG file");
            await File.WriteAllBytesAsync(cfg.WatermarkImagePath, b);
            return Results.NoContent();
        }).DisableAntiforgery();

        secured.MapDelete("/settings/watermark/png", (AppConfig cfg) =>
        {
            if (File.Exists(cfg.WatermarkImagePath)) File.Delete(cfg.WatermarkImagePath);
            return Results.NoContent();
        });
    }
}
