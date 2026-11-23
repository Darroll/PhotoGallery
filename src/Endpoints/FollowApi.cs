using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Follow a player: signed-in visitors with a verified email follow or stop following from a gallery, from a player's own
/// page (their photos from all the team's galleries), or from the players list; or stop from the email link. Following is
/// switched on for a whole team (Settings, Teams) or for one gallery (its Players section).
/// </summary>
public static class FollowApi
{
    record FollowInput(string? Tag, bool Follow);
    record PlayerFollowInput(bool Follow);
    record AllowInput(bool? Allow);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";

    /// <summary>The short name on a chip within its team: "#10", or "Emma 10" when the team puts names in tags.</summary>
    static string Short(Team t, Player p) => t.TagWithNames && p.Name.Length > 0 ? $"{p.Name} {p.Number}" : $"#{p.Number}";

    /// <summary>Follows or stops following one player for the signed-in visitor. Null when it worked, otherwise the reply to send.</summary>
    static async Task<IResult?> ToggleAsync(HttpContext ctx, AppDb db, Player player, bool follow)
    {
        var sub = AuthApi.Sub(ctx);
        if (sub == null) return Results.Json(new { signInRequired = true }, statusCode: 401);
        var email = GalleryPeople.VerifiedEmail(ctx.User);
        if (email == null) return Results.BadRequest("Your sign-in has no verified email address, so emails cannot be sent to you.");
        var f = await db.PlayerFollows.FirstOrDefaultAsync(x => x.Sub == sub && x.PlayerId == player.Id);
        if (follow && f == null)
        {
            if (await db.PlayerFollows.CountAsync(x => x.Sub == sub) >= PlayerFollows.MaxPerPerson)
                return Results.BadRequest($"You can follow up to {PlayerFollows.MaxPerPerson} players.");
            db.PlayerFollows.Add(new PlayerFollow { Sub = sub, Email = email, PlayerId = player.Id, Token = Passwords.NewToken() });
        }
        else if (follow && f != null) f.Email = email;   // mail goes to the address the account has now
        else if (!follow && f != null) db.PlayerFollows.Remove(f);
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>The team, when following is on in at least one of its galleries; otherwise null (the player pages do not exist then).</summary>
    static async Task<Team?> FollowedTeamAsync(AppDb db, int teamId)
    {
        var team = await db.Teams.AsNoTracking().Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == teamId);
        if (team == null) return null;
        var galleries = await db.Galleries.AsNoTracking().Where(g => g.TeamId == teamId && !g.FollowOff).ToListAsync();
        return galleries.Any(g => PlayerFollows.FollowOn(g, team)) ? team : null;
    }

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/g/{slug}/follow", async (string slug, FollowInput input, HttpContext ctx, AppDb db, AccessService access, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FirstOrDefaultAsync(x => x.Slug == slug);
            if (g == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, g);
            if (!a.Allowed) return Results.NotFound();
            var team = g.TeamId == null ? null : await db.Teams.AsNoTracking().Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == g.TeamId);
            if (!PlayerFollows.Offered(g, team, settings)) return Results.BadRequest("Following players is not available in this gallery.");
            var player = PlayerFollows.PlayerOf(team!, input.Tag);
            if (player == null) return Results.BadRequest("That player is not on this team.");
            if (await ToggleAsync(ctx, db, player, input.Follow) is { } fail) return fail;
            return Results.Ok(new { following = await PlayerFollows.FollowingAsync(db, AuthApi.Sub(ctx)!, team!) });
        }).RequireRateLimiting("follow");

        // every player who can be followed, by team, with how many of their photos this visitor can see, and who they follow
        app.MapGet("/api/players", async (HttpContext ctx, AppDb db, AccessService access, SettingsService settings) =>
        {
            var sub = AuthApi.Sub(ctx);
            var mine = sub == null ? new HashSet<int>() : (await db.PlayerFollows.AsNoTracking().Where(f => f.Sub == sub).Select(f => f.PlayerId).ToListAsync()).ToHashSet();
            var teams = new List<object>();
            foreach (var t in await db.Teams.AsNoTracking().Include(t => t.Players).OrderBy(t => t.Name).ToListAsync())
            {
                if (await FollowedTeamAsync(db, t.Id) == null) continue;
                var galleries = await PlayerFollows.VisibleGalleriesAsync(db, ctx, access, t);
                var players = new List<object>();
                foreach (var p in t.Players.OrderBy(p => int.TryParse(p.Number, out var n) ? n : int.MaxValue).ThenBy(p => p.Number.Length))
                {
                    var count = (await PlayerFollows.PhotosOfAsync(db, t, p, galleries)).Count;
                    var following = mine.Contains(p.Id);
                    if (count == 0 && !following) continue;
                    players.Add(new { number = p.Number, tag = PlayerTags.TagName(t, p), label = Short(t, p), name = PlayerFollows.Label(t, p), photos = count, following });
                }
                if (players.Count > 0) teams.Add(new { t.Id, t.Name, players });
            }
            return Results.Ok(new { signedIn = sub != null, sso = ctx.RequestServices.GetRequiredService<SsoConfig>().Enabled, canFollow = PlayerFollows.Available(settings), teams });
        });

        // one player's page: their photos from every gallery of the team where following is on and that this visitor can see
        app.MapGet("/api/player/{teamId:int}/{number}", async (int teamId, string number, HttpContext ctx, AppDb db, AccessService access, SettingsService settings) =>
        {
            var team = await FollowedTeamAsync(db, teamId);
            var player = team?.Players.FirstOrDefault(p => p.Number == number);
            if (team == null || player == null) return Results.NotFound();
            var galleries = await PlayerFollows.VisibleGalleriesAsync(db, ctx, access, team);
            var photos = await PlayerFollows.PhotosOfAsync(db, team, player, galleries);
            var sub = AuthApi.Sub(ctx);
            var tag = PlayerTags.TagName(team, player);
            var groups = galleries
                .Select(g => (g, list: GalleryRules.Sort(photos.Where(p => p.GalleryId == g.Id).ToList(), g, p => p.TakenAt, p => p.Name, p => p.AddedAt)))
                .Where(x => x.list.Count > 0)
                .OrderByDescending(x => x.g.EventDate ?? x.list.Max(p => p.TakenAt))
                .Select(x => new
                {
                    x.g.Slug, x.g.Title, location = x.g.Location, eventDate = x.g.EventDate, eventEndDate = x.g.EventEndDate,
                    count = x.list.Count, photos = x.list.Select(p => new { p.Id, p.Name, w = p.Width, h = p.Height })
                });
            return Results.Ok(new
            {
                teamId = team.Id, team = team.Name, number = player.Number, tag, label = Short(team, player), name = PlayerFollows.Label(team, player),
                canFollow = PlayerFollows.Available(settings), signedIn = sub != null, sso = ctx.RequestServices.GetRequiredService<SsoConfig>().Enabled,
                following = sub != null && await db.PlayerFollows.AnyAsync(f => f.Sub == sub && f.PlayerId == player.Id),
                count = photos.Count, groups
            });
        });

        app.MapPost("/api/player/{teamId:int}/{number}/follow", async (int teamId, string number, PlayerFollowInput input, HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var team = await FollowedTeamAsync(db, teamId);
            var player = team?.Players.FirstOrDefault(p => p.Number == number);
            if (team == null || player == null) return Results.NotFound();
            if (!PlayerFollows.Available(settings)) return Results.BadRequest("Following players is not available yet.");
            if (await ToggleAsync(ctx, db, player, input.Follow) is { } fail) return fail;
            return Results.Ok(new { following = input.Follow });
        }).RequireRateLimiting("follow");

        // the link in every email: works without signing in, and says the same whether or not the link was known
        app.MapGet("/follow/stop/{token}", async (string token, AppDb db) =>
        {
            await db.PlayerFollows.Where(f => f.Token == token).ExecuteDeleteAsync();
            return Results.Redirect("/?unfollowed=1");
        });

        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");
        // true or false for this gallery alone, or null to follow the team's switch
        secured.MapPut("/galleries/{id:int}/follow", async (int id, AllowInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            g.FollowSetting = input.Allow;
            await db.SaveChangesAsync();
            var team = g.TeamId == null ? null : await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == g.TeamId);
            return Results.Ok(new { allow = g.FollowSetting, on = PlayerFollows.FollowOn(g, team) });
        });

        // the switch for all of a team's galleries
        secured.MapPut("/teams/{id:int}/follow", async (int id, AllowInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var t = await db.Teams.FindAsync(id);
            if (t == null) return Results.NotFound();
            t.AllowFollow = input.Allow == true;
            await db.SaveChangesAsync();
            return Results.Ok(new { allow = t.AllowFollow });
        });
    }
}
