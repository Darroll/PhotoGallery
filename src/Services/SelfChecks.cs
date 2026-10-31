using System.Net;

namespace PhotoGallery;

/// <summary>Can the app read the photo library and write its own data folder? The most likely first failure on a new install.</summary>
public static class FolderAccess
{
    public record Result(bool LibraryReadable, string? LibraryError, bool DataWritable, string? DataError);

    public static Result Probe(AppConfig cfg)
    {
        bool libOk = false; string? libErr = null;
        if (!Directory.Exists(cfg.LibraryRoot)) libErr = "missing";
        else
        {
            try { _ = Directory.EnumerateFileSystemEntries(cfg.LibraryRoot).FirstOrDefault(); libOk = true; }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { libErr = e.Message; }
        }

        bool dataOk = false; string? dataErr = null;
        var probe = Path.Combine(cfg.DataDir, ".write-test-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, "x"); File.Delete(probe); dataOk = true; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { dataErr = e.Message; }
        return new Result(libOk, libErr, dataOk, dataErr);
    }
}

/// <summary>Notices requests that carry proxy headers from an address outside TRUSTED_PROXIES. Those headers are ignored, so statistics and rate limits see the proxy instead of the visitor.</summary>
public class ProxyWatch
{
    static readonly TimeSpan Window = TimeSpan.FromHours(24);
    readonly object gate = new();
    DateTime lastSeen;
    string peer = "";
    long count;

    public void Note(HttpContext ctx, bool peerTrusted)
    {
        if (peerTrusted) return;
        var h = ctx.Request.Headers;
        if (!h.ContainsKey("X-Forwarded-For") && !h.ContainsKey("CF-Connecting-IP")) return;
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        lock (gate) { lastSeen = DateTime.UtcNow; peer = ip?.ToString() ?? "?"; count++; }
    }

    /// <summary>The newest untrusted proxy seen in the last 24 hours, or null.</summary>
    public (string Peer, long Count, DateTime Time)? Recent(DateTime now)
    {
        lock (gate) return count > 0 && now - lastSeen <= Window ? (peer, count, lastSeen) : null;
    }
}
