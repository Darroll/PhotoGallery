using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>A label the admin can put on photos. Tags are global (one list for the whole site) and public: they show on photos and can be searched.</summary>
public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Colour as #rrggbb, or empty to let the site pick one from the name.</summary>
    public string Color { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Links a tag to a photo.</summary>
public class PhotoTag
{
    public int PhotoId { get; set; }
    public int TagId { get; set; }
}

/// <summary>Links a tag to a whole gallery.</summary>
public class GalleryTag
{
    public int GalleryId { get; set; }
    public int TagId { get; set; }
}

public static class Tags
{
    public const int MaxLength = 40;

    /// <summary>A tag name as the admin typed it: spaces collapsed, leading # dropped, clipped.</summary>
    public static string Clean(string? s)
    {
        var t = Regex.Replace((s ?? "").Trim().TrimStart('#').Trim(), @"\s+", " ");
        return t.Length > MaxLength ? t[..MaxLength] : t;
    }

    /// <summary>A colour the admin picked: #rrggbb in lower case, or empty. Anything else is rejected (null).</summary>
    public static string? CleanColor(string? s)
    {
        var t = (s ?? "").Trim().ToLowerInvariant();
        return t.Length == 0 || Regex.IsMatch(t, "^#[0-9a-f]{6}$") ? t : null;
    }

    /// <summary>The colours the admin set for these tag names, keyed by lower-case name. Tags left on automatic are not listed.</summary>
    public static async Task<Dictionary<string, string>> ColorsAsync(AppDb db, IEnumerable<string> names)
    {
        var lower = names.Select(n => n.ToLower()).Distinct().ToList();
        if (lower.Count == 0) return new();
        var rows = await db.Tags.AsNoTracking().Where(t => t.Color != "" && lower.Contains(t.Name.ToLower())).Select(t => new { t.Name, t.Color }).ToListAsync();
        return rows.ToDictionary(r => r.Name.ToLowerInvariant(), r => r.Color);
    }

    /// <summary>The tag names of each photo in the list, sorted.</summary>
    public static async Task<Dictionary<int, List<string>>> NamesByPhotoAsync(AppDb db, List<int> photoIds)
    {
        if (photoIds.Count == 0) return new();
        var rows = await db.PhotoTags.AsNoTracking().Where(pt => photoIds.Contains(pt.PhotoId))
            .Join(db.Tags, pt => pt.TagId, t => t.Id, (pt, t) => new { pt.PhotoId, t.Name })
            .ToListAsync();
        return rows.GroupBy(r => r.PhotoId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).OrderBy(n => n).ToList());
    }

    /// <summary>The tag names of each gallery in the list, sorted.</summary>
    public static async Task<Dictionary<int, List<string>>> NamesByGalleryAsync(AppDb db, List<int> galleryIds)
    {
        if (galleryIds.Count == 0) return new();
        var rows = await db.GalleryTags.AsNoTracking().Where(gt => galleryIds.Contains(gt.GalleryId))
            .Join(db.Tags, gt => gt.TagId, t => t.Id, (gt, t) => new { gt.GalleryId, t.Name })
            .ToListAsync();
        return rows.GroupBy(r => r.GalleryId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).OrderBy(n => n).ToList());
    }
}
