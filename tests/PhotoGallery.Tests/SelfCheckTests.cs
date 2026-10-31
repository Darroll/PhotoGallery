using System.Net;
using Microsoft.Extensions.Configuration;

namespace PhotoGallery.Tests;

public class ProxyCheckQuietTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Proxy_check_is_green_when_no_ignored_headers_were_seen()
    {
        var admin = await app.AdminAsync();
        var checks = (await (await admin.GetAsync("/api/admin/health")).ReadAsync()).GetProperty("checks").EnumerateArray().ToList();
        var proxy = Assert.Single(checks, c => c.GetProperty("id").GetString() == "proxy");
        Assert.Equal("ok", proxy.GetProperty("status").GetString());
    }
}

public class ProxyCheckWarnTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Proxy_headers_from_an_untrusted_address_turn_the_check_amber()
    {
        var visitor = app.NewClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/license");
        req.Headers.Add("X-Forwarded-For", "203.0.113.9");
        Assert.Equal(HttpStatusCode.OK, (await visitor.SendAsync(req)).StatusCode);

        var admin = await app.AdminAsync();
        var checks = (await (await admin.GetAsync("/api/admin/health")).ReadAsync()).GetProperty("checks").EnumerateArray().ToList();
        var proxy = Assert.Single(checks, c => c.GetProperty("id").GetString() == "proxy");
        Assert.Equal("warn", proxy.GetProperty("status").GetString());
        Assert.Contains("TRUSTED_PROXIES", proxy.GetProperty("detail").GetString());
    }
}

public class FolderAccessTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Health_reports_a_writable_data_folder()
    {
        var admin = await app.AdminAsync();
        var checks = (await (await admin.GetAsync("/api/admin/health")).ReadAsync()).GetProperty("checks").EnumerateArray().ToList();
        var data = Assert.Single(checks, c => c.GetProperty("id").GetString() == "datadir");
        Assert.Equal("ok", data.GetProperty("status").GetString());
        Assert.Empty(Directory.GetFiles(app.Data, ".write-test-*"));
    }

    [Fact]
    public void Probe_reports_folders_that_cannot_be_used()
    {
        var gone = Path.Combine(Path.GetTempPath(), "pg-missing-" + Guid.NewGuid().ToString("N"));
        var cfg = new AppConfig(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PHOTO_LIBRARY"] = gone, ["DATA_DIR"] = Path.Combine(gone, "data"),
        }).Build());
        var r = FolderAccess.Probe(cfg);
        Assert.False(r.LibraryReadable);
        Assert.False(r.DataWritable);
        Assert.NotNull(r.DataError);
    }
}
