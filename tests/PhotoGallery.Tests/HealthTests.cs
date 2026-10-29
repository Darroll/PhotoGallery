using System.Net;

namespace PhotoGallery.Tests;

public class HealthTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Health_reports_an_overall_status_and_coloured_checks()
    {
        var admin = await app.AdminAsync();
        var r = await admin.GetAsync("/api/admin/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.ReadAsync();
        Assert.Contains(j.GetProperty("overall").GetString(), new[] { "ok", "warn", "bad" });
        var checks = j.GetProperty("checks").EnumerateArray().ToList();
        Assert.NotEmpty(checks);
        Assert.All(checks, c => Assert.Contains(c.GetProperty("status").GetString(), new[] { "ok", "warn", "bad", "off" }));
        Assert.Contains(checks, c => c.GetProperty("id").GetString() == "library");
    }
}
