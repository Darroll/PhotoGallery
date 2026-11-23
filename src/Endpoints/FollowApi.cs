using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Follow a player: signed-in visitors with a verified email follow or stop following from a gallery's chips, from the photo
/// viewer, or from the "My players" page, and stop from the link in every email.
/// </summary>
public static class FollowApi
{
    record FollowInput(string? Tag, bool Follow);
    record PlayerFollowInput(int PlayerId, bool Follow);
    record AllowInput(bool? Allow);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";

    /// <summary>Who may follow: a signed-in account with a verified email. Returns the problem as a result, or null.</summary>
    static IResult? Who(HttpContext ctx, out string sub, out string email)
    {
        sub = AuthApi.Sub(ctx) ?? ""; email = "";
        if (sub.Length == 0) return Results.Json(new { signInRequired = true }, statusCode: 401);
        var e = GalleryPeople.VerifiedEmail(ctx.User);
        if (e == null) return Results.BadRequest("Your sign-in has no verified email address, so emails cannot be sent to you.");
        email = e;
        return null;
    }

    public static void Map(WebApplication app)
    {
        // from a gallery or the photo viewer: the player is named by the tag on the photo
        app.MapPost("/api/g/{slug}/follow", async (string slug, FollowInput input, HttpContext ctx, AppDb db, AccessService access, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed) return Results.NotFound();
            var team = g.TeamId == null ? null : await db.Teams.AsNoTracking().Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == g.TeamId);
            if (!PlayerFollows.Offered(g, team, settings)) return Results.BadRequest("Following players is not available in this gallery.");
            if (Who(ctx, out var sub, out var email) is { } problem) return problem;
            var player = PlayerFollows.PlayerOf(team!, input.Tag);
            if (player == null) return Results.BadRequest("That player is not on this team.");
            if (await PlayerFollows.SetAsync(db, sub, email, player, input.Follow) is { } err) return Results.BadRequest(err);
            return Results.Ok(new { following = await PlayerFollows.FollowingAsync(db, sub, team!) });
        }).RequireRateLimiting("follow");

        // the "My players" page: every team this person can follow players of, and who they follow
        app.MapGet("/api/me/players", async (HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            var sub = AuthApi.Sub(ctx);
            if (sub == null || !PlayerFollows.Available(settings)) return Results.Ok(new { signedIn = sub != null, available = PlayerFollows.Available(settings), teams = Array.Empty<object>() });
            var email = GalleryPeople.VerifiedEmail(ctx.User);
            var followed = await PlayerTags.SameGirlAsync(db, await db.PlayerFollows.AsNoTracking().Where(f => f.Sub == sub).Select(f => f.PlayerId).ToListAsync());
            var teams = await PlayerFollows.TeamsForAsync(db, sub, email, settings.Follow.Enabled);
            return Results.Ok(new
            {
                signedIn = true, available = true, verified = email != null,
                teams = teams.Select(t => new
                {
                    t.Id, name = t.FullName,
                    players = t.Players.OrderBy(p => int.TryParse(p.Number, out var n) ? n : int.MaxValue).ThenBy(p => p.Number.Length)
                        .Select(p => new { p.Id, number = p.Number, label = PlayerFollows.ShortLabel(t, p), following = followed.Contains(p.Id) }),
                }),
            });
        });

        app.MapPost("/api/me/players", async (PlayerFollowInput input, HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            if (!PlayerFollows.Available(settings)) return Results.BadRequest("Following players is not available on this site.");
            if (Who(ctx, out var sub, out var email) is { } problem) return problem;
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == input.PlayerId);
            // only players this page would list: stopping is always allowed
            if (player == null || (input.Follow && !(await PlayerFollows.TeamsForAsync(db, sub, email, settings.Follow.Enabled)).Any(t => t.Id == player.TeamId)))
                return Results.BadRequest("That player cannot be followed here.");
            if (await PlayerFollows.SetAsync(db, sub, email, player, input.Follow) is { } err) return Results.BadRequest(err);
            return Results.Ok(new { following = input.Follow });
        }).RequireRateLimiting("follow");

        // the link in every email: works without signing in, and says the same whether or not the link was known
        app.MapGet("/follow/stop/{token}", async (string token, AppDb db) =>
        {
            // stops following the girl, on every roster she is on
            var f = await db.PlayerFollows.AsNoTracking().FirstOrDefaultAsync(x => x.Token == token);
            if (f != null)
            {
                var same = await PlayerTags.SameGirlAsync(db, new[] { f.PlayerId });
                await db.PlayerFollows.Where(x => x.Sub == f.Sub && same.Contains(x.PlayerId)).ExecuteDeleteAsync();
            }
            return Results.Redirect("/?unfollowed=1");
        });

        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");
        secured.MapGet("/settings/follow", (SettingsService settings) => Results.Ok(new { enabled = settings.Follow.Enabled, mail = PlayerFollows.Available(settings) }));
        secured.MapPut("/settings/follow", (FollowSettings input, HttpContext ctx, SettingsService settings) =>
            !Csrf(ctx) ? Results.BadRequest("missing header") : Results.Ok(settings.SetFollow(input)));
        // a gallery's own choice: true or false, or null to follow the team's setting
        secured.MapPut("/galleries/{id:int}/follow", async (int id, AllowInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            g.FollowPlayers = input.Allow;
            await db.SaveChangesAsync();
            return Results.Ok(new { allow = g.FollowPlayers });
        });
    }
}
