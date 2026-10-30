using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetVips;

namespace PhotoGallery;

public class LinkPreviewSettings
{
    /// <summary>Let the server fetch a title and picture for External links. Off means nothing is fetched from those sites.</summary>
    public bool Enabled { get; set; } = true;
    public LinkPreviewSettings Sanitized() => new() { Enabled = Enabled };
}

/// <summary>What the server knows about the page behind an External link.</summary>
public record ExternalPreview(string? Title, bool HasImage, DateTime FetchedAt, bool Failed);

/// <summary>
/// Title and picture for External links. The server fetches the page once (through <see cref="SafeHttp"/>), reads its
/// title and share picture, shrinks the picture and keeps both in DATA_DIR/cache/lpreview. Visitors only ever load the
/// cached copy from the gallery's own address, so they never contact the other site until they click the link.
/// </summary>
public static class ExternalLinkPreviews
{
    static readonly TimeSpan GoodFor = TimeSpan.FromDays(7), RetryFailedAfter = TimeSpan.FromHours(6), MinGap = TimeSpan.FromMinutes(10);
    const long MaxPage = 512 * 1024, MaxImage = 3 * 1024 * 1024;
    const long MaxPixels = 150_000_000;
    static readonly ConcurrentDictionary<string, DateTime> lastTry = new();

    /// <summary>Downloads a page or picture; null when it cannot be had safely. Replaceable in tests.</summary>
    public static Func<Uri, long, string, Task<SafeHttp.Response?>> Fetch = (uri, max, accept) => SafeHttp.GetAsync(uri, max, accept, truncate: accept.StartsWith("text/html"));

    public static string Key(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim()));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    static string Dir(AppConfig cfg) => Path.Combine(cfg.DataDir, "cache", "lpreview");
    public static string MetaPath(AppConfig cfg, string key) => Path.Combine(Dir(cfg), key + ".json");
    public static string ImagePath(AppConfig cfg, string key) => Path.Combine(Dir(cfg), key + ".webp");

    public static ExternalPreview? Cached(AppConfig cfg, string url)
    {
        try
        {
            var f = MetaPath(cfg, Key(url));
            return File.Exists(f) ? JsonSerializer.Deserialize<ExternalPreview>(File.ReadAllText(f)) : null;
        }
        catch { return null; }
    }

    /// <summary>The preview as the pages show it: null when there is none worth showing.</summary>
    public static object? ForPage(AppConfig cfg, string url)
    {
        var p = Cached(cfg, url);
        if (p == null || p.Failed || (p.Title == null && !p.HasImage)) return null;
        return new { title = p.Title, image = p.HasImage ? $"/lp/{Key(url)}.webp?v={p.FetchedAt.Ticks}" : null };
    }

    public static bool NeedsRefresh(AppConfig cfg, string url)
    {
        var key = Key(url);
        if (lastTry.TryGetValue(key, out var t) && DateTime.UtcNow - t < MinGap) return false;
        var p = Cached(cfg, url);
        return p == null || DateTime.UtcNow - p.FetchedAt > (p.Failed ? RetryFailedAfter : GoodFor);
    }

    /// <summary>Fetches again and replaces the cached preview. A failed fetch keeps the earlier good preview, if there was one.</summary>
    public static async Task<bool> RefreshAsync(AppConfig cfg, string url)
    {
        var key = Key(url);
        lastTry[key] = DateTime.UtcNow;
        var old = Cached(cfg, url);
        var fresh = await BuildAsync(cfg, key, url);
        if (fresh.Failed && old is { Failed: false }) return false;
        Directory.CreateDirectory(Dir(cfg));
        if (!fresh.HasImage && File.Exists(ImagePath(cfg, key))) File.Delete(ImagePath(cfg, key));
        await File.WriteAllTextAsync(MetaPath(cfg, key), JsonSerializer.Serialize(fresh));
        return !fresh.Failed;
    }

    /// <summary>Starts a background fetch for the links that have no fresh preview. Never throws, never waits.</summary>
    public static void EnsureInBackground(AppConfig cfg, IEnumerable<string> urls)
    {
        var todo = urls.Where(u => NeedsRefresh(cfg, u)).Distinct().ToList();
        if (todo.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var u in todo)
            {
                try { await RefreshAsync(cfg, u); } catch { }
            }
        });
    }

    static async Task<ExternalPreview> BuildAsync(AppConfig cfg, string key, string url)
    {
        var failed = new ExternalPreview(null, false, DateTime.UtcNow, true);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return failed;
        var page = await Fetch(uri, MaxPage, "text/html,application/xhtml+xml");
        if (page == null || !page.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return failed;

        var (title, image) = ReadPage(Encoding.UTF8.GetString(page.Body), page.FinalUri);
        var hasImage = false;
        if (image != null)
        {
            var pic = await Fetch(image, MaxImage, "image/webp,image/png,image/jpeg,image/gif");
            if (pic != null && pic.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !pic.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
            {
                var webp = Shrink(pic.Body);
                if (webp != null)
                {
                    Directory.CreateDirectory(Dir(cfg));
                    await File.WriteAllBytesAsync(ImagePath(cfg, key), webp);
                    hasImage = true;
                }
            }
        }
        // a page with neither a title nor a picture is not worth a card; try again later
        return title == null && !hasImage ? failed : new ExternalPreview(title, hasImage, DateTime.UtcNow, false);
    }

    /// <summary>Re-encodes a downloaded picture as a small WebP, which also discards anything that is not really an image.</summary>
    static byte[]? Shrink(byte[] bytes)
    {
        try
        {
            using (var probe = Image.NewFromBuffer(bytes))
                if ((long)probe.Width * probe.Height > MaxPixels) return null;
            using var img = Image.ThumbnailBuffer(bytes, 640, height: 640, size: Enums.Size.Down);
            return img.WebpsaveBuffer(q: 82);
        }
        catch { return null; }
    }

    static readonly Regex MetaTag = new("<meta\\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Attr = new("([a-zA-Z:-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.Compiled);
    static readonly Regex TitleTag = new("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The page title and share picture, from Open Graph or Twitter tags, falling back to the title element.</summary>
    public static (string? title, Uri? image) ReadPage(string html, Uri baseUri)
    {
        var metas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MetaTag.Matches(html))
        {
            string? name = null, content = null;
            foreach (Match a in Attr.Matches(m.Value))
            {
                var k = a.Groups[1].Value.ToLowerInvariant();
                var v = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value;
                if (k is "property" or "name") name = v;
                else if (k == "content") content = v;
            }
            if (name != null && content != null) metas.TryAdd(name.Trim(), WebUtility.HtmlDecode(content).Trim());
        }
        string? Pick(params string[] names) => names.Select(n => metas.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var title = Pick("og:title", "twitter:title");
        if (title == null && TitleTag.Match(html) is { Success: true } t) title = Regex.Replace(WebUtility.HtmlDecode(t.Groups[1].Value), "\\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(title)) title = null;
        else if (title.Length > 120) title = title[..120];

        Uri? image = null;
        if (Pick("og:image", "og:image:url", "twitter:image", "twitter:image:src") is { } src
            && Uri.TryCreate(baseUri, src, out var abs) && abs.Scheme is "http" or "https") image = abs;
        return (title, image);
    }
}
