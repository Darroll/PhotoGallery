using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public static class PublicApi
{
    record UnlockRequest(string? Password);
    record FavInput(bool On);
    record NameInput(string? Name);
    record LicenseAcceptInput(string? Version);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/site", (AppConfig cfg, ImageService img, SettingsService settings) => new
        {
            title = settings.SiteName,
            version = cfg.Version, commit = cfg.Commit, built = cfg.BuildDate,
            sizes = cfg.Sizes,
            formats = ImageService.LosslessFormats,
            watermark = img.WatermarkAvailable,
            theme = settings.Theme.ForVisitors(),
            subscribe = Subscribers.Available(settings)
        });

        app.MapGet("/api/license", async (HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            var v = settings.License.ForVisitors(settings.SiteName);
            return Results.Ok(new { v.Enabled, v.Require, v.Title, v.Text, v.Version, accepted = await LicenseGate.AcceptedAsync(ctx, db, settings) });
        });

        app.MapPost("/api/license/accept", async (LicenseAcceptInput input, HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest("missing header");
            var v = settings.License.ForVisitors(settings.SiteName);
            if (!v.Enabled) return Results.NoContent();
            if (input.Version != v.Version) return Results.Conflict("The license has changed. Reload the page and read it again.");
            var name = (ctx.User.Identity?.Name ?? "").Trim();
            db.LicenseAcceptances.Add(new LicenseAcceptance
            {
                Sub = AuthApi.Sub(ctx) ?? "", VisitorId = ActivityLog.EnsureVisitorId(ctx),
                Name = name.Length > 120 ? name[..120] : name, Version = v.Version, AcceptedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // The home page list. Visitors get public, unexpired galleries; the admin gets every gallery whatever its visibility or expiry.
        app.MapGet("/api/galleries", async (HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            var now = DateTime.UtcNow;
            var admin = AccessService.IsAdmin(ctx);
            var rows = await db.Galleries.Where(g => admin || (g.Visibility == Visibility.Public && (g.ExpiresAt == null || g.ExpiresAt > now)))
                .Select(g => new { g, count = g.Photos.Count(p => p.Approved) }).ToListAsync();
            var counts = rows.ToDictionary(r => r.g.Id, r => r.count);
            var galleryTags = await Tags.NamesByGalleryAsync(db, rows.Select(r => r.g.Id).ToList());
            var list = GalleryRules.OrderGalleries(rows.Select(r => r.g), settings.Home.Order).Select(g => new
            {
                g.Id, g.Slug, g.Title, g.Description, g.Location, eventDate = g.EventDate, cover = g.CoverPhotoId, count = counts[g.Id],
                g.Visibility, expired = g.ExpiresAt != null && g.ExpiresAt <= now,
                tags = galleryTags.TryGetValue(g.Id, out var gt) ? gt : new List<string>()
            });
            return Results.Ok(list);
        });

        app.MapGet("/api/g/{slug}", async (string slug, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg, ActivityLog activity, SettingsService settings) =>
        {
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (a.Expired) return Results.Json(new { expired = true, title = g.Title }, statusCode: 410);
            if (!a.Allowed)
            {
                if (a.NeedsPassword) return Results.Json(new { needsPassword = true, title = g.Title }, statusCode: 401);
                if (a.NeedsSignIn) return Results.Json(new { needsSignIn = true, title = g.Title }, statusCode: 401);
                return Results.StatusCode(403);
            }

            activity.Record(ctx, "view_gallery", g.Id);
            var admin = a.Admin;
            var vid = ActivityLog.ReadVisitorId(ctx);
            await Likes.AdoptAsync(db, ctx, vid);
            var favoriteIds = vid == "" ? new List<int>() : await db.Favorites.AsNoTracking().Where(f => f.GalleryId == g.Id && f.VisitorId == vid).Select(f => f.PhotoId).ToListAsync();
            var favoriteName = vid == "" ? "" : await db.FavoriteOwners.AsNoTracking().Where(o => o.GalleryId == g.Id && o.VisitorId == vid).Select(o => o.Name).FirstOrDefaultAsync() ?? "";
            var photos = await db.Photos.Where(p => p.GalleryId == g.Id && (p.Approved || admin))
                .Select(p => new { p.Id, w = p.Width, h = p.Height, p.Name, raw = p.RawRelPath != null, rawOnly = p.RawRelPath != null && p.RawRelPath == p.RelPath, taken = p.TakenAt, added = p.AddedAt, p.Approved, p.Caption, p.AltText })
                .ToListAsync();
            photos = GalleryRules.Sort(photos, g, x => x.taken, x => x.Name, x => x.added);

            var tagNames = await Tags.NamesByPhotoAsync(db, photos.Select(x => x.Id).ToList());
            var likeCounts = g.AllowLikes && (g.ShowLikeCounts || admin) ? await Likes.CountsAsync(db, g.Id) : new Dictionary<int, int>();
            var shaped = photos.Select(x => new { x.Id, x.w, x.h, x.Name, x.raw, x.rawOnly, x.taken, x.added, x.Approved, x.Caption, x.AltText, likes = likeCounts.GetValueOrDefault(x.Id),
                tags = tagNames.TryGetValue(x.Id, out var t) ? t : new List<string>() }).ToList();

            var gTags = (await Tags.NamesByGalleryAsync(db, new List<int> { g.Id })).GetValueOrDefault(g.Id) ?? new List<string>();
            var tagColors = await Tags.ColorsAsync(db, gTags.Concat(tagNames.Values.SelectMany(v => v)));
            var locked = g.WatermarkDownload && !admin;
            var previews = settings.LinkPreviews.Enabled;
            if (previews) ExternalLinkPreviews.EnsureInBackground(cfg, GalleryRules.Links(g).Where(l => GalleryRules.CategoryOf(l) == "external").Select(l => l.Url));
            return Results.Ok(new
            {
                g.Id, g.Slug, g.Title, g.Description, g.Visibility, cover = g.CoverPhotoId, tags = gTags, tagColors,
                g.Location, eventDate = g.EventDate, hideRawOnly = g.HideRawOnly, allowLikes = g.AllowLikes, showLikeCounts = g.ShowLikeCounts || admin, links = GalleryRules.Links(g).Select(l => new { l.Label, l.Url, videoId = GalleryRules.YouTubeId(l.Url), category = GalleryRules.CategoryOf(l), button = GalleryRules.ButtonOf(l), l.Note, preview = previews && GalleryRules.CategoryOf(l) == "external" ? ExternalLinkPreviews.ForPage(cfg, l.Url) : null }), favorites = favoriteIds, favoriteName, sizes = GalleryRules.Sizes(g, cfg), formats = GalleryRules.Formats(g),
                allowDownload = g.AllowDownload,
                allowOriginal = g.AllowDownload && !locked,
                allowRaw = g.AllowDownload && g.AllowRaw && !locked,
                allowZip = g.AllowDownload && g.AllowZip,
                allowUpload = g.AllowGuestUpload || admin,
                uploadsModerated = !admin,
                watermarkView = g.WatermarkView,
                watermarkDownload = g.WatermarkDownload,
                admin,
                expired = g.ExpiresAt != null && g.ExpiresAt <= DateTime.UtcNow,
                link = a.Link == null ? null : new
                {
                    expires = a.Link.ExpiresAt,
                    remaining = a.Link.MaxDownloads == null ? (int?)null : Math.Max(0, a.Link.MaxDownloads.Value - a.Link.DownloadCount)
                },
                photos = shaped
            });
        });

        app.MapPost("/api/g/{slug}/viewed/{photoId:int}", async (string slug, int photoId, HttpContext ctx, AppDb db, AccessService access, ActivityLog activity) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest();
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed) return Results.StatusCode(403);
            if (!await db.Photos.AnyAsync(p => p.Id == photoId && p.GalleryId == g.Id && p.Approved)) return Results.NotFound();
            activity.Record(ctx, "view_photo", g.Id, photoId);
            return Results.NoContent();
        }).RequireRateLimiting("viewed");

        app.MapPost("/api/g/{slug}/favorite/{photoId:int}", async (string slug, int photoId, FavInput input, HttpContext ctx, AppDb db, AccessService access, ActivityLog activity) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest();
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed) return Results.StatusCode(403);
            if (!g.AllowLikes) return Results.StatusCode(403);
            if (!await db.Photos.AnyAsync(p => p.Id == photoId && p.GalleryId == g.Id && p.Approved)) return Results.NotFound();
            var vid = ActivityLog.EnsureVisitorId(ctx);
            await Likes.AdoptAsync(db, ctx, vid);
            var existing = await db.Favorites.FirstOrDefaultAsync(f => f.GalleryId == g.Id && f.PhotoId == photoId && f.VisitorId == vid);
            if (input.On && existing == null)
            {
                if (await db.Favorites.CountAsync(f => f.GalleryId == g.Id && f.VisitorId == vid) >= 2000) return Results.StatusCode(429);
                db.Favorites.Add(new Favorite { GalleryId = g.Id, PhotoId = photoId, VisitorId = vid, IdentityKey = Likes.IdentityKey(ctx), Ip = ActivityLog.ClientIp(ctx) });
                activity.Record(ctx, "favorite", g.Id, photoId);
            }
            else if (!input.On && existing != null) db.Favorites.Remove(existing);
            await db.SaveChangesAsync();
            return Results.Ok(new { count = await db.Favorites.CountAsync(f => f.GalleryId == g.Id && f.VisitorId == vid), likes = g.ShowLikeCounts || a.Admin ? await Likes.CountAsync(db, photoId) : 0 });
        }).RequireRateLimiting("viewed");

        app.MapPut("/api/g/{slug}/favorite-name", async (string slug, NameInput input, HttpContext ctx, AppDb db, AccessService access) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest();
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed) return Results.StatusCode(403);
            var vid = ActivityLog.EnsureVisitorId(ctx);
            var name = (input.Name ?? "").Trim();
            if (name.Length > 60) name = name[..60];
            var owner = await db.FavoriteOwners.FirstOrDefaultAsync(o => o.GalleryId == g.Id && o.VisitorId == vid);
            if (owner == null) db.FavoriteOwners.Add(new FavoriteOwner { GalleryId = g.Id, VisitorId = vid, Name = name });
            else { owner.Name = name; owner.UpdatedAt = DateTime.UtcNow; }
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRateLimiting("viewed");

        app.MapPost("/api/g/{slug}/unlock", async (string slug, UnlockRequest req, HttpContext ctx, AppDb db, AccessService access, ActivityLog activity) =>
        {
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null || g.Visibility != Visibility.Protected || string.IsNullOrEmpty(g.PasswordHash))
                return Results.StatusCode(401);
            if (!Passwords.Verify(req.Password ?? "", g.PasswordHash)) { activity.Record(ctx, "unlock_fail", g.Id); return Results.StatusCode(401); }
            activity.Record(ctx, "unlock_ok", g.Id);
            access.Issue(ctx, g, null);
            return Results.NoContent();
        }).RequireRateLimiting("unlock");

        app.MapGet("/s/{token}", async (string token, HttpContext ctx, AppDb db, AccessService access, ActivityLog activity) =>
        {
            var link = await db.ShareLinks.Include(l => l.Gallery).FirstOrDefaultAsync(l => l.Token == token);
            if (link?.Gallery == null || !link.IsValid(DateTime.UtcNow)) { activity.Record(ctx, "share_invalid", link?.GalleryId, null, link?.Label ?? ""); return Results.Redirect("/expired"); }
            activity.Record(ctx, "share_open", link.GalleryId, null, link.Label);
            access.Issue(ctx, link.Gallery, link);
            return Results.Redirect($"/g/{Uri.EscapeDataString(link.Gallery.Slug)}");
        });

        app.MapGet("/img/{id:int}/{kind}", async (int id, string kind, HttpContext ctx, AppDb db, AccessService access, ImageService images) =>
        {
            if (kind is not ("thumb" or "mid" or "view")) return Results.NotFound();
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id);
            if (p?.Gallery == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, p.Gallery);
            if (!a.Allowed || (!p.Approved && !a.Admin)) return Results.NotFound();

            var wm = kind != "thumb" && p.Gallery.WatermarkView && images.WatermarkAvailable;
            var v = await images.GetVariantAsync(p, kind, kind == "thumb" ? 480 : kind == "mid" ? 1280 : 2048, "webp", wm);
            if (v == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = p.Gallery.Visibility == Visibility.Public ? "public, max-age=86400" : "private, max-age=3600";
            return Results.File(v.Value.path, "image/webp", lastModified: File.GetLastWriteTimeUtc(v.Value.path));
        });

        // single-photo download: ?size=original | <px>  &fmt=png|webp|tif
        // YouTube preview images are fetched by the server and cached, so visitors never contact YouTube until they press play.
        // A cached image older than 12 hours is fetched again, so a thumbnail changed on YouTube shows up here too.
        app.MapGet("/vthumb/{vid}", async (string vid, HttpContext ctx, AppDb db, AppConfig cfg) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(vid, "^[A-Za-z0-9_-]{11}$")) return Results.NotFound();
            var file = VideoThumbs.PathFor(cfg, vid);
            if (!File.Exists(file))
            {
                if (!await db.Galleries.AnyAsync(x => x.FootageLinks.Contains(vid))) return Results.NotFound();
                if (!await VideoThumbs.RefreshAsync(cfg, vid)) return Results.NotFound();
            }
            else if (VideoThumbs.NeedsRefresh(file)) await VideoThumbs.RefreshAsync(cfg, vid);
            ctx.Response.Headers.CacheControl = "public, no-cache"; // browsers re-check, and get a 304 while nothing changed
            return Results.File(file, "image/jpeg", lastModified: File.GetLastWriteTimeUtc(file));
        });

        // cached pictures for External links; the name is a hash of the link's address, so only a known link can be asked for
        app.MapGet("/lp/{file}", (string file, HttpContext ctx, AppConfig cfg) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(file, "^[0-9a-f]{24}\\.webp$")) return Results.NotFound();
            var path = ExternalLinkPreviews.ImagePath(cfg, file[..24]);
            if (!File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(path, "image/webp", lastModified: File.GetLastWriteTimeUtc(path));
        });

        app.MapGet("/dl/{id:int}", async (int id, string? size, string? fmt, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg, ImageService images, ActivityLog activity, SettingsService settings) =>
        {
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id);
            if (p?.Gallery == null) return Results.NotFound();
            var g = p.Gallery;
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed || (!p.Approved && !a.Admin)) return Results.NotFound();
            if (!g.AllowDownload && !a.Admin) return Results.StatusCode(403);
            if (!await LicenseGate.AcceptedAsync(ctx, db, settings)) return LicenseGate.Required();

            var baseName = Path.GetFileNameWithoutExtension(p.Name);
            var wm = g.WatermarkDownload && !a.Admin && images.WatermarkAvailable;
            if (string.Equals(size, "original", StringComparison.OrdinalIgnoreCase))
            {
                if (p.RawOnly) return Results.BadRequest("Only a RAW exists for this photo");
                if (g.WatermarkDownload && !a.Admin) return Results.StatusCode(403);
                var path = cfg.ResolvePhoto(p, p.RelPath);
                if (path == null || !File.Exists(path)) return Results.NotFound();
                if (!await access.TryConsumeDownloadAsync(a)) return Results.StatusCode(410);
                activity.Record(ctx, "download", g.Id, p.Id, "original");
                return Results.File(path, ImageService.ContentType(Path.GetExtension(path).TrimStart('.')), p.Name);
            }

            if (!int.TryParse(size, out var px) || !GalleryRules.Sizes(g, cfg).Contains(px)) return Results.BadRequest("Unknown size");
            var f = (fmt ?? "png").ToLowerInvariant();
            if (!GalleryRules.Formats(g).Contains(f)) return Results.BadRequest("Unknown format");
            var v = await images.GetVariantAsync(p, "size", px, f, wm);
            if (v == null) return Results.NotFound();
            if (!await access.TryConsumeDownloadAsync(a)) return Results.StatusCode(410);
            activity.Record(ctx, "download", g.Id, p.Id, $"{px}px {f}");
            return Results.File(v.Value.path, ImageService.ContentType(f), $"{baseName}_{px}.{f}");
        }).RequireRateLimiting("download");

        app.MapGet("/api/g/{slug}/photo/{id:int}/info", async (string slug, int id, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg) =>
        {
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id && x.Gallery!.Slug == slug);
            if (p?.Gallery == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, p.Gallery);
            if (!a.Allowed || (!p.Approved && !a.Admin)) return Results.NotFound();
            var versions = await PhotoInfo.ReadAsync(cfg, p, includeLocation: a.Admin);
            return Results.Ok(new { name = p.Name, width = p.Width, height = p.Height, takenAt = p.TakenAt, versions });
        });

        app.MapGet("/dl/{id:int}/raw", async (int id, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg, ActivityLog activity, SettingsService settings) =>
        {
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id);
            if (p?.Gallery == null || p.RawRelPath == null) return Results.NotFound();
            var g = p.Gallery;
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed || (!p.Approved && !a.Admin)) return Results.NotFound();
            if (!a.Admin && (!g.AllowDownload || !g.AllowRaw || g.WatermarkDownload)) return Results.StatusCode(403);
            if (!await LicenseGate.AcceptedAsync(ctx, db, settings)) return LicenseGate.Required();
            var path = cfg.ResolvePhoto(p, p.RawRelPath);
            if (path == null || !File.Exists(path)) return Results.NotFound();
            if (!await access.TryConsumeDownloadAsync(a)) return Results.StatusCode(410);
            activity.Record(ctx, "download", g.Id, p.Id, "raw");
            return Results.File(path, "application/octet-stream", Path.GetFileName(path));
        }).RequireRateLimiting("download");

        app.MapGet("/api/g/{slug}/zip", async (string slug, string? size, string? fmt, bool? raw, string? ids, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg, ImageService images, ActivityLog activity, SettingsService settings) =>
        {
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed) return Results.StatusCode(403);
            if (!a.Admin && (!g.AllowDownload || !g.AllowZip)) return Results.StatusCode(403);
            if (!await LicenseGate.AcceptedAsync(ctx, db, settings)) return LicenseGate.Required();

            var original = string.Equals(size, "original", StringComparison.OrdinalIgnoreCase);
            int px = 0;
            var f = (fmt ?? "png").ToLowerInvariant();
            if (!original)
            {
                if (!int.TryParse(size, out px) || !GalleryRules.Sizes(g, cfg).Contains(px)) return Results.BadRequest("Unknown size");
                if (!GalleryRules.Formats(g).Contains(f)) return Results.BadRequest("Unknown format");
            }
            var wmLock = g.WatermarkDownload && !a.Admin;
            if (original && wmLock) return Results.StatusCode(403);
            var includeRaw = raw == true && (a.Admin || (g.AllowRaw && !wmLock));

            var photos = await db.Photos.Where(p => p.GalleryId == g.Id && (p.Approved || a.Admin))
                .ToListAsync();
            if (!string.IsNullOrEmpty(ids))
            {
                var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Take(500).ToHashSet();
                photos = photos.Where(p => wanted.Contains(p.Id)).ToList();
            }
            photos = GalleryRules.Sort(photos, g, x => x.TakenAt, x => x.Name, x => x.AddedAt);
            if (photos.Count == 0) return Results.NotFound();
            if (!await access.TryConsumeDownloadAsync(a)) return Results.StatusCode(410);
            activity.Record(ctx, "zip", g.Id, null, (original ? "original" : $"{px}px {f}") + (includeRaw ? " +RAW" : "") + (string.IsNullOrEmpty(ids) ? "" : $" ({photos.Count} selected)"));

            var body = ctx.Features.Get<IHttpBodyControlFeature>();
            if (body != null) body.AllowSynchronousIO = true;
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{g.Slug}{(string.IsNullOrEmpty(ids) ? "" : "-selected")}{(original ? "" : "_" + px)}.zip\"";

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string Unique(string name)
            {
                if (used.Add(name)) return name;
                var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
                for (int i = 2; ; i++) { var n = $"{stem}_{i}{ext}"; if (used.Add(n)) return n; }
            }

            using var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
            async Task AddFile(string path, string name)
            {
                var entry = zip.CreateEntry(Unique(name), CompressionLevel.NoCompression);
                await using var es = entry.Open();
                await using var fs = File.OpenRead(path);
                await fs.CopyToAsync(es, ctx.RequestAborted);
            }

            foreach (var p in photos)
            {
                ctx.RequestAborted.ThrowIfCancellationRequested();
                var baseName = Path.GetFileNameWithoutExtension(p.Name);
                if (original)
                {
                    if (!p.RawOnly)
                    {
                        var path = cfg.ResolvePhoto(p, p.RelPath);
                        if (path != null && File.Exists(path)) await AddFile(path, p.Name);
                    }
                }
                else
                {
                    var wm = wmLock && images.WatermarkAvailable;
                    var v = await images.GetVariantAsync(p, "size", px, f, wm);
                    if (v != null) await AddFile(v.Value.path, $"{baseName}_{px}.{f}");
                }
                if (includeRaw && p.RawRelPath != null)
                {
                    var rp = cfg.ResolvePhoto(p, p.RawRelPath);
                    if (rp != null && File.Exists(rp)) await AddFile(rp, "RAW/" + Path.GetFileName(rp));
                }
            }
            return Results.Empty;
        }).RequireRateLimiting("zip");

        app.MapPost("/api/g/{slug}/upload", async (string slug, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg, ImageService images, ActivityLog activity) =>
        {
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed) return Results.StatusCode(403);
            if (!a.Admin && !g.AllowGuestUpload) return Results.StatusCode(403);
            if (!ctx.Request.HasFormContentType) return Results.BadRequest("multipart form required");
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest("missing header");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            if (form.Files.Count != 1) return Results.BadRequest("one file per request");
            var file = form.Files[0];
            if (file.Length == 0 || file.Length > cfg.MaxUploadMb * 1024L * 1024L) return Results.BadRequest("file too large");

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var isRaw = AppConfig.RawExts.Contains(ext);
            if (!isRaw && !AppConfig.ImageExts.Contains(ext)) return Results.BadRequest("unsupported file type");
            if (isRaw && cfg.ExifTool == null) return Results.BadRequest("RAW uploads are not enabled");

            if (!a.Admin)
            {
                var pending = await db.Photos.CountAsync(p => p.GalleryId == g.Id && !p.Approved);
                if (pending >= cfg.MaxPendingPerGallery) return Results.StatusCode(429);
            }

            var guestDir = Path.Combine(cfg.GuestRoot, g.Id.ToString());
            Directory.CreateDirectory(guestDir);
            var stored = Guid.NewGuid().ToString("N") + ext;
            var full = Path.Combine(guestDir, stored);
            await using (var fs = File.Create(full)) await file.CopyToAsync(fs, ctx.RequestAborted);

            var safeName = new string(Path.GetFileName(file.FileName).Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ').ToArray());
            if (string.IsNullOrWhiteSpace(safeName)) safeName = stored;
            var photo = new Photo
            {
                GalleryId = g.Id, RelPath = $"{g.Id}/{stored}", Name = safeName,
                RawRelPath = isRaw ? $"{g.Id}/{stored}" : null, SizeBytes = file.Length,
                TakenAt = DateTime.UtcNow, IsGuest = true, Approved = a.Admin
            };
            try
            {
                if (!isRaw)
                {
                    var (w, h, taken) = images.ReadMeta(full);
                    photo.Width = w; photo.Height = h; if (taken != null) photo.TakenAt = taken.Value;
                    db.Photos.Add(photo);
                    await db.SaveChangesAsync();
                }
                else
                {
                    db.Photos.Add(photo);
                    await db.SaveChangesAsync();
                    var src = await images.GetSourceAsync(photo);
                    if (src == null) throw new InvalidDataException("no preview in RAW");
                    var (w, h, _) = images.ReadMeta(src);
                    photo.Width = w; photo.Height = h;
                    await db.SaveChangesAsync();
                }
            }
            catch
            {
                if (photo.Id != 0) { db.Photos.Remove(photo); await db.SaveChangesAsync(); }
                File.Delete(full);
                return Results.BadRequest("not a valid image");
            }
            activity.Record(ctx, "upload", g.Id, photo.Id, safeName);
            return Results.Accepted(value: new { pending = !a.Admin });
        }).RequireRateLimiting("upload").DisableAntiforgery();
    }
}
