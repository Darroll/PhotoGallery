using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Search across public galleries: titles, file names, captions, alt text, locations, dates and the names of people tagged as public.
/// Protected, unlisted and expired galleries are never searched. The admin tags people; names of non-public people are never returned.
/// </summary>
public static class SearchApi
{
    record PeopleInput(List<string>? Names);
    record PersonInput(bool? Public);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";

    static string Clean(string? s, int max)
    {
        var t = (s ?? "").Replace("%", "").Replace("_", "").Trim();
        return t.Length > max ? t[..max] : t;
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/search", async (string? q, string? where, string? from, string? to, AppDb db) =>
        {
            var term = Clean(q, 80);
            var place = Clean(where, 80);
            DateTime? start = DateTime.TryParse(from, out var f) ? f.Date : null;
            DateTime? end = DateTime.TryParse(to, out var t) ? t.Date.AddDays(1) : null;

            var galleries = (await db.Galleries.AsNoTracking().Where(g => g.Visibility == Visibility.Public).ToListAsync())
                .Where(g => !GalleryRules.IsExpired(g)).ToList();
            var byId = galleries.ToDictionary(g => g.Id);
            var ids = byId.Keys.ToList();

            var photos = await db.Photos.AsNoTracking().Where(p => p.Approved && ids.Contains(p.GalleryId))
                .Select(p => new { p.Id, p.GalleryId, p.Name, p.Caption, p.AltText, p.TakenAt }).ToListAsync();
            var publicNames = (await db.PhotoPeople.AsNoTracking()
                .Join(db.People.Where(x => x.Public), pp => pp.PersonId, x => x.Id, (pp, x) => new { pp.PhotoId, x.Name })
                .ToListAsync())
                .GroupBy(x => x.PhotoId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToList());

            bool Hit(string? s) => term.Length == 0 || (s ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);

            var hits = photos.Where(p =>
            {
                var g = byId[p.GalleryId];
                if (place.Length > 0 && !(g.Location.Contains(place, StringComparison.OrdinalIgnoreCase)
                    || (g.EventDate?.ToString("MMMM yyyy").Contains(place, StringComparison.OrdinalIgnoreCase) ?? false))) return false;
                if (start != null && p.TakenAt < start) return false;
                if (end != null && p.TakenAt >= end) return false;
                if (term.Length == 0) return true;
                return Hit(p.Name) || Hit(p.Caption) || Hit(p.AltText) || Hit(g.Title)
                    || (publicNames.TryGetValue(p.Id, out var names) && names.Any(n => Hit(n)));
            })
            .OrderByDescending(p => p.TakenAt).Take(60)
            .Select(p => new
            {
                id = p.Id, slug = byId[p.GalleryId].Slug, gallery = byId[p.GalleryId].Title,
                name = p.Name, caption = p.Caption, altText = p.AltText, takenAt = p.TakenAt,
                people = publicNames.TryGetValue(p.Id, out var pn) ? pn : new List<string>()
            }).ToList();

            var matchedGalleries = term.Length == 0 && place.Length == 0 ? new List<object>() : galleries
                .Where(g => (term.Length > 0 && (Hit(g.Title) || Hit(g.Description))) || (place.Length > 0 && g.Location.Contains(place, StringComparison.OrdinalIgnoreCase)))
                .Take(12).Select(g => (object)new { g.Slug, g.Title, location = g.Location, eventDate = g.EventDate, cover = g.CoverPhotoId }).ToList();

            return Results.Ok(new { galleries = matchedGalleries, photos = hits });
        });

        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        secured.MapGet("/people", async (AppDb db) => Results.Ok(await db.People.OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.Public, photos = db.PhotoPeople.Count(pp => pp.PersonId == x.Id) }).ToListAsync()));

        secured.MapPut("/people/{id:int}", async (int id, PersonInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var person = await db.People.FindAsync(id);
            if (person == null) return Results.NotFound();
            person.Public = input.Public ?? person.Public;
            await db.SaveChangesAsync();
            return Results.Ok(new { person.Id, person.Name, person.Public });
        });

        secured.MapGet("/photos/{id:int}/people", async (int id, AppDb db) =>
            Results.Ok(await db.PhotoPeople.Where(pp => pp.PhotoId == id).Join(db.People, pp => pp.PersonId, x => x.Id, (pp, x) => x.Name).OrderBy(n => n).ToListAsync()));

        // replaces the photo's people with this list; names are matched without regard to case and created when new
        secured.MapPut("/photos/{id:int}/people", async (int id, PeopleInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            if (!await db.Photos.AnyAsync(p => p.Id == id)) return Results.NotFound();
            var names = (input.Names ?? new List<string>()).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0)
                .Select(n => n.Length > 80 ? n[..80] : n).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();

            var wanted = new List<Person>();
            foreach (var name in names)
            {
                var person = await db.People.FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());
                if (person == null)
                {
                    person = new Person { Name = name };
                    db.People.Add(person);
                }
                wanted.Add(person);
            }
            await db.SaveChangesAsync();

            var current = await db.PhotoPeople.Where(pp => pp.PhotoId == id).ToListAsync();
            db.PhotoPeople.RemoveRange(current.Where(pp => !wanted.Any(w => w.Id == pp.PersonId)));
            foreach (var w in wanted.Where(w => !current.Any(pp => pp.PersonId == w.Id)))
                db.PhotoPeople.Add(new PhotoPerson { PhotoId = id, PersonId = w.Id });
            await db.SaveChangesAsync();
            return Results.Ok(names);
        });
    }
}
