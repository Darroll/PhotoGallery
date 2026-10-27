using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>One recorded visitor action (gallery view, photo view, download, unlock, ...).</summary>
public class Visit
{
    public long Id { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
    /// <summary>view_gallery | view_photo | download | zip | unlock_ok | unlock_fail | share_open | share_invalid | upload</summary>
    public string Kind { get; set; } = "";
    public int? GalleryId { get; set; }
    public int? PhotoId { get; set; }
    public string Ip { get; set; } = "";
    /// <summary>Anonymous per-browser id from the first-party cookie (empty for bots, or when the cookie is switched off).</summary>
    public string VisitorId { get; set; } = "";
    public string Country { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public bool IsBot { get; set; }
    public string Detail { get; set; } = "";
}

public class StatsSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>0 keeps records forever.</summary>
    public int RetentionDays { get; set; } = 180;
    /// <summary>Count visits made while signed in as admin (off by default; handy for testing).</summary>
    public bool IncludeAdmin { get; set; }
    /// <summary>Give each browser an anonymous id cookie so two people on one IP and device type can be told apart.</summary>
    public bool UseCookie { get; set; } = true;

    public StatsSettings Sanitized() => new() { Enabled = Enabled, RetentionDays = Math.Clamp(RetentionDays, 0, 3650), IncludeAdmin = IncludeAdmin, UseCookie = UseCookie };
}

/// <summary>Collects visitor activity without slowing requests down: records are queued and written in batches.</summary>
public class ActivityLog(IServiceScopeFactory scopes, SettingsService settings, ILogger<ActivityLog> log) : BackgroundService
{
    readonly Channel<Visit> queue = Channel.CreateBounded<Visit>(new BoundedChannelOptions(20000) { FullMode = BoundedChannelFullMode.DropOldest });

    static readonly Regex BotRx = new(
        @"bot\b|bot/|bots|crawl|spider|slurp|preview|facebookexternalhit|headless|curl/|wget|python|go-http|httpclient|java/|okhttp|axios|scrapy|monitor|uptime|lighthouse|pingdom|libwww|apache-http|node-fetch|undici|postman|whatsapp|telegram|discord|slack|linkedin|twitter|embedly|validator|scanner|nmap|masscan|zgrab|censys|shodan|netcraft|semrush|ahrefs|mj12|dotbot|petalbot|bytespider|gptbot|claudebot|ccbot|amazonbot|yandex|baidu|duckduck|bingpreview|google-|googlebot|applebot",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeBot(string ua) => string.IsNullOrWhiteSpace(ua) || ua.Length < 12 || BotRx.IsMatch(ua);

    /// <summary>The visitor's address. A Cloudflare header wins when present (proxy enabled); otherwise the forwarded/remote address.</summary>
    public static string ClientIp(HttpContext ctx)
    {
        var cf = ctx.Items[TrustedPeerKey] is true ? ctx.Request.Headers["CF-Connecting-IP"].ToString() : "";
        if (!string.IsNullOrEmpty(cf) && IPAddress.TryParse(cf.Trim(), out var cfIp)) return Normalize(cfIp);
        var ip = ctx.Connection.RemoteIpAddress;
        return ip == null ? "" : Normalize(ip);
    }

    static string Normalize(IPAddress ip) => (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    public const string CookieName = "pgv";
    public const string TrustedPeerKey = "pg.trustedPeer";
    static readonly Regex VidRx = new("^[A-Za-z0-9_-]{16,32}$", RegexOptions.Compiled);

    /// <summary>Reads the anonymous visitor id, issuing a new one (2 years) when the browser has none.</summary>
    /// <summary>The visitor id if the browser already has one, otherwise empty (never issues a cookie).</summary>
    public static string ReadVisitorId(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue(CookieName, out var v) && v != null && VidRx.IsMatch(v) ? v : "";

    /// <summary>The visitor id, issuing a new cookie when needed (used by favorites).</summary>
    public static string EnsureVisitorId(HttpContext ctx) => VisitorCookie(ctx);

    static string VisitorCookie(HttpContext ctx)
    {
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var existing) && existing != null && VidRx.IsMatch(existing)) return existing;
        var id = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        if (!ctx.Response.HasStarted)
            ctx.Response.Cookies.Append(CookieName, id, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddYears(2), Path = "/", IsEssential = true
            });
        return id;
    }

    public void Record(HttpContext ctx, string kind, int? galleryId = null, int? photoId = null, string detail = "")
    {
        var st = settings.Stats;
        if (!st.Enabled || (AccessService.IsAdmin(ctx) && !st.IncludeAdmin)) return;
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var country = ctx.Request.Headers["CF-IPCountry"].ToString();
        var isBot = LooksLikeBot(ua);
        var vid = isBot || !st.UseCookie ? "" : VisitorCookie(ctx);
        queue.Writer.TryWrite(new Visit
        {
            Time = DateTime.UtcNow, Kind = kind, GalleryId = galleryId, PhotoId = photoId,
            Ip = ClientIp(ctx), VisitorId = vid, Country = country.Length == 2 ? country.ToUpperInvariant() : "",
            UserAgent = ua.Length > 300 ? ua[..300] : ua, IsBot = isBot, Detail = detail
        });
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = Task.Run(() => PurgeLoop(ct), ct);
        var batch = new List<Visit>();
        try
        {
            while (await queue.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < 200 && queue.Reader.TryRead(out var v)) batch.Add(v);
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                    db.Visits.AddRange(batch);
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not save {Count} activity records", batch.Count); }
                await Task.Delay(500, ct);   // coalesce bursts into larger batches
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task PurgeLoop(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), ct);
            while (!ct.IsCancellationRequested)
            {
                var days = settings.Stats.RetentionDays;
                if (days > 0)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                        var cutoff = DateTime.UtcNow.AddDays(-days);
                        var n = await db.Visits.Where(v => v.Time < cutoff).ExecuteDeleteAsync(ct);
                        if (n > 0) log.LogInformation("Removed {Count} activity records older than {Days} days", n, days);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Activity purge failed"); }
                }
                await Task.Delay(TimeSpan.FromHours(6), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
