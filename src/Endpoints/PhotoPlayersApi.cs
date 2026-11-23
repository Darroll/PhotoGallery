using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// The players in one photo, from the photo viewer: everyone sees who is tagged; the admin tags or untags several players
/// at once; signed-in visitors (parents) suggest players when the gallery allows it, and the admin reviews those.
/// </summary>
public static class PhotoPlayersApi
{
    record ChangeInput(List<string>? Add, List<string>? Remove);
    record SuggestInput(List<string>? Numbers);
    record AllowInput(bool Allow);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";

    /// <summary>What the viewer may offer: "admin" tags straight away, "suggest" sends suggestions for review, null shows tags only.</summary>
    public static string? Can(HttpContext ctx, Gallery g) =>
        g.TeamId == null ? null : AccessService.IsAdmin(ctx) ? "admin" : g.AllowPlayerTags ? "suggest" : null;

    /// <summary>A roster number as typed (1 to 3 digits, kept as written), or null.</summary>
    static string? Num(string? s) { var t = (s ?? "").Trim().TrimStart('#'); return System.Text.RegularExpressions.Regex.IsMatch(t, "^[0-9]{1,3}$") ? t : null; }

    static object Chip(Team t, Player p, bool admin) => new { number = p.Number, teamId = t.Id, tag = PlayerTags.TagName(t, p), label = admin && p.Name.Length > 0 ? $"#{p.Number} {p.Name}" : t.TagWithNames && p.Name.Length > 0 ? $"{p.Name} {p.Number}" : $"#{p.Number}" };

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/g/{slug}/photo/{id:int}/players", async (string slug, int id, HttpContext ctx, AppDb db, AccessService access) =>
        {
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id && x.Gallery!.Slug == slug);
            if (p?.Gallery == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, p.Gallery);
            if (!a.Allowed || (!p.Approved && !AccessService.IsAdmin(ctx))) return Results.NotFound();
            var g = p.Gallery;
            var team = g.TeamId == null ? null : await db.Teams.AsNoTracking().Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == g.TeamId);
            if (team == null) return Results.Ok(new { can = (string?)null, players = Array.Empty<object>() });
            var admin = AccessService.IsAdmin(ctx);
            var can = Can(ctx, g);
            var on = await PlayerTags.OnPhotoAsync(db, id, team);
            var roster = can == null ? null : team.Players.OrderBy(x => int.TryParse(x.Number, out var n) ? n : int.MaxValue).ThenBy(x => x.Number.Length).Select(x => Chip(team, x, admin));
            var pending = !admin ? null : await (from s in db.PlayerSuggestions join pl in db.Players on s.PlayerId equals pl.Id
                                                 where s.PhotoId == id && s.Status == "pending" && pl.TeamId == team.Id
                                                 select new { s.Id, pl.Number, s.Confidence, s.Source, s.Unsure, s.Note }).ToListAsync();
            return Results.Ok(new
            {
                can, signedIn = AuthApi.Sub(ctx) != null, team = team.Name,
                players = on.Select(x => Chip(team, x, admin)),
                roster,
                pending = pending?.Select(s => new { s.Id, number = s.Number, s.Confidence, s.Source, s.Unsure, s.Note }),
                tags = admin ? (await Tags.NamesByPhotoAsync(db, new List<int> { id })).GetValueOrDefault(id) ?? new() : null,   // so the viewer can refresh after a review
            });
        });

        // a signed-in visitor says these players are in the photo: suggestions for the admin to review
        app.MapPost("/api/g/{slug}/photo/{id:int}/players/suggest", async (string slug, int id, SuggestInput input, HttpContext ctx, AppDb db, AccessService access) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id && x.Gallery!.Slug == slug);
            if (p?.Gallery == null) return Results.NotFound();
            var a = await access.CheckAsync(ctx, p.Gallery);
            if (!a.Allowed || !p.Approved) return Results.NotFound();
            if (Can(ctx, p.Gallery) != "suggest") return Results.BadRequest("Player tags cannot be suggested in this gallery.");
            var sub = AuthApi.Sub(ctx);
            if (sub == null) return Results.Json(new { signInRequired = true }, statusCode: 401);
            var name = (ctx.User.Identity?.Name ?? "").Trim();
            var team = await db.Teams.Include(t => t.Players).FirstAsync(t => t.Id == p.Gallery.TeamId);
            var known = (await db.PlayerSuggestions.Where(s => s.PhotoId == id).Select(s => s.PlayerId).ToListAsync()).ToHashSet();
            var added = 0;
            foreach (var number in (input.Numbers ?? new()).Select(Num).Where(n => n != null).Distinct().Take(10))
            {
                var pl = team.Players.FirstOrDefault(x => x.Number == number);
                if (pl == null || !known.Add(pl.Id)) continue;   // only roster numbers; one suggestion per player and photo (a rejected one stays rejected)
                db.PlayerSuggestions.Add(new PlayerSuggestion { PhotoId = id, PlayerId = pl.Id, Confidence = 100, Source = "parent " + (name.Length > 0 ? (name.Length > 60 ? name[..60] : name) : "(signed in)"), Status = "pending" });
                added++;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { suggested = added });
        }).RequireRateLimiting("suggest");

        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        // the admin tags and untags players on one photo; a number not on the roster waits under that number until the player is added
        secured.MapPost("/photos/{id:int}/players", async (int id, ChangeInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var p = await db.Photos.Include(x => x.Gallery).FirstOrDefaultAsync(x => x.Id == id);
            if (p?.Gallery == null) return Results.NotFound();
            if (p.Gallery.TeamId == null) return Results.BadRequest("Choose the team for this gallery first (gallery settings, Players).");
            var team = await db.Teams.Include(t => t.Players).FirstAsync(t => t.Id == p.Gallery.TeamId);
            var held = new List<string>();
            foreach (var number in (input.Remove ?? new()).Select(Num).Where(n => n != null).Distinct().Take(60))
                if (team.Players.FirstOrDefault(x => x.Number == number) is { } pl) await PlayerTags.UntagAsync(db, id, team, pl);
            foreach (var number in (input.Add ?? new()).Select(Num).Where(n => n != null).Distinct().Take(60))
            {
                if (team.Players.FirstOrDefault(x => x.Number == number) is { } pl) await PlayerTags.TagByHandAsync(db, id, team, pl);
                else { await PlayerTags.HoldNumberAsync(db, id, team, number!); held.Add(number!); }
            }
            await db.SaveChangesAsync();
            var tags = (await Tags.NamesByPhotoAsync(db, new List<int> { id })).GetValueOrDefault(id) ?? new();
            var on = await PlayerTags.OnPhotoAsync(db, id, team);
            return Results.Ok(new { tags, players = on.Select(x => Chip(team, x, true)), held });
        });

        secured.MapPut("/galleries/{id:int}/playertags", async (int id, AllowInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            g.AllowPlayerTags = input.Allow;
            await db.SaveChangesAsync();
            return Results.Ok(new { allow = g.AllowPlayerTags });
        });
    }
}
