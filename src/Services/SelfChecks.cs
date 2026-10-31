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

    DateTime directSeen;
    string directPeer = "";
    long directCount;

    /// <summary>True for addresses that are not on the internet: loopback, private, link-local, carrier-grade NAT (Tailscale) and IPv6 unique-local.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (b.Length == 4)
            return b[0] == 10 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
    }

    /// <summary>A connection that came straight from an internet address, not through a proxy on your network, means the app's port is published to the internet.</summary>
    public void NoteDirect(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip == null || IsPrivate(ip)) return;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        lock (gate) { directSeen = DateTime.UtcNow; directPeer = ip.ToString(); directCount++; }
    }

    public (string Peer, long Count, DateTime Time)? RecentDirect(DateTime now)
    {
        lock (gate) return directCount > 0 && now - directSeen <= Window ? (directPeer, directCount, directSeen) : null;
    }

    /// <summary>The newest untrusted proxy seen in the last 24 hours, or null.</summary>
    public (string Peer, long Count, DateTime Time)? Recent(DateTime now)
    {
        lock (gate) return count > 0 && now - lastSeen <= Window ? (peer, count, lastSeen) : null;
    }
}
