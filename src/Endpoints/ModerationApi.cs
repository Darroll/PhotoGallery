using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// The photographer's side of comments: look through every comment, hide or delete any of them, stop a person from commenting,
/// and read the comment log. The log keeps the address and browser a comment came from; they are stored for the admin's records
/// and are never sent to any page.
/// </summary>
public static class ModerationApi
{
    record HideInput(bool Hidden);
    record BlockInput(string? Reason, bool HideAll);

    const string AdminActor = "Photographer";
    const int PageSize = 50;

    static string Status(Comment c) => c.Deleted ? "deleted" : c.Hidden ? "hidden" : "visible";

    static string LikePattern(string q) => "%" + q.Trim().Replace("%", "").Replace("_", "") + "%";

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin/comments").RequireAuthorization("Admin");

        // every comment on the site, newest first, with the gallery and photo it belongs to
        g.MapGet("", async (string? q, int? gallery, string? status, string? person, int? skip, AppDb db) =>
        {
            var rows = db.Comments.AsNoTracking().AsQueryable();
            if (gallery is > 0) rows = rows.Where(c => c.GalleryId == gallery);
            if (!string.IsNullOrWhiteSpace(person)) { var s = person.Trim(); rows = rows.Where(c => c.Sub == s); }
            rows = status switch
            {
                "visible" => rows.Where(c => !c.Deleted && !c.Hidden),
                "hidden" => rows.Where(c => !c.Deleted && c.Hidden),
                "deleted" => rows.Where(c => c.Deleted),
                _ => rows
            };
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = LikePattern(q);
                rows = rows.Where(c => EF.Functions.Like(c.Text, like) || EF.Functions.Like(c.Author, like) || EF.Functions.Like(c.Email, like));
            }
            var total = await rows.CountAsync();
            var page = await rows.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Skip(Math.Max(0, skip ?? 0)).Take(PageSize).ToListAsync();
            var galleries = await db.Galleries.AsNoTracking().ToDictionaryAsync(x => x.Id, x => new { x.Title, x.Slug });
            var photoIds = page.Select(c => c.PhotoId).Distinct().ToList();
            var photos = await db.Photos.AsNoTracking().Where(p => photoIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
            var blocks = await db.CommentBlocks.AsNoTracking().ToListAsync();
            return Results.Ok(new
            {
                total,
                items = page.Select(c => new
                {
                    c.Id, c.PhotoId, c.GalleryId, author = c.Author, c.IsAdmin, c.Sub, c.Email, text = c.Text, at = c.CreatedAt, edited = c.EditedAt,
                    status = Status(c),
                    gallery = galleries.TryGetValue(c.GalleryId, out var gal) ? gal.Title : "(gallery removed)",
                    slug = galleries.TryGetValue(c.GalleryId, out var gal2) ? gal2.Slug : "",
                    photo = photos.TryGetValue(c.PhotoId, out var pn) ? pn : "",
                    blocked = !c.IsAdmin && blocks.Any(b => (c.Sub != "" && b.Sub == c.Sub) || (c.Email != "" && b.Email == c.Email))
                })
            });
        });

        // hide a comment from visitors, or show it again; nothing is lost
        g.MapPut("/{id:int}/hidden", async (int id, HideInput input, HttpContext ctx, AppDb db) =>
        {
            var c = await db.Comments.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return Results.NotFound();
            if (c.Deleted) return Results.BadRequest("That comment was deleted.");
            if (c.Hidden != input.Hidden)
            {
                c.Hidden = input.Hidden;
                db.CommentEvents.Add(Comments.Event(input.Hidden ? "hide" : "unhide", c, "", "", AdminActor, c.Text, ctx));
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { status = Status(c) });
        });

        // remove any comment. It disappears from the page and stays in the log.
        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, AppDb db) =>
        {
            var c = await db.Comments.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return Results.NotFound();
            if (!c.Deleted)
            {
                c.Deleted = true;
                c.DeletedAt = DateTime.UtcNow;
                db.CommentEvents.Add(Comments.Event("delete", c, "", "", AdminActor, c.Text, ctx));
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        });

        // stop the person who wrote this comment from commenting again
        g.MapPost("/{id:int}/block", async (int id, BlockInput input, HttpContext ctx, AppDb db) =>
        {
            var c = await db.Comments.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return Results.NotFound();
            if (c.IsAdmin || (c.Sub == "" && c.Email == "")) return Results.BadRequest("That comment cannot be blocked.");
            if (!await Comments.IsBlockedAsync(db, c.Sub, c.Email))
            {
                var reason = (input.Reason ?? "").Trim();
                if (reason.Length > 300) reason = reason[..300];
                db.CommentBlocks.Add(new CommentBlock { Sub = c.Sub, Email = c.Email, Name = c.Author, Reason = reason });
                db.CommentEvents.Add(Comments.Event("block", c, "", c.Email, AdminActor, $"{c.Author} ({c.Email})", ctx));
            }
            var hidden = 0;
            if (input.HideAll)
            {
                var sub = c.Sub; var email = c.Email;
                var theirs = await db.Comments.Where(x => !x.Deleted && !x.Hidden && !x.IsAdmin && ((sub != "" && x.Sub == sub) || (email != "" && x.Email == email))).ToListAsync();
                foreach (var x in theirs)
                {
                    x.Hidden = true;
                    db.CommentEvents.Add(Comments.Event("hide", x, "", "", AdminActor, x.Text, ctx));
                }
                hidden = theirs.Count;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { blocked = true, hidden });
        });

        g.MapGet("/blocks", async (AppDb db) =>
            Results.Ok((await db.CommentBlocks.AsNoTracking().OrderByDescending(b => b.At).ToListAsync())
                .Select(b => new { b.Id, b.Name, b.Email, b.Reason, at = b.At })));

        g.MapDelete("/blocks/{id:int}", async (int id, HttpContext ctx, AppDb db) =>
        {
            var b = await db.CommentBlocks.FirstOrDefaultAsync(x => x.Id == id);
            if (b == null) return Results.NotFound();
            db.CommentBlocks.Remove(b);
            db.CommentEvents.Add(new CommentEvent
            {
                Kind = "unblock", ActorName = AdminActor, ActorEmail = b.Email, Text = $"{b.Name} ({b.Email})",
                Ip = ActivityLog.ClientIp(ctx), UserAgent = Comments.Agent(ctx)
            });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // the log: what happened to which comment, when and by whom. Addresses are not sent.
        g.MapGet("/log", async (string? q, int? gallery, string? kind, int? skip, AppDb db) =>
        {
            var rows = db.CommentEvents.AsNoTracking().AsQueryable();
            if (gallery is > 0) rows = rows.Where(e => e.GalleryId == gallery);
            if (!string.IsNullOrWhiteSpace(kind)) rows = rows.Where(e => e.Kind == kind);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = LikePattern(q);
                rows = rows.Where(e => EF.Functions.Like(e.Text, like) || EF.Functions.Like(e.ActorName, like) || EF.Functions.Like(e.ActorEmail, like));
            }
            var total = await rows.CountAsync();
            var page = await rows.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(Math.Max(0, skip ?? 0)).Take(PageSize).ToListAsync();
            var galleries = await db.Galleries.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Title);
            var photoIds = page.Select(e => e.PhotoId).Distinct().ToList();
            var photos = await db.Photos.AsNoTracking().Where(p => photoIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
            return Results.Ok(new
            {
                total,
                items = page.Select(e => new
                {
                    e.Id, e.CommentId, e.Kind, at = e.At, who = e.ActorName, email = e.ActorEmail, text = e.Text,
                    gallery = galleries.TryGetValue(e.GalleryId, out var t) ? t : e.GalleryId == 0 ? "" : "(gallery removed)",
                    photo = photos.TryGetValue(e.PhotoId, out var pn) ? pn : "",
                    browser = e.UserAgent
                })
            });
        });
    }
}
