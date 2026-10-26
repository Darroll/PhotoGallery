namespace PhotoGallery;

/// <summary>Per-gallery rules shared by the viewer and download endpoints.</summary>
public static class GalleryRules
{
    public static int[] Sizes(Gallery g, AppConfig cfg)
    {
        var allowed = (g.AllowedSizes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToHashSet();
        return allowed.Count == 0 ? cfg.Sizes : cfg.Sizes.Where(allowed.Contains).ToArray();
    }

    public static string[] Formats(Gallery g)
    {
        var allowed = (g.AllowedFormats ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToHashSet();
        return allowed.Count == 0 ? ImageService.LosslessFormats : ImageService.LosslessFormats.Where(allowed.Contains).ToArray();
    }

    /// <summary>Orders galleries for the home page and the admin list. Galleries without an event date come last in date orders.</summary>
    public static List<Gallery> OrderGalleries(IEnumerable<Gallery> list, string order) => order switch
    {
        "event-desc" => list.OrderBy(g => g.EventDate == null).ThenByDescending(g => g.EventDate).ThenByDescending(g => g.CreatedAt).ToList(),
        "event-asc" => list.OrderBy(g => g.EventDate == null).ThenBy(g => g.EventDate).ThenByDescending(g => g.CreatedAt).ToList(),
        "name-asc" => list.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList(),
        "name-desc" => list.OrderByDescending(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList(),
        "created-asc" => list.OrderBy(g => g.CreatedAt).ToList(),
        "manual" => list.OrderBy(g => g.SortOrder).ThenByDescending(g => g.CreatedAt).ToList(),
        _ => list.OrderByDescending(g => g.CreatedAt).ToList()
    };

    public record FootageLink(string Label, string Url);

    public static List<FootageLink> Links(Gallery g)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<FootageLink>>(g.FootageLinks ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Keeps only http(s) links (never javascript: and friends), trimmed, at most 10.</summary>
    public static string CleanLinks(IEnumerable<FootageLink>? links)
    {
        var clean = new List<FootageLink>();
        foreach (var l in links ?? [])
        {
            var url = (l.Url ?? "").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) continue;
            var label = (l.Label ?? "").Trim();
            if (label.Length == 0) label = u.Host;
            clean.Add(new FootageLink(label.Length > 60 ? label[..60] : label, u.ToString()));
            if (clean.Count == 10) break;
        }
        return System.Text.Json.JsonSerializer.Serialize(clean);
    }

    public static bool IsExpired(Gallery g) => g.ExpiresAt != null && g.ExpiresAt <= DateTime.UtcNow;

    public static List<T> Sort<T>(IEnumerable<T> items, Gallery g, Func<T, DateTime> taken, Func<T, string> name, Func<T, DateTime> added)
    {
        Func<T, object> key = g.SortMode switch
        {
            GallerySort.Name => x => name(x).ToLowerInvariant(),
            GallerySort.Added => x => added(x),
            _ => x => taken(x)
        };
        var cmp = Comparer<object>.Default;
        var ordered = g.SortDesc
            ? items.OrderByDescending(key, cmp).ThenByDescending(x => name(x).ToLowerInvariant())
            : items.OrderBy(key, cmp).ThenBy(x => name(x).ToLowerInvariant());
        return ordered.ToList();
    }
}
