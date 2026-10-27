using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace PhotoGallery;

/// <summary>Visitor sign-in through single sign-on (see Sso.cs).</summary>
public static class AuthApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/me", (HttpContext ctx, AppConfig cfg) =>
        {
            var signedIn = ctx.User.Identity?.IsAuthenticated == true;
            return new
            {
                sso = cfg.OidcEnabled,
                signedIn,
                name = signedIn ? ctx.User.Identity?.Name : null,
                admin = AccessService.IsAdmin(ctx)
            };
        });

        app.MapGet("/auth/login", (HttpContext ctx, AppConfig cfg, string? next) =>
        {
            if (!cfg.OidcEnabled) return Results.NotFound();
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

        app.MapGet("/auth/logout", async (HttpContext ctx, AppConfig cfg) =>
        {
            if (cfg.OidcEnabled && ctx.User.Identity?.IsAuthenticated == true && ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier) != null)
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
