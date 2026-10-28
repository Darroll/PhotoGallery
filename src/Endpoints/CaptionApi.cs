using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Captions and alt text. Signed-in visitors suggest changes and the admin reviews them like a merge request:
/// the suggestion remembers the text it was written against, and a suggestion whose text has since changed is a conflict.
/// The admin's own changes apply straight away.
/// </summary>
public static class CaptionApi
{
    record SuggestInput(string? Caption, string? Alt);
    record ApproveInput(bool? Force);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/g/{slug}/photo/{id:int}/suggest", async (string slug, int id, SuggestInput input, HttpContext ctx, AppDb db, AccessService access) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id && x.Gallery!.Slug == slug);
            if (p?.Gallery == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, p.Gallery);
            if (!a.Allowed || !p.Approved) return Results.NotFound();

            if (AccessService.IsAdmin(ctx))
            {
                if (input.Caption != null) Captions.Set(p, "caption", input.Caption, "admin");
                if (input.Alt != null) Captions.Set(p, "alt", input.Alt, "admin");
                await db.SaveChangesAsync();
                return Results.Ok(new { applied = true, caption = p.Caption, alt = p.AltText });
            }

            var sub = AuthApi.Sub(ctx);
            if (sub == null) return Results.Json(new { signInRequired = true }, statusCode: 401);
            var name = (ctx.User.Identity?.Name ?? "").Trim();

            var edits = new List<CaptionEdit>();
            foreach (var (field, value) in new[] { ("caption", input.Caption), ("alt", input.Alt) })
            {
                if (value == null) continue;
                var proposed = Captions.Clean(value, field == "alt" ? Captions.MaxAlt : Captions.MaxCaption);
                var current = Captions.Get(p, field);
                if (proposed == current) continue;
                var already = await db.CaptionEdits.AnyAsync(e => e.PhotoId == p.Id && e.Field == field && e.Status == "pending" && e.Sub == sub && e.Proposed == proposed);
                if (already) continue;
                edits.Add(new CaptionEdit
                {
                    PhotoId = p.Id, Field = field, Base = current, Proposed = proposed,
                    Sub = sub, Name = name.Length > 120 ? name[..120] : name
                });
            }
            db.CaptionEdits.AddRange(edits);
            await db.SaveChangesAsync();
            return Results.Ok(new { applied = false, suggested = edits.Count });
        }).RequireRateLimiting("suggest");

        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        secured.MapGet("/caption-edits", async (AppDb db) =>
        {
            var pending = await db.CaptionEdits.Where(e => e.Status == "pending").OrderBy(e => e.Id).ToListAsync();
            var photos = await db.Photos.AsNoTracking().Where(p => pending.Select(e => e.PhotoId).Contains(p.Id))
                .ToDictionaryAsync(p => p.Id);
            return Results.Ok(pending.Select(e =>
            {
                var current = photos.TryGetValue(e.PhotoId, out var p) ? Captions.Get(p, e.Field) : "";
                return new
                {
                    e.Id, e.PhotoId, photoName = p?.Name ?? "(removed)", e.Field, e.Base, e.Proposed, current,
                    conflict = current != e.Base, e.Name, e.CreatedAt
                };
            }));
        });

        secured.MapPost("/caption-edits/{id:int}/approve", async (int id, ApproveInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var e = await db.CaptionEdits.FindAsync(id);
            if (e == null || e.Status != "pending") return Results.NotFound();
            var p = await db.Photos.FindAsync(e.PhotoId);
            if (p == null) return Results.NotFound();
            var current = Captions.Get(p, e.Field);
            // the merge check: the text moved on since this was suggested, so the admin must look and choose
            if (current != e.Base && input.Force != true)
                return Results.Conflict(new { current, proposed = e.Proposed, message = "This text changed after it was suggested. Compare the two, then approve anyway or reject." });
            Captions.Set(p, e.Field, e.Proposed, "admin");
            e.Status = "approved";
            e.ReviewedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { caption = p.Caption, alt = p.AltText });
        });

        secured.MapPost("/caption-edits/{id:int}/reject", async (int id, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var e = await db.CaptionEdits.FindAsync(id);
            if (e == null || e.Status != "pending") return Results.NotFound();
            e.Status = "rejected";
            e.ReviewedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
