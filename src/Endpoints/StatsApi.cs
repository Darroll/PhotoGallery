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
                .Where(x => x.views > 0).OrderByDescending(x => x.views).ThenByDescending(x => x.downloads).Take(12).ToList();
            var dlGroups = rows.Where(r => r.PhotoId != null && r.Kind == "download").GroupBy(r => r.PhotoId!.Value)
                .Select(grp => new { id = grp.Key, downloads = grp.Count(), visitors = grp.Select(r => r.Ip).Distinct().Count() })
                .OrderByDescending(x => x.downloads).Take(12).ToList();
            var photoIds = photoGroups.Select(x => x.id).Concat(dlGroups.Select(x => x.id)).Distinct().ToList();
            var photoInfo = await db.Photos.AsNoTracking().Where(p => photoIds.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.GalleryId }).ToDictionaryAsync(p => p.Id);
            var slugs = await db.Galleries.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Slug);
            var topPhotos = photoGroups.Select(x => new
            {
                x.id, x.views, x.downloads,
                name = photoInfo.TryGetValue(x.id, out var pi) ? pi.Name : "(deleted)",
                gallery = photoInfo.TryGetValue(x.id, out var pi2) && galleries.TryGetValue(pi2.GalleryId, out var gt) ? gt : "",
                slug = photoInfo.TryGetValue(x.id, out var pi3) && slugs.TryGetValue(pi3.GalleryId, out var sg) ? sg : ""
            }).ToList();
            var topDownloads = dlGroups.Select(x => new
            {
                x.id, x.downloads, x.visitors,
                name = photoInfo.TryGetValue(x.id, out var pi) ? pi.Name : "(deleted)",
                gallery = photoInfo.TryGetValue(x.id, out var pi2) && galleries.TryGetValue(pi2.GalleryId, out var gt) ? gt : "",
                slug = photoInfo.TryGetValue(x.id, out var pi3) && slugs.TryGetValue(pi3.GalleryId, out var sg) ? sg : ""
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

            return Results.Ok(new { totals, visitorEvents, botEvents, topGalleries, topPhotos, topDownloads, countries, downloadsBy, byDay });
        });

        // grouping: "ip" = one visitor per IP, "browser" = per IP + browser string, anything else = per cookie id
        // (rows without a cookie fall back to IP + browser)
        static string KeyOf(string group, string ip, string ua, string vid) => group switch
        {
            "ip" => "ip:" + ip,
            "browser" => "br:" + ip + "|" + ua,
            _ => vid != "" ? "c:" + vid : "u:" + ip + "|" + ua
        };

        static IQueryable<Visit> ForKey(IQueryable<Visit> q, string key)
        {
            var colon = key.IndexOf(':');
            var kind = colon < 0 ? "" : key[..colon];
            var rest = colon < 0 ? key : key[(colon + 1)..];
            if (kind == "ip") return q.Where(v => v.Ip == rest);
            if (kind == "c") return q.Where(v => v.VisitorId == rest);
            var bar = rest.IndexOf('|');
            var ip = bar < 0 ? rest : rest[..bar];
            var ua = bar < 0 ? "" : rest[(bar + 1)..];
            return kind == "u" ? q.Where(v => v.VisitorId == "" && v.Ip == ip && v.UserAgent == ua) : q.Where(v => v.Ip == ip && v.UserAgent == ua);
        }

        // one row per visitor: what each visitor did
        g.MapGet("/visitors", async (int? days, int? gallery, string? who, string? group, string? sort, int? skip, int? take, AppDb db) =>
        {
            group = group is "ip" or "browser" ? group : "cookie";
            var rows = await Filter(db, days, gallery, who)
                .Select(v => new { v.Time, v.Kind, v.GalleryId, v.PhotoId, v.Ip, v.VisitorId, v.Country, v.UserAgent, v.IsBot }).ToListAsync();
            var tabBase = Filter(db, days, gallery, "all");
            var visitorEvents = await tabBase.CountAsync(v => !v.IsBot);
            var botEvents = await tabBase.CountAsync(v => v.IsBot);
            var galleries = await db.Galleries.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Title);

            var list = rows.GroupBy(r => KeyOf(group!, r.Ip, r.UserAgent, r.VisitorId)).Select(grp =>
            {
                var items = grp.ToList();
                var latest = items.OrderByDescending(r => r.Time).First();
                return new
                {
                    key = grp.Key,
                    ip = latest.Ip,
                    ips = items.GroupBy(r => r.Ip).OrderByDescending(x => x.Max(r => r.Time)).Select(x => x.Key).Take(5).ToList(),
                    ipCount = items.Select(r => r.Ip).Distinct().Count(),
                    browserCount = items.Select(r => r.UserAgent).Distinct().Count(),
                    cookie = latest.VisitorId != "",
                    country = items.Where(r => r.Country != "").GroupBy(r => r.Country).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "",
                    first = DateTime.SpecifyKind(items.Min(r => r.Time), DateTimeKind.Utc),
                    last = DateTime.SpecifyKind(items.Max(r => r.Time), DateTimeKind.Utc),
                    events = items.Count,
                    galleryViews = items.Count(r => r.Kind == "view_gallery"),
                    photoViews = items.Count(r => r.Kind == "view_photo"),
                    downloads = items.Count(r => r.Kind is "download" or "zip"),
                    unlockFails = items.Count(r => r.Kind == "unlock_fail"),
                    galleries = items.Where(r => r.GalleryId != null).Select(r => r.GalleryId!.Value).Distinct()
                        .Select(id => galleries.TryGetValue(id, out var t) ? t : "(deleted)").OrderBy(t => t).ToList(),
                    isBot = items.Any(r => r.IsBot),
                    userAgent = latest.UserAgent
                };
            });
            list = sort switch
            {
                "events" => list.OrderByDescending(x => x.events),
                "downloads" => list.OrderByDescending(x => x.downloads).ThenByDescending(x => x.last),
                "photos" => list.OrderByDescending(x => x.photoViews).ThenByDescending(x => x.last),
                _ => list.OrderByDescending(x => x.last)
            };
            var all = list.ToList();
            var page = all.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 50, 1, 200)).ToList();
            return Results.Ok(new { total = all.Count, visitorEvents, botEvents, group, items = page });
        });

        // everything one visitor did
        g.MapGet("/visitor", async (string key, string? group, int? days, int? gallery, AppDb db) =>
        {
            group = group is "ip" or "browser" ? group : "cookie";
            var rows = await ForKey(Filter(db, days, gallery, "all"), key).OrderBy(v => v.Time).ToListAsync();
            if (rows.Count == 0) return Results.NotFound();
            var galleries = await db.Galleries.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Title);
            var pids = rows.Where(r => r.PhotoId != null).Select(r => r.PhotoId!.Value).Distinct().ToList();
            var photoInfo = await db.Photos.AsNoTracking().Where(p => pids.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.GalleryId }).ToDictionaryAsync(p => p.Id);
            string GalleryName(int? id) => id != null && galleries.TryGetValue(id.Value, out var t) ? t : "";
            DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

            var summary = new
            {
                key,
                ip = rows.Last().Ip,
                country = rows.Where(r => r.Country != "").GroupBy(r => r.Country).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "",
                first = Utc(rows.First().Time), last = Utc(rows.Last().Time), events = rows.Count,
                hasCookie = rows.Any(r => r.VisitorId != ""),
                isBot = rows.Any(r => r.IsBot),
                galleryViews = rows.Count(r => r.Kind == "view_gallery"), photoViews = rows.Count(r => r.Kind == "view_photo"),
                downloads = rows.Count(r => r.Kind == "download"), zips = rows.Count(r => r.Kind == "zip"),
                unlockFails = rows.Count(r => r.Kind == "unlock_fail"), uploads = rows.Count(r => r.Kind == "upload")
            };

            var browsers = rows.GroupBy(r => r.UserAgent).Select(grp => new { userAgent = grp.Key, events = grp.Count(), last = Utc(grp.Max(r => r.Time)) })
                .OrderByDescending(x => x.events).Take(8).ToList();
            var ips = rows.GroupBy(r => r.Ip).Select(grp => new { ip = grp.Key, country = grp.Select(r => r.Country).FirstOrDefault(c => c != "") ?? "", events = grp.Count(), last = Utc(grp.Max(r => r.Time)) })
                .OrderByDescending(x => x.last).Take(10).ToList();

            // other visitors that used any of the same IP addresses (e.g. the rest of the household)
            var ipList = rows.Select(r => r.Ip).Distinct().ToList();
            var neighbours = (await Filter(db, days, gallery, "all").Where(v => ipList.Contains(v.Ip)).ToListAsync())
                .GroupBy(r => KeyOf(group!, r.Ip, r.UserAgent, r.VisitorId)).Where(grp => grp.Key != key)
                .Select(grp => new
                {
                    key = grp.Key, ip = grp.OrderByDescending(r => r.Time).First().Ip,
                    userAgent = grp.OrderByDescending(r => r.Time).First().UserAgent, cookie = grp.Any(r => r.VisitorId != ""),
                    events = grp.Count(), last = Utc(grp.Max(r => r.Time))
                }).OrderByDescending(x => x.last).Take(10).ToList();

            var perGallery = rows.Where(r => r.GalleryId != null).GroupBy(r => r.GalleryId!.Value).Select(grp => new
            {
                id = grp.Key, title = GalleryName(grp.Key),
                views = grp.Count(r => r.Kind == "view_gallery"), photoViews = grp.Count(r => r.Kind == "view_photo"),
                downloads = grp.Count(r => r.Kind is "download" or "zip"),
                first = Utc(grp.Min(r => r.Time)), last = Utc(grp.Max(r => r.Time))
            }).OrderByDescending(x => x.last).ToList();

            var photos = rows.Where(r => r.PhotoId != null && r.Kind is "view_photo" or "download").GroupBy(r => r.PhotoId!.Value).Select(grp =>
            {
                photoInfo.TryGetValue(grp.Key, out var pi);
                return new
                {
                    id = grp.Key, name = pi?.Name ?? "(deleted)", gallery = GalleryName(pi?.GalleryId),
                    views = grp.Count(r => r.Kind == "view_photo"), downloads = grp.Count(r => r.Kind == "download"),
                    downloaded = grp.Where(r => r.Kind == "download").Select(r => r.Detail).Distinct().ToList(),
                    last = Utc(grp.Max(r => r.Time))
                };
            }).OrderByDescending(x => x.last).Take(120).ToList();

            // visits: a new one starts after 30 minutes of silence
            var sessions = new List<List<Visit>>();
            foreach (var r in rows)
            {
                if (sessions.Count == 0 || r.Time - sessions[^1][^1].Time > TimeSpan.FromMinutes(30)) sessions.Add(new List<Visit>());
                sessions[^1].Add(r);
            }
            var sessionDtos = sessions.AsEnumerable().Reverse().Take(30).Select(s => new
            {
                start = Utc(s.First().Time), end = Utc(s.Last().Time), events = s.Count,
                galleries = s.Where(r => r.GalleryId != null).Select(r => GalleryName(r.GalleryId)).Distinct().ToList(),
                photoViews = s.Count(r => r.Kind == "view_photo"), downloads = s.Count(r => r.Kind is "download" or "zip"),
                photoIds = s.Where(r => r.Kind == "view_photo" && r.PhotoId != null).Select(r => r.PhotoId!.Value).Distinct().Take(12).ToList()
            }).ToList();

            var timeline = rows.AsEnumerable().Reverse().Take(300).Select(r => new
            {
                time = Utc(r.Time), r.Kind, r.Detail, r.Ip,
                gallery = GalleryName(r.GalleryId),
                photoId = r.PhotoId,
                photo = r.PhotoId != null && photoInfo.TryGetValue(r.PhotoId.Value, out var pi) ? pi.Name : ""
            }).ToList();

            return Results.Ok(new { summary, browsers, ips, neighbours, galleries = perGallery, photos, sessions = sessionDtos, timeline });
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
                    r.Id, time = DateTime.SpecifyKind(r.Time, DateTimeKind.Utc), r.Kind, r.Ip, r.VisitorId, r.Country, r.UserAgent, r.IsBot, r.Detail,
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
