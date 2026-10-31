using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

public class UpdateCheckTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Theory]
    [InlineData("[{\"name\":\"v1.0.0\"},{\"name\":\"v1.10.0\"},{\"name\":\"v1.2.0\"},{\"name\":\"edge\"},{\"name\":\"v2.0.0-rc1\"}]", "1.10.0")]
    [InlineData("[{\"name\":\"latest\"}]", null)]
    [InlineData("[]", null)]
    public void Newest_picks_the_highest_plain_release_tag(string json, string? expected) =>
        Assert.Equal(expected, UpdateChecker.Newest(json));

    [Fact]
    public async Task Updates_are_off_by_default_and_nothing_is_fetched()
    {
        var admin = await app.AdminAsync();
        var s = await (await admin.GetAsync("/api/admin/settings/updates")).ReadAsync();
        Assert.False(s.GetProperty("enabled").GetBoolean());
        var checks = (await (await admin.GetAsync("/api/admin/health")).ReadAsync()).GetProperty("checks").EnumerateArray().ToList();
        var u = Assert.Single(checks, c => c.GetProperty("id").GetString() == "updates");
        Assert.Equal("off", u.GetProperty("status").GetString());
        var checker = app.Services.GetService(typeof(UpdateChecker)) as UpdateChecker;
        Assert.Null(checker!.Last);
    }

    [Fact]
    public async Task A_newer_tag_turns_the_check_amber_and_the_same_version_is_green()
    {
        var checker = (UpdateChecker)app.Services.GetService(typeof(UpdateChecker))!;
        var current = AppVersion();
        string Reply(string v) => "[{\"name\":\"v" + v + "\"},{\"name\":\"v0.1.0\"}]";

        checker.Http = () => new HttpClient(new Stub(Reply("99.0.0")));
        var admin = await app.AdminAsync();
        var r = await admin.PutAsJsonAsync("/api/admin/settings/updates", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("99.0.0", (await r.ReadAsync()).GetProperty("latest").GetString());
        var u = await UpdateCheckAsync(admin);
        Assert.Equal("warn", u.GetProperty("status").GetString());
        Assert.Contains("99.0.0", u.GetProperty("value").GetString());

        checker.Http = () => new HttpClient(new Stub(Reply(current)));
        await checker.CheckAsync();
        Assert.Equal("ok", (await UpdateCheckAsync(admin)).GetProperty("status").GetString());

        checker.Http = () => new HttpClient(new Stub(null));
        await checker.CheckAsync();
        Assert.Equal("warn", (await UpdateCheckAsync(admin)).GetProperty("status").GetString());
    }

    string AppVersion() => ((AppConfig)app.Services.GetService(typeof(AppConfig))!).Version;

    static async Task<System.Text.Json.JsonElement> UpdateCheckAsync(HttpClient admin) =>
        (await (await admin.GetAsync("/api/admin/health")).ReadAsync()).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "updates");

    sealed class Stub(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(body == null ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
