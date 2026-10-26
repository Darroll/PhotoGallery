using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public static class PublicApi
{
    record UnlockRequest(string? Password);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/site", (AppConfig cfg, ImageService img) => new
        {
            title = cfg.SiteTitle,
            sizes = cfg.Sizes,
            formats = ImageService.LosslessFormats,
            watermark = img.WatermarkAvailable
        });

        app.MapGet("/api/galleries", async (AppDb db) =>
        {
            var list = await db.Galleries.Where(g => g.Visibility == Visibility.Public)
                .OrderByDescending(g => g.CreatedAt)
                .Select(g => new
                {
                    g.Slug, g.Title, g.Description, cover = g.CoverPhotoId,
                    count = g.Photos.Count(p => p.Approved)
                }).ToListAsync();
            return Results.Ok(list);
        });

        app.MapGet("/api/g/{slug}", async (string slug, HttpContext ctx, AppDb db, AccessService access) =>
        {
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed)
                return a.NeedsPassword
                    ? Results.Json(new { needsPassword = true, title = g.Title }, statusCode: 401)
                    : Results.StatusCode(403);

            var admin = a.Admin;
            var photos = await db.Photos.Where(p => p.GalleryId == g.Id && (p.Approved || admin))
                .OrderBy(p => p.TakenAt).ThenBy(p => p.Name)
                .Select(p => new { p.Id, w = p.Width, h = p.Height, p.Name, raw = p.RawRelPath != null, rawOnly = p.RawRelPath != null && p.RawRelPath == p.RelPath, taken = p.TakenAt, p.Approved })
                .ToListAsync();

            var locked = g.WatermarkDownload && !admin;
            return Results.Ok(new
            {
                g.Id, g.Slug, g.Title, g.Description, g.Visibility, cover = g.CoverPhotoId,
                allowDownload = g.AllowDownload,
                allowOriginal = g.AllowDownload && !locked,
                allowRaw = g.AllowDownload && g.AllowRaw && !locked,
                allowZip = g.AllowDownload && g.AllowZip,
                allowUpload = g.AllowGuestUpload || admin,
                uploadsModerated = !admin,
                watermarkView = g.WatermarkView,
                watermarkDownload = g.WatermarkDownload,
                admin,
                link = a.Link == null ? null : new
                {
                    expires = a.Link.ExpiresAt,
                    remaining = a.Link.MaxDownloads == null ? (int?)null : Math.Max(0, a.Link.MaxDownloads.Value - a.Link.DownloadCount)
                },
                photos
            });
        });

        app.MapPost("/api/g/{slug}/unlock", async (string slug, UnlockRequest req, HttpContext ctx, AppDb db, AccessService access) =>
        {
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null || g.Visibility != Visibility.Protected || string.IsNullOrEmpty(g.PasswordHash))
                return Results.StatusCode(401);
            if (!Passwords.Verify(req.Password ?? "", g.PasswordHash)) return Results.StatusCode(401);
            access.Issue(ctx, g, null);
            return Results.NoContent();
        }).RequireRateLimiting("unlock");

        app.MapGet("/s/{token}", async (string token, HttpContext ctx, AppDb db, AccessService access) =>
        {
            var link = await db.ShareLinks.Include(l => l.Gallery).FirstOrDefaultAsync(l => l.Token == token);
            if (link?.Gallery == null || !link.IsValid(DateTime.UtcNow)) return Results.Redirect("/#/expired");
            access.Issue(ctx, link.Gallery, link);
            return Results.Redirect($"/#/g/{link.Gallery.Slug}");
        });

        app.MapGet("/img/{id:int}/{kind}", async (int id, string kind, HttpContext ctx, AppDb db, AccessService access, ImageService images) =>
        {
            if (kind is not ("thumb" or "view")) return Results.NotFound();
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id);
            if (p?.Gallery == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, p.Gallery);
            if (!a.Allowed || (!p.Approved && !a.Admin)) return Results.NotFound();

            var wm = kind == "view" && p.Gallery.WatermarkView && images.WatermarkAvailable;
            var v = await images.GetVariantAsync(p, kind, kind == "thumb" ? 480 : 2048, "webp", wm);
            if (v == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = p.Gallery.Visibility == Visibility.Public ? "public, max-age=86400" : "private, max-age=3600";
            return Results.File(v.Value.path, "image/webp", lastModified: File.GetLastWriteTimeUtc(v.Value.path));
        });

        // single-photo download: ?size=original | <px>  &fmt=png|webp|tif
        app.MapGet("/dl/{id:int}", async (int id, string? size, string? fmt, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg, ImageService images) =>
        {
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id);
            if (p?.Gallery == null) return Results.NotFound();
            var g = p.Gallery;
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed || (!p.Approved && !a.Admin)) return Results.NotFound();
            if (!g.AllowDownload && !a.Admin) return Results.StatusCode(403);

            var baseName = Path.GetFileNameWithoutExtension(p.Name);
            var wm = g.WatermarkDownload && !a.Admin && images.WatermarkAvailable;
            if (string.Equals(size, "original", StringComparison.OrdinalIgnoreCase))
            {
                if (p.RawOnly) return Results.BadRequest("Only a RAW exists for this photo");
                if (g.WatermarkDownload && !a.Admin) return Results.StatusCode(403);
                var path = cfg.ResolvePhoto(p, p.RelPath);
                if (path == null || !File.Exists(path)) return Results.NotFound();
                if (!await access.TryConsumeDownloadAsync(a)) return Results.StatusCode(410);
                return Results.File(path, ImageService.ContentType(Path.GetExtension(path).TrimStart('.')), p.Name);
            }

            if (!int.TryParse(size, out var px) || !cfg.Sizes.Contains(px)) return Results.BadRequest("Unknown size");
            var f = (fmt ?? "png").ToLowerInvariant();
            if (!ImageService.LosslessFormats.Contains(f)) return Results.BadRequest("Unknown format");
            var v = await images.GetVariantAsync(p, "size", px, f, wm);
            if (v == null) return Results.NotFound();
            if (!await access.TryConsumeDownloadAsync(a)) return Results.StatusCode(410);
            return Results.File(v.Value.path, ImageService.ContentType(f), $"{baseName}_{px}.{f}");
        });

        app.MapGet("/dl/{id:int}/raw", async (int id, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg) =>
        {
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id);
            if (p?.Gallery == null || p.RawRelPath == null) return Results.NotFound();
            var g = p.Gallery;
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed || (!p.Approved && !a.Admin)) return Results.NotFound();
            if (!a.Admin && (!g.AllowDownload || !g.AllowRaw || g.WatermarkDownload)) return Results.StatusCode(403);
            var path = cfg.ResolvePhoto(p, p.RawRelPath);
            if (path == null || !File.Exists(path)) return Results.NotFound();
            if (!await access.TryConsumeDownloadAsync(a)) return Results.StatusCode(410);
            return Results.File(path, "application/octet-stream", Path.GetFileName(path));
        });

        app.MapGet("/api/g/{slug}/zip", async (string slug, string? size, string? fmt, bool? raw, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg, ImageService images) =>
        {
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed) return Results.StatusCode(403);
            if (!a.Admin && (!g.AllowDownload || !g.AllowZip)) return Results.StatusCode(403);

            var original = string.Equals(size, "original", StringComparison.OrdinalIgnoreCase);
            int px = 0;
            var f = (fmt ?? "png").ToLowerInvariant();
            if (!original)
            {
                if (!int.TryParse(size, out px) || !cfg.Sizes.Contains(px)) return Results.BadRequest("Unknown size");
                if (!ImageService.LosslessFormats.Contains(f)) return Results.BadRequest("Unknown format");
            }
            var wmLock = g.WatermarkDownload && !a.Admin;
            if (original && wmLock) return Results.StatusCode(403);
            var includeRaw = raw == true && (a.Admin || (g.AllowRaw && !wmLock));

            var photos = await db.Photos.Where(p => p.GalleryId == g.Id && (p.Approved || a.Admin))
                .OrderBy(p => p.TakenAt).ThenBy(p => p.Name).ToListAsync();
            if (photos.Count == 0) return Results.NotFound();
            if (!await access.TryConsumeDownloadAsync(a)) return Results.StatusCode(410);

            var body = ctx.Features.Get<IHttpBodyControlFeature>();
            if (body != null) body.AllowSynchronousIO = true;
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{g.Slug}{(original ? "" : "_" + px)}.zip\"";

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
        });

        app.MapPost("/api/g/{slug}/upload", async (string slug, HttpContext ctx, AppDb db, AccessService access, AppConfig cfg, ImageService images) =>
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
            return Results.Accepted(value: new { pending = !a.Admin });
        }).RequireRateLimiting("upload").DisableAntiforgery();
    }
}
