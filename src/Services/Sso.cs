using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace PhotoGallery;

/// <summary>Single sign-on through an OpenID Connect provider such as Keycloak. Off unless OIDC_AUTHORITY and OIDC_CLIENT_ID are set.</summary>
public static class Sso
{
    public const string Scheme = "oidc";
    public const string GroupClaim = "pg_group";

    public static AuthenticationBuilder AddSso(this AuthenticationBuilder auth, AppConfig cfg)
    {
        if (!cfg.OidcEnabled) return auth;
        return auth.AddOpenIdConnect(Scheme, o =>
        {
            o.Authority = cfg.OidcAuthority;
            o.ClientId = cfg.OidcClientId;
            o.ClientSecret = cfg.OidcClientSecret;
            o.RequireHttpsMetadata = !cfg.OidcAllowHttp;
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
            o.Events.OnTokenValidated = async c => await SignedIn(c, cfg);
            o.Events.OnRemoteFailure = c =>
            {
                c.Response.Redirect("/?signin=failed");
                c.HandleResponse();
                return Task.CompletedTask;
            };
        });
    }

    static async Task SignedIn(TokenValidatedContext c, AppConfig cfg)
    {
        var src = c.Principal!;
        string? Get(string t) => src.FindFirst(t)?.Value;
        var sub = Get("sub");
        if (string.IsNullOrEmpty(sub)) { c.Fail("no subject"); return; }

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(JsonElement e, HashSet<string> into)
        {
            if (e.ValueKind == JsonValueKind.String) into.Add(e.GetString()!.TrimStart('/'));
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) into.Add(x.GetString()!.TrimStart('/'));
        }
        void FromClaims(IEnumerable<Claim> claims)
        {
            foreach (var cl in claims)
            {
                if (cl.Type == "roles") roles.Add(cl.Value.TrimStart('/'));
                else if (cl.Type == "groups") groups.Add(cl.Value.TrimStart('/'));
            }
        }
        FromClaims(src.Claims);
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

        // being admin needs the admin role in the provider and a verified email address
        var admin = verified && (roles.Contains(cfg.OidcAdminRole) || groups.Contains(cfg.OidcAdminRole));

        var id = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        id.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
        id.AddClaim(new Claim(ClaimTypes.Name, name));
        if (email.Length > 0) id.AddClaim(new Claim(ClaimTypes.Email, email));
        if (verified) id.AddClaim(new Claim("email_verified", "true"));
        if (admin) id.AddClaim(new Claim(ClaimTypes.Role, AccessService.AdminRole));
        foreach (var gname in groups.Take(50)) id.AddClaim(new Claim(GroupClaim, gname));
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
