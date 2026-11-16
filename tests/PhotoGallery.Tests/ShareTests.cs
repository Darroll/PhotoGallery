using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

/// <summary>The Share button: the gallery's address in the payload, and its QR code for anyone who can open the gallery.</summary>
public class ShareTests(AppFactory app) : IClassFixture<AppFactory>
{
    async Task<(string slug, int id, HttpClient admin)> NewGalleryAsync(string title, int visibility)
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(2), visibility });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        return (g.GetProperty("slug").GetString()!, g.GetProperty("id").GetInt32(), admin);
    }

    [Fact]
    public async Task The_gallery_payload_carries_the_address_to_share()
    {
        var (slug, _, _) = await NewGalleryAsync("Share Me", 1);
        var g = await (await app.NewClient().GetAsync($"/api/g/{slug}")).ReadAsync();
        Assert.EndsWith($"/g/{slug}", g.GetProperty("shareUrl").GetString());
    }

    [Fact]
    public async Task The_QR_code_is_an_SVG_for_anyone_who_can_open_the_gallery()
    {
        var (slug, _, _) = await NewGalleryAsync("Share QR", 1);
        var r = await app.NewClient().GetAsync($"/api/g/{slug}/qr");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/svg+xml", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<svg", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_protected_gallery_gives_no_QR_code_to_someone_who_cannot_open_it()
    {
        var (slug, _, _) = await NewGalleryAsync("Share Locked", 2);
        Assert.Equal(HttpStatusCode.NotFound, (await app.NewClient().GetAsync($"/api/g/{slug}/qr")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await app.AdminAsync()).GetAsync($"/api/g/{slug}/qr")).StatusCode);
    }

    [Fact]
    public void A_section_link_opens_that_section_and_an_unknown_one_is_ignored()
    {
        var g = new Gallery { Slug = "cup", Sections = """[{"Folder":"Game 1","Title":"Game 1","Links":[]}]""" };
        Assert.Equal("https://pics.example.test/g/cup", GalleryRules.ShareUrl(g, null, "https://pics.example.test/", "http://localhost"));
        Assert.Equal("https://pics.example.test/g/cup#s=Game%201", GalleryRules.ShareUrl(g, "game 1", "https://pics.example.test", "http://localhost"));
        Assert.Equal("http://localhost/g/cup", GalleryRules.ShareUrl(g, "<script>", "", "http://localhost"));
    }
}
