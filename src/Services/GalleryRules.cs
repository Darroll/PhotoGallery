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
