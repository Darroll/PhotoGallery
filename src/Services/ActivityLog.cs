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

    public StatsSettings Sanitized() => new() { Enabled = Enabled, RetentionDays = Math.Clamp(RetentionDays, 0, 3650) };
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
        var cf = ctx.Request.Headers["CF-Connecting-IP"].ToString();
        if (!string.IsNullOrEmpty(cf) && IPAddress.TryParse(cf.Trim(), out var cfIp)) return Normalize(cfIp);
        var ip = ctx.Connection.RemoteIpAddress;
        return ip == null ? "" : Normalize(ip);
    }

    static string Normalize(IPAddress ip) => (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    public void Record(HttpContext ctx, string kind, int? galleryId = null, int? photoId = null, string detail = "")
    {
        if (!settings.Stats.Enabled || AccessService.IsAdmin(ctx)) return;
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var country = ctx.Request.Headers["CF-IPCountry"].ToString();
        queue.Writer.TryWrite(new Visit
        {
            Time = DateTime.UtcNow, Kind = kind, GalleryId = galleryId, PhotoId = photoId,
            Ip = ClientIp(ctx), Country = country.Length == 2 ? country.ToUpperInvariant() : "",
            UserAgent = ua.Length > 300 ? ua[..300] : ua, IsBot = LooksLikeBot(ua), Detail = detail
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
