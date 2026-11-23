using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// A signed-in visitor (a parent) who wants an email when new photos of one player are tagged. Kept by the provider's
/// account id and the verified email address it had when they followed.
/// </summary>
public class PlayerFollow
{
    public int Id { get; set; }
    public string Sub { get; set; } = "";
    /// <summary>The verified sign-in email, lower case. Mail goes here.</summary>
    public string Email { get; set; } = "";
    public int PlayerId { get; set; }
    /// <summary>Secret in the "stop following" link of every email.</summary>
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Photos tagged up to this moment have been mailed (or were tagged before following).</summary>
    public DateTime NotifiedUpTo { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Follow a player: a parent picks a player (in a gallery, or on the player's own page that spans the team's galleries) where
/// following is on, for the whole team or one gallery, and gets one email a day at most, listing the new
/// photos of that player in the team's galleries that allow following and that the parent can open. A tag counts once it is
/// live (approved), and only after it has stood for a while, so a review session ends before the email goes out. Players
/// are named the way the team's tags name them: by number, unless the team puts names in tags.
/// </summary>
public static class PlayerFollows
{
    /// <summary>How long a tag waits before it is mailed: the admin may still be reviewing, or may take it off again.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(30);
    /// <summary>At most one email per person in this time.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromHours(23);
    /// <summary>Most players one person can follow.</summary>
    public const int MaxPerPerson = 20;

    /// <summary>Emails can be sent: a mail relay and the site address (every email links back) are set up.</summary>
    public static bool Available(SettingsService settings) => settings.Email.Ready && settings.Email.SiteUrl.Length > 0;

    /// <summary>Following counts in this gallery: it has this team, and its own switch says so, or it follows the team's switch and that is on.</summary>
    public static bool FollowOn(Gallery g, Team? t) => t != null && g.TeamId == t.Id && (g.FollowSetting ?? t.AllowFollow);

    /// <summary>The gallery offers "Follow" on its player chips.</summary>
    public static bool Offered(Gallery g, Team? t, SettingsService settings) => FollowOn(g, t) && Available(settings);

    /// <summary>
    /// The team's galleries where following counts, as this visitor may see them on a page that spans galleries (a player's
    /// page, the players list): public galleries, and protected or specific-people galleries the visitor has already opened.
    /// Unlisted galleries are left out, as in search, because anyone could then find them. The admin sees every one.
    /// </summary>
    public static async Task<List<Gallery>> VisibleGalleriesAsync(AppDb db, HttpContext ctx, AccessService access, Team t)
    {
        var admin = AccessService.IsAdmin(ctx);
        var list = new List<Gallery>();
        foreach (var g in await db.Galleries.AsNoTracking().Where(g => g.TeamId == t.Id).ToListAsync())
        {
            if (!FollowOn(g, t) || GalleryRules.IsExpired(g) && !admin) continue;
            if (!admin && g.Visibility == Visibility.Unlisted) continue;
            if (!(await access.CheckAsync(ctx, g)).Allowed) continue;
            list.Add(g);
        }
        return list;
    }

    /// <summary>The ids of approved photos in these galleries tagged with any of the player's tags (the hidden cover and RAW-only frames left out), with their gallery.</summary>
    public static async Task<List<Photo>> PhotosOfAsync(AppDb db, Team t, Player p, List<Gallery> galleries)
    {
        if (galleries.Count == 0) return new();
        var names = PlayerTags.TagNames(t, p).Select(n => n.ToLower()).ToList();
        var tagIds = await db.Tags.Where(x => names.Contains(x.Name.ToLower())).Select(x => x.Id).ToListAsync();
        if (tagIds.Count == 0) return new();
        var gids = galleries.Select(g => g.Id).ToList();
        var hidden = galleries.Select(GalleryRules.HiddenCoverId).Where(x => x != 0).ToList();
        var photos = await db.Photos.AsNoTracking()
            .Where(x => x.Approved && gids.Contains(x.GalleryId) && !hidden.Contains(x.Id) && db.PhotoTags.Any(pt => pt.PhotoId == x.Id && tagIds.Contains(pt.TagId)))
            .ToListAsync();
        return photos.Where(x => !x.RawOnly).ToList();
    }

    /// <summary>How the player is named in emails: "#10 (Hurricanes)", or "Emma 10" when the team puts names in tags.</summary>
    public static string Label(Team t, Player p) => t.TagWithNames && p.Name.Length > 0 ? $"{p.Name} {p.Number}" : $"#{p.Number} ({t.Name})";

    /// <summary>The player's own page, with their photos from all the team's galleries where following is on.</summary>
    public static string PagePath(Team t, Player p) => $"/player/{t.Id}/{Uri.EscapeDataString(p.Number)}";

    /// <summary>The team's player carrying this tag (either form, "Team 10" or "Emma 10"), or null.</summary>
    public static Player? PlayerOf(Team team, string? tag)
    {
        var t = Tags.Clean(tag ?? "");
        return t.Length == 0 ? null : team.Players.FirstOrDefault(p => PlayerTags.TagNames(team, p).Any(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The jersey numbers of the players this person follows on the gallery's team (a gallery has one team).</summary>
    public static async Task<List<string>> FollowingAsync(AppDb db, string sub, Team team)
    {
        var ids = team.Players.Select(p => p.Id).ToList();
        var mine = await db.PlayerFollows.AsNoTracking().Where(f => f.Sub == sub && ids.Contains(f.PlayerId)).Select(f => f.PlayerId).ToListAsync();
        return team.Players.Where(p => mine.Contains(p.Id)).Select(p => p.Number).ToList();
    }

    /// <summary>
    /// Sends each follower who is due one email with the new photos of every player they follow. Called from the email loop;
    /// cheap when nobody follows anyone.
    /// </summary>
    public static async Task SendAsync(AppDb db, SettingsService settings, DateTime? nowOverride = null)
    {
        if (!Available(settings)) return;
        var follows = await db.PlayerFollows.ToListAsync();
        if (follows.Count == 0) return;
        var now = nowOverride ?? DateTime.UtcNow;
        var cutoff = now - Settle;
        var site = settings.SiteName;
        var url = settings.Email.SiteUrl;

        var teams = await db.Teams.AsNoTracking().Include(t => t.Players).ToListAsync();
        var players = teams.SelectMany(t => t.Players.Select(p => (Team: t, Player: p))).ToDictionary(x => x.Player.Id);
        var teamById = teams.ToDictionary(t => t.Id);
        var galleries = (await db.Galleries.AsNoTracking().Where(g => g.TeamId != null && !g.FollowOff).ToListAsync())
            .Where(g => FollowOn(g, teamById.GetValueOrDefault(g.TeamId!.Value))).ToList();
        if (galleries.Count == 0) return;
        var galleryIds = galleries.Select(g => g.Id).ToList();
        var byId = galleries.ToDictionary(g => g.Id);
        var changed = false;

        foreach (var person in follows.GroupBy(f => f.Sub))
        {
            var sentKey = "follow-sent:" + person.Key;
            var last = await db.AlertStates.FirstOrDefaultAsync(a => a.Key == sentKey);
            if (last != null && now - last.At < Gap) continue;
            var email = person.OrderByDescending(f => f.CreatedAt).First().Email;

            var sections = new List<string>();
            var labels = new List<string>();
            var stops = new List<string>();
            foreach (var f in person.OrderBy(f => f.CreatedAt))
            {
                if (!players.TryGetValue(f.PlayerId, out var tp)) continue;
                var names = PlayerTags.TagNames(tp.Team, tp.Player).Select(n => n.ToLower()).ToList();
                var tagIds = await db.Tags.Where(t => names.Contains(t.Name.ToLower())).Select(t => t.Id).ToListAsync();
                var since = f.NotifiedUpTo;
                var rows = tagIds.Count == 0 ? new() : await (
                    from pt in db.PhotoTags
                    join p in db.Photos on pt.PhotoId equals p.Id
                    where tagIds.Contains(pt.TagId) && pt.TaggedAt > since && pt.TaggedAt <= cutoff && p.Approved && galleryIds.Contains(p.GalleryId)
                    select new { p.Id, p.GalleryId }).Distinct().ToListAsync();
                var label = Label(tp.Team, tp.Player);
                var lines = new List<string>();
                foreach (var grp in rows.GroupBy(r => r.GalleryId))
                {
                    var g = byId[grp.Key];
                    if (g.TeamId != tp.Team.Id || GalleryRules.IsExpired(g)) continue;
                    if (g.Visibility == Visibility.Restricted && !await GalleryPeople.AllowsEmailAsync(db, g, f.Email)) continue;
                    var hidden = GalleryRules.HiddenCoverId(g);
                    var n = grp.Select(r => r.Id).Distinct().Count(id => id != hidden);
                    if (n == 0) continue;
                    lines.Add($"  {n} new photo{(n == 1 ? "" : "s")} in {g.Title}\n  {url}/g/{g.Slug}?player={Uri.EscapeDataString(PlayerTags.TagName(tp.Team, tp.Player))}");
                }
                if (lines.Count > 1) lines.Add($"  Every photo of {label}:\n  {url}{PagePath(tp.Team, tp.Player)}");
                if (lines.Count > 0) { sections.Add($"{label}:\n" + string.Join("\n", lines)); labels.Add(label); }
                stops.Add($"To stop following {label}, open:\n{url}/follow/stop/{f.Token}");
            }

            foreach (var f in person) if (f.NotifiedUpTo < cutoff) f.NotifiedUpTo = cutoff;   // never back: a new follow starts at the moment it was made
            changed = true;
            if (sections.Count == 0) continue;
            var subject = labels.Count == 1 ? $"New photos of {labels[0]}" : "New photos of the players you follow";
            db.EmailOutbox.Add(new EmailOutbox
            {
                ToAddr = email,
                Subject = subject,
                Body = $"New photos have been added on {site}:\n\n" + string.Join("\n\n", sections) +
                       $"\n\n---\nYou get this because you follow players on {site}, at most once a day.\n" + string.Join("\n", stops)
            });
            if (last == null) db.AlertStates.Add(new AlertState { Key = sentKey, At = now }); else last.At = now;
        }
        if (changed) await db.SaveChangesAsync();
    }
}
