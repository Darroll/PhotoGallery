using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public static class AdminApi
{
    record LoginRequest(string? Password);
    record GalleryInput(string? Title, string? Description, string? Folder, string? Slug, Visibility? Visibility,
        string? Password, bool? AllowDownload, bool? AllowRaw, bool? AllowZip, bool? AllowGuestUpload,
        bool? WatermarkView, bool? WatermarkDownload, RawMatch? RawMatchMode,
        bool? HideRawOnly, GallerySort? SortMode, bool? SortDesc, string? EventDate, string? Location,
        string? AllowedSizes, string? AllowedFormats, string? ExpiresAt);
    record LinkRawInput(int RawPhotoId);
    record LinkInput(string? Label, int? ExpiresInDays, int? MaxDownloads);
    record CoverInput(int PhotoId);

    static string Slugify(string s)
    {
        var slug = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "gallery" : slug;
    }

    static object Dto(Gallery g, int count, int pending) => new
    {
        g.Id, g.Slug, g.Title, g.Description, g.Folder, g.Visibility,
        hasPassword = !string.IsNullOrEmpty(g.PasswordHash),
        g.AllowDownload, g.AllowRaw, g.AllowZip, g.AllowGuestUpload, g.WatermarkView, g.WatermarkDownload,
        rawMatchMode = g.RawMatchMode, hideRawOnly = g.HideRawOnly, sortMode = g.SortMode, sortDesc = g.SortDesc,
        eventDate = g.EventDate, g.Location, g.AllowedSizes, g.AllowedFormats, expiresAt = g.ExpiresAt,
        cover = g.CoverPhotoId, count, pending
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

        admin.MapPost("/login", async (LoginRequest req, HttpContext ctx, AppConfig cfg) =>
        {
            if (!Passwords.FixedEquals(req.Password ?? "", cfg.AdminPassword)) return Results.StatusCode(401);
            var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));
            return Results.NoContent();
        }).RequireRateLimiting("login");

        admin.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        admin.MapGet("/me", (HttpContext ctx) => new { admin = AccessService.IsAdmin(ctx) });

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

        secured.MapGet("/galleries", async (AppDb db) =>
        {
            var list = await db.Galleries.OrderBy(g => g.Title).Select(g => new
            {
                g,
                count = g.Photos.Count(p => p.Approved),
                pending = g.Photos.Count(p => !p.Approved)
            }).ToListAsync();
            return Results.Ok(list.Select(x => Dto(x.g, x.count, x.pending)));
        });

        secured.MapPost("/galleries", async (GalleryInput input, AppDb db, AppConfig cfg, Scanner scanner) =>
        {
            if (string.IsNullOrWhiteSpace(input.Title)) return Results.BadRequest("Title required");
            var dir = cfg.Resolve(input.Folder ?? "");
            if (dir == null || !Directory.Exists(dir)) return Results.BadRequest("Folder not found in library");
            var newFolder = cfg.Rel(dir);
            var owner = await db.Galleries.Where(x => x.Folder.ToLower() == newFolder.ToLower()).Select(x => x.Title).FirstOrDefaultAsync();
            if (owner != null) return Results.BadRequest($"That folder already belongs to the gallery \"{owner}\"");

            var baseSlug = Slugify(string.IsNullOrWhiteSpace(input.Slug) ? input.Title : input.Slug);
            var slug = baseSlug;
            for (int i = 2; await db.Galleries.AnyAsync(g => g.Slug == slug); i++) slug = $"{baseSlug}-{i}";

            var g = new Gallery
            {
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
            return Results.Ok(new { gallery = Dto(g, scan.Total, 0), scan });
        });

        secured.MapPut("/galleries/{id:int}", async (int id, GalleryInput input, AppDb db, AppConfig cfg, Scanner scanner) =>
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
            if (input.Title != null) g.Title = input.Title.Trim();
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
            if (input.EventDate != null) g.EventDate = DateTime.TryParse(input.EventDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ed) ? ed : null;
            if (input.ExpiresAt != null) g.ExpiresAt = DateTime.TryParse(input.ExpiresAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ex) ? ex : null;
            if (input.AllowedSizes != null) g.AllowedSizes = string.Join(',', input.AllowedSizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => int.TryParse(s, out _)));
            if (input.AllowedFormats != null) g.AllowedFormats = string.Join(',', input.AllowedFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).Where(s => ImageService.LosslessFormats.Contains(s)));
            await db.SaveChangesAsync();
            if (folderChanged)
            {
                g.CoverPhotoId = null; // old cover belonged to the previous folder
                await db.SaveChangesAsync();
                await scanner.ScanAsync(db, g);
            }
            return Results.Ok(Dto(g, await db.Photos.CountAsync(p => p.GalleryId == id && p.Approved), await db.Photos.CountAsync(p => p.GalleryId == id && !p.Approved)));
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

        secured.MapPut("/settings/watermark", (WatermarkSettings input, SettingsService settings) =>
            Results.Ok(settings.SetWatermark(input)));

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
