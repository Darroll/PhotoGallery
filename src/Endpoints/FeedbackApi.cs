using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// A short message from a visitor to the photographer ("loved these", "can I get photo 12 in print?"). It needs no account: a name
/// and a way to reply are optional, the text is limited, and one address can only send a few a minute. The admin reads and clears them.
/// </summary>
public static class FeedbackApi
{
    record SendInput(string? Name, string? Contact, string? Text, string? Website);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";

    static string Cut(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/g/{slug}/feedback", async (string slug, SendInput input, HttpContext ctx, AppDb db, AccessService access) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null || !g.AllowFeedback) return Results.NotFound();
            if (!(await access.CheckAsync(ctx, g)).Allowed) return Results.StatusCode(403);
            if (!string.IsNullOrEmpty(input.Website)) return Results.Ok();   // a hidden field only a bot fills in: pretend it worked
            var text = Cut(input.Text, 1500);
            if (text.Length < 2) return Results.BadRequest("Write a few words first.");
            db.FeedbackMessages.Add(new FeedbackMessage { GalleryId = g.Id, Name = Cut(input.Name, 60), Contact = Cut(input.Contact, 120), Text = text });
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireRateLimiting("feedback");

        var secured = app.MapGroup("/api/admin/feedback").RequireAuthorization("Admin");

        secured.MapGet("", async (AppDb db, bool? unread) =>
        {
            var titles = await db.Galleries.AsNoTracking().ToDictionaryAsync(g => g.Id, g => new { g.Title, g.Slug });
            var rows = await db.FeedbackMessages.AsNoTracking().Where(m => unread != true || !m.Read).OrderByDescending(m => m.CreatedAt).Take(500).ToListAsync();
            return Results.Ok(new
            {
                unread = await db.FeedbackMessages.CountAsync(m => !m.Read),
                messages = rows.Select(m => new
                {
                    m.Id, m.GalleryId, gallery = titles.TryGetValue(m.GalleryId, out var t) ? t.Title : "(deleted gallery)",
                    slug = titles.TryGetValue(m.GalleryId, out var t2) ? t2.Slug : "", m.Name, m.Contact, m.Text, at = m.CreatedAt, m.Read
                })
            });
        });

        secured.MapPut("/{id:int}/read", async (int id, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var m = await db.FeedbackMessages.FindAsync(id);
            if (m == null) return Results.NotFound();
            m.Read = ctx.Request.Query["read"] != "false";
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        secured.MapPost("/read-all", async (HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            await db.FeedbackMessages.Where(m => !m.Read).ExecuteUpdateAsync(s => s.SetProperty(m => m.Read, true));
            return Results.NoContent();
        });

        secured.MapDelete("/{id:int}", async (int id, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var m = await db.FeedbackMessages.FindAsync(id);
            if (m == null) return Results.NotFound();
            db.FeedbackMessages.Remove(m);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
