using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

public class GalleryTagTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Admin_tags_a_gallery_and_visitors_see_them_on_the_gallery_and_home_page()
    {
        var admin = await app.AdminAsync();
        var title = "Tagged Wedding " + Guid.NewGuid().ToString("N")[..6];
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(2), visibility = 0 });
        var gallery = (await r.ReadAsync()).GetProperty("gallery");
        var id = gallery.GetProperty("id").GetInt32();
        var slug = gallery.GetProperty("slug").GetString()!;

        var put = await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/tags", new { names = new[] { "Wedding", "wedding", " Summer  2026 " } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(new[] { "Summer 2026", "Wedding" }, (await put.ReadAsync()).EnumerateArray().Select(x => x.GetString()).ToArray());

        var read = await (await admin.GetAsync($"/api/admin/galleries/{id}/tags")).ReadAsync();
        Assert.Equal(new[] { "Summer 2026", "Wedding" }, read.EnumerateArray().Select(x => x.GetString()).ToArray());

        var anon = app.NewClient();
        var page = await (await anon.GetAsync($"/api/g/{slug}")).ReadAsync();
        Assert.Equal(new[] { "Summer 2026", "Wedding" }, page.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToArray());

        var home = await (await anon.GetAsync("/api/galleries")).ReadAsync();
        var card = home.EnumerateArray().First(g => g.GetProperty("slug").GetString() == slug);
        Assert.Contains("Wedding", card.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task A_gallery_tag_is_searchable_and_brings_its_photos_into_a_tag_search()
    {
        var admin = await app.AdminAsync();
        var tag = "Regatta " + Guid.NewGuid().ToString("N")[..5];
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Rowing " + Guid.NewGuid().ToString("N")[..6], folder = app.NewFolder(2), visibility = 0 });
        var gallery = (await r.ReadAsync()).GetProperty("gallery");
        var id = gallery.GetProperty("id").GetInt32();
        var slug = gallery.GetProperty("slug").GetString()!;
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/tags", new { names = new[] { tag } });

        var anon = app.NewClient();
        var search = await (await anon.GetAsync("/api/search?q=" + Uri.EscapeDataString(tag))).ReadAsync();
        Assert.Contains(search.GetProperty("galleries").EnumerateArray(), g => g.GetProperty("slug").GetString() == slug);
        Assert.Contains(search.GetProperty("photos").EnumerateArray(), p => p.GetProperty("slug").GetString() == slug);

        var byTag = await (await anon.GetAsync("/api/search?tag=" + Uri.EscapeDataString(tag))).ReadAsync();
        Assert.Contains(byTag.GetProperty("galleries").EnumerateArray(), g => g.GetProperty("slug").GetString() == slug);
        Assert.Contains(byTag.GetProperty("photos").EnumerateArray(), p => p.GetProperty("slug").GetString() == slug);
    }

    [Fact]
    public async Task Protected_galleries_stay_out_of_search_even_when_tagged()
    {
        var admin = await app.AdminAsync();
        var tag = "Private " + Guid.NewGuid().ToString("N")[..5];
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Locked " + Guid.NewGuid().ToString("N")[..6], folder = app.NewFolder(1), visibility = 2, password = "locked-tag" });
        var gallery = (await r.ReadAsync()).GetProperty("gallery");
        var slug = gallery.GetProperty("slug").GetString()!;
        await admin.PutAsJsonAsync($"/api/admin/galleries/{gallery.GetProperty("id").GetInt32()}/tags", new { names = new[] { tag } });

        var search = await (await app.NewClient().GetAsync("/api/search?tag=" + Uri.EscapeDataString(tag))).ReadAsync();
        Assert.DoesNotContain(search.GetProperty("galleries").EnumerateArray(), g => g.GetProperty("slug").GetString() == slug);
        Assert.DoesNotContain(search.GetProperty("photos").EnumerateArray(), p => p.GetProperty("slug").GetString() == slug);
    }

    [Fact]
    public async Task Only_the_admin_tags_galleries()
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Admin Only Tags " + Guid.NewGuid().ToString("N")[..6], folder = app.NewFolder(1), visibility = 0 });
        var id = (await r.ReadAsync()).GetProperty("gallery").GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().PutAsJsonAsync($"/api/admin/galleries/{id}/tags", new { names = new[] { "Nope" } })).StatusCode);
        var signedInNoHeader = await app.AdminAsync();
        signedInNoHeader.DefaultRequestHeaders.Remove("X-Requested-With");
        Assert.Equal(HttpStatusCode.BadRequest, (await signedInNoHeader.PutAsJsonAsync($"/api/admin/galleries/{id}/tags", new { names = new[] { "Nope" } })).StatusCode);
    }
}
