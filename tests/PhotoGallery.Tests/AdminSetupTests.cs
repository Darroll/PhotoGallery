using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoGallery.Tests;

/// <summary>No ADMIN_PASSWORD in the container: the admin chooses one in the app on first visit.</summary>
public class AdminSetupTests
{
    sealed class NoPasswordFactory : AppFactory
    {
        public NoPasswordFactory() : base(new() { ["ADMIN_PASSWORD"] = null }) { }
        public string Code => Services.GetRequiredService<AdminCredentials>().SetupCode;
    }

    static async Task<JsonElement> Me(HttpClient c) => await c.GetFromJsonAsync<JsonElement>("/api/admin/me");

    [Fact]
    public async Task First_visit_offers_setup_and_login_is_refused_until_a_password_exists()
    {
        using var f = new NoPasswordFactory();
        var c = f.NewClient();
        var me = await Me(c);
        Assert.True(me.GetProperty("setupRequired").GetBoolean());
        Assert.False(me.GetProperty("admin").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/admin/login", new { password = "" })).StatusCode);
    }

    [Fact]
    public async Task Setup_needs_the_code_when_the_request_is_not_from_the_home_network()
    {
        using var f = new NoPasswordFactory();
        var c = f.NewClient();   // the test server has no client address, so this counts as "not local"
        Assert.True((await Me(c)).GetProperty("setupNeedsCode").GetBoolean());
        var wrong = await c.PostAsJsonAsync("/api/admin/setup", new { password = "a-long-password-1", code = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.True((await Me(c)).GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task Setup_with_the_code_signs_in_stores_the_password_and_closes_setup()
    {
        using var f = new NoPasswordFactory();
        var c = f.NewClient();
        var r = await c.PostAsJsonAsync("/api/admin/setup", new { password = "a-long-password-1", code = f.Code });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.True((await Me(c)).GetProperty("admin").GetBoolean());
        Assert.True(File.Exists(Path.Combine(f.Data, "admin-password")));
        Assert.DoesNotContain("a-long-password-1", File.ReadAllText(Path.Combine(f.Data, "admin-password")));
        // setup cannot be used again to take over
        Assert.Equal(HttpStatusCode.NotFound, (await f.NewClient().PostAsJsonAsync("/api/admin/setup", new { password = "another-long-password", code = f.Code })).StatusCode);
        // the chosen password signs in; a wrong one does not
        Assert.Equal(HttpStatusCode.NoContent, (await f.NewClient().PostAsJsonAsync("/api/admin/login", new { password = "a-long-password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().PostAsJsonAsync("/api/admin/login", new { password = "a-long-password-2" })).StatusCode);
    }

    [Fact]
    public async Task A_short_password_is_refused_with_a_reason()
    {
        using var f = new NoPasswordFactory();
        var r = await f.NewClient().PostAsJsonAsync("/api/admin/setup", new { password = "short", code = f.Code });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("at least", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Password_can_be_changed_with_the_current_one_and_the_password_file_survives_a_restart()
    {
        using var f = new NoPasswordFactory();
        var c = f.NewClient();
        await c.PostAsJsonAsync("/api/admin/setup", new { password = "a-long-password-1", code = f.Code });
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/admin/password", new { current = "wrong-password-x", @new = "brand-new-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/admin/password", new { current = "a-long-password-1", @new = "brand-new-password" })).StatusCode);
        var cfg = Cfg.Make(new() { ["DATA_DIR"] = f.Data, ["PHOTO_LIBRARY"] = f.Library, ["ADMIN_PASSWORD"] = null });
        var again = new AdminCredentials(cfg);   // what a restarted container would load
        Assert.False(again.SetupRequired);
        Assert.True(again.Verify("brand-new-password"));
        Assert.False(again.Verify("a-long-password-1"));
    }

    [Fact]
    public async Task A_password_set_in_the_container_wins_and_cannot_be_changed_in_the_app()
    {
        using var f = new AppFactory();
        var admin = await f.AdminAsync();
        var me = await Me(admin);
        Assert.False(me.GetProperty("setupRequired").GetBoolean());
        Assert.True(me.GetProperty("passwordFromContainer").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/password", new { current = AppFactory.AdminPassword, @new = "brand-new-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.NewClient().PostAsJsonAsync("/api/admin/setup", new { password = "a-long-password-1", code = "x" })).StatusCode);
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.17.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:db8::1", false)]
    public void Private_addresses_are_recognised(string ip, bool expected) =>
        Assert.Equal(expected, AdminCredentials.IsPrivate(IPAddress.Parse(ip)));
}
