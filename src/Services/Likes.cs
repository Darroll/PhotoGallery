using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// The heart is the like. Every heart a visitor gives is kept in their own favorites list, and the like count on a photo
/// is the number of different people behind those hearts: one per signed-in account (whatever network they use), or one
/// per network address for anyone not signed in. An IPv6 address counts as its /64 network, so a device that rotates its
/// privacy address does not count twice. People sharing one address (a household) share one anonymous like; signing in
/// gives each person their own.
/// </summary>
public static class Likes
{
    /// <summary>Who a heart given right now belongs to.</summary>
    public static string IdentityKey(HttpContext ctx)
    {
        if (AuthApi.Sub(ctx) is { Length: > 0 } sub) return "u:" + sub;
        return "ip:" + Network(ActivityLog.ClientIp(ctx));
    }

    public static string Network(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return ip;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetworkV6) return a.ToString();
        var b = a.GetAddressBytes();
        Array.Clear(b, 8, 8);
        return new IPAddress(b).ToString() + "/64";
    }

    /// <summary>Number of different people who liked each photo of a gallery.</summary>
    public static async Task<Dictionary<int, int>> CountsAsync(AppDb db, int galleryId) =>
        await db.Favorites.AsNoTracking().Where(f => f.GalleryId == galleryId)
            .GroupBy(f => f.PhotoId)
            .Select(x => new { x.Key, n = x.Select(f => f.IdentityKey).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.n);

    public static async Task<int> CountAsync(AppDb db, int photoId) =>
        await db.Favorites.AsNoTracking().Where(f => f.PhotoId == photoId).Select(f => f.IdentityKey).Distinct().CountAsync();

    /// <summary>
    /// A visitor who signs in after liking photos as a guest keeps those likes, now counted under their account
    /// instead of their address, so nothing is counted twice.
    /// </summary>
    public static async Task AdoptAsync(AppDb db, HttpContext ctx, string visitorId)
    {
        if (visitorId == "" || AuthApi.Sub(ctx) is not { Length: > 0 } sub) return;
        var key = "u:" + sub;
        await db.Favorites.Where(f => f.VisitorId == visitorId && f.IdentityKey != key).ExecuteUpdateAsync(s => s.SetProperty(f => f.IdentityKey, key));
    }
}
