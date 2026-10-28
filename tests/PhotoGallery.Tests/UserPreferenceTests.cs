using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoGallery.Tests;

/// <summary>Lets a test act as a signed-in account by sending X-Test-Sub (a real sign-in needs a live identity provider).</summary>
public class UserFactory : AppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddTransient<IStartupFilter, TestUserFilter>());
    }

    /// <summary>A browser session of the account with this provider id.</summary>
    public HttpClient AsUser(string sub)
    {
        var c = NewClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        return c;
    }

    class TestUserFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Sub", out var sub))
                    ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, sub.ToString()), new Claim(ClaimTypes.Name, "Test " + sub)], "test"));
                await n();
            });
            next(app);
        };
    }
}

public class UserPreferencesUnitTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var json = new UserPreferences { Theme = "warm" }.ToJson();
        Assert.Equal("warm", UserPreferences.Parse(json).Theme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Missing_or_broken_stored_values_mean_no_preferences(string? stored)
    {
        Assert.Null(UserPreferences.Parse(stored).Theme);
    }

    [Fact]
    public void A_removed_theme_is_dropped_and_auto_is_kept_as_a_real_choice()
    {
        Assert.Null(UserPreferences.Parse("{\"theme\":\"retired-theme\"}").Theme);
        Assert.Equal("auto", UserPreferences.Parse("{\"theme\":\"auto\"}").Theme);
    }
}

public class UserPreferencesSchemaTests
{
    [Fact]
    public void Existing_databases_get_the_new_column_without_losing_users()
    {
        var file = Path.Combine(Path.GetTempPath(), "pgschema-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var opts = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={file}").Options;
            using (var db = new AppDb(opts))
            {
                // a database from before preferences existed: the current schema minus the new column
                db.Database.EnsureCreated();
                db.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" DROP COLUMN \"Preferences\"");
                db.Database.ExecuteSqlRaw("INSERT INTO \"Users\" VALUES ('old-user', 'o@example.test', 'Old', '2025-01-01', '2025-01-02')");
            }
            using (var db = new AppDb(opts)) { Schema.Upgrade(db); Schema.Upgrade(db); }   // twice: it must be repeatable
            using (var db = new AppDb(opts))
            {
                var u = db.Users.Single(x => x.Sub == "old-user");
                Assert.Equal("Old", u.Name);
                Assert.Equal("{}", u.Preferences);
                Assert.Null(UserPreferences.Parse(u.Preferences).Theme);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch { }
        }
    }
}

public class UserPreferencesApiTests(UserFactory app) : IClassFixture<UserFactory>
{
    static JsonElement Theme(JsonElement me) => me.GetProperty("preferences").GetProperty("theme");

    [Fact]
    public async Task Anonymous_visitors_have_no_preferences_and_cannot_save_any()
    {
        var anon = app.NewClient();
        var me = await (await anon.GetAsync("/api/me")).ReadAsync();
        Assert.Equal(JsonValueKind.Null, me.GetProperty("preferences").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/me/preferences", new { theme = "dark" })).StatusCode);
    }

    [Fact]
    public async Task The_password_admin_has_no_account_so_preferences_stay_in_the_browser()
    {
        var admin = await app.AdminAsync();
        var me = await (await admin.GetAsync("/api/me")).ReadAsync();
        Assert.True(me.GetProperty("signedIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("preferences").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.PutAsJsonAsync("/api/me/preferences", new { theme = "dark" })).StatusCode);
    }

    [Fact]
    public async Task A_new_account_starts_with_no_theme_choice()
    {
        var me = await (await app.AsUser("fresh-1").GetAsync("/api/me")).ReadAsync();
        Assert.True(me.GetProperty("signedIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Theme(me).ValueKind);
    }

    [Fact]
    public async Task A_saved_theme_follows_the_account_to_another_device()
    {
        var laptop = app.AsUser("traveller");
        Assert.Equal(HttpStatusCode.OK, (await laptop.PutAsJsonAsync("/api/me/preferences", new { theme = "warm" })).StatusCode);

        var phone = app.AsUser("traveller");      // a different browser session, same account
        var me = await (await phone.GetAsync("/api/me")).ReadAsync();
        Assert.Equal("warm", Theme(me).GetString());
    }

    [Fact]
    public async Task Accounts_do_not_see_each_others_preferences()
    {
        await app.AsUser("alice").PutAsJsonAsync("/api/me/preferences", new { theme = "midnight" });
        await app.AsUser("bob").PutAsJsonAsync("/api/me/preferences", new { theme = "white" });
        Assert.Equal("midnight", Theme(await (await app.AsUser("alice").GetAsync("/api/me")).ReadAsync()).GetString());
        Assert.Equal("white", Theme(await (await app.AsUser("bob").GetAsync("/api/me")).ReadAsync()).GetString());
    }

    [Fact]
    public async Task Choosing_auto_is_remembered_as_a_choice_and_null_clears_it()
    {
        var c = app.AsUser("indecisive");
        await c.PutAsJsonAsync("/api/me/preferences", new { theme = "auto" });
        Assert.Equal("auto", Theme(await (await c.GetAsync("/api/me")).ReadAsync()).GetString());
        await c.PutAsJsonAsync("/api/me/preferences", new { theme = (string?)null });
        Assert.Equal(JsonValueKind.Null, Theme(await (await c.GetAsync("/api/me")).ReadAsync()).ValueKind);
    }

    [Fact]
    public async Task Unknown_themes_and_requests_without_the_csrf_header_are_refused()
    {
        var c = app.AsUser("picky");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/me/preferences", new { theme = "hot-pink" })).StatusCode);

        var noHeader = app.CreateClient();
        noHeader.DefaultRequestHeaders.Add("X-Test-Sub", "picky");
        Assert.Equal(HttpStatusCode.BadRequest, (await noHeader.PutAsJsonAsync("/api/me/preferences", new { theme = "dark" })).StatusCode);

        Assert.Equal(JsonValueKind.Null, Theme(await (await c.GetAsync("/api/me")).ReadAsync()).ValueKind);
    }

    [Fact]
    public async Task Saving_keeps_the_profile_that_single_sign_on_recorded()
    {
        await app.WithDbAsync(async db =>
        {
            db.Users.Add(new AppUser { Sub = "sso-person", Email = "person@example.test", Name = "Pat Person" });
            return await db.SaveChangesAsync();
        });
        await app.AsUser("sso-person").PutAsJsonAsync("/api/me/preferences", new { theme = "dark" });
        var u = await app.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(x => x.Sub == "sso-person"));
        Assert.Equal("person@example.test", u.Email);
        Assert.Equal("Pat Person", u.Name);
        Assert.Equal("dark", UserPreferences.Parse(u.Preferences).Theme);
    }

    [Fact]
    public async Task A_theme_that_was_later_removed_reads_back_as_no_choice()
    {
        await app.WithDbAsync(async db =>
        {
            db.Users.Add(new AppUser { Sub = "stale", Preferences = "{\"theme\":\"retired-theme\"}" });
            return await db.SaveChangesAsync();
        });
        var me = await (await app.AsUser("stale").GetAsync("/api/me")).ReadAsync();
        Assert.Equal(JsonValueKind.Null, Theme(me).ValueKind);
    }
}
