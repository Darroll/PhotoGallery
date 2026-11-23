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
        var followed = (await db.PlayerFollows.AsNoTracking().Where(f => f.Sub == sub).Select(f => f.PlayerId).ToListAsync()).ToHashSet();
        var galleries = await db.Galleries.AsNoTracking().Where(g => g.TeamId != null && (g.Visibility == Visibility.Public || g.Visibility == Visibility.Restricted)).ToListAsync();
        var result = new List<Team>();
        foreach (var t in teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
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
    public static async Task<string?> SetAsync(AppDb db, string sub, string email, Player player, bool follow)
    {
        var f = await db.PlayerFollows.FirstOrDefaultAsync(x => x.Sub == sub && x.PlayerId == player.Id);
        if (follow && f == null)
        {
            if (await db.PlayerFollows.CountAsync(x => x.Sub == sub) >= MaxPerPerson) return $"You can follow up to {MaxPerPerson} players.";
            db.PlayerFollows.Add(new PlayerFollow { Sub = sub, Email = email, PlayerId = player.Id, Token = Passwords.NewToken() });
        }
        else if (follow && f != null) f.Email = email;   // mail goes to the address the account has now
        else if (!follow && f != null) db.PlayerFollows.Remove(f);
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>How the player is named in emails: "#10 (Hurricanes)", or "Emma 10" when the team puts names in tags.</summary>
    public static string Label(Team t, Player p) => t.TagWithNames && p.Name.Length > 0 ? $"{p.Name} {p.Number}" : $"#{p.Number} ({t.Name})";

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
                       $"\n\n---\nYou get this because you follow players on {site}, at most once a day. See or change who you follow:\n{url}/players\n" + string.Join("\n", stops)
            });
            if (last == null) db.AlertStates.Add(new AlertState { Key = sentKey, At = now }); else last.At = now;
        }
        if (changed) await db.SaveChangesAsync();
    }
}
