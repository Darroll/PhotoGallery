using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace PhotoGallery;

/// <summary>Visitor sign-in through single sign-on (see Sso.cs).</summary>
public static class AuthApi
{
    /// <summary>The identity provider's stable user id of the signed-in visitor, or null (anonymous, or the password admin).</summary>
    public static string? Sub(HttpContext ctx) =>
        ctx.User.Identity?.IsAuthenticated == true ? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value : null;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/me", async (HttpContext ctx, SsoConfig sso, AppDb db) =>
        {
            var signedIn = ctx.User.Identity?.IsAuthenticated == true;
            // preferences exist only for accounts with a provider id (not for the password admin), and follow the person across devices
            var sub = Sub(ctx);
            var prefs = sub == null ? null : UserPreferences.Parse(await db.Users.AsNoTracking().Where(u => u.Sub == sub).Select(u => u.Preferences).FirstOrDefaultAsync());
            return new
            {
                sso = sso.Enabled,
                signedIn,
                name = signedIn ? ctx.User.Identity?.Name : null,
                admin = AccessService.IsAdmin(ctx),
                likes = await Likes.Mine(db, ActivityLog.ReadVisitorId(ctx), Likes.IdentityKey(ctx)).Select(f => f.PhotoId).Distinct().CountAsync(),
                preferences = prefs
            };
        });

        app.MapPut("/api/me/preferences", async (UserPreferences input, HttpContext ctx, AppDb db) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest("missing header");
            var sub = Sub(ctx);
            if (sub == null) return Results.StatusCode(401);
            if (input.Theme != null && !ThemeSettings.IsValid(input.Theme)) return Results.BadRequest("Unknown theme");
            var u = await db.Users.FirstOrDefaultAsync(x => x.Sub == sub);
            if (u == null)
            {
                u = new AppUser { Sub = sub, Name = ctx.User.Identity?.Name ?? "" };
                db.Users.Add(u);
            }
            u.Preferences = input.ToJson();
            await db.SaveChangesAsync();
            return Results.Ok(UserPreferences.Parse(u.Preferences));
        });

        app.MapGet("/auth/login", (HttpContext ctx, SsoConfig sso, string? next) =>
        {
            if (!sso.Enabled) return Results.NotFound();
            var props = new AuthenticationProperties { RedirectUri = "/auth/done?next=" + Uri.EscapeDataString(Sso.SafeNext(next)) };
            return Results.Challenge(props, [Sso.Scheme]);
        });

        // The provider sends the browser back from another site, so the session cookie (SameSite=Strict) is not sent on that
        // first request chain. A page that refreshes on its own starts a fresh same-site navigation that carries the cookie.
        app.MapGet("/auth/done", (HttpContext ctx, string? next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var url = System.Net.WebUtility.HtmlEncode(Sso.SafeNext(next));
            return Results.Content($"<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={url}\"><title>Signing in</title><a href=\"{url}\">Continue</a>", "text/html; charset=utf-8");
        });

        app.MapGet("/auth/logout", async (HttpContext ctx, SsoConfig sso) =>
        {
            if (sso.Configured && ctx.User.Identity?.IsAuthenticated == true && ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier) != null)
            {
                // ends the provider's session as well, then comes back to the home page
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                await ctx.SignOutAsync(Sso.Scheme, new AuthenticationProperties { RedirectUri = "/" });
                return Results.Empty;
            }
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });
    }
}
