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

    /// <summary>
    /// A link on a gallery. <c>Category</c> is "video" (YouTube links, shown in the Videos grid above the photos) or
    /// "external" (shown below the gallery). <c>Button</c> also shows it as a button under the title. Links saved before
    /// categories existed have neither: YouTube links count as videos, other links as external with a button, as before.
    /// </summary>
    public record FootageLink(string Label, string Url, string? Category = null, bool? Button = null);

    public static string CategoryOf(FootageLink l) => l.Category != "external" && YouTubeId(l.Url) != null ? "video" : "external";

    public static bool ButtonOf(FootageLink l) => l.Button ?? (l.Category == null && YouTubeId(l.Url) == null);

    static readonly System.Text.RegularExpressions.Regex YtId = new("^[A-Za-z0-9_-]{11}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The 11-character YouTube video id in a watch, youtu.be, embed, shorts or live link; null for anything else.</summary>
    public static string? YouTubeId(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        string? id = null;
        if (host == "youtu.be") id = u.AbsolutePath.Trim('/').Split('/')[0];
        else if (host is "youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtube-nocookie.com")
        {
            var parts = u.AbsolutePath.Trim('/').Split('/');
            if (parts.Length >= 2 && (parts[0] is "embed" or "shorts" or "live" or "v")) id = parts[1];
            else if (parts.Length >= 1 && parts[0] == "watch")
                id = System.Web.HttpUtility.ParseQueryString(u.Query)["v"];
        }
        return id != null && YtId.IsMatch(id) ? id : null;
    }

    public static List<FootageLink> Links(Gallery g)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<FootageLink>>(g.FootageLinks ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Keeps only http(s) links (never javascript: and friends), trimmed, at most 30.</summary>
    public static string CleanLinks(IEnumerable<FootageLink>? links)
    {
        var clean = new List<FootageLink>();
        foreach (var l in links ?? [])
        {
            var url = (l.Url ?? "").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) continue;
            var label = (l.Label ?? "").Trim();
            if (label.Length == 0) label = u.Host;
            var link = new FootageLink(label.Length > 60 ? label[..60] : label, u.ToString(), l.Category, l.Button);
            clean.Add(link with { Category = CategoryOf(link), Button = ButtonOf(link) });
            if (clean.Count == 30) break;
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
