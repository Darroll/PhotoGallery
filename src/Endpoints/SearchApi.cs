using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Search across public galleries: titles, file names, captions, alt text, tags, locations and dates.
/// Protected, unlisted and expired galleries are never searched. The admin manages the tags.
/// </summary>
public static class SearchApi
{
    record TagsInput(List<string>? Names);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";

    static string Clean(string? s, int max)
    {
        var t = (s ?? "").Replace("%", "").Replace("_", "").Trim();
        return t.Length > max ? t[..max] : t;
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/search", async (string? q, string? where, string? tag, string? from, string? to, string? scope, AppDb db) =>
        {
            var term = Clean(q, 80);
            var place = Clean(where, 80);
            var tagName = Tags.Clean(tag);
            // scope: "galleries" or "photos" searches only that kind; anything else searches both
            var kind = (scope ?? "").Trim().ToLowerInvariant();
            var withGalleries = kind != "photos";
            var withPhotos = kind != "galleries";
            DateTime? start = DateTime.TryParse(from, out var fromDate) ? fromDate.Date : null;
            DateTime? end = DateTime.TryParse(to, out var toDate) ? toDate.Date.AddDays(1) : null;

            var galleries = (await db.Galleries.AsNoTracking().Where(g => g.Visibility == Visibility.Public).ToListAsync())
                .Where(g => !GalleryRules.IsExpired(g)).ToList();
            var byId = galleries.ToDictionary(g => g.Id);
            var ids = byId.Keys.ToList();

            var photos = await db.Photos.AsNoTracking().Where(p => p.Approved && ids.Contains(p.GalleryId))
                .Select(p => new { p.Id, p.GalleryId, p.Name, p.Caption, p.AltText, p.TakenAt }).ToListAsync();
            var tags = await Tags.NamesByPhotoAsync(db, photos.Select(p => p.Id).ToList());
            var galleryTags = await Tags.NamesByGalleryAsync(db, ids);

            bool Hit(string? s) => term.Length == 0 || (s ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);

            var hits = !withPhotos ? new List<object>() : photos.Where(p =>
            {
                var g = byId[p.GalleryId];
                var photoTags = tags.TryGetValue(p.Id, out var list) ? list : new List<string>();
                var inherited = galleryTags.TryGetValue(g.Id, out var gt) ? gt : new List<string>();
                if (place.Length > 0 && !(g.Location.Contains(place, StringComparison.OrdinalIgnoreCase)
                    || (g.EventDate?.ToString("MMMM yyyy").Contains(place, StringComparison.OrdinalIgnoreCase) ?? false))) return false;
                if (tagName.Length > 0 && !photoTags.Concat(inherited).Any(x => x.Equals(tagName, StringComparison.OrdinalIgnoreCase))) return false;
                if (start != null && p.TakenAt < start) return false;
                if (end != null && p.TakenAt >= end) return false;
                if (term.Length == 0) return true;
                return Hit(p.Name) || Hit(p.Caption) || Hit(p.AltText) || Hit(g.Title) || photoTags.Concat(inherited).Any(x => Hit(x));
            })
            .OrderByDescending(p => p.TakenAt).Take(60)
            .Select(p => (object)new
            {
                id = p.Id, slug = byId[p.GalleryId].Slug, gallery = byId[p.GalleryId].Title,
                name = p.Name, caption = p.Caption, altText = p.AltText, takenAt = p.TakenAt,
                tags = tags.TryGetValue(p.Id, out var photoTagNames) ? photoTagNames : new List<string>()
            }).ToList();

            static List<string> TagsOf(Dictionary<int, List<string>> map, int id) => map.TryGetValue(id, out var list) ? list : new List<string>();
            var matchedGalleries = !withGalleries || (term.Length == 0 && place.Length == 0 && tagName.Length == 0) ? new List<object>() : galleries
                .Where(g => (term.Length > 0 && (Hit(g.Title) || Hit(g.Description) || TagsOf(galleryTags, g.Id).Any(x => Hit(x))))
                    || (place.Length > 0 && g.Location.Contains(place, StringComparison.OrdinalIgnoreCase))
                    || (tagName.Length > 0 && TagsOf(galleryTags, g.Id).Any(x => x.Equals(tagName, StringComparison.OrdinalIgnoreCase))))
                .Take(12).Select(g => (object)new { g.Slug, g.Title, location = g.Location, eventDate = g.EventDate, cover = g.CoverPhotoId, tags = TagsOf(galleryTags, g.Id) }).ToList();

            return Results.Ok(new { galleries = matchedGalleries, photos = hits });
        });

        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        secured.MapGet("/tags", async (AppDb db) => Results.Ok(await db.Tags.OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, photos = db.PhotoTags.Count(pt => pt.TagId == x.Id) }).ToListAsync()));

        // replaces the photo's tags with this list; names match without regard to case and are created when new
        secured.MapPut("/photos/{id:int}/tags", async (int id, TagsInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            if (!await db.Photos.AnyAsync(p => p.Id == id)) return Results.NotFound();
            var names = (input.Names ?? new List<string>()).Select(Tags.Clean).Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();

            var wanted = new List<Tag>();
            foreach (var name in names)
            {
                var tag = await db.Tags.FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());
                if (tag == null)
                {
                    tag = new Tag { Name = name };
                    db.Tags.Add(tag);
                }
                wanted.Add(tag);
            }
            await db.SaveChangesAsync();

            var current = await db.PhotoTags.Where(pt => pt.PhotoId == id).ToListAsync();
            db.PhotoTags.RemoveRange(current.Where(pt => !wanted.Any(w => w.Id == pt.TagId)));
            foreach (var w in wanted.Where(w => !current.Any(pt => pt.TagId == w.Id)))
                db.PhotoTags.Add(new PhotoTag { PhotoId = id, TagId = w.Id });
            await db.SaveChangesAsync();
            return Results.Ok(names.OrderBy(n => n).ToList());
        });

        secured.MapGet("/galleries/{id:int}/tags", async (int id, AppDb db) =>
            Results.Ok((await Tags.NamesByGalleryAsync(db, new List<int> { id })).GetValueOrDefault(id) ?? new List<string>()));

        // replaces the gallery's tags with this list; names match without regard to case and are created when new
        secured.MapPut("/galleries/{id:int}/tags", async (int id, TagsInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            if (!await db.Galleries.AnyAsync(g => g.Id == id)) return Results.NotFound();
            var names = (input.Names ?? new List<string>()).Select(Tags.Clean).Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();

            var wanted = new List<Tag>();
            foreach (var name in names)
            {
                var tag = await db.Tags.FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());
                if (tag == null)
                {
                    tag = new Tag { Name = name };
                    db.Tags.Add(tag);
                }
                wanted.Add(tag);
            }
            await db.SaveChangesAsync();

            var current = await db.GalleryTags.Where(gt => gt.GalleryId == id).ToListAsync();
            db.GalleryTags.RemoveRange(current.Where(gt => !wanted.Any(w => w.Id == gt.TagId)));
            foreach (var w in wanted.Where(w => !current.Any(gt => gt.TagId == w.Id)))
                db.GalleryTags.Add(new GalleryTag { GalleryId = id, TagId = w.Id });
            await db.SaveChangesAsync();
            return Results.Ok(names.OrderBy(n => n).ToList());
        });

        // removing a tag takes it off every photo and gallery
        secured.MapDelete("/tags/{id:int}", async (int id, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var tag = await db.Tags.FindAsync(id);
            if (tag == null) return Results.NotFound();
            db.PhotoTags.RemoveRange(db.PhotoTags.Where(pt => pt.TagId == id));
            db.GalleryTags.RemoveRange(db.GalleryTags.Where(gt => gt.TagId == id));
            db.Tags.Remove(tag);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
