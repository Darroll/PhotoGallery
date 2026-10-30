using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace PhotoGallery.Tests;

/// <summary>A pretend sign-in provider on a local port: serves its settings, a key list and a token endpoint that knows one client.</summary>
sealed class StubProvider : IDisposable
{
    readonly HttpListener listener = new();
    public string Base { get; }
    public string Realm => Base + "/realms/test";
    /// <summary>What the provider calls itself; defaults to its real address.</summary>
    public string? Issuer;
    public string KnownClient = "photogallery", KnownSecret = "s3cret";
    public bool Hidden404;   // answer 404 for the settings, like a realm that does not exist

    // for a full sign-in: the token endpoint hands out a signed id token for this person
    readonly System.Security.Cryptography.RSA rsa = System.Security.Cryptography.RSA.Create(2048);
    public string? Nonce;
    public string Sub = "sub-1", Email = "pat@example.test", Name = "Pat Example";
    public bool EmailVerified = true;
    public string[] Roles = [];

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    string Jwks()
    {
        var p = rsa.ExportParameters(false);
        return "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"k1\",\"n\":\"" + B64(p.Modulus!) + "\",\"e\":\"" + B64(p.Exponent!) + "\"}]}";
    }

    string TokenResponse()
    {
        var key = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "k1" };
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(Realm, KnownClient, new[]
        {
            new System.Security.Claims.Claim("sub", Sub), new System.Security.Claims.Claim("email", Email), new System.Security.Claims.Claim("name", Name),
            new System.Security.Claims.Claim("email_verified", EmailVerified ? "true" : "false", System.Security.Claims.ClaimValueTypes.Boolean),
            new System.Security.Claims.Claim("nonce", Nonce ?? "")
        }, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new Microsoft.IdentityModel.Tokens.SigningCredentials(key, "RS256"));
        var idToken = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(jwt);
        string Part(string t) => B64(Encoding.UTF8.GetBytes(t));
        var access = Part("{\"alg\":\"none\"}") + "." + Part("{\"realm_access\":{\"roles\":[" + string.Join(",", Roles.Select(r => "\"" + r + "\"")) + "]}}") + ".x";
        return "{\"token_type\":\"Bearer\",\"expires_in\":300,\"access_token\":\"" + access + "\",\"id_token\":\"" + idToken + "\"}";
    }

    public StubProvider()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        Base = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Base + "/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        });
    }

    async Task Serve(HttpListenerContext c)
    {
        string body = "{}"; int status = 200;
        var path = c.Request.Url!.AbsolutePath;
        if (path.EndsWith("/.well-known/openid-configuration"))
        {
            if (Hidden404) { status = 404; body = "{}"; }
            else body = $$"""{"issuer":"{{Issuer ?? Realm}}","authorization_endpoint":"{{Realm}}/auth","token_endpoint":"{{Realm}}/token","jwks_uri":"{{Realm}}/jwks","code_challenge_methods_supported":["S256"]}""";
        }
        else if (path.EndsWith("/jwks")) body = Jwks();
        else if (path.EndsWith("/token"))
        {
            var form = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
            var fields = form.Split('&').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));
            var ok = fields.GetValueOrDefault("client_id") == KnownClient && fields.GetValueOrDefault("client_secret") == KnownSecret;
            if (ok && fields.GetValueOrDefault("code") == "good-code") { status = 200; body = TokenResponse(); }
            else
            {
                status = 400;
                body = ok ? """{"error":"invalid_grant","error_description":"Code not valid"}""" : """{"error":"unauthorized_client","error_description":"Invalid client or Invalid client credentials"}""";
            }
        }
        c.Response.StatusCode = status;
        c.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        await c.Response.OutputStream.WriteAsync(bytes);
        c.Response.Close();
    }

    public void Dispose() { try { listener.Stop(); } catch { } }
}

public class SsoSetupTests
{
    static object Body(string authority, string clientId = "photogallery", string secret = "", bool enabled = false, bool allowHttp = true, bool hide = false, string role = "") =>
        new { enabled, authority, clientId, clientSecret = secret, adminRole = role, allowHttp, hidePasswordLogin = hide };

    [Fact]
    public async Task The_page_shows_the_addresses_to_register_and_needs_an_admin()
    {
        using var app = new AppFactory();
        var admin = await app.AdminAsync();
        var view = await (await admin.GetAsync("/api/admin/settings/sso")).ReadAsync();
        Assert.EndsWith("/signin-oidc", view.GetProperty("redirectUri").GetString());
        Assert.EndsWith("/signout-callback-oidc", view.GetProperty("logoutUri").GetString());
        Assert.Equal("gallery-admin", view.GetProperty("adminRole").GetString());
        Assert.False(view.GetProperty("enabled").GetBoolean());
        Assert.NotEqual(HttpStatusCode.OK, (await app.NewClient().GetAsync("/api/admin/settings/sso")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await app.NewClient().GetAsync("/auth/test-login")).StatusCode);
    }

    [Fact]
    public async Task Saving_checks_the_details_and_keeps_the_secret_private()
    {
        using var app = new AppFactory();
        var admin = await app.AdminAsync();

        var plain = await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("http://keycloak.example.test/realms/x", allowHttp: false));
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Contains("http://", await plain.Content.ReadAsStringAsync());

        var notUrl = await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("keycloak"));
        Assert.Equal(HttpStatusCode.BadRequest, notUrl.StatusCode);

        var noClient = await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("https://auth.example.test/realms/x", clientId: "", enabled: true));
        Assert.Equal(HttpStatusCode.BadRequest, noClient.StatusCode);

        var saved = await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("https://auth.example.test/realms/x/", secret: "top-secret"));
        var view = await saved.ReadAsync();
        Assert.Equal("https://auth.example.test/realms/x", view.GetProperty("authority").GetString());
        Assert.True(view.GetProperty("secretSet").GetBoolean());
        Assert.DoesNotContain("top-secret", await (await admin.GetAsync("/api/admin/settings/sso")).Content.ReadAsStringAsync());

        // an empty secret keeps the stored one; a different client does not inherit it
        var same = await (await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("https://auth.example.test/realms/x"))).ReadAsync();
        Assert.True(same.GetProperty("secretSet").GetBoolean());
        var other = await (await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("https://auth.example.test/realms/x", clientId: "other"))).ReadAsync();
        Assert.False(other.GetProperty("secretSet").GetBoolean());
    }

    [Fact]
    public async Task The_password_form_can_only_be_hidden_after_a_test_sign_in_worked()
    {
        using var app = new AppFactory();
        var admin = await app.AdminAsync();
        var r = await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("https://auth.example.test/realms/x", enabled: true, hide: true));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("test sign-in", await r.Content.ReadAsStringAsync());

        // turning sign-in on leaves the password form available
        var on = await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("https://auth.example.test/realms/x", enabled: true));
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var me = await (await app.NewClient().GetAsync("/api/admin/me")).ReadAsync();
        Assert.True(me.GetProperty("sso").GetBoolean());
        Assert.True(me.GetProperty("passwordLogin").GetBoolean());
        var login = await app.NewClient().PostAsJsonAsync("/api/admin/login", new { password = AppFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        // after a worked test the form can go, and then the password login is refused
        app.Services.GetService(typeof(SettingsService)).As<SettingsService>().MarkSsoTested();
        var hide = await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("https://auth.example.test/realms/x", enabled: true, hide: true));
        Assert.Equal(HttpStatusCode.OK, hide.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.NewClient().PostAsJsonAsync("/api/admin/login", new { password = AppFactory.AdminPassword })).StatusCode);
        Assert.False((await (await app.NewClient().GetAsync("/api/admin/me")).ReadAsync()).GetProperty("passwordLogin").GetBoolean());

        // changing the connection details takes the proof away and brings the form back
        var changed = await admin.PutAsJsonAsync("/api/admin/settings/sso", Body("https://auth.example.test/realms/x", clientId: "different", enabled: true));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.True((await (await app.NewClient().GetAsync("/api/admin/me")).ReadAsync()).GetProperty("passwordLogin").GetBoolean());
    }

    [Fact]
    public async Task Settings_saved_in_the_app_apply_at_once_without_a_restart()
    {
        using var app = new AppFactory();
        using var provider = new StubProvider();
        var admin = await app.AdminAsync();
        var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/auth/login")).StatusCode);   // off until set up

        await admin.PutAsJsonAsync("/api/admin/settings/sso", Body(provider.Realm, clientId: "first-client", enabled: true));
        var go = await anon.GetAsync("/auth/login?next=/");
        Assert.Equal(HttpStatusCode.Redirect, go.StatusCode);
        var to = go.Headers.Location!.ToString();
        Assert.StartsWith(provider.Realm + "/auth", to);
        Assert.Contains("client_id=first-client", to);
        Assert.Contains("code_challenge_method=S256", to);

        await admin.PutAsJsonAsync("/api/admin/settings/sso", Body(provider.Realm, clientId: "second-client", enabled: true));
        Assert.Contains("client_id=second-client", (await anon.GetAsync("/auth/login?next=/")).Headers.Location!.ToString());

        await admin.PutAsJsonAsync("/api/admin/settings/sso", Body(provider.Realm, clientId: "second-client", enabled: false));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/auth/login")).StatusCode);
        Assert.False((await (await anon.GetAsync("/api/me")).ReadAsync()).GetProperty("sso").GetBoolean());
    }

    [Fact]
    public async Task The_connection_check_says_in_plain_words_what_is_wrong()
    {
        using var app = new AppFactory();
        var admin = await app.AdminAsync();
        async Task<(bool ok, string text)> Check(object body)
        {
            var r = await (await admin.PostAsJsonAsync("/api/admin/settings/sso/check", body)).ReadAsync();
            return (r.GetProperty("ok").GetBoolean(), string.Join(" | ", r.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("status").GetString() + ": " + s.GetProperty("text").GetString())));
        }

        using (var good = new StubProvider())
        {
            var ok = await Check(Body(good.Realm, secret: "s3cret"));
            Assert.True(ok.ok, ok.text);
            Assert.Contains("client ID and secret were accepted", ok.text);

            var wrong = await Check(Body(good.Realm, secret: "nope"));
            Assert.False(wrong.ok);
            Assert.Contains("did not accept the client ID and secret", wrong.text);

            var noSecret = await Check(Body(good.Realm));
            Assert.Contains("without a secret", noSecret.text);

            good.Issuer = "https://elsewhere.example.test/realms/test";
            var mismatch = await Check(Body(good.Realm, secret: "s3cret"));
            Assert.False(mismatch.ok);
            Assert.Contains("which is not what you entered", mismatch.text);
        }
        using (var missing = new StubProvider { Hidden404 = true })
            Assert.Contains("no sign-in service at that address", (await Check(Body(missing.Realm))).text);

        var closed = new StubProvider(); var gone = closed.Realm; closed.Dispose();
        Assert.Contains("Could not reach the provider", (await Check(Body(gone))).text);

        Assert.Contains("http://", (await Check(Body("http://auth.example.test/realms/x", allowHttp: false))).text);
        Assert.Contains("Enter the provider address", (await Check(Body(""))).text);
    }

    /// <summary>Walks a test sign-in the way a browser would: out to the provider, back with a code, then reads the result.</summary>
    static async Task<System.Text.Json.JsonElement> TestSignInAsync(AppFactory app, HttpClient admin, StubProvider provider)
    {
        var jar = new Dictionary<string, string>();
        void Remember(HttpResponseMessage r)
        {
            if (!r.Headers.TryGetValues("Set-Cookie", out var all)) return;
            foreach (var c in all) { var kv = c.Split(';')[0].Split('=', 2); jar[kv[0]] = kv[1]; }
        }
        HttpRequestMessage Get(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value)));
            return req;
        }
        // the admin session cookie rides along, as it would in the browser
        var login = await app.NewClient().PostAsJsonAsync("/api/admin/login", new { password = AppFactory.AdminPassword });
        Remember(login);
        var session = jar["pg_session"];

        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Requested-With", "pg");
        var start = await client.SendAsync(Get("/auth/test-login"));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Remember(start);
        var q = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query);
        provider.Nonce = q["nonce"];

        var back = await client.SendAsync(Get("/signin-oidc?code=good-code&state=" + Uri.EscapeDataString(q["state"]!)));
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var landing = back.Headers.Location!.ToString();
        Assert.StartsWith("/auth/done?next=", landing);
        Assert.False(back.Headers.TryGetValues("Set-Cookie", out var set) && set.Any(c => c.StartsWith("pg_session=")), "a test must not change the session");
        var next = Uri.UnescapeDataString(landing["/auth/done?next=".Length..]);
        var id = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + next).Query)["test"];
        Assert.NotNull(id);
        Assert.NotEmpty(session);
        return await (await admin.GetAsync("/api/admin/settings/sso/test-result?id=" + id)).ReadAsync();
    }

    [Fact]
    public async Task A_test_sign_in_reports_who_signed_in_and_proves_admin_access_without_touching_the_session()
    {
        using var app = new AppFactory();
        using var provider = new StubProvider { Roles = ["gallery-admin", "offline_access"] };
        var admin = await app.AdminAsync();
        await admin.PutAsJsonAsync("/api/admin/settings/sso", Body(provider.Realm, secret: "s3cret", enabled: true));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await (await admin.GetAsync("/api/admin/settings/sso")).ReadAsync()).GetProperty("testedAt").ValueKind);

        var result = await TestSignInAsync(app, admin, provider);
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal("Pat Example", result.GetProperty("name").GetString());
        Assert.True(result.GetProperty("emailVerified").GetBoolean());
        Assert.True(result.GetProperty("adminRoleFound").GetBoolean());
        Assert.True(result.GetProperty("isAdmin").GetBoolean());
        Assert.Contains("offline_access", result.GetProperty("roles").EnumerateArray().Select(x => x.GetString()));
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, (await (await admin.GetAsync("/api/admin/settings/sso")).ReadAsync()).GetProperty("testedAt").ValueKind);
        Assert.Equal(0, await app.WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Users)));   // no account was created
    }

    [Fact]
    public async Task A_test_sign_in_without_the_admin_role_or_a_verified_email_does_not_count_as_proof()
    {
        using var app = new AppFactory();
        using var provider = new StubProvider { Roles = ["something-else"], EmailVerified = false };
        var admin = await app.AdminAsync();
        await admin.PutAsJsonAsync("/api/admin/settings/sso", Body(provider.Realm, secret: "s3cret", enabled: true, role: "gallery-admin"));

        var result = await TestSignInAsync(app, admin, provider);
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.False(result.GetProperty("emailVerified").GetBoolean());
        Assert.False(result.GetProperty("adminRoleFound").GetBoolean());
        Assert.False(result.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await (await admin.GetAsync("/api/admin/settings/sso")).ReadAsync()).GetProperty("testedAt").ValueKind);
    }

    [Fact]
    public async Task A_test_result_is_shown_to_admins_only()
    {
        using var app = new AppFactory();
        var admin = await app.AdminAsync();
        var id = SsoTests.Put(new SsoTestResult(true, null, "Pat", "pat@example.test", false, ["offline_access"], [], false, "gallery-admin", false));
        var r = await (await admin.GetAsync("/api/admin/settings/sso/test-result?id=" + id)).ReadAsync();
        Assert.Equal("Pat", r.GetProperty("name").GetString());
        Assert.False(r.GetProperty("isAdmin").GetBoolean());
        Assert.NotEqual(HttpStatusCode.OK, (await app.NewClient().GetAsync("/api/admin/settings/sso/test-result?id=" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/settings/sso/test-result?id=nope")).StatusCode);
    }

    [Fact]
    public void Failures_during_a_test_sign_in_are_explained()
    {
        Assert.Contains("client ID and secret", Sso.Explain(new Exception("invalid_client")));
        Assert.Contains("return address", Sso.Explain(new Exception("Invalid parameter: redirect_uri")));
        Assert.Contains("matched to this browser", Sso.Explain(new Exception("Correlation failed.")));
    }
}

static class ObjectExtensions
{
    public static T As<T>(this object? o) where T : class => (T)o!;
}
