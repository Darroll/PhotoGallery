using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace PhotoGallery.Tests;

public class LinkHealthTests(AppFactory app) : IClassFixture<AppFactory>
{
    static string NewUrl() => $"https://health-{Guid.NewGuid():N}.example.org/page";

    static async Task<T> WithProbeAsync<T>(Func<Uri, (SafeHttp.Reach, int?)> answer, Func<Task<T>> body)
    {
        var original = LinkHealth.Probe;
        try
        {
            LinkHealth.Probe = uri => Task.FromResult(answer(uri));
            return await body();
        }
        finally { LinkHealth.Probe = original; }
    }

    [Fact]
    public async Task A_link_is_called_broken_after_two_failed_checks_in_a_row_or_at_once_when_the_admin_asks()
    {
        var cfg = app.Services.GetRequiredService<AppConfig>();
        var url = NewUrl();
        await WithProbeAsync(_ => (SafeHttp.Reach.Gone, 404), async () =>
        {
            Assert.Equal("suspect", (await LinkHealth.CheckAsync(cfg, url, manual: false)).Status);
            Assert.Equal("broken", (await LinkHealth.CheckAsync(cfg, url, manual: false)).Status);
            return 0;
        });
        // it comes back
        await WithProbeAsync(_ => (SafeHttp.Reach.Ok, 200), async () =>
        {
            var r = await LinkHealth.CheckAsync(cfg, url, manual: false);
            Assert.Equal("ok", r.Status);
            Assert.Equal(0, r.Failures);
            return 0;
        });

        var other = NewUrl();
        await WithProbeAsync(_ => (SafeHttp.Reach.Unreachable, null), async () =>
        {
            Assert.Equal("broken", (await LinkHealth.CheckAsync(cfg, other, manual: true)).Status);
            return 0;
        });
    }

    [Fact]
    public async Task Sites_that_turn_robots_away_and_local_addresses_are_never_called_broken()
    {
        var cfg = app.Services.GetRequiredService<AppConfig>();
        var url = NewUrl();
        await WithProbeAsync(_ => (SafeHttp.Reach.Unsure, 403), async () =>
        {
            for (var i = 0; i < 3; i++) Assert.Equal("unsure", (await LinkHealth.CheckAsync(cfg, url, manual: true)).Status);
            return 0;
        });
        await WithProbeAsync(_ => (SafeHttp.Reach.NotChecked, null), async () =>
        {
            Assert.Equal("unchecked", (await LinkHealth.CheckAsync(cfg, NewUrl(), manual: true)).Status);
            return 0;
        });
    }

    [Fact]
    public async Task The_probe_does_not_contact_local_addresses_and_does_not_call_them_unreachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var (reach, _) = await SafeHttp.ProbeAsync(new Uri($"http://127.0.0.1:{port}/"));
        Assert.Equal(SafeHttp.Reach.NotChecked, reach);
        Assert.Equal(SafeHttp.Reach.NotChecked, (await SafeHttp.ProbeAsync(new Uri("http://192.168.2.3/"))).reach);
        Assert.Equal(SafeHttp.Reach.NotChecked, (await SafeHttp.ProbeAsync(new Uri("ftp://example.com/"))).reach);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task The_gallery_page_and_Overview_report_links_that_do_not_open()
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Link Health", folder = app.NewFolder(1), visibility = 0 });
        var id = (await r.ReadAsync()).GetProperty("gallery").GetProperty("id").GetInt32();
        var good = NewUrl();
        var dead = NewUrl();
        var video = "https://www.youtube.com/watch?v=abcdefghijk";
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { footageLinks = new object[] {
            new { label = "Good", url = good, category = "external" },
            new { label = "Dead", url = dead, category = "external" },
            new { label = "Game", url = video } } });

        var asked = new List<Uri>();
        await WithProbeAsync(uri =>
        {
            lock (asked) asked.Add(uri);
            return uri.ToString().Contains("health-") && uri.ToString() == dead ? (SafeHttp.Reach.Gone, 404) : (SafeHttp.Reach.Ok, 200);
        }, async () =>
        {
            var check = await (await admin.PostAsync($"/api/admin/galleries/{id}/links/check", null)).ReadAsync();
            Assert.True(check.GetProperty("enabled").GetBoolean());
            var items = check.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("url").GetString()!, x => x.GetProperty("status").GetString());
            Assert.Equal("ok", items[good]);
            Assert.Equal("broken", items[dead]);
            Assert.Equal("ok", items[video]);

            // a YouTube link is checked through its oEmbed answer
            Assert.Contains(asked, u => u.Host == "www.youtube.com" && u.AbsolutePath == "/oembed" && u.Query.Contains("abcdefghijk"));

            var again = await (await admin.GetAsync($"/api/admin/galleries/{id}/links/health")).ReadAsync();
            Assert.Equal(3, again.GetProperty("items").GetArrayLength());

            var health = await (await admin.GetAsync("/api/admin/health")).ReadAsync();
            var links = health.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "links");
            Assert.Equal("warn", links.GetProperty("status").GetString());
            Assert.Contains("Dead", links.GetProperty("detail").GetString());
            return 0;
        });

        // switched off: nothing is asked
        await admin.PutAsJsonAsync("/api/admin/settings/link-previews", new { enabled = false });
        try
        {
            var before = asked.Count;
            await WithProbeAsync(uri => { lock (asked) asked.Add(uri); return (SafeHttp.Reach.Ok, 200); }, async () =>
            {
                var off = await (await admin.PostAsync($"/api/admin/galleries/{id}/links/check", null)).ReadAsync();
                Assert.False(off.GetProperty("enabled").GetBoolean());
                return 0;
            });
            Assert.Equal(before, asked.Count);
        }
        finally { await admin.PutAsJsonAsync("/api/admin/settings/link-previews", new { enabled = true }); }
    }

    [Fact]
    public async Task Links_keep_their_order_and_notes()
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Link Notes", folder = app.NewFolder(1), visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var id = g.GetProperty("id").GetInt32();
        var slug = g.GetProperty("slug").GetString()!;
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { footageLinks = new object[] {
            new { label = "Third", url = "https://example.com/3", category = "external", note = "  The long one  " },
            new { label = "First", url = "https://example.com/1", category = "external", note = new string('x', 300) },
            new { label = "Second", url = "https://example.com/2", category = "external" } } });

        var links = (await (await app.NewClient().GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(["Third", "First", "Second"], links.Select(l => l.GetProperty("label").GetString()));
        Assert.Equal("The long one", links[0].GetProperty("note").GetString());
        Assert.Equal(200, links[1].GetProperty("note").GetString()!.Length);
        Assert.Equal(JsonValueKind.Null, links[2].GetProperty("note").ValueKind);
    }
}
