using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Comments on photos. Only people signed in through single sign-on with a verified email may write, under a display name they
/// choose (never their email). The photographer can always reply, marked as such. Comments show at once, are flat, and the author
/// can change or remove one for 15 minutes. Every post, edit and delete is also written to the comment log.
/// </summary>
public static class CommentApi
{
    record PostInput(string? Text, int? ReplyTo);
    record EditInput(string? Text);
    record NameInput(string? Name);

    sealed record Target(Gallery Gallery, Photo Photo, bool Admin);

    /// <summary>The gallery and photo, when this visitor may see them. Otherwise the answer to send back.</summary>
    static async Task<(Target? t, IResult? fail)> Load(string slug, int photoId, HttpContext ctx, AppDb db, AccessService access)
    {
        var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
        if (g == null) return (null, Results.NotFound());
        var a = await access.CheckAsync(ctx, g);
        if (!a.Allowed) return (null, Results.StatusCode(403));
        var p = await db.Photos.FirstOrDefaultAsync(x => x.Id == photoId && x.GalleryId == g.Id && (x.Approved || a.Admin));
        return p == null ? (null, Results.NotFound()) : (new Target(g, p, a.Admin), null);
    }

    static bool Open(Target t) => t.Gallery.AllowComments && !t.Photo.CommentsOff;

    static object Shape(Comment c, Dictionary<int, string> authors, bool mine) => new
    {
        c.Id, author = c.Author, isAdmin = c.IsAdmin, text = c.Text, at = c.CreatedAt, edited = c.EditedAt != null, mine,
        replyTo = c.ReplyToId != null && authors.TryGetValue(c.ReplyToId.Value, out var who) ? new { id = c.ReplyToId.Value, author = who } : null,
        canChangeUntil = mine ? (DateTime?)c.CreatedAt + Comments.EditWindow : null
    };

    static bool IsMine(Comment c, HttpContext ctx, bool admin) =>
        c.IsAdmin ? admin : AuthApi.Sub(ctx) is { Length: > 0 } sub && c.Sub == sub;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/g/{slug}/photo/{photoId:int}/comments", async (string slug, int photoId, HttpContext ctx, AppDb db, AccessService access, SsoConfig sso) =>
        {
            var (t, fail) = await Load(slug, photoId, ctx, db, access);
            if (t == null) return fail!;
            var open = Open(t);
            var sub = AuthApi.Sub(ctx);
            var member = Comments.IsVerifiedMember(ctx);
            var name = member ? await db.Users.AsNoTracking().Where(u => u.Sub == sub).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "" : "";
            var me = new
            {
                admin = t.Admin,
                signedIn = sub != null,
                verified = member,
                canSignIn = sso.Enabled,
                name,
                suggestedName = member && name == "" ? Comments.SuggestName(ctx.User.Identity?.Name) : "",
                canPost = open && (t.Admin || (member && name != ""))
            };
            if (!open) return Results.Ok(new { enabled = false, me, comments = Array.Empty<object>() });

            var rows = await db.Comments.AsNoTracking().Where(c => c.PhotoId == photoId && !c.Deleted && !c.Hidden).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync();
            var authors = rows.ToDictionary(c => c.Id, c => c.Author);
            return Results.Ok(new { enabled = true, me, comments = rows.Select(c => Shape(c, authors, IsMine(c, ctx, t.Admin))) });
        });

        app.MapPost("/api/g/{slug}/photo/{photoId:int}/comments", async (string slug, int photoId, PostInput input, HttpContext ctx, AppDb db, AccessService access) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest();
            var (t, fail) = await Load(slug, photoId, ctx, db, access);
            if (t == null) return fail!;
            if (!Open(t)) return Results.BadRequest("Comments are switched off here.");

            var sub = AuthApi.Sub(ctx) ?? "";
            var email = Comments.Email(ctx);
            string author;
            if (t.Admin) author = Comments.AdminName;
            else
            {
                if (sub == "") return Results.Json(new { error = "Sign in to comment." }, statusCode: 401);
                if (!Comments.IsVerifiedMember(ctx)) return Results.Json(new { error = "Your email address has to be verified by the sign-in provider before you can comment." }, statusCode: 403);
                author = await db.Users.AsNoTracking().Where(u => u.Sub == sub).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "";
                if (author == "") return Results.Json(new { error = "Choose the name to show on your comments first.", needsName = true }, statusCode: 409);

                // a person, not a flood: a few a minute, a few dozen an hour
                var now = DateTime.UtcNow;
                if (await db.Comments.CountAsync(c => c.Sub == sub && c.CreatedAt > now.AddMinutes(-1)) >= 3
                    || await db.Comments.CountAsync(c => c.Sub == sub && c.CreatedAt > now.AddHours(-1)) >= 40)
                    return Results.Json(new { error = "You are posting very quickly. Wait a minute and try again." }, statusCode: 429);
            }

            var text = Comments.CleanText(input.Text);
            if (Comments.Problem(text) is { } problem) return Results.BadRequest(problem);
            var since = DateTime.UtcNow.AddMinutes(-10);
            if (await db.Comments.AnyAsync(c => c.PhotoId == photoId && c.Sub == sub && c.IsAdmin == t.Admin && !c.Deleted && c.Text == text && c.CreatedAt > since))
                return Results.Json(new { error = "You already posted that." }, statusCode: 409);

            int? replyTo = null;
            if (input.ReplyTo is { } rid)
            {
                if (!await db.Comments.AnyAsync(c => c.Id == rid && c.PhotoId == photoId && !c.Deleted && !c.Hidden)) return Results.BadRequest("The comment you are answering is gone.");
                replyTo = rid;
            }

            var comment = new Comment
            {
                GalleryId = t.Gallery.Id, PhotoId = photoId, Sub = sub, Email = t.Admin ? "" : email, Author = author, IsAdmin = t.Admin, ReplyToId = replyTo, Text = text,
                Ip = ActivityLog.ClientIp(ctx), UserAgent = Comments.Agent(ctx)
            };
            db.Comments.Add(comment);
            await db.SaveChangesAsync();
            db.CommentEvents.Add(Comments.Event("post", comment, sub, comment.Email, author, text, ctx));
            await db.SaveChangesAsync();
            var authors = new Dictionary<int, string>();
            if (replyTo != null) authors[replyTo.Value] = await db.Comments.Where(c => c.Id == replyTo).Select(c => c.Author).FirstAsync();
            return Results.Ok(Shape(comment, authors, true));
        }).RequireRateLimiting("comment");

        // change or remove your own comment, for 15 minutes after posting
        async Task<(Comment? c, Target? t, IResult? fail)> Own(string slug, int photoId, int id, HttpContext ctx, AppDb db, AccessService access)
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return (null, null, Results.BadRequest());
            var (t, fail) = await Load(slug, photoId, ctx, db, access);
            if (t == null) return (null, null, fail);
            var c = await db.Comments.FirstOrDefaultAsync(x => x.Id == id && x.PhotoId == photoId && !x.Deleted);
            if (c == null) return (null, null, Results.NotFound());
            if (!IsMine(c, ctx, t.Admin)) return (null, null, Results.StatusCode(403));
            if (DateTime.UtcNow - c.CreatedAt > Comments.EditWindow)
                return (null, null, Results.Json(new { error = "The 15 minutes for changing a comment are over." }, statusCode: 403));
            return (c, t, null);
        }

        app.MapPut("/api/g/{slug}/photo/{photoId:int}/comments/{id:int}", async (string slug, int photoId, int id, EditInput input, HttpContext ctx, AppDb db, AccessService access) =>
        {
            var (c, t, fail) = await Own(slug, photoId, id, ctx, db, access);
            if (c == null) return fail!;
            var text = Comments.CleanText(input.Text);
            if (Comments.Problem(text) is { } problem) return Results.BadRequest(problem);
            if (text != c.Text)
            {
                db.CommentEvents.Add(Comments.Event("edit", c, AuthApi.Sub(ctx) ?? "", c.Email, c.Author, c.Text, ctx));   // the log keeps the earlier text
                c.Text = text;
                c.EditedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            var authors = new Dictionary<int, string>();
            if (c.ReplyToId != null) authors[c.ReplyToId.Value] = await db.Comments.Where(x => x.Id == c.ReplyToId).Select(x => x.Author).FirstOrDefaultAsync() ?? "";
            return Results.Ok(Shape(c, authors, true));
        }).RequireRateLimiting("comment");

        app.MapDelete("/api/g/{slug}/photo/{photoId:int}/comments/{id:int}", async (string slug, int photoId, int id, HttpContext ctx, AppDb db, AccessService access) =>
        {
            var (c, t, fail) = await Own(slug, photoId, id, ctx, db, access);
            if (c == null) return fail!;
            db.CommentEvents.Add(Comments.Event("delete", c, AuthApi.Sub(ctx) ?? "", c.Email, c.Author, c.Text, ctx));
            c.Deleted = true;
            c.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRateLimiting("comment");

        // the name shown on a person's comments, past and future
        app.MapPut("/api/me/display-name", async (NameInput input, HttpContext ctx, AppDb db) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest();
            var sub = AuthApi.Sub(ctx);
            if (sub == null || !Comments.IsVerifiedMember(ctx)) return Results.StatusCode(401);
            var name = Comments.CleanName(input.Name, out var problem);
            if (name == null) return Results.BadRequest(problem);
            var lower = name.ToLower();
            if (await db.Users.AnyAsync(u => u.Sub != sub && u.DisplayName.ToLower() == lower)) return Results.BadRequest("Someone already uses that name. Choose another.");
            var u = await db.Users.FirstOrDefaultAsync(x => x.Sub == sub);
            if (u == null) { u = new AppUser { Sub = sub, Name = ctx.User.Identity?.Name ?? "" }; db.Users.Add(u); }
            u.DisplayName = name;
            await db.Comments.Where(c => c.Sub == sub && !c.IsAdmin).ExecuteUpdateAsync(s => s.SetProperty(c => c.Author, name));
            await db.SaveChangesAsync();
            return Results.Ok(new { name });
        }).RequireRateLimiting("comment");
    }
}
