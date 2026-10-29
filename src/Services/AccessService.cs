using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public record AccessInfo(bool Allowed, bool Admin, ShareLink? Link, bool NeedsPassword, bool Expired = false, bool NeedsSignIn = false);

public class AccessService(IDataProtectionProvider dpp, AppDb db)
{
    record Grant(int G, int S, long E, string St);
    readonly IDataProtector protector = dpp.CreateProtector("gallery-grant.v1");
    static string CookieName(int galleryId) => $"pg_{galleryId}";

    public const string AdminRole = "admin";

    /// <summary>Only the admin role counts; visitors who signed in through single sign-on are not admins.</summary>
    public static bool IsAdmin(HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true && ctx.User.IsInRole(AdminRole);

    public async Task<AccessInfo> CheckAsync(HttpContext ctx, Gallery g)
    {
        if (IsAdmin(ctx)) return new(true, true, null, false);
        if (GalleryRules.IsExpired(g)) return new(false, false, null, false, true);
        // people only: no password or share link opens these; anonymous visitors are asked to sign in
        if (g.Visibility == Visibility.Restricted)
        {
            if (await GalleryPeople.AllowsAsync(db, g, ctx.User)) return new(true, false, null, false);
            return new(false, false, null, false, false, NeedsSignIn: ctx.User.Identity?.IsAuthenticated != true);
        }
        if (g.Visibility != Visibility.Protected) return new(true, false, null, false);

        if (ctx.Request.Cookies.TryGetValue(CookieName(g.Id), out var raw) && !string.IsNullOrEmpty(raw))
        {
            try
            {
                var grant = JsonSerializer.Deserialize<Grant>(protector.Unprotect(raw));
                if (grant != null && grant.G == g.Id && grant.St == g.AccessStamp
                    && grant.E > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    if (grant.S == 0) return new(true, false, null, false);
                    var link = await db.ShareLinks.FirstOrDefaultAsync(l => l.Id == grant.S && l.GalleryId == g.Id);
                    if (link != null && link.IsValid(DateTime.UtcNow)) return new(true, false, link, false);
                }
            }
            catch { /* tampered or expired key: treat as no grant */ }
        }
        return new(false, false, null, !string.IsNullOrEmpty(g.PasswordHash));
    }

    public void Issue(HttpContext ctx, Gallery g, ShareLink? link)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        if (link?.ExpiresAt is { } le && new DateTimeOffset(DateTime.SpecifyKind(le, DateTimeKind.Utc)) < expires)
            expires = new DateTimeOffset(DateTime.SpecifyKind(le, DateTimeKind.Utc));
        var payload = JsonSerializer.Serialize(new Grant(g.Id, link?.Id ?? 0, expires.ToUnixTimeSeconds(), g.AccessStamp));
        ctx.Response.Cookies.Append(CookieName(g.Id), protector.Protect(payload), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Expires = expires,
            Path = "/"
        });
    }

    /// <summary>Counts a download against a share link's limit. Returns false if the limit is used up.</summary>
    public async Task<bool> TryConsumeDownloadAsync(AccessInfo access)
    {
        if (access.Link == null) return true;
        var link = await db.ShareLinks.FirstAsync(l => l.Id == access.Link.Id);
        if (link.DownloadsExhausted) return false;
        link.DownloadCount++;
        await db.SaveChangesAsync();
        return true;
    }
}
