using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

/// <summary>Same app, but with single sign-on configured (no real provider is contacted unless someone signs in).</summary>
public class SsoFactory() : AppFactory(new()
{
    ["OIDC_AUTHORITY"] = "https://auth.example.test/realms/gallery",
    ["OIDC_CLIENT_ID"] = "photogallery",
    ["OIDC_CLIENT_SECRET"] = "secret",
});

/// <summary>SSO configured, but the password form explicitly kept.</summary>
public class SsoWithPasswordFactory() : AppFactory(new()
{
    ["OIDC_AUTHORITY"] = "https://auth.example.test/realms/gallery",
    ["OIDC_CLIENT_ID"] = "photogallery",
    ["ADMIN_PASSWORD_LOGIN"] = "true",
});

public class BasicsTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Health_check_answers()
    {
        var r = await app.NewClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("ok", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/admin")]
    [InlineData("/license")]
    [InlineData("/expired")]
    [InlineData("/g/anything")]
    public async Task App_routes_serve_the_page_with_the_site_name_and_no_caching(string path)
    {
        var r = await app.NewClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("<title>Test Photos</title>", html);
        Assert.DoesNotContain("__V__", html);
        Assert.DoesNotContain("__TITLE__", html);
    }

    [Fact]
    public async Task Static_assets_are_served()
    {
        var c = app.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/style.css")).StatusCode);
        var js = await c.GetAsync("/app.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Contains("viewLicense", await js.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Security_headers_are_present()
    {
        var r = await app.NewClient().GetAsync("/");
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        var csp = r.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
    }

    [Fact]
    public async Task Site_info_reports_title_and_sizes()
    {
        var j = await (await app.NewClient().GetAsync("/api/site")).ReadAsync();
        Assert.Equal("Test Photos", j.GetProperty("title").GetString());
        Assert.True(j.GetProperty("sizes").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Without_SSO_the_sign_in_status_says_so()
    {
        var j = await (await app.NewClient().GetAsync("/api/me")).ReadAsync();
        Assert.False(j.GetProperty("sso").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await app.NewClient().GetAsync("/auth/login")).StatusCode);
    }
}

public class AdminAuthTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Theory]
    [InlineData("/api/admin/galleries")]
    [InlineData("/api/admin/folders")]
    [InlineData("/api/admin/settings")]
    [InlineData("/api/admin/settings/license")]
    [InlineData("/api/admin/settings/email")]
    [InlineData("/api/admin/cache")]
    [InlineData("/api/admin/health")]
    public async Task Admin_endpoints_reject_anonymous_visitors(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Admin_writes_without_the_csrf_header_are_refused()
    {
        var c = app.CreateClient();   // no X-Requested-With
        var r = await c.PostAsJsonAsync("/api/admin/login", new { password = AppFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Status_before_login_says_not_admin_and_password_login_is_available()
    {
        var j = await (await app.NewClient().GetAsync("/api/admin/me")).ReadAsync();
        Assert.False(j.GetProperty("admin").GetBoolean());
        Assert.True(j.GetProperty("passwordLogin").GetBoolean());
        Assert.False(j.GetProperty("sso").GetBoolean());
    }

    [Fact]
    public async Task Wrong_password_is_refused_and_right_password_signs_in_and_out()
    {
        var c = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/admin/login", new { password = "nope" })).StatusCode);
        Assert.False((await (await c.GetAsync("/api/admin/me")).ReadAsync()).GetProperty("admin").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/admin/login", new { password = AppFactory.AdminPassword })).StatusCode);
        Assert.True((await (await c.GetAsync("/api/admin/me")).ReadAsync()).GetProperty("admin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/admin/galleries")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/admin/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/galleries")).StatusCode);
    }
}

public class SsoModeTests(SsoFactory app, SsoWithPasswordFactory both) : IClassFixture<SsoFactory>, IClassFixture<SsoWithPasswordFactory>
{
    [Fact]
    public async Task With_SSO_the_admin_password_form_is_gone()
    {
        var c = app.NewClient();
        var me = await (await c.GetAsync("/api/admin/me")).ReadAsync();
        Assert.False(me.GetProperty("passwordLogin").GetBoolean());
        Assert.True(me.GetProperty("sso").GetBoolean());
        var r = await c.PostAsJsonAsync("/api/admin/login", new { password = AppFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/galleries")).StatusCode);
    }

    [Fact]
    public async Task With_SSO_the_public_status_offers_sign_in()
    {
        var j = await (await app.NewClient().GetAsync("/api/me")).ReadAsync();
        Assert.True(j.GetProperty("sso").GetBoolean());
        Assert.False(j.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public void Sign_in_return_paths_cannot_become_open_redirects()
    {
        Assert.Equal("/", Sso.SafeNext("https://evil.example/x"));
        Assert.Equal("/", Sso.SafeNext("//evil.example"));
        Assert.Equal("/g/summer", Sso.SafeNext("/g/summer"));
        Assert.Equal("/", Sso.SafeNext(null));
    }

    [Fact]
    public async Task Explicit_ADMIN_PASSWORD_LOGIN_true_keeps_the_form_alongside_SSO()
    {
        var c = both.NewClient();
        var me = await (await c.GetAsync("/api/admin/me")).ReadAsync();
        Assert.True(me.GetProperty("passwordLogin").GetBoolean());
        Assert.True(me.GetProperty("sso").GetBoolean());
        Assert.NotEqual(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/admin/login", new { password = "wrong" })).StatusCode);
    }
}

public class LicenseApiTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Default_license_is_public_and_requires_acceptance()
    {
        var j = await (await app.NewClient().GetAsync("/api/license")).ReadAsync();
        Assert.True(j.GetProperty("enabled").GetBoolean());
        Assert.True(j.GetProperty("require").GetBoolean());
        var text = j.GetProperty("text").GetString()!;
        Assert.Contains("non-commercial", text);
        Assert.Contains("Test Photos", text);          // holder falls back to the site name
        Assert.DoesNotContain("{holder}", text);
        Assert.DoesNotContain("{year}", text);
        Assert.False(string.IsNullOrEmpty(j.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Only_the_admin_can_change_it()
    {
        var body = new { enabled = false, requireAcceptance = false, title = "x", holder = "x", contact = "x", text = "x" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().PutAsJsonAsync("/api/admin/settings/license", body)).StatusCode);
    }

    [Fact]
    public async Task Admin_edits_show_up_for_visitors_and_change_the_version()
    {
        var admin = await app.AdminAsync();
        var before = (await (await app.NewClient().GetAsync("/api/license")).ReadAsync()).GetProperty("version").GetString();

        var put = await admin.PutAsJsonAsync("/api/admin/settings/license", new
        {
            enabled = true, requireAcceptance = true, title = "Studio License", holder = "Darroll Walsh", contact = "me@example.test",
            text = "No selling. Contact {contact}. (c) {year} {holder}"
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var j = await (await app.NewClient().GetAsync("/api/license")).ReadAsync();
        Assert.Equal("Studio License", j.GetProperty("title").GetString());
        Assert.Equal($"No selling. Contact me@example.test. (c) {DateTime.UtcNow.Year} Darroll Walsh", j.GetProperty("text").GetString());
        Assert.NotEqual(before, j.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Admin_view_returns_the_raw_text_and_the_default_for_reset()
    {
        var admin = await app.AdminAsync();
        await admin.PutAsJsonAsync("/api/admin/settings/license", new { enabled = true, requireAcceptance = true, title = "T", holder = "", contact = "", text = "{holder} raw" });
        var j = await (await admin.GetAsync("/api/admin/settings/license")).ReadAsync();
        Assert.Equal("{holder} raw", j.GetProperty("license").GetProperty("text").GetString());
        Assert.Contains("non-commercial", j.GetProperty("defaultText").GetString());
    }

    [Fact]
    public async Task Turning_it_off_stops_requiring_acceptance()
    {
        var admin = await app.AdminAsync();
        await admin.PutAsJsonAsync("/api/admin/settings/license", new { enabled = false, requireAcceptance = true, title = "T", holder = "", contact = "", text = "t" });
        var j = await (await app.NewClient().GetAsync("/api/license")).ReadAsync();
        Assert.False(j.GetProperty("enabled").GetBoolean());
        Assert.False(j.GetProperty("require").GetBoolean());
    }
}

public class GalleryFlowTests(AppFactory app) : IClassFixture<AppFactory>
{
    /// <summary>A fresh library folder with n small JPEGs (a.jpg, b.jpg, ...). Each test needs its own, a folder can only belong to one gallery.</summary>
    string Folder(int n = 1)
    {
        var name = "f" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.Combine(app.Library, name));
        for (int i = 0; i < n; i++) TestImages.Write(Path.Combine(app.Library, name, $"{(char)('a' + i)}.jpg"), 400 + i * 40, 300, 60 + i * 30);
        return name;
    }

    async Task<(int id, string slug)> CreateAsync(HttpClient admin, string title, string folder, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = title, ["folder"] = folder, ["visibility"] = 0 };
        if (extra != null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var g = (await r.ReadAsync()).GetProperty("gallery");
        return (g.GetProperty("id").GetInt32(), g.GetProperty("slug").GetString()!);
    }

    static async Task<int[]> PhotoIdsAsync(HttpClient c, string slug) =>
        (await (await c.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToArray();

    [Fact]
    public async Task Creating_a_gallery_scans_the_folder_and_lists_it_when_public()
    {
        var admin = await app.AdminAsync();
        var (_, slug) = await CreateAsync(admin, "Summer Game", Folder(2));
        Assert.Equal("summer-game", slug);

        var list = await (await app.NewClient().GetAsync("/api/galleries")).ReadAsync();
        Assert.Contains(list.EnumerateArray(), g => g.GetProperty("slug").GetString() == slug);

        var anon = app.NewClient();
        var ids = await PhotoIdsAsync(anon, slug);
        Assert.Equal(2, ids.Length);
    }

    [Fact]
    public async Task Duplicate_titles_and_folders_and_escaping_paths_are_refused()
    {
        var admin = await app.AdminAsync();
        var used = Folder();
        await CreateAsync(admin, "Dupes", used);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "dupes", folder = Folder() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Another", folder = used })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Escape", folder = "../" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "", folder = Folder() })).StatusCode);
    }

    [Fact]
    public async Task Unlisted_galleries_are_hidden_from_the_home_page_but_open_by_link()
    {
        var admin = await app.AdminAsync();
        Directory.CreateDirectory(Path.Combine(app.Library, "Unlisted"));
        TestImages.Write(Path.Combine(app.Library, "Unlisted", "u.jpg"), 400, 300, 60);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Hidden", folder = "Unlisted", visibility = 1 });
        var slug = (await r.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;

        var list = await (await app.NewClient().GetAsync("/api/galleries")).ReadAsync();
        Assert.DoesNotContain(list.EnumerateArray(), g => g.GetProperty("slug").GetString() == slug);
        Assert.Equal(HttpStatusCode.OK, (await app.NewClient().GetAsync($"/api/g/{slug}")).StatusCode);
    }

    [Fact]
    public async Task Protected_gallery_needs_the_password_before_showing_photos_or_downloads()
    {
        var admin = await app.AdminAsync();
        Directory.CreateDirectory(Path.Combine(app.Library, "Private"));
        TestImages.Write(Path.Combine(app.Library, "Private", "p.jpg"), 400, 300, 70);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Private Party", folder = "Private", visibility = 2, password = "open sesame" });
        var slug = (await r.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;
        var photoId = (await PhotoIdsAsync(admin, slug)).Single();

        var guest = app.NewClient();
        var locked = await guest.GetAsync($"/api/g/{slug}");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.True((await locked.ReadAsync()).GetProperty("needsPassword").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/dl/{photoId}?size=original")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/img/{photoId}/thumb")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync($"/api/g/{slug}/unlock", new { password = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsJsonAsync($"/api/g/{slug}/unlock", new { password = "open sesame" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/g/{slug}")).StatusCode);
        await guest.AcceptLicenseAsync();
        var dl = await guest.GetAsync($"/dl/{photoId}?size=original");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("image/jpeg", dl.Content.Headers.ContentType?.MediaType);
        Assert.True((await dl.Content.ReadAsByteArrayAsync()).Length > 100);

        // a different visitor is still locked out
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync($"/api/g/{slug}")).StatusCode);
    }

    [Fact]
    public async Task Original_download_is_byte_for_byte_the_file_in_the_library()
    {
        var admin = await app.AdminAsync();
        var folder = Folder(2);
        var (_, slug) = await CreateAsync(admin, "Originals", folder);
        var anon = app.NewClient();
        var j = await (await anon.GetAsync($"/api/g/{slug}")).ReadAsync();
        var a = j.GetProperty("photos").EnumerateArray().First(p => p.GetProperty("name").GetString() == "a.jpg");
        await anon.AcceptLicenseAsync();
        var bytes = await (await anon.GetAsync($"/dl/{a.GetProperty("id").GetInt32()}?size=original")).Content.ReadAsByteArrayAsync();
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(app.Library, folder, "a.jpg")), bytes);
    }

    [Fact]
    public async Task Sized_downloads_work_and_unknown_sizes_or_formats_are_rejected()
    {
        var admin = await app.AdminAsync();
        var (_, slug) = await CreateAsync(admin, "Sized", Folder());
        var anon = app.NewClient();
        var id = (await PhotoIdsAsync(anon, slug))[0];

        await anon.AcceptLicenseAsync();
        var ok = await anon.GetAsync($"/dl/{id}?size=1080&fmt=png");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/png", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync($"/dl/{id}?size=123456&fmt=png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync($"/dl/{id}?size=1080&fmt=bmp")).StatusCode);
    }

    [Fact]
    public async Task Downloads_can_be_switched_off_per_gallery()
    {
        var admin = await app.AdminAsync();
        Directory.CreateDirectory(Path.Combine(app.Library, "NoDl"));
        TestImages.Write(Path.Combine(app.Library, "NoDl", "n.jpg"), 400, 300, 80);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "View Only", folder = "NoDl", visibility = 0, allowDownload = false });
        var slug = (await r.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;
        var id = (await PhotoIdsAsync(app.NewClient(), slug)).Single();
        Assert.Equal(HttpStatusCode.Forbidden, (await app.NewClient().GetAsync($"/dl/{id}?size=original")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.NewClient().GetAsync($"/img/{id}/thumb")).StatusCode);
    }

    [Fact]
    public async Task Share_links_open_a_protected_gallery_and_stop_at_their_download_limit()
    {
        var admin = await app.AdminAsync();
        Directory.CreateDirectory(Path.Combine(app.Library, "Shared"));
        TestImages.Write(Path.Combine(app.Library, "Shared", "s.jpg"), 400, 300, 90);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Shared Set", folder = "Shared", visibility = 2, password = "secret" });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var gid = g.GetProperty("id").GetInt32(); var slug = g.GetProperty("slug").GetString()!;
        var photoId = (await PhotoIdsAsync(admin, slug)).Single();

        var link = await (await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/links", new { label = "Coach", maxDownloads = 1 })).ReadAsync();
        var token = link.GetProperty("token").GetString()!;

        var guest = app.NewClient();
        var open = await guest.GetAsync($"/s/{token}");
        Assert.Equal(HttpStatusCode.Redirect, open.StatusCode);
        Assert.Equal($"/g/{slug}", open.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/g/{slug}")).StatusCode);

        await guest.AcceptLicenseAsync();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/dl/{photoId}?size=original")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await guest.GetAsync($"/dl/{photoId}?size=original")).StatusCode);
    }

    [Fact]
    public async Task Bad_or_revoked_share_links_go_to_the_expired_page()
    {
        var anon = app.NewClient();
        var r = await anon.GetAsync("/s/not-a-real-token");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/expired", r.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Pending_guest_uploads_stay_hidden_until_approved()
    {
        var admin = await app.AdminAsync();
        Directory.CreateDirectory(Path.Combine(app.Library, "Guests"));
        TestImages.Write(Path.Combine(app.Library, "Guests", "g.jpg"), 400, 300, 100);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Guest Night", folder = "Guests", visibility = 0, allowGuestUpload = true });
        var slug = (await r.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;

        var tmp = Path.Combine(app.Root, "guest.jpg");
        TestImages.Write(tmp, 320, 240, 30);
        var form = new MultipartFormDataContent { { new ByteArrayContent(await File.ReadAllBytesAsync(tmp)), "file", "guest.jpg" } };
        var up = await app.NewClient().PostAsync($"/api/g/{slug}/upload", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());

        Assert.Single(await PhotoIdsAsync(app.NewClient(), slug));      // only the library photo is visible
        Assert.Equal(2, (await PhotoIdsAsync(admin, slug)).Length);     // the admin also sees the pending one
    }

    [Fact]
    public async Task Deleting_a_gallery_leaves_the_original_files_alone()
    {
        var admin = await app.AdminAsync();
        Directory.CreateDirectory(Path.Combine(app.Library, "Temp"));
        var file = Path.Combine(app.Library, "Temp", "t.jpg");
        TestImages.Write(file, 400, 300, 50);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Temporary", folder = "Temp", visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");

        Assert.True((await admin.DeleteAsync($"/api/admin/galleries/{g.GetProperty("id").GetInt32()}")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.NewClient().GetAsync($"/api/g/{g.GetProperty("slug").GetString()}")).StatusCode);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Stats_and_settings_are_admin_only()
    {
        var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/admin/settings/site", new { name = "Hacked" })).StatusCode);
    }

    [Fact]
    public async Task Site_name_change_shows_up_in_the_page_title()
    {
        var admin = await app.AdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/settings/site", new { name = "Walsh Photography" })).StatusCode);
        var html = await app.NewClient().GetStringAsync("/");
        Assert.Contains("<title>Walsh Photography</title>", html);
        var j = await (await app.NewClient().GetAsync("/api/site")).ReadAsync();
        Assert.Equal("Walsh Photography", j.GetProperty("title").GetString());
    }
}
