using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>What the last checks found out about one link.</summary>
/// <param name="Status">ok | suspect (failed once, will be tried again soon) | broken | unsure (the site gave an answer we cannot judge) | unchecked (not a public address)</param>
public record LinkHealthRecord(string Status, int Failures, DateTime LastChecked, int? Code);

/// <summary>
/// Finds links that no longer open. A link is only called broken after two failed checks in a row (so one bad moment
/// at the other site does not raise an alarm), or at once when the admin asks for a check. Sites that merely refuse
/// robots (403, 429, 5xx) are never called broken, and addresses on your own network are not contacted.
/// </summary>
public static class LinkHealth
{
    static readonly TimeSpan Normal = TimeSpan.FromHours(24), Retry = TimeSpan.FromHours(1);

    /// <summary>Asks a link whether it opens. Replaceable in tests.</summary>
    public static Func<Uri, Task<(SafeHttp.Reach reach, int? code)>> Probe = SafeHttp.ProbeAsync;

    static string Dir(AppConfig cfg) => Path.Combine(cfg.DataDir, "cache", "linkhealth");
    static string FileFor(AppConfig cfg, string url) => Path.Combine(Dir(cfg), ExternalLinkPreviews.Key(url) + ".json");

    public static LinkHealthRecord? Get(AppConfig cfg, string url)
    {
        try
        {
            var f = FileFor(cfg, url);
            return File.Exists(f) ? JsonSerializer.Deserialize<LinkHealthRecord>(File.ReadAllText(f)) : null;
        }
        catch { return null; }
    }

    public static bool IsDue(AppConfig cfg, string url)
    {
        var r = Get(cfg, url);
        return r == null || DateTime.UtcNow - r.LastChecked > (r.Status == "suspect" ? Retry : Normal);
    }

    /// <summary>What to ask: a YouTube link is checked through its oEmbed answer (a removed video is a 404 there), anything else directly.</summary>
    static Uri? Target(string url)
    {
        if (GalleryRules.YouTubeId(url) is { } vid)
            return new Uri("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=" + vid));
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;
    }

    public static async Task<LinkHealthRecord> CheckAsync(AppConfig cfg, string url, bool manual)
    {
        var old = Get(cfg, url);
        var target = Target(url);
        var (reach, code) = target == null ? (SafeHttp.Reach.NotChecked, null) : await Probe(target);

        LinkHealthRecord now;
        switch (reach)
        {
            case SafeHttp.Reach.Ok:
                now = new("ok", 0, DateTime.UtcNow, code); break;
            case SafeHttp.Reach.Gone or SafeHttp.Reach.Unreachable:
                var failures = (old?.Failures ?? 0) + 1;
                now = new(manual || failures >= 2 ? "broken" : "suspect", failures, DateTime.UtcNow, code); break;
            case SafeHttp.Reach.NotChecked:
                now = new("unchecked", 0, DateTime.UtcNow, null); break;
            default:
                now = new("unsure", 0, DateTime.UtcNow, code); break;
        }
        Directory.CreateDirectory(Dir(cfg));
        await File.WriteAllTextAsync(FileFor(cfg, url), JsonSerializer.Serialize(now));
        return now;
    }

    /// <summary>The links of a gallery, paired with what is known about each.</summary>
    public static object Report(AppConfig cfg, Gallery g) =>
        GalleryRules.Links(g).Select(l => Get(cfg, l.Url) is { } r
            ? new { url = l.Url, status = r.Status, lastChecked = (DateTime?)r.LastChecked, code = r.Code }
            : new { url = l.Url, status = "new", lastChecked = (DateTime?)null, code = (int?)null }).ToList();
}

/// <summary>Checks every gallery's links in the background, one at a time, a few minutes after start and then regularly.</summary>
public class LinkHealthService(IServiceScopeFactory scopes, AppConfig cfg, ILogger<LinkHealthService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Link check loop error"); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        if (!scope.ServiceProvider.GetRequiredService<SettingsService>().LinkPreviews.Enabled) return;
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;
        var galleries = await db.Galleries.AsNoTracking().Where(g => g.ExpiresAt == null || g.ExpiresAt > now).ToListAsync(ct);
        foreach (var url in galleries.SelectMany(GalleryRules.Links).Select(l => l.Url).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (LinkHealth.IsDue(cfg, url)) await LinkHealth.CheckAsync(cfg, url, manual: false);
        }
    }
}
