using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>Follow a player: signed-in visitors with a verified email follow or stop following from a gallery, or stop from the email link.</summary>
public static class FollowApi
{
    record FollowInput(string? Tag, bool Follow);
    record AllowInput(bool Allow);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/g/{slug}/follow", async (string slug, FollowInput input, HttpContext ctx, AppDb db, AccessService access, SettingsService settings) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest("missing header");
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed) return Results.NotFound();
            if (!PlayerFollows.Offered(g, settings)) return Results.BadRequest("Following players is not available in this gallery.");
            var sub = AuthApi.Sub(ctx);
            if (sub == null) return Results.Json(new { signInRequired = true }, statusCode: 401);
            var email = GalleryPeople.VerifiedEmail(ctx.User);
            if (email == null) return Results.BadRequest("Your sign-in has no verified email address, so emails cannot be sent to you.");
            var team = await db.Teams.AsNoTracking().Include(t => t.Players).FirstAsync(t => t.Id == g.TeamId);
            var player = PlayerFollows.PlayerOf(team, input.Tag);
            if (player == null) return Results.BadRequest("That player is not on this team.");

            var f = await db.PlayerFollows.FirstOrDefaultAsync(x => x.Sub == sub && x.PlayerId == player.Id);
            if (input.Follow && f == null)
            {
                if (await db.PlayerFollows.CountAsync(x => x.Sub == sub) >= PlayerFollows.MaxPerPerson)
                    return Results.BadRequest($"You can follow up to {PlayerFollows.MaxPerPerson} players.");
                db.PlayerFollows.Add(new PlayerFollow { Sub = sub, Email = email, PlayerId = player.Id, Token = Passwords.NewToken() });
            }
            else if (input.Follow && f != null) f.Email = email;   // mail goes to the address the account has now
            else if (!input.Follow && f != null) db.PlayerFollows.Remove(f);
            await db.SaveChangesAsync();
            return Results.Ok(new { following = await PlayerFollows.FollowingAsync(db, sub, team) });
        }).RequireRateLimiting("suggest");

        // the link in every email: works without signing in, and says the same whether or not the link was known
        app.MapGet("/follow/stop/{token}", async (string token, AppDb db) =>
        {
            await db.PlayerFollows.Where(f => f.Token == token).ExecuteDeleteAsync();
            return Results.Redirect("/?unfollowed=1");
        });

        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");
        secured.MapPut("/galleries/{id:int}/follow", async (int id, AllowInput input, HttpContext ctx, AppDb db) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest("missing header");
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            g.AllowFollow = input.Allow;
            await db.SaveChangesAsync();
            return Results.Ok(new { allow = g.AllowFollow });
        });
    }
}
