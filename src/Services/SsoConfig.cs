using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace PhotoGallery;

/// <summary>Sign-in settings kept in settings.json, used when the container does not set OIDC_AUTHORITY and OIDC_CLIENT_ID itself.</summary>
public class SsoSettings
{
    /// <summary>Sign-in is offered to visitors only when this is on (the rest can be saved and tried first).</summary>
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>Never sent to the browser; an empty value in a save keeps the stored one.</summary>
    public string ClientSecret { get; set; } = "";
    public string AdminRole { get; set; } = "gallery-admin";
    /// <summary>Allows an http:// address for the provider, for a server on a home network.</summary>
    public bool AllowHttp { get; set; }
    /// <summary>Takes the admin password form away. Allowed only after a test sign-in as an admin worked.</summary>
    public bool HidePasswordLogin { get; set; }
    /// <summary>When a test sign-in last worked as an admin with a verified email; cleared whenever the connection details change.</summary>
    public DateTime? TestedAt { get; set; }

    public SsoSettings Sanitized()
    {
        static string T(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }
        return new SsoSettings
        {
            Enabled = Enabled, Authority = T(Authority, 300).TrimEnd('/'), ClientId = T(ClientId, 200), ClientSecret = T(ClientSecret, 500),
            AdminRole = T(AdminRole, 100) is { Length: > 0 } r ? r : "gallery-admin", AllowHttp = AllowHttp, HidePasswordLogin = HidePasswordLogin, TestedAt = TestedAt
        };
    }

    public SsoSettings Clone() => Sanitized();
}

/// <summary>
/// The sign-in settings in effect right now. Settings set in the container (OIDC_AUTHORITY and OIDC_CLIENT_ID) win and cannot
/// be edited in the app; otherwise the admin page decides, and a change applies at once without a restart.
/// </summary>
public class SsoConfig(AppConfig cfg, SettingsService settings, IOptionsMonitorCache<OpenIdConnectOptions> optionsCache)
{
    public const string DefaultRole = "gallery-admin";

    public bool FromContainer => cfg.OidcEnabled;
    static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public string? Authority => FromContainer ? cfg.OidcAuthority : Nz(settings.Sso.Authority);
    public string? ClientId => FromContainer ? cfg.OidcClientId : Nz(settings.Sso.ClientId);
    public string? ClientSecret => FromContainer ? cfg.OidcClientSecret : Nz(settings.Sso.ClientSecret);
    public string AdminRole => FromContainer ? cfg.OidcAdminRole : settings.Sso.AdminRole;
    public bool AllowHttp => FromContainer ? cfg.OidcAllowHttp : settings.Sso.AllowHttp;

    /// <summary>There is enough to try a sign-in (the test button works).</summary>
    public bool Configured => Authority != null && ClientId != null;
    /// <summary>Visitors are offered sign-in.</summary>
    public bool Enabled => Configured && (FromContainer || settings.Sso.Enabled);

    /// <summary>Whether the admin password form is available.</summary>
    public bool PasswordLogin =>
        FromContainer || cfg.AdminPasswordLoginSet ? cfg.AdminPasswordLogin : !(Enabled && settings.Sso.HidePasswordLogin);

    /// <summary>The provider connection is rebuilt from the settings the next time it is used.</summary>
    public void Changed() => optionsCache.Clear();
}

/// <summary>What one test sign-in found out, kept for a few minutes so the settings page can show it.</summary>
public record SsoTestResult(bool Ok, string? Error, string Name, string Email, bool EmailVerified, string[] Roles, string[] Groups, bool AdminRoleFound, string AdminRole, bool IsAdmin);

public static class SsoTests
{
    static readonly ConcurrentDictionary<string, (DateTime at, SsoTestResult result)> Results = new();

    public static string Put(SsoTestResult r)
    {
        foreach (var old in Results.Where(x => x.Value.at < DateTime.UtcNow.AddMinutes(-15)).Select(x => x.Key).ToList()) Results.TryRemove(old, out _);
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        Results[id] = (DateTime.UtcNow, r);
        return id;
    }

    public static SsoTestResult? Get(string? id) => id != null && Results.TryGetValue(id, out var v) && v.at > DateTime.UtcNow.AddMinutes(-15) ? v.result : null;
}

/// <summary>Looks at the provider without signing anyone in, and says in plain words what is wrong.</summary>
public static class SsoCheck
{
    public record Step(string Status, string Text);

    public static async Task<List<Step>> RunAsync(string? authority, string? clientId, string? secret, bool allowHttp, string redirectUri, HttpMessageHandler? handler = null)
    {
        var steps = new List<Step>();
        if (string.IsNullOrWhiteSpace(authority) || !Uri.TryCreate(authority, UriKind.Absolute, out var auth) || auth.Scheme is not ("http" or "https"))
        {
            steps.Add(new("bad", "Enter the provider address first, the whole web address of your realm, for example https://auth.example.com/realms/gallery."));
            return steps;
        }
        if (auth.Scheme == "http" && !allowHttp)
        {
            steps.Add(new("bad", "The address starts with http://, which sends passwords unprotected. Use https://, or tick \"Allow an http:// address\" if the provider is on your home network."));
            return steps;
        }
        if (string.IsNullOrWhiteSpace(clientId)) { steps.Add(new("bad", "Enter the client ID first.")); return steps; }

        using var http = handler != null ? new HttpClient(handler, false) : new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        JsonElement doc;
        var discovery = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        try
        {
            using var resp = await http.GetAsync(discovery);
            if (!resp.IsSuccessStatusCode)
            {
                steps.Add(new("bad", resp.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "The server answered, but there is no sign-in service at that address. For Keycloak the address must end with /realms/ and the realm name, and the realm must exist."
                    : $"The server answered with an error ({(int)resp.StatusCode}). Check the address."));
                return steps;
            }
            doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            steps.Add(new("bad", ex switch
            {
                JsonException => "The address answered, but not with a sign-in provider's settings. Check that it points at the realm, not at the Keycloak home page.",
                TaskCanceledException => "The provider did not answer within 10 seconds. Check the address, and that this server can reach it.",
                HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => "The provider's security certificate was not accepted. Use an address with a valid certificate (a name, not an IP address).",
                _ => "Could not reach the provider. Check the address for typos, and that this server can reach it (the container needs network access to it)."
            }));
            return steps;
        }
        steps.Add(new("ok", "The provider answered."));

        string? Str(string name) => doc.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var issuer = Str("issuer");
        if (issuer != null && !string.Equals(issuer.TrimEnd('/'), authority.TrimEnd('/'), StringComparison.Ordinal))
            steps.Add(new("bad", $"The provider says its address is {issuer}, which is not what you entered. Use exactly that address, or sign-in will be refused."));
        else steps.Add(new("ok", "The address matches what the provider calls itself."));

        var token = Str("token_endpoint");
        if (Str("authorization_endpoint") == null || token == null)
        {
            steps.Add(new("bad", "The provider does not offer the standard sign-in steps (OpenID Connect). It cannot be used here."));
            return steps;
        }
        if (doc.TryGetProperty("code_challenge_methods_supported", out var m) && m.ValueKind == JsonValueKind.Array && !m.EnumerateArray().Any(x => x.GetString() == "S256"))
            steps.Add(new("warn", "The provider does not list PKCE (S256), which this site uses. Sign-in may fail."));

        // a made-up code: a provider that knows the client answers "invalid_grant", one that does not answers "invalid_client"
        try
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = "pg-connection-check", ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId, ["code_verifier"] = new string('a', 43)
            };
            if (!string.IsNullOrEmpty(secret)) form["client_secret"] = secret;
            using var resp = await http.PostAsync(token, new FormUrlEncodedContent(form));
            string? error = null;
            try { error = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null; } catch (JsonException) { }
            switch (error)
            {
                case "invalid_grant" or "invalid_request":
                    steps.Add(new("ok", string.IsNullOrEmpty(secret) ? "The client ID was accepted." : "The client ID and secret were accepted."));
                    break;
                case "invalid_client" or "unauthorized_client":
                    steps.Add(new("bad", string.IsNullOrEmpty(secret)
                        ? "The provider did not accept this client ID without a secret. Check the client ID, or copy the client secret from the client's Credentials tab and enter it."
                        : "The provider did not accept the client ID and secret. Check both for typos; the secret is on the client's Credentials tab."));
                    break;
                default:
                    steps.Add(new("warn", $"Could not tell whether the client ID and secret are right (the provider answered {(int)resp.StatusCode}). The test sign-in will show."));
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            steps.Add(new("warn", "Could not check the client ID and secret. The test sign-in will show."));
        }
        return steps;
    }
}
