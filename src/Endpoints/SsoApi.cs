using Microsoft.AspNetCore.Authentication;

namespace PhotoGallery;

/// <summary>The admin page for setting up single sign-on: save the details, check them, try a real sign-in.</summary>
public static class SsoApi
{
    record SsoInput(bool Enabled, string? Authority, string? ClientId, string? ClientSecret, string? AdminRole, bool AllowHttp, bool HidePasswordLogin);

    static string Origin(HttpContext ctx) => $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    static object View(HttpContext ctx, SsoConfig sso, SettingsService settings) => new
    {
        fromContainer = sso.FromContainer,
        enabled = sso.Enabled,
        configured = sso.Configured,
        authority = sso.Authority ?? "",
        clientId = sso.ClientId ?? "",
        secretSet = !string.IsNullOrEmpty(sso.ClientSecret),
        adminRole = sso.AdminRole,
        allowHttp = sso.AllowHttp,
        hidePasswordLogin = !sso.PasswordLogin,
        testedAt = settings.Sso.TestedAt,
        redirectUri = Origin(ctx) + "/signin-oidc",
        logoutUri = Origin(ctx) + "/signout-callback-oidc",
        origin = Origin(ctx),
        defaultRole = SsoConfig.DefaultRole
    };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin/settings/sso").RequireAuthorization("Admin");

        g.MapGet("", (HttpContext ctx, SsoConfig sso, SettingsService settings) => Results.Ok(View(ctx, sso, settings)));

        g.MapPut("", (SsoInput input, HttpContext ctx, SsoConfig sso, SettingsService settings) =>
        {
            if (sso.FromContainer) return Results.BadRequest("Sign-in is set in the container's settings (OIDC_AUTHORITY and OIDC_CLIENT_ID), so it cannot be changed here.");
            var old = settings.Sso;
            var next = new SsoSettings
            {
                Enabled = input.Enabled, Authority = input.Authority ?? "", ClientId = input.ClientId ?? "", ClientSecret = input.ClientSecret ?? "",
                AdminRole = input.AdminRole ?? "", AllowHttp = input.AllowHttp, HidePasswordLogin = input.HidePasswordLogin
            }.Sanitized();

            if (next.Authority.Length > 0)
            {
                if (!Uri.TryCreate(next.Authority, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    return Results.BadRequest("The provider address must be a full web address such as https://auth.example.com/realms/gallery.");
                if (u.Scheme == "http" && !next.AllowHttp)
                    return Results.BadRequest("That address starts with http://, which sends passwords unprotected. Use https://, or tick \"Allow an http:// address\" for a provider on your home network.");
            }
            if (next.Enabled && (next.Authority.Length == 0 || next.ClientId.Length == 0))
                return Results.BadRequest("Enter the provider address and the client ID before turning sign-in on.");

            // an empty secret keeps the stored one, as long as it is still the same client
            var sameClient = string.Equals(next.Authority, old.Authority, StringComparison.Ordinal) && string.Equals(next.ClientId, old.ClientId, StringComparison.Ordinal);
            if (next.ClientSecret.Length == 0 && sameClient) next.ClientSecret = old.ClientSecret;

            var changed = !sameClient || next.ClientSecret != old.ClientSecret || next.AdminRole != old.AdminRole;
            next.TestedAt = changed ? null : old.TestedAt;

            if (next.HidePasswordLogin && (!next.Enabled || next.TestedAt == null))
                return Results.BadRequest("Run the test sign-in as an admin first. The password form can only be hidden once an admin has signed in through your provider, or you could lock yourself out.");

            settings.SetSso(next);
            sso.Changed();
            return Results.Ok(View(ctx, sso, settings));
        });

        // looks at the provider with the details on screen, without saving them
        g.MapPost("/check", async (SsoInput input, HttpContext ctx, SsoConfig sso, SettingsService settings) =>
        {
            var old = settings.Sso;
            string? authority, clientId, secret;
            bool allowHttp;
            if (sso.FromContainer) (authority, clientId, secret, allowHttp) = (sso.Authority, sso.ClientId, sso.ClientSecret, sso.AllowHttp);
            else
            {
                authority = input.Authority?.Trim().TrimEnd('/'); clientId = input.ClientId?.Trim(); allowHttp = input.AllowHttp;
                var sameClient = string.Equals(authority, old.Authority, StringComparison.Ordinal) && string.Equals(clientId, old.ClientId, StringComparison.Ordinal);
                secret = string.IsNullOrEmpty(input.ClientSecret) && sameClient ? old.ClientSecret : input.ClientSecret?.Trim();
            }
            var steps = await SsoCheck.RunAsync(authority, clientId, secret, allowHttp, Origin(ctx) + "/signin-oidc");
            return Results.Ok(new { ok = steps.All(s => s.Status != "bad"), steps });
        });

        g.MapGet("/test-result", (string? id) =>
            SsoTests.Get(id) is { } r ? Results.Ok(r) : Results.NotFound());
    }

    /// <summary>Starts a test sign-in through the saved details. It reports what the provider sends and does not change anyone's session.</summary>
    public static void MapTestLogin(WebApplication app) =>
        app.MapGet("/auth/test-login", (SsoConfig sso) =>
        {
            if (!sso.Configured) return Results.NotFound();
            var props = new AuthenticationProperties { RedirectUri = "/" };
            props.Items[Sso.TestFlag] = "1";
            return Results.Challenge(props, [Sso.Scheme]);
        }).RequireAuthorization("Admin");
}
