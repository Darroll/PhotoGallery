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
/// Follow a player: a parent picks a player in a gallery that allows it and gets one email a day at most, listing the new
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

    /// <summary>New photos of the team's players in this gallery go to followers: the gallery's own choice, else the site-wide switch.</summary>
    public static bool On(Gallery g, Team? team, bool siteWide) => team != null && g.TeamId == team.Id && (g.FollowPlayers ?? siteWide);

    /// <summary>The gallery offers "Follow" on its player chips and in the photo viewer.</summary>
    public static bool Offered(Gallery g, Team? team, SettingsService settings) => On(g, team, settings.Follow.Enabled) && Available(settings);

    /// <summary>How the player is named on a chip or button: "#10", or "Emma 10" when the team puts names in tags.</summary>
    public static string ShortLabel(Team t, Player p) => t.TagWithNames && p.Name.Length > 0 ? $"{p.Name} {p.Number}" : $"#{p.Number}";

    /// <summary>
    /// The teams a signed-in person can follow players of from the "My players" page: teams with a followable gallery that is
    /// public or that they are invited to, plus any team they already follow someone on. Teams seen only in unlisted or
    /// password galleries are left out here (they can still follow from those galleries), so the page reveals nothing new.
    /// </summary>
    public static async Task<List<Team>> TeamsForAsync(AppDb db, string sub, string? email, bool siteWide)
    {
        var teams = await db.Teams.AsNoTracking().Include(t => t.Players).ToListAsync();
        var followed = await PlayerTags.SameGirlAsync(db, await db.PlayerFollows.AsNoTracking().Where(f => f.Sub == sub).Select(f => f.PlayerId).ToListAsync());
        var galleries = await db.Galleries.AsNoTracking().Where(g => g.TeamId != null && (g.Visibility == Visibility.Public || g.Visibility == Visibility.Restricted)).ToListAsync();
        var result = new List<Team>();
        foreach (var t in teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(t => t.CreatedAt))
        {
            if (t.Players.Count == 0) continue;
            var ok = t.Players.Any(p => followed.Contains(p.Id));
            foreach (var g in galleries.Where(g => !ok && On(g, t, siteWide) && !GalleryRules.IsExpired(g)))
                if (g.Visibility == Visibility.Public || (email != null && await GalleryPeople.AllowsEmailAsync(db, g, email))) ok = true;
            if (ok) result.Add(t);
        }
        return result;
    }

    /// <summary>Follows or stops following a player for this person. Returns an error message, or null when done.</summary>
    /// <summary>
    /// Following a player follows the girl: one follow covers every roster she is on (other teams and seasons, linked by name).
    /// Stopping removes the follows of all her roster places.
    /// </summary>
    public static async Task<string?> SetAsync(AppDb db, string sub, string email, Player player, bool follow)
    {
        var same = await PlayerTags.SameGirlAsync(db, new[] { player.Id });
        var mine = await db.PlayerFollows.Where(x => x.Sub == sub && same.Contains(x.PlayerId)).ToListAsync();
        if (follow && mine.Count == 0)
        {
            if (await db.PlayerFollows.CountAsync(x => x.Sub == sub) >= MaxPerPerson) return $"You can follow up to {MaxPerPerson} players.";
            db.PlayerFollows.Add(new PlayerFollow { Sub = sub, Email = email, PlayerId = player.Id, Token = Passwords.NewToken() });
        }
        else if (follow) foreach (var f in mine) f.Email = email;   // mail goes to the address the account has now
        else db.PlayerFollows.RemoveRange(mine);
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>How the player is named in emails: "#10 (Hurricanes)", or "Emma 10" when the team puts names in tags.</summary>
    public static string Label(Team t, Player p) => t.TagWithNames && p.Name.Length > 0 ? $"{p.Name} {p.Number}" : $"#{p.Number} ({t.FullName})";

    /// <summary>The team's player carrying this tag (either form, "Team 10" or "Emma 10"), or null.</summary>
    public static Player? PlayerOf(Team team, string? tag)
    {
        var t = Tags.Clean(tag ?? "");
        return t.Length == 0 ? null : team.Players.FirstOrDefault(p => PlayerTags.TagNames(team, p).Any(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The jersey numbers of the players this person follows on the gallery's team (a gallery has one team).</summary>
    public static async Task<List<string>> FollowingAsync(AppDb db, string sub, Team team)
    {
        var mine = await PlayerTags.SameGirlAsync(db, await db.PlayerFollows.AsNoTracking().Where(f => f.Sub == sub).Select(f => f.PlayerId).ToListAsync());
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
        var byAthlete = players.Values.Where(x => x.Player.AthleteId != null).GroupBy(x => x.Player.AthleteId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Team.CreatedAt).ToList());
        var teamById = teams.ToDictionary(t => t.Id);
        var galleries = (await db.Galleries.AsNoTracking().Where(g => g.TeamId != null).ToListAsync())
            .Where(g => On(g, teamById.GetValueOrDefault(g.TeamId!.Value), settings.Follow.Enabled)).ToList();
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
            var seenGirls = new HashSet<string>();
            foreach (var f in person.OrderBy(f => f.CreatedAt))
            {
                if (!players.TryGetValue(f.PlayerId, out var tp)) continue;
                // the girl on every roster she is on: other teams and seasons count too
                var places = tp.Player.AthleteId is { } aid ? byAthlete[aid] : new List<(Team Team, Player Player)> { tp };
                var girlKey = tp.Player.AthleteId is { } a2 ? "a" + a2 : "p" + tp.Player.Id;
                // her name only when the team she was followed on puts names in tags; otherwise her numbers (on the rosters with
                // new photos), which say nothing about her
                string LabelFor(List<(Team Team, Player Player)> on) => on.Count <= 1 ? Label(on.Count == 1 ? on[0].Team : tp.Team, on.Count == 1 ? on[0].Player : tp.Player)
                    : tp.Team.TagWithNames && tp.Player.Name.Length > 0 ? tp.Player.Name
                    : string.Join(", ", on.Select(x => $"#{x.Player.Number} ({x.Team.FullName})").Distinct());
                var label = Label(tp.Team, tp.Player);
                if (seenGirls.Add(girlKey))
                {
                    var since = person.Where(x => players.TryGetValue(x.PlayerId, out var o) && places.Any(pl => pl.Player.Id == o.Player.Id)).Min(x => x.NotifiedUpTo);
                    var lines = new List<string>();
                    var withPhotos = new List<(Team Team, Player Player)>();
                    foreach (var place in places)
                    {
                        var before = lines.Count;
                        var names = PlayerTags.TagNames(place.Team, place.Player).Select(n => n.ToLower()).ToList();
                        var tagIds = await db.Tags.Where(t => names.Contains(t.Name.ToLower())).Select(t => t.Id).ToListAsync();
                        var rows = tagIds.Count == 0 ? new() : await (
                            from pt in db.PhotoTags
                            join p in db.Photos on pt.PhotoId equals p.Id
                            where tagIds.Contains(pt.TagId) && pt.TaggedAt > since && pt.TaggedAt <= cutoff && p.Approved && galleryIds.Contains(p.GalleryId)
                            select new { p.Id, p.GalleryId }).Distinct().ToListAsync();
                        foreach (var grp in rows.GroupBy(r => r.GalleryId))
                        {
                            var g = byId[grp.Key];
                            if (g.TeamId != place.Team.Id || GalleryRules.IsExpired(g)) continue;   // a tag only means her in her own team's galleries
                            if (g.Visibility == Visibility.Restricted && !await GalleryPeople.AllowsEmailAsync(db, g, f.Email)) continue;
                            var hidden = GalleryRules.HiddenCoverId(g);
                            var n = grp.Select(r => r.Id).Distinct().Count(id => id != hidden);
                            if (n == 0) continue;
                            lines.Add($"  {n} new photo{(n == 1 ? "" : "s")} in {g.Title}\n  {url}/g/{g.Slug}?player={Uri.EscapeDataString(PlayerTags.TagName(place.Team, place.Player))}");
                        }
                        if (lines.Count > before) withPhotos.Add(place);
                    }
                    if (lines.Count > 0) { var shown = LabelFor(withPhotos); sections.Add($"{shown}:\n" + string.Join("\n", lines)); labels.Add(shown); }
                }
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
                       $"\n\n---\nYou get this because you follow players on {site}, at most once a day. See or change who you follow:\n{url}/players\n" + string.Join("\n", stops)
            });
            if (last == null) db.AlertStates.Add(new AlertState { Key = sentKey, At = now }); else last.At = now;
        }
        if (changed) await db.SaveChangesAsync();
    }
}
