using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
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

public class PortExposureTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("100.100.1.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("203.0.113.9", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:db8::1", false)]
    public void Private_addresses_are_recognised(string ip, bool isPrivate) =>
        Assert.Equal(isPrivate, ProxyWatch.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public void A_direct_internet_connection_is_noted_but_a_lan_one_is_not()
    {
        var w = new ProxyWatch();
        var lan = new DefaultHttpContext(); lan.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.9");
        w.NoteDirect(lan);
        Assert.Null(w.RecentDirect(DateTime.UtcNow));
        var wan = new DefaultHttpContext(); wan.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        w.NoteDirect(wan);
        Assert.Equal("203.0.113.9", w.RecentDirect(DateTime.UtcNow)!.Value.Peer);
        Assert.Null(w.RecentDirect(DateTime.UtcNow.AddHours(25)));
    }

    [Fact]
    public async Task Exposure_check_is_green_on_a_fresh_install()
    {
        var admin = await app.AdminAsync();
        var checks = (await (await admin.GetAsync("/api/admin/health")).ReadAsync()).GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal("ok", Assert.Single(checks, c => c.GetProperty("id").GetString() == "exposure").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_new_gallery_defaults_to_protected()
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Default", folder });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.ReadAsync()).GetProperty("gallery").GetProperty("visibility").GetInt32());
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
