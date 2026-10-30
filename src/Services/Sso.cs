using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace PhotoGallery;

/// <summary>Single sign-on through an OpenID Connect provider such as Keycloak. Off until it is set up (admin page, or OIDC_AUTHORITY and OIDC_CLIENT_ID in the container).</summary>
public static class Sso
{
    public const string Scheme = "oidc";
    public const string GroupClaim = "pg_group";

    /// <summary>Test sign-ins carry this in their properties: they report what the provider says and do not sign anyone in.</summary>
    public const string TestFlag = "pg_test";

    /// <summary>
    /// The sign-in scheme is always registered, and its settings are read when it is first used, so the admin page can set it up
    /// (or change it) without a restart. With nothing configured it holds harmless placeholders and no one is sent to it.
    /// </summary>
    public static AuthenticationBuilder AddSso(this AuthenticationBuilder auth)
    {
        auth.AddOpenIdConnect(Scheme, o =>
        {
            o.Authority = "https://sso-not-configured.invalid";
            o.ClientId = "not-configured";
            o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.ResponseMode = OpenIdConnectResponseMode.Query;   // a plain redirect back, no cross-site form post
            o.UsePkce = true;
            o.MapInboundClaims = false;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.Scope.Clear();
            foreach (var s in new[] { "openid", "profile", "email" }) o.Scope.Add(s);
            // the provider redirects back with a top-level GET, so Lax is enough (None would need HTTPS even for local tests)
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            o.Events.OnTokenValidated = SignedIn;
            o.Events.OnRemoteFailure = c =>
            {
                if (c.Properties?.Items.ContainsKey(TestFlag) == true)
                {
                    var id = SsoTests.Put(new SsoTestResult(false, Explain(c.Failure), "", "", false, [], [], false, "", false));
                    c.Response.Redirect("/auth/done?next=" + Uri.EscapeDataString("/admin/settings/sso?test=" + id));
                }
                else c.Response.Redirect("/?signin=failed");
                c.HandleResponse();
                return Task.CompletedTask;
            };
        });
        auth.Services.AddOptions<OpenIdConnectOptions>(Scheme).Configure<SsoConfig>((o, sso) =>
        {
            if (!sso.Configured) return;
            o.Authority = sso.Authority;
            o.ClientId = sso.ClientId;
            o.ClientSecret = sso.ClientSecret;
            o.RequireHttpsMetadata = !sso.AllowHttp;
        });
        return auth;
    }

    /// <summary>Plain words for the failures a test sign-in can run into.</summary>
    public static string Explain(Exception? ex)
    {
        var m = ex?.Message ?? "";
        if (m.Contains("invalid_client", StringComparison.OrdinalIgnoreCase) || m.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase))
            return "The provider did not accept the client ID and secret. Check both.";
        if (m.Contains("redirect_uri", StringComparison.OrdinalIgnoreCase))
            return "The provider does not accept this site's return address. Add it to the client's valid redirect URIs exactly as shown on this page.";
        if (m.Contains("access_denied", StringComparison.OrdinalIgnoreCase))
            return "Sign-in was cancelled or the provider refused it.";
        if (m.Contains("correlation", StringComparison.OrdinalIgnoreCase) || m.Contains("unprotect", StringComparison.OrdinalIgnoreCase))
            return "The sign-in could not be matched to this browser. Try again; if it keeps happening, check that the site address in the browser is the one you registered, and that a proxy passes the Host and X-Forwarded-Proto headers.";
        if (m.Contains("issuer", StringComparison.OrdinalIgnoreCase))
            return "The provider's address does not match what it calls itself. Use the address shown by the connection check.";
        if (m.Contains("IDX20803") || m.Contains("IDX20804") || m.Contains("Unable to obtain configuration", StringComparison.OrdinalIgnoreCase))
            return "This site could not reach the provider to finish signing in. Check the address and that the container can reach it.";
        return string.IsNullOrWhiteSpace(m) ? "Sign-in failed." : "Sign-in failed: " + m;
    }

    record Who(string Sub, string Email, bool Verified, string Name, HashSet<string> Roles, HashSet<string> Groups, bool RoleFound);

    static Who Analyze(TokenValidatedContext c, string adminRole)
    {
        var src = c.Principal!;
        string? Get(string t) => src.FindFirst(t)?.Value;
        var sub = Get("sub") ?? "";

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(JsonElement e, HashSet<string> into)
        {
            if (e.ValueKind == JsonValueKind.String) into.Add(e.GetString()!.TrimStart('/'));
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) into.Add(x.GetString()!.TrimStart('/'));
        }
        foreach (var cl in src.Claims)
        {
            if (cl.Type == "roles") roles.Add(cl.Value.TrimStart('/'));
            else if (cl.Type == "groups") groups.Add(cl.Value.TrimStart('/'));
        }
        // Keycloak keeps realm roles in the access token (realm_access.roles) unless a mapper adds them to the id token
        var access = c.TokenEndpointResponse?.AccessToken;
        if (!string.IsNullOrEmpty(access))
        {
            try
            {
                var parts = access.Split('.');
                if (parts.Length == 3)
                {
                    var b64 = parts[1].Replace('-', '+').Replace('_', '/');
                    b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                    using var d = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                    var root = d.RootElement;
                    if (root.TryGetProperty("realm_access", out var ra) && ra.TryGetProperty("roles", out var rr)) Collect(rr.Clone(), roles);
                    if (root.TryGetProperty("roles", out var r2)) Collect(r2.Clone(), roles);
                    if (root.TryGetProperty("groups", out var g2)) Collect(g2.Clone(), groups);
                }
            }
            catch { /* roles from the id token still count */ }
        }

        var email = Get("email") ?? "";
        var verified = string.Equals(Get("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        var name = Get("name") ?? Get("preferred_username") ?? email;
        if (string.IsNullOrWhiteSpace(name)) name = "Member";
        return new Who(sub, email, verified, name, roles, groups, roles.Contains(adminRole) || groups.Contains(adminRole));
    }

    static async Task SignedIn(TokenValidatedContext c)
    {
        var sso = c.HttpContext.RequestServices.GetRequiredService<SsoConfig>();
        var who = Analyze(c, sso.AdminRole);
        if (string.IsNullOrEmpty(who.Sub)) { c.Fail("no subject"); return; }
        var (sub, email, verified, name) = (who.Sub, who.Email, who.Verified, who.Name);

        // being admin needs the admin role in the provider and a verified email address
        var admin = verified && who.RoleFound;

        if (c.Properties?.Items.ContainsKey(TestFlag) == true)
        {
            // a test: report what the provider sent, leave the browser's session alone
            if (admin) c.HttpContext.RequestServices.GetRequiredService<SettingsService>().MarkSsoTested();
            var tid = SsoTests.Put(new SsoTestResult(true, null, name, email, verified, who.Roles.Order().ToArray(), who.Groups.Order().ToArray(), who.RoleFound, sso.AdminRole, admin));
            c.Response.Redirect("/auth/done?next=" + Uri.EscapeDataString("/admin/settings/sso?test=" + tid));
            c.HandleResponse();
            return;
        }

        var id = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        id.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
        id.AddClaim(new Claim(ClaimTypes.Name, name));
        if (email.Length > 0) id.AddClaim(new Claim(ClaimTypes.Email, email));
        if (verified) id.AddClaim(new Claim("email_verified", "true"));
        if (admin) id.AddClaim(new Claim(ClaimTypes.Role, AccessService.AdminRole));
        foreach (var gname in who.Groups.Take(50)) id.AddClaim(new Claim(GroupClaim, gname));
        c.Principal = new ClaimsPrincipal(id);

        // keep the id token so signing out can also end the provider's session
        if (!string.IsNullOrEmpty(c.TokenEndpointResponse?.IdToken))
            c.Properties!.StoreTokens([new AuthenticationToken { Name = "id_token", Value = c.TokenEndpointResponse!.IdToken }]);
        c.Properties!.IsPersistent = false;

        var db = c.HttpContext.RequestServices.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;
        var u = await db.Users.FirstOrDefaultAsync(x => x.Sub == sub);
        if (u == null) db.Users.Add(new AppUser { Sub = sub, Email = Clip(email, 200), Name = Clip(name, 120), FirstSeen = now, LastSeen = now });
        else { u.Email = Clip(email, 200); u.Name = Clip(name, 120); u.LastSeen = now; }
        await db.SaveChangesAsync();
        c.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Sso")
            .LogInformation("Signed in {Name} ({Sub}){Admin}", name, sub, admin ? " as admin" : "");
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Accepts only a path on this site; anything else becomes the home page.</summary>
    public static string SafeNext(string? next) =>
        !string.IsNullOrEmpty(next) && next.Length < 500 && next[0] == '/' && !next.StartsWith("//") && !next.Contains('\\') && !next.Any(char.IsControl)
            && !next.StartsWith("/auth/") ? next : "/";
}
