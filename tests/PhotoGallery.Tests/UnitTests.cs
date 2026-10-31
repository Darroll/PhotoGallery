using Microsoft.Extensions.Configuration;

namespace PhotoGallery.Tests;

static class Cfg
{
    public static AppConfig Make(Dictionary<string, string?>? values = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pgunit-" + Guid.NewGuid().ToString("N"));
        var all = new Dictionary<string, string?> { ["DATA_DIR"] = dir, ["PHOTO_LIBRARY"] = dir, ["ADMIN_PASSWORD"] = "pw" };
        if (values != null) foreach (var kv in values) all[kv.Key] = kv.Value;
        return new AppConfig(new ConfigurationBuilder().AddInMemoryCollection(all).Build());
    }
}

public class PasswordTests
{
    [Fact]
    public void Hash_verifies_only_the_right_password()
    {
        var h = Passwords.Hash("correct horse");
        Assert.True(Passwords.Verify("correct horse", h));
        Assert.False(Passwords.Verify("wrong", h));
        Assert.False(Passwords.Verify("", h));
    }

    [Fact]
    public void Hashes_are_salted()
    {
        Assert.NotEqual(Passwords.Hash("same"), Passwords.Hash("same"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1.abc.AAAA.AAAA")]
    [InlineData("v2.1000.AAAA.AAAA")]
    public void Malformed_stored_values_never_verify(string? stored)
    {
        Assert.False(Passwords.Verify("anything", stored));
    }

    [Fact]
    public void FixedEquals_compares_content()
    {
        Assert.True(Passwords.FixedEquals("abc", "abc"));
        Assert.False(Passwords.FixedEquals("abc", "abd"));
        Assert.False(Passwords.FixedEquals("abc", "abcd"));
    }

    [Fact]
    public void Tokens_are_unique_and_url_safe()
    {
        var a = Passwords.NewToken(); var b = Passwords.NewToken();
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }
}

public class AppConfigTests
{
    [Fact]
    public void Password_login_is_on_without_SSO()
    {
        var c = Cfg.Make();
        Assert.False(c.OidcEnabled);
        Assert.True(c.AdminPasswordLogin);
    }

    [Fact]
    public void Password_login_turns_off_once_SSO_is_configured()
    {
        var c = Cfg.Make(new() { ["OIDC_AUTHORITY"] = "https://auth.example.test/realms/x", ["OIDC_CLIENT_ID"] = "photogallery" });
        Assert.True(c.OidcEnabled);
        Assert.False(c.AdminPasswordLogin);
    }

    [Fact]
    public void SSO_needs_both_authority_and_client_id()
    {
        Assert.False(Cfg.Make(new() { ["OIDC_AUTHORITY"] = "https://auth.example.test" }).OidcEnabled);
        Assert.False(Cfg.Make(new() { ["OIDC_CLIENT_ID"] = "x" }).OidcEnabled);
    }

    [Fact]
    public void Explicit_setting_overrides_the_SSO_default()
    {
        var sso = new Dictionary<string, string?> { ["OIDC_AUTHORITY"] = "https://auth.example.test", ["OIDC_CLIENT_ID"] = "x" };
        Assert.True(Cfg.Make(new(sso) { ["ADMIN_PASSWORD_LOGIN"] = "true" }).AdminPasswordLogin);
        Assert.False(Cfg.Make(new() { ["ADMIN_PASSWORD_LOGIN"] = "false" }).AdminPasswordLogin);
    }

    [Fact]
    public void Blank_or_missing_admin_password_means_the_admin_chooses_one_in_the_app()
    {
        var c = Cfg.Make(new() { ["ADMIN_PASSWORD"] = "  " });
        Assert.False(c.AdminPasswordSet);
        Assert.Equal("", c.AdminPassword);
        Assert.True(Cfg.Make(new() { ["ADMIN_PASSWORD"] = "a-long-password" }).AdminPasswordSet);
    }

    [Fact]
    public void Authority_trailing_slash_is_trimmed_and_admin_role_defaults()
    {
        var c = Cfg.Make(new() { ["OIDC_AUTHORITY"] = "https://auth.example.test/realms/x/", ["OIDC_CLIENT_ID"] = "x" });
        Assert.Equal("https://auth.example.test/realms/x", c.OidcAuthority);
        Assert.Equal("gallery-admin", c.OidcAdminRole);
    }

    [Fact]
    public void Resolve_refuses_paths_outside_the_library()
    {
        var c = Cfg.Make();
        Assert.NotNull(c.Resolve("Album"));
        Assert.Null(c.Resolve("../outside"));
        Assert.Null(c.Resolve("Album/../../outside"));
        Assert.Null(c.Resolve(""));
    }

    [Fact]
    public void Sizes_are_parsed_sorted_and_deduplicated()
    {
        Assert.Equal([800, 1600], Cfg.Make(new() { ["SIZES"] = "1600, 800,800" }).Sizes);
    }
}

public class LicenseSettingsTests
{
    [Fact]
    public void Defaults_are_a_non_commercial_license_that_is_on()
    {
        var l = new LicenseSettings().Sanitized();
        Assert.True(l.Enabled);
        Assert.True(l.RequireAcceptance);
        Assert.Contains("non-commercial", l.Text);
        Assert.Contains("make money", l.Text);
    }

    [Fact]
    public void Empty_text_and_title_fall_back_to_the_defaults()
    {
        var l = new LicenseSettings { Text = "   ", Title = "" }.Sanitized();
        Assert.Equal(LicenseSettings.DefaultText.Replace("\r\n", "\n").Trim(), l.Text);
        Assert.Equal("Photo License Agreement", l.Title);
    }

    [Fact]
    public void Overlong_fields_are_clipped()
    {
        var l = new LicenseSettings { Text = new string('x', LicenseSettings.MaxText + 500), Holder = new string('h', 500), Title = new string('t', 500) }.Sanitized();
        Assert.Equal(LicenseSettings.MaxText, l.Text.Length);
        Assert.Equal(100, l.Holder.Length);
        Assert.Equal(100, l.Title.Length);
    }

    [Fact]
    public void Placeholders_are_filled_for_visitors()
    {
        var l = new LicenseSettings { Holder = "Jane Doe", Contact = "jane@example.test", Text = "{holder}|{site}|{year}|{contact}" }.Sanitized();
        var v = l.ForVisitors("My Site");
        Assert.Equal($"Jane Doe|My Site|{DateTime.UtcNow.Year}|jane@example.test", v.Text);
    }

    [Fact]
    public void Holder_and_contact_fall_back_to_the_site_name()
    {
        var l = new LicenseSettings { Text = "{holder}/{contact}" }.Sanitized();
        var v = l.ForVisitors("My Site");
        Assert.Equal("My Site/My Site through this site", v.Text);
    }

    [Fact]
    public void Acceptance_is_only_required_while_the_license_is_enabled()
    {
        var on = new LicenseSettings { Enabled = true, RequireAcceptance = true }.Sanitized().ForVisitors("s");
        var off = new LicenseSettings { Enabled = false, RequireAcceptance = true }.Sanitized().ForVisitors("s");
        var optional = new LicenseSettings { Enabled = true, RequireAcceptance = false }.Sanitized().ForVisitors("s");
        Assert.True(on.Require);
        Assert.False(off.Require);
        Assert.False(optional.Require);
    }

    [Fact]
    public void Version_changes_when_the_wording_changes()
    {
        var a = new LicenseSettings { Text = "one" }.Sanitized().ForVisitors("s");
        var b = new LicenseSettings { Text = "one" }.Sanitized().ForVisitors("s");
        var c = new LicenseSettings { Text = "two" }.Sanitized().ForVisitors("s");
        Assert.Equal(a.Version, b.Version);
        Assert.NotEqual(a.Version, c.Version);
    }
}

public class SettingsServiceTests
{
    [Fact]
    public void License_survives_a_restart()
    {
        var cfg = Cfg.Make();
        new SettingsService(cfg).SetLicense(new LicenseSettings { Title = "Mine", Holder = "Me", Text = "custom terms", RequireAcceptance = false });
        var again = new SettingsService(cfg).License;
        Assert.Equal("Mine", again.Title);
        Assert.Equal("Me", again.Holder);
        Assert.Equal("custom terms", again.Text);
        Assert.False(again.RequireAcceptance);
    }

    [Fact]
    public void Site_name_is_trimmed_clipped_and_never_empty()
    {
        var s = new SettingsService(Cfg.Make(new() { ["SITE_TITLE"] = "Start" }));
        Assert.Equal("Start", s.SiteName);
        Assert.Equal("Photos", s.SetSiteName("   "));
        Assert.Equal(80, s.SetSiteName(new string('n', 200)).Length);
    }

    [Fact]
    public void Watermark_values_are_clamped()
    {
        var s = new SettingsService(Cfg.Make());
        var w = s.SetWatermark(new WatermarkSettings { Mode = "bogus", Position = "nowhere", Opacity = 500, SizePercent = 0 });
        Assert.Equal("none", w.Mode);
        Assert.Equal("bottom-right", w.Position);
        Assert.Equal(100, w.Opacity);
        Assert.Equal(5, w.SizePercent);
    }

    [Fact]
    public void Unknown_home_order_falls_back()
    {
        var s = new SettingsService(Cfg.Make());
        Assert.Equal("created-desc", s.SetHome(new HomeSettings { Order = "nonsense" }).Order);
        Assert.Equal("name-asc", s.SetHome(new HomeSettings { Order = "name-asc" }).Order);
    }
}

public class GalleryRulesTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void YouTube_ids_are_extracted_only_from_youtube_links(string? url, string? expected)
    {
        Assert.Equal(expected, GalleryRules.YouTubeId(url));
    }

    [Fact]
    public void Gallery_size_allow_list_is_applied()
    {
        var cfg = Cfg.Make(new() { ["SIZES"] = "1080,1920,2560" });
        Assert.Equal([1080, 1920, 2560], GalleryRules.Sizes(new Gallery(), cfg));
        Assert.Equal([1920], GalleryRules.Sizes(new Gallery { AllowedSizes = "1920,9999" }, cfg));
    }

    [Fact]
    public void Gallery_format_allow_list_is_applied()
    {
        Assert.Equal(ImageService.LosslessFormats, GalleryRules.Formats(new Gallery()));
        Assert.Equal(["png"], GalleryRules.Formats(new Gallery { AllowedFormats = "PNG,bmp" }));
    }

    [Fact]
    public void Home_page_orders_galleries()
    {
        var a = new Gallery { Title = "alpha", CreatedAt = new DateTime(2024, 1, 1) };
        var b = new Gallery { Title = "Bravo", CreatedAt = new DateTime(2025, 1, 1) };
        var c = new Gallery { Title = "charlie", CreatedAt = new DateTime(2023, 1, 1) };
        var list = new[] { c, a, b };
        Assert.Equal(["alpha", "Bravo", "charlie"], GalleryRules.OrderGalleries(list, "name-asc").Select(g => g.Title));
        Assert.Equal(["charlie", "Bravo", "alpha"], GalleryRules.OrderGalleries(list, "name-desc").Select(g => g.Title));
        Assert.Equal(["Bravo", "alpha", "charlie"], GalleryRules.OrderGalleries(list, "created-desc").Select(g => g.Title));
        Assert.Equal(["charlie", "alpha", "Bravo"], GalleryRules.OrderGalleries(list, "created-asc").Select(g => g.Title));
    }
}
