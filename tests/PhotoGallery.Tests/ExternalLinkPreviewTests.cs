using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using NetVips;

namespace PhotoGallery.Tests;

static class NoRealNetwork
{
    /// <summary>Tests never fetch real web pages; the preview tests install their own fake.</summary>
    [ModuleInitializer]
    internal static void Init() => ExternalLinkPreviews.Fetch = (_, _, _) => Task.FromResult<SafeHttp.Response?>(null);
}

public class ExternalLinkPreviewTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.2.3", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Only_public_addresses_are_allowed(string ip, bool allowed) =>
        Assert.Equal(allowed, SafeHttp.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task The_fetcher_never_contacts_addresses_on_the_local_network()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Null(await SafeHttp.GetAsync(new Uri($"http://127.0.0.1:{port}/"), 1000, "*/*"));
        Assert.Null(await SafeHttp.GetAsync(new Uri($"http://localhost:{port}/"), 1000, "*/*"));
        Assert.Null(await SafeHttp.GetAsync(new Uri("http://[::1]/"), 1000, "*/*"));
        Assert.Null(await SafeHttp.GetAsync(new Uri("http://192.168.2.3/"), 1000, "*/*"));
        Assert.Null(await SafeHttp.GetAsync(new Uri("http://169.254.169.254/latest/meta-data/"), 1000, "*/*"));
        Assert.Null(await SafeHttp.GetAsync(new Uri("http://example.com:8080/"), 1000, "*/*")); // only ports 80 and 443
        Assert.Null(await SafeHttp.GetAsync(new Uri("ftp://example.com/"), 1000, "*/*"));
        Assert.False(listener.Pending(), "something connected to the local listener");
    }

    [Fact]
    public void The_title_and_picture_are_read_from_the_page()
    {
        const string html = """
            <html><head><title> Fallback   title </title>
            <meta property="og:title" content="Tom &amp; Jerry&#39;s &quot;Cup&quot;">
            <meta content='/img/cover.jpg' property='og:image'>
            </head></html>
            """;
        var (title, image) = ExternalLinkPreviews.ReadPage(html, new Uri("https://news.example.org/a/b"));
        Assert.Equal("Tom & Jerry's \"Cup\"", title);
        Assert.Equal(new Uri("https://news.example.org/img/cover.jpg"), image);

        var (t2, i2) = ExternalLinkPreviews.ReadPage("<title>\n Only a   title </title>", new Uri("https://x.example.org/"));
        Assert.Equal("Only a title", t2);
        Assert.Null(i2);

        // a picture address that is not http(s) is ignored
        var (_, i3) = ExternalLinkPreviews.ReadPage("<meta property=\"og:image\" content=\"javascript:alert(1)\">", new Uri("https://x.example.org/"));
        Assert.Null(i3);
    }

    [Fact]
    public async Task An_External_link_gets_a_cached_title_and_picture_that_visitors_load_from_the_gallery()
    {
        var original = ExternalLinkPreviews.Fetch;
        var asked = new List<string>();
        try
        {
            using var png = Image.Black(40, 30);
            var pngBytes = png.PngsaveBuffer();
            ExternalLinkPreviews.Fetch = (uri, _, _) =>
            {
                lock (asked) asked.Add(uri.ToString());
                if (uri.AbsolutePath == "/story")
                    return Task.FromResult<SafeHttp.Response?>(new("text/html", Encoding.UTF8.GetBytes("<meta property=\"og:title\" content=\"The Big Game\"><meta property=\"og:image\" content=\"/cover.png\">"), uri));
                if (uri.AbsolutePath == "/cover.png") return Task.FromResult<SafeHttp.Response?>(new("image/png", pngBytes, uri));
                return Task.FromResult<SafeHttp.Response?>(null);
            };

            var admin = await app.AdminAsync();
            var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Previews", folder = app.NewFolder(1), visibility = 0 });
            var g = (await r.ReadAsync()).GetProperty("gallery");
            var id = g.GetProperty("id").GetInt32();
            var slug = g.GetProperty("slug").GetString()!;
            await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { footageLinks = new[] { new { label = "story.example.org", url = "https://story.example.org/story", category = "external", button = false } } });

            var visitor = app.NewClient();
            System.Text.Json.JsonElement link = default;
            for (var i = 0; i < 50; i++)   // the fetch runs in the background
            {
                link = (await (await visitor.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("links")[0];
                if (link.GetProperty("preview").ValueKind != System.Text.Json.JsonValueKind.Null) break;
                await Task.Delay(100);
            }
            var preview = link.GetProperty("preview");
            Assert.Equal("The Big Game", preview.GetProperty("title").GetString());
            var imageUrl = preview.GetProperty("image").GetString()!;
            Assert.StartsWith("/lp/", imageUrl);

            // the picture comes from the gallery itself, as a small WebP
            var pic = await visitor.GetAsync(imageUrl);
            Assert.Equal(HttpStatusCode.OK, pic.StatusCode);
            Assert.Equal("image/webp", pic.Content.Headers.ContentType?.MediaType);
            Assert.Contains("https://story.example.org/cover.png", asked);

            // nothing else is served from that folder
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/lp/../data.webp")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/lp/000000000000000000000000.webp")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/lp/photogallery.db")).StatusCode);

            // the admin button fetches it again
            var refresh = await (await admin.PostAsync($"/api/admin/galleries/{id}/video-thumbnails/refresh", null)).ReadAsync();
            Assert.Equal(1, refresh.GetProperty("links").GetInt32());
            Assert.Equal(1, refresh.GetProperty("linksRefreshed").GetInt32());

            // switched off: no preview is shown and nothing more is fetched
            await admin.PutAsJsonAsync("/api/admin/settings/link-previews", new { enabled = false });
            try
            {
                var before = asked.Count;
                var off = (await (await visitor.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("links")[0];
                Assert.Equal(System.Text.Json.JsonValueKind.Null, off.GetProperty("preview").ValueKind);
                var offRefresh = await (await admin.PostAsync($"/api/admin/galleries/{id}/video-thumbnails/refresh", null)).ReadAsync();
                Assert.Equal(0, offRefresh.GetProperty("links").GetInt32());
                Assert.Equal(before, asked.Count);
            }
            finally { await admin.PutAsJsonAsync("/api/admin/settings/link-previews", new { enabled = true }); }
        }
        finally { ExternalLinkPreviews.Fetch = original; }
    }
}
