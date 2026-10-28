using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

public class LinkPreviewTests(AppFactory app) : IClassFixture<AppFactory>
{
    static string Tag(string html, string property) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, $"<meta (?:property|name)=\"{property}\" content=\"([^\"]*)\">").Groups[1].Value);

    [Fact]
    public async Task A_public_gallery_previews_its_name_description_and_cover()
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder(2);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Summer Tournament", folder, visibility = 0, description = "Day <one> & two" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var slug = g.GetProperty("slug").GetString()!;
        var html = await app.NewClient().GetStringAsync($"/g/{slug}");
        Assert.Equal("Summer Tournament · Test Photos", Tag(html, "og:title"));
        Assert.Contains("content=\"Day &lt;one&gt; &amp; two\"", html);
        Assert.Matches(@"<meta property=""og:image"" content=""http://localhost/img/\d+/view"">", html);
        Assert.Equal("summary_large_image", Tag(html, "twitter:card"));
        Assert.Equal("Test Photos", Tag(html, "og:site_name"));
        Assert.DoesNotContain("__META__", html);
    }

    [Fact]
    public async Task A_photo_page_previews_that_photo()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Photo Preview", app.NewFolder(2));
        var photos = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray().ToList();
        var second = photos[1];

        var html = await app.NewClient().GetStringAsync($"/g/{slug}/p/{second.GetProperty("id").GetInt32()}");
        Assert.Equal($"{second.GetProperty("name").GetString()} · Photo Preview · Test Photos", Tag(html, "og:title"));
        Assert.Contains($"http://localhost/img/{second.GetProperty("id").GetInt32()}/view", html);
    }

    [Fact]
    public async Task A_protected_gallery_reveals_nothing_but_the_site_name()
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder(1);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Private Wedding", folder, visibility = 2, password = "shh-secret", description = "The bride's family" });
        var slug = (await r.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;

        var html = await app.NewClient().GetStringAsync($"/g/{slug}");
        Assert.DoesNotContain("Private Wedding", html);
        Assert.DoesNotContain("bride", html);
        Assert.DoesNotContain("og:image", html);
        Assert.Equal("Test Photos", Tag(html, "og:title"));
    }

    [Fact]
    public async Task The_home_page_and_admin_pages_get_site_previews()
    {
        var html = await app.NewClient().GetStringAsync("/");
        Assert.Equal("Test Photos", Tag(html, "og:title"));
        Assert.Equal("summary", Tag(html, "twitter:card"));
        Assert.DoesNotContain("og:image", html);
    }
}
