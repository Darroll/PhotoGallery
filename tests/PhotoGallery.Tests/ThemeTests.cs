using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace PhotoGallery.Tests;

public class ThemeSettingsTests
{
    [Fact]
    public void Defaults_follow_the_device_and_let_visitors_choose()
    {
        var t = new ThemeSettings().Sanitized();
        Assert.Equal("auto", t.Default);
        Assert.True(t.AllowVisitorChoice);
    }

    [Theory]
    [InlineData("midnight", "midnight")]
    [InlineData("contrast", "contrast")]
    [InlineData("neon-pink", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    public void Unknown_defaults_fall_back_to_auto(string? given, string expected)
    {
        Assert.Equal(expected, new ThemeSettings { Default = given! }.Sanitized().Default);
    }

    [Fact]
    public void Theme_ids_are_unique_lowercase_words_and_auto_comes_first()
    {
        Assert.Equal("auto", ThemeSettings.All[0].Id);
        Assert.Equal(ThemeSettings.All.Length, ThemeSettings.All.Select(t => t.Id).Distinct().Count());
        Assert.All(ThemeSettings.All, t => Assert.Matches("^[a-z]+$", t.Id));
        Assert.All(ThemeSettings.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Label)));
    }

    [Fact]
    public void Theme_settings_survive_a_restart()
    {
        var cfg = Cfg.Make();
        new SettingsService(cfg).SetTheme(new ThemeSettings { Default = "warm", AllowVisitorChoice = false });
        var again = new SettingsService(cfg).Theme;
        Assert.Equal("warm", again.Default);
        Assert.False(again.AllowVisitorChoice);
    }
}

/// <summary>The stylesheet has to define every theme the server offers, and each one completely.</summary>
public class ThemeStylesheetTests
{
    static readonly string[] RequiredTokens =
    [
        "--bg", "--fg", "--muted", "--card", "--line", "--accent", "--accent-fg", "--danger", "--ok",
        "--radius", "--radius-sm", "--card-gap", "--grid-gap", "--shadow", "--font", "--font-heading", "color-scheme"
    ];

    static string Css()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "wwwroot", "style.css");
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new FileNotFoundException("src/wwwroot/style.css not found above " + AppContext.BaseDirectory);
    }

    static string Block(string css, string selector)
    {
        var m = Regex.Match(css, Regex.Escape(selector) + @"\s*\{([^}]*)\}");
        Assert.True(m.Success, $"style.css has no block for {selector}");
        return m.Groups[1].Value;
    }

    [Fact]
    public void Every_fixed_theme_has_a_complete_block()
    {
        var css = Css();
        foreach (var t in ThemeSettings.All.Where(t => t.Id != "auto"))
        {
            var block = Block(css, $"html[data-theme=\"{t.Id}\"]");
            foreach (var token in RequiredTokens)
                Assert.True(Regex.IsMatch(block, @"(^|[\s;])" + Regex.Escape(token) + @"\s*:"), $"theme '{t.Id}' does not set {token}");
        }
    }

    [Fact]
    public void The_auto_theme_defines_every_token_and_follows_the_device()
    {
        var css = Css();
        var root = Block(css, ":root");
        foreach (var token in RequiredTokens) Assert.Contains(token + ":", root);
        Assert.Matches(@"@media \(prefers-color-scheme: dark\)\s*\{\s*:root:not\(\[data-theme\]\)", css);
    }

    [Fact]
    public void Dark_themes_declare_a_dark_color_scheme_and_light_ones_a_light_one()
    {
        var css = Css();
        foreach (var id in new[] { "dark", "midnight", "contrast" }) Assert.Contains("color-scheme: dark", Block(css, $"html[data-theme=\"{id}\"]"));
        foreach (var id in new[] { "light", "white", "warm" }) Assert.Contains("color-scheme: light", Block(css, $"html[data-theme=\"{id}\"]"));
    }

    [Fact]
    public void Themes_do_not_load_remote_fonts_because_the_site_policy_forbids_them()
    {
        var css = Css();
        Assert.DoesNotContain("@import", css);
        Assert.DoesNotContain("fonts.googleapis", css);
    }
}

public class ThemeApiTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Site_info_lists_the_themes_the_default_and_whether_visitors_may_choose()
    {
        var j = (await (await app.NewClient().GetAsync("/api/site")).ReadAsync()).GetProperty("theme");
        Assert.Equal("auto", j.GetProperty("default").GetString());
        Assert.True(j.GetProperty("allowChoice").GetBoolean());
        var ids = j.GetProperty("themes").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToArray();
        Assert.Equal(ThemeSettings.All.Select(t => t.Id), ids);
    }

    [Fact]
    public async Task Only_the_admin_can_change_the_theme_settings()
    {
        var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/settings/theme")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/admin/settings/theme", new { @default = "dark", allowVisitorChoice = true })).StatusCode);
    }

    [Fact]
    public async Task Unknown_themes_are_rejected()
    {
        var admin = await app.AdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/settings/theme", new { @default = "hot-pink", allowVisitorChoice = true })).StatusCode);
    }
}

/// <summary>Changes the site default, so it gets its own app instance.</summary>
public class ThemeDefaultTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Admin_default_reaches_the_page_before_it_paints_and_the_public_api()
    {
        var before = await app.NewClient().GetStringAsync("/");
        Assert.Contains("data-default-theme=\"auto\"", before);
        Assert.Contains("data-theme-choice=\"1\"", before);

        var admin = await app.AdminAsync();
        var put = await admin.PutAsJsonAsync("/api/admin/settings/theme", new { @default = "midnight", allowVisitorChoice = false });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = await app.NewClient().GetStringAsync("/");
        Assert.Contains("data-default-theme=\"midnight\"", after);
        Assert.Contains("data-theme-choice=\"0\"", after);
        Assert.DoesNotContain("__THEME", after);

        var site = (await (await app.NewClient().GetAsync("/api/site")).ReadAsync()).GetProperty("theme");
        Assert.Equal("midnight", site.GetProperty("default").GetString());
        Assert.False(site.GetProperty("allowChoice").GetBoolean());
    }

    [Fact]
    public async Task The_early_theme_script_is_served_and_loaded_first()
    {
        var c = app.NewClient();
        var js = await c.GetAsync("/theme.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Contains("pg-theme", await js.Content.ReadAsStringAsync());
        var html = await c.GetStringAsync("/");
        Assert.True(html.IndexOf("/theme.js", StringComparison.Ordinal) < html.IndexOf("/style.css", StringComparison.Ordinal), "theme.js must load before the stylesheet");
    }
}
