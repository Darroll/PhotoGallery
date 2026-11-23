using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Saved gallery settings that a new gallery starts with, so a weekly game gallery is one step: the team and its jersey,
/// automatic player checks, follow a player, downloads and RAW, curated only, who can open it, tags and so on.
/// A preset is made from a gallery that is already set up the way the admin likes. Title, folder, dates, cover, links and
/// sections belong to each event and are never part of a preset.
/// </summary>
public class GalleryPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string FromGallery { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Visibility Visibility { get; set; } = Visibility.Protected;
    public string? PasswordHash { get; set; }
    public List<int> GroupIds { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public string Location { get; set; } = "";
    /// <summary>The season new galleries are listed under ("Fall 2026"), or empty to work it out from the event date.</summary>
    public string Season { get; set; } = "";

    public bool AllowDownload { get; set; } = true;
    public bool AllowRaw { get; set; }
    public bool AllowZip { get; set; } = true;
    public string AllowedSizes { get; set; } = "";
    public string AllowedFormats { get; set; } = "";
    public RawMatch RawMatchMode { get; set; } = RawMatch.Anywhere;
    public bool HideRawOnly { get; set; } = true;
    public GallerySort SortMode { get; set; } = GallerySort.Taken;
    public bool SortDesc { get; set; }

    public bool AllowGuestUpload { get; set; }
    public bool AllowLikes { get; set; } = true;
    public bool ShowLikeCounts { get; set; } = true;
    public bool AllowComments { get; set; }
    public bool AllowFeedback { get; set; }
    public bool WatermarkView { get; set; }
    public bool WatermarkDownload { get; set; }

    public int? TeamId { get; set; }
    public string TeamKit { get; set; } = "";
    public bool? AutoTag { get; set; }
    public bool AllowPlayerTags { get; set; }
    public bool? FollowPlayers { get; set; }

    public const int MaxName = 60;
    public const int MaxPresets = 30;

    public GalleryPreset Sanitized()
    {
        var c = (GalleryPreset)MemberwiseClone();
        c.Id = new string((Id ?? "").Where(char.IsLetterOrDigit).Take(24).ToArray());
        if (c.Id.Length == 0) c.Id = Guid.NewGuid().ToString("N")[..12];
        c.Name = Clean(Name, MaxName);
        c.FromGallery = Clean(FromGallery, 100);
        c.Location = Clean(Location, 200);
        c.Season = GalleryRules.CleanSeason(Season);
        c.TeamKit = Clean(TeamKit, 200);
        c.AllowedSizes = AllowedSizes ?? "";
        c.AllowedFormats = AllowedFormats ?? "";
        c.GroupIds = (GroupIds ?? new()).Distinct().Take(100).ToList();
        c.Tags = (Tags ?? new()).Select(PhotoGallery.Tags.Clean).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        if (!Enum.IsDefined(c.Visibility)) c.Visibility = Visibility.Protected;
        if (!Enum.IsDefined(c.RawMatchMode)) c.RawMatchMode = RawMatch.Anywhere;
        if (!Enum.IsDefined(c.SortMode)) c.SortMode = GallerySort.Taken;
        if (c.TeamId == null) c.TeamKit = "";
        return c;
    }

    static string Clean(string? s, int max) => new string((s ?? "").Where(ch => !char.IsControl(ch)).Take(max).ToArray()).Trim();
}

/// <summary>The saved presets, and the one new galleries start with unless another (or none) is picked.</summary>
public class PresetSettings
{
    public List<GalleryPreset> Items { get; set; } = new();
    public string? DefaultId { get; set; }

    public PresetSettings Sanitized()
    {
        var items = (Items ?? new()).Where(p => p != null).Select(p => p.Sanitized()).Where(p => p.Name.Length > 0)
            .GroupBy(p => p.Id).Select(g => g.First()).Take(GalleryPreset.MaxPresets).ToList();
        return new PresetSettings { Items = items, DefaultId = items.Any(p => p.Id == DefaultId) ? DefaultId : null };
    }
}

public static class GalleryPresets
{
    /// <summary>A preset holding this gallery's current settings.</summary>
    public static async Task<GalleryPreset> FromGalleryAsync(AppDb db, Gallery g, string name)
    {
        var tags = (await PhotoGallery.Tags.NamesByGalleryAsync(db, new List<int> { g.Id })).GetValueOrDefault(g.Id) ?? new List<string>();
        var groups = await db.GalleryGroups.AsNoTracking().Where(x => x.GalleryId == g.Id).Select(x => x.GroupId).ToListAsync();
        return new GalleryPreset
        {
            Name = name, FromGallery = g.Title,
            Visibility = g.Visibility, PasswordHash = g.PasswordHash, GroupIds = groups, Tags = tags, Location = g.Location, Season = g.Season,
            AllowDownload = g.AllowDownload, AllowRaw = g.AllowRaw, AllowZip = g.AllowZip,
            AllowedSizes = g.AllowedSizes, AllowedFormats = g.AllowedFormats,
            RawMatchMode = g.RawMatchMode, HideRawOnly = g.HideRawOnly, SortMode = g.SortMode, SortDesc = g.SortDesc,
            AllowGuestUpload = g.AllowGuestUpload, AllowLikes = g.AllowLikes, ShowLikeCounts = g.ShowLikeCounts,
            AllowComments = g.AllowComments, AllowFeedback = g.AllowFeedback,
            WatermarkView = g.WatermarkView, WatermarkDownload = g.WatermarkDownload,
            TeamId = g.TeamId, TeamKit = g.TeamId == null ? "" : g.TeamKit, AutoTag = g.AutoTag,
            AllowPlayerTags = g.AllowPlayerTags, FollowPlayers = g.FollowPlayers
        }.Sanitized();
    }

    /// <summary>Copies the preset's settings onto a gallery that has not been saved yet. A team that no longer exists is left out.</summary>
    public static async Task ApplyAsync(AppDb db, GalleryPreset p, Gallery g)
    {
        g.Visibility = p.Visibility;
        g.PasswordHash = p.PasswordHash;
        g.Location = p.Location;
        g.Season = p.Season;
        g.AllowDownload = p.AllowDownload; g.AllowRaw = p.AllowRaw; g.AllowZip = p.AllowZip;
        g.AllowedSizes = p.AllowedSizes; g.AllowedFormats = p.AllowedFormats;
        g.RawMatchMode = p.RawMatchMode; g.HideRawOnly = p.HideRawOnly; g.SortMode = p.SortMode; g.SortDesc = p.SortDesc;
        g.AllowGuestUpload = p.AllowGuestUpload; g.AllowLikes = p.AllowLikes; g.ShowLikeCounts = p.ShowLikeCounts;
        g.AllowComments = p.AllowComments; g.AllowFeedback = p.AllowFeedback;
        g.WatermarkView = p.WatermarkView; g.WatermarkDownload = p.WatermarkDownload;
        g.AutoTag = p.AutoTag; g.AllowPlayerTags = p.AllowPlayerTags; g.FollowPlayers = p.FollowPlayers;
        if (p.TeamId != null && await db.Teams.AnyAsync(t => t.Id == p.TeamId)) { g.TeamId = p.TeamId; g.TeamKit = p.TeamKit; }
    }

    /// <summary>After the gallery is saved: invites the preset's groups (those that still exist) and adds its tags.</summary>
    public static async Task ApplyLinksAsync(AppDb db, GalleryPreset p, Gallery g)
    {
        var groups = await db.PeopleGroups.Where(x => p.GroupIds.Contains(x.Id)).Select(x => x.Id).ToListAsync();
        db.GalleryGroups.AddRange(groups.Select(id => new GalleryGroup { GalleryId = g.Id, GroupId = id }));
        foreach (var name in p.Tags)
        {
            var tag = await db.Tags.FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());
            if (tag == null) { tag = new Tag { Name = name }; db.Tags.Add(tag); await db.SaveChangesAsync(); }
            db.GalleryTags.Add(new GalleryTag { GalleryId = g.Id, TagId = tag.Id });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>What the admin sees in lists: everything except the password itself.</summary>
    public static object Dto(GalleryPreset p, string? defaultId, Dictionary<int, string> teams) => new
    {
        p.Id, p.Name, p.FromGallery, p.CreatedAt, isDefault = p.Id == defaultId,
        p.Visibility, hasPassword = p.PasswordHash != null, groups = p.GroupIds.Count, p.Tags, p.Location, p.Season,
        p.AllowDownload, p.AllowRaw, p.AllowZip, p.HideRawOnly, p.AllowLikes, p.AllowComments, p.AllowFeedback, p.AllowGuestUpload,
        p.WatermarkView, p.WatermarkDownload,
        p.TeamId, team = p.TeamId is { } t ? teams.GetValueOrDefault(t) : null, p.TeamKit, p.AutoTag, p.AllowPlayerTags, p.FollowPlayers
    };
}
