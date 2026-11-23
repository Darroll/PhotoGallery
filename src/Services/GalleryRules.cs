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

    public const int MaxSeason = 40;

    /// <summary>The day a gallery counts from when it is placed in a season: its event date, or the day it was made.</summary>
    public static DateTime SeasonDate(Gallery g) => g.EventDate ?? g.CreatedAt;

    /// <summary>
    /// The season a gallery is worked out to be in from its date, with the home page's season setting:
    /// "Fall 2026" / "Spring 2027" (fall-spring: August to December is fall), "2026-27 season" (school-year: August to July),
    /// "2026" (year). Empty when seasons are off.
    /// </summary>
    public static string AutoSeason(Gallery g, string mode)
    {
        var d = SeasonDate(g);
        return mode switch
        {
            "off" => "",
            "year" => d.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "school-year" => d.Month >= 8 ? $"{d.Year}-{(d.Year + 1) % 100:00} season" : $"{d.Year - 1}-{d.Year % 100:00} season",
            _ => d.Month >= 8 ? $"Fall {d.Year}" : $"Spring {d.Year}"
        };
    }

    /// <summary>
    /// The season heading a gallery is listed under: the one typed on the gallery, else the season of its team (the team
    /// season it picked, "Fall 2026"), else the automatic one from the date. Empty when seasons are off.
    /// </summary>
    public static string SeasonOf(Gallery g, string mode, string? teamSeason = null) =>
        mode == "off" ? "" : CleanSeason(g.Season) is { Length: > 0 } own ? own : CleanSeason(teamSeason) is { Length: > 0 } ts ? ts : AutoSeason(g, mode);

    /// <summary>The season a gallery gets when its Season box is empty: its team's season, else the one from the date.</summary>
    public static string DefaultSeason(Gallery g, string mode, string? teamSeason = null) =>
        mode == "off" ? "" : CleanSeason(teamSeason) is { Length: > 0 } ts ? ts : AutoSeason(g, mode);

    public static string CleanSeason(string? s) => new string((s ?? "").Where(c => !char.IsControl(c)).Take(MaxSeason).ToArray()).Trim();

    /// <summary>
    /// Galleries grouped by season, newest season first (the season whose latest gallery is newest), keeping the given order inside
    /// each season. Galleries with the same season text (any letter case) share a group. With seasons off, one group with an empty name.
    /// </summary>
    public static List<(string Season, List<Gallery> Galleries)> GroupBySeason(IEnumerable<Gallery> ordered, string mode, Func<Gallery, string?>? teamSeason = null)
    {
        var list = ordered.ToList();
        if (mode == "off") return [("", list)];
        string Of(Gallery g) => SeasonOf(g, mode, teamSeason?.Invoke(g));
        return list.GroupBy(Of, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Max(SeasonDate))
            .Select(x => (Of(x.First()), x.ToList())).ToList();
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
    public record FootageLink(string Label, string Url, string? Category = null, bool? Button = null, string? Note = null);

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

    /// <summary>The gallery's own links plus the links of every section.</summary>
    public static List<FootageLink> AllLinks(Gallery g) => Links(g).Concat(Sections(g).SelectMany(s => s.Links ?? new())).ToList();

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
            var note = (l.Note ?? "").Trim();
            var link = new FootageLink(label.Length > 60 ? label[..60] : label, u.ToString(), l.Category, l.Button, note.Length == 0 ? null : note.Length > 200 ? note[..200] : note);
            clean.Add(link with { Category = CategoryOf(link), Button = ButtonOf(link) });
            if (clean.Count == 30) break;
        }
        return System.Text.Json.JsonSerializer.Serialize(clean);
    }

    /// <summary>A sub folder of the gallery folder shown under its own heading. <c>Folder</c> "" is the photos that sit directly in the gallery folder.</summary>
    public record GallerySection(string Folder, string Title, List<FootageLink>? Links = null);

    public static List<GallerySection> Sections(Gallery g)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<GallerySection>>(g.Sections ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { return new(); }
    }

    /// <summary>One entry per sub folder name (any case), a title for each, links cleaned like gallery links, at most 50.</summary>
    public static string CleanSections(IEnumerable<GallerySection>? sections)
    {
        var clean = new List<GallerySection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sections ?? [])
        {
            var folder = (s.Folder ?? "").Trim().Trim('/');
            if (folder.Contains('/') || folder.Contains('\\') || folder == ".." || folder == ".") continue;
            if (!seen.Add(folder)) continue;
            var title = (s.Title ?? "").Trim();
            if (title.Length == 0) title = Pretty(folder);
            var links = Links(new Gallery { FootageLinks = CleanLinks(s.Links) });
            clean.Add(new GallerySection(folder, title.Length > 80 ? title[..80] : title, links));
            if (clean.Count == 50) break;
        }
        return System.Text.Json.JsonSerializer.Serialize(clean);
    }

    public static string Pretty(string folder) => folder.Length == 0 ? "Other photos" : folder.Replace('_', ' ').Trim();

    /// <summary>The first sub folder of the gallery folder that holds this file; "" for a file directly in the gallery folder.</summary>
    public static string SectionOf(Gallery g, string relPath)
    {
        var root = (g.Folder ?? "").Trim('/');
        var rest = relPath.Replace('\\', '/').TrimStart('/');
        if (root.Length > 0)
        {
            if (!rest.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return "";
            rest = rest[(root.Length + 1)..];
        }
        var i = rest.IndexOf('/');
        return i < 0 ? "" : rest[..i];
    }

    /// <summary>
    /// The sections to show, top to bottom: the ones the admin set up, in their order, then any other sub folder that holds photos
    /// (by name, so Game 2 comes before Game 10), then loose photos. Empty when no sections are set up, which keeps the flat grid.
    /// </summary>
    public static List<GallerySection> Layout(Gallery g, IEnumerable<string> foldersWithPhotos)
    {
        var configured = Sections(g);
        if (configured.Count == 0) return new();
        var present = foldersWithPhotos.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = configured.Where(s => s.Folder.Length > 0).ToList();
        var known = result.Select(s => s.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.AddRange(present.Where(f => f.Length > 0 && !known.Contains(f)).OrderBy(f => f, new NaturalComparer()).Select(f => new GallerySection(f, Pretty(f))));
        if (present.Contains("")) result.Add(new GallerySection("", configured.FirstOrDefault(s => s.Folder.Length == 0)?.Title is { Length: > 0 } t ? t : "Other photos", configured.FirstOrDefault(s => s.Folder.Length == 0)?.Links));
        return result;
    }

    /// <summary>Compares names so numbers inside them count by value: "Game 2" before "Game 10".</summary>
    public class NaturalComparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    var c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    /// <summary>Id of the cover photo when it is kept out of the photo grid (cover only, or banner); 0 when every photo shows. Ids start at 1.</summary>
    public static int HiddenCoverId(Gallery g) => g.CoverMode != CoverMode.WithPhotos && g.CoverPhotoId is { } id ? id : 0;

    /// <summary>The day or days of an event: "March 14, 2026", "March 14-16, 2026", "March 30 - April 2, 2026" or across a year end.</summary>
    public static string DateRangeText(DateTime? start, DateTime? end)
    {
        if (start is not { } s) return "";
        var c = System.Globalization.CultureInfo.InvariantCulture;
        if (end is not { } e || e.Date <= s.Date) return s.ToString("MMMM d, yyyy", c);
        if (s.Year != e.Year) return $"{s.ToString("MMMM d, yyyy", c)} - {e.ToString("MMMM d, yyyy", c)}";
        if (s.Month != e.Month) return $"{s.ToString("MMMM d", c)} - {e.ToString("MMMM d, yyyy", c)}";
        return $"{s.ToString("MMMM d", c)}-{e.Day}, {s.Year}";
    }

    /// <summary>Every "MMMM yyyy" the event touches, so a search for the month of any of its days finds it.</summary>
    public static IEnumerable<string> MonthsSpanned(Gallery g)
    {
        if (g.EventDate is not { } s) yield break;
        var last = (g.EventEndDate ?? s).Date;
        var m = new DateTime(s.Year, s.Month, 1);
        for (int i = 0; i < 24 && m <= last; i++, m = m.AddMonths(1)) yield return m.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Does the event (its first to its last day) overlap the days from..to inclusive? Either end may be open.</summary>
    public static bool EventOverlaps(Gallery g, DateTime? from, DateTime? to)
    {
        if (from == null && to == null) return true;
        if (g.EventDate is not { } s) return false;
        var firstDay = s.Date; var lastDay = (g.EventEndDate ?? s).Date;
        return (to == null || firstDay <= to.Value.Date) && (from == null || lastDay >= from.Value.Date);
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

    /// <summary>The address to give people for a gallery, or for one of its sections. Uses the address set in Setup when there is one, otherwise the one this request came in on.</summary>
    public static string ShareUrl(Gallery g, string? section, string siteUrl, string requestOrigin)
    {
        var origin = (siteUrl.Length > 0 ? siteUrl : requestOrigin).TrimEnd('/');
        var url = $"{origin}/g/{Uri.EscapeDataString(g.Slug)}";
        var match = string.IsNullOrWhiteSpace(section) ? null : Sections(g).FirstOrDefault(s => string.Equals(s.Folder, section, StringComparison.OrdinalIgnoreCase));
        return match == null ? url : $"{url}#s={Uri.EscapeDataString(match.Folder)}";
    }
}
