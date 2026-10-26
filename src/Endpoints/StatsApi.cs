using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public static class StatsApi
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin/stats").RequireAuthorization();
        g.AddEndpointFilter(async (ctx, next) =>
        {
            var m = ctx.HttpContext.Request.Method;
            if (m is "POST" or "PUT" or "DELETE" && ctx.HttpContext.Request.Headers["X-Requested-With"] != "pg")
                return Results.BadRequest("missing header");
            return await next(ctx);
        });

        static IQueryable<Visit> Filter(AppDb db, int? days, int? gallery, string? who, string? kind = null)
        {
            var q = db.Visits.AsNoTracking().AsQueryable();
            if (days is > 0) { var since = DateTime.UtcNow.AddDays(-days.Value); q = q.Where(v => v.Time >= since); }
            if (gallery != null) q = q.Where(v => v.GalleryId == gallery);
            if (who == "bots") q = q.Where(v => v.IsBot);
            else if (who != "all") q = q.Where(v => !v.IsBot);
            if (!string.IsNullOrEmpty(kind)) q = q.Where(v => v.Kind == kind);
            return q;
        }

        g.MapGet("", async (int? days, int? gallery, string? who, AppDb db) =>
        {
            var rows = await Filter(db, days, gallery, who)
                .Select(v => new { v.Time, v.Kind, v.GalleryId, v.PhotoId, v.Ip, v.Country, v.Detail }).ToListAsync();

            // event counts for the Visitors / Bots tabs, ignoring the tab itself
            var tabBase = Filter(db, days, gallery, "all");
            var visitorEvents = await tabBase.CountAsync(v => !v.IsBot);
            var botEvents = await tabBase.CountAsync(v => v.IsBot);

            int Count(string kind) => rows.Count(r => r.Kind == kind);
            var totals = new
            {
                galleryViews = Count("view_gallery"),
                photoViews = Count("view_photo"),
                downloads = Count("download"),
                zips = Count("zip"),
                uploads = Count("upload"),
                unlockFails = Count("unlock_fail"),
                shareOpens = Count("share_open"),
                uniqueVisitors = rows.Where(r => r.Ip != "").Select(r => r.Ip).Distinct().Count()
            };

            var galleries = await db.Galleries.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Title);
            var topGalleries = rows.Where(r => r.GalleryId != null).GroupBy(r => r.GalleryId!.Value)
                .Select(grp => new
                {
                    id = grp.Key, title = galleries.TryGetValue(grp.Key, out var t) ? t : "(deleted)",
                    views = grp.Count(r => r.Kind == "view_gallery"), photoViews = grp.Count(r => r.Kind == "view_photo"),
                    downloads = grp.Count(r => r.Kind is "download" or "zip"),
                    visitors = grp.Select(r => r.Ip).Distinct().Count()
                }).OrderByDescending(x => x.views + x.photoViews + x.downloads).Take(15).ToList();

            var photoGroups = rows.Where(r => r.PhotoId != null && r.Kind is "view_photo" or "download").GroupBy(r => r.PhotoId!.Value)
                .Select(grp => new { id = grp.Key, views = grp.Count(r => r.Kind == "view_photo"), downloads = grp.Count(r => r.Kind == "download") })
                .OrderByDescending(x => x.views + x.downloads * 2).Take(12).ToList();
            var photoIds = photoGroups.Select(x => x.id).ToList();
            var photoInfo = await db.Photos.AsNoTracking().Where(p => photoIds.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.GalleryId }).ToDictionaryAsync(p => p.Id);
            var topPhotos = photoGroups.Select(x => new
            {
                x.id, x.views, x.downloads,
                name = photoInfo.TryGetValue(x.id, out var pi) ? pi.Name : "(deleted)",
                gallery = photoInfo.TryGetValue(x.id, out var pi2) && galleries.TryGetValue(pi2.GalleryId, out var gt) ? gt : ""
            }).ToList();

            var countries = rows.Where(r => r.Country != "").GroupBy(r => r.Country)
                .Select(grp => new { country = grp.Key, events = grp.Count(), visitors = grp.Select(r => r.Ip).Distinct().Count() })
                .OrderByDescending(x => x.visitors).Take(15).ToList();

            var downloadsBy = rows.Where(r => r.Kind is "download" or "zip").GroupBy(r => r.Detail)
                .Select(grp => new { detail = grp.Key, count = grp.Count() }).OrderByDescending(x => x.count).Take(10).ToList();

            var byDay = rows.GroupBy(r => r.Time.Date).OrderBy(x => x.Key)
                .Select(grp => new
                {
                    date = grp.Key.ToString("yyyy-MM-dd"),
                    views = grp.Count(r => r.Kind is "view_gallery" or "view_photo"),
                    downloads = grp.Count(r => r.Kind is "download" or "zip")
                }).ToList();

            return Results.Ok(new { totals, visitorEvents, botEvents, topGalleries, topPhotos, countries, downloadsBy, byDay });
        });

        g.MapGet("/activity", async (int? days, int? gallery, string? who, string? kind, int? skip, int? take, AppDb db) =>
        {
            var q = Filter(db, days, gallery, who, kind);
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(v => v.Time).Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 50, 1, 200)).ToListAsync();
            var galleries = await db.Galleries.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Title);
            var pids = rows.Where(r => r.PhotoId != null).Select(r => r.PhotoId!.Value).Distinct().ToList();
            var names = await db.Photos.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
            return Results.Ok(new
            {
                total,
                items = rows.Select(r => new
                {
                    r.Id, time = DateTime.SpecifyKind(r.Time, DateTimeKind.Utc), r.Kind, r.Ip, r.Country, r.UserAgent, r.IsBot, r.Detail,
                    gallery = r.GalleryId != null && galleries.TryGetValue(r.GalleryId.Value, out var t) ? t : "",
                    photo = r.PhotoId != null && names.TryGetValue(r.PhotoId.Value, out var n) ? n : ""
                })
            });
        });

        g.MapGet("/settings", (SettingsService settings) => Results.Ok(settings.Stats));
        g.MapPut("/settings", (StatsSettings input, SettingsService settings) => Results.Ok(settings.SetStats(input)));

        g.MapDelete("", async (AppDb db) =>
        {
            var n = await db.Visits.ExecuteDeleteAsync();
            return Results.Ok(new { removed = n });
        });
    }
}
