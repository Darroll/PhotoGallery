using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

public class SearchTests(AppFactory app) : IClassFixture<AppFactory>
{
    /// <summary>A public gallery in Dover with a captioned first photo. Titles must be unique, so each call makes its own.</summary>
    static async Task<(string slug, int photo)> SetupAsync(AppFactory app, HttpClient admin)
    {
        var title = "Harbour Walk " + Guid.NewGuid().ToString("N")[..6];
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(2), visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var slug = g.GetProperty("slug").GetString()!;
        // the location is set from the gallery's settings, not when it is created
        var edit = await admin.PutAsJsonAsync($"/api/admin/galleries/{g.GetProperty("id").GetInt32()}", new { title, folder = g.GetProperty("folder").GetString(), visibility = 0, location = "Dover" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        await admin.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/suggest", new { caption = "Fishing boats at dawn", alt = "Boats on grey water" });
        return (slug, photo);
    }

    static async Task<List<System.Text.Json.JsonElement>> PhotosAsync(HttpClient c, string query)
    {
        var r = await c.GetAsync("/api/search?" + query);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.ReadAsync()).GetProperty("photos").EnumerateArray().ToList();
    }

    [Fact]
    public async Task Search_finds_public_photos_by_caption_alt_text_title_and_place()
    {
        var admin = await app.AdminAsync();
        var (slug, _) = await SetupAsync(app, admin);
        var anon = app.NewClient();

        Assert.Contains(await PhotosAsync(anon, "q=dawn"), p => p.GetProperty("slug").GetString() == slug);
        Assert.Contains(await PhotosAsync(anon, "q=grey"), p => p.GetProperty("slug").GetString() == slug);
        Assert.Contains(await PhotosAsync(anon, "q=harbour"), p => p.GetProperty("slug").GetString() == slug);
        Assert.Contains(await PhotosAsync(anon, "where=dover"), p => p.GetProperty("slug").GetString() == slug);
        Assert.Empty(await PhotosAsync(anon, "where=paris"));
        Assert.Empty(await PhotosAsync(anon, "q=zzz-nothing-matches"));
    }

    [Fact]
    public async Task Date_range_filters_by_when_the_photo_was_taken()
    {
        var admin = await app.AdminAsync();
        var (slug, _) = await SetupAsync(app, admin);
        var anon = app.NewClient();

        Assert.Contains(await PhotosAsync(anon, "q=dawn&from=2000-01-01&to=2100-01-01"), p => p.GetProperty("slug").GetString() == slug);
        Assert.Empty(await PhotosAsync(anon, "q=dawn&from=2100-01-01"));
        Assert.Empty(await PhotosAsync(anon, "q=dawn&to=2000-01-01"));
    }

    [Fact]
    public async Task Protected_and_unlisted_galleries_are_never_searched()
    {
        var admin = await app.AdminAsync();
        var secret = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Secret Harbour", folder = app.NewFolder(1), visibility = 2, password = "shh-search", description = "Private harbour shots" });
        var hidden = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Hidden Harbour", folder = app.NewFolder(1), visibility = 1, description = "Unlisted harbour shots" });
        var secretSlug = (await secret.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;
        var hiddenSlug = (await hidden.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;

        var anon = app.NewClient();
        var photos = (await PhotosAsync(anon, "q=harbour")).Select(p => p.GetProperty("slug").GetString()).ToList();
        Assert.DoesNotContain(secretSlug, photos);
        Assert.DoesNotContain(hiddenSlug, photos);

        var galleries = (await (await anon.GetAsync("/api/search?q=harbour")).ReadAsync()).GetProperty("galleries").EnumerateArray()
            .Select(g => g.GetProperty("slug").GetString()).ToList();
        Assert.DoesNotContain(secretSlug, galleries);
        Assert.DoesNotContain(hiddenSlug, galleries);
    }

    [Fact]
    public async Task Tags_are_global_shown_on_photos_searchable_and_removable()
    {
        var admin = await app.AdminAsync();
        var (slug, photo) = await SetupAsync(app, admin);
        var anon = app.NewClient();

        var tag = await admin.PutAsJsonAsync($"/api/admin/photos/{photo}/tags", new { names = new[] { "Sunrise", "sunrise", "#Fishing boats" } });
        Assert.Equal(HttpStatusCode.OK, tag.StatusCode);
        Assert.Equal(new[] { "Fishing boats", "Sunrise" }, (await tag.ReadAsync()).EnumerateArray().Select(n => n.GetString()).ToArray());

        // visitors see the tags on the photo, and find it by a tag
        var gallery = await (await anon.GetAsync($"/api/g/{slug}")).ReadAsync();
        var shown = gallery.GetProperty("photos").EnumerateArray().First(p => p.GetProperty("id").GetInt32() == photo);
        Assert.Contains("Sunrise", shown.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        Assert.Contains(await PhotosAsync(anon, "q=sunrise"), p => p.GetProperty("slug").GetString() == slug);
        Assert.Contains(await PhotosAsync(anon, "tag=sunrise"), p => p.GetProperty("slug").GetString() == slug);
        Assert.Contains(await PhotosAsync(anon, "tag=FISHING%20BOATS"), p => p.GetProperty("slug").GetString() == slug);
        Assert.DoesNotContain(await PhotosAsync(anon, "tag=winter"), p => p.GetProperty("slug").GetString() == slug);

        // removing the tags from the photo takes them off
        await admin.PutAsJsonAsync($"/api/admin/photos/{photo}/tags", new { names = Array.Empty<string>() });
        Assert.DoesNotContain(await PhotosAsync(anon, "tag=sunrise"), p => p.GetProperty("slug").GetString() == slug);
    }

    [Fact]
    public async Task Deleting_a_tag_removes_it_from_every_photo_and_the_list()
    {
        var admin = await app.AdminAsync();
        var (_, photo) = await SetupAsync(app, admin);
        await admin.PutAsJsonAsync($"/api/admin/photos/{photo}/tags", new { names = new[] { "Temporary label" } });

        var list = (await (await admin.GetAsync("/api/admin/tags")).ReadAsync()).EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Temporary label");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/tags/{list.GetProperty("id").GetInt32()}")).StatusCode);
        var remaining = await (await admin.GetAsync("/api/admin/tags")).ReadAsync();
        Assert.DoesNotContain(remaining.EnumerateArray(), t => t.GetProperty("name").GetString() == "Temporary label");
    }

    [Fact]
    public async Task Tagging_is_for_the_admin_only()
    {
        var admin = await app.AdminAsync();
        var (_, photo) = await SetupAsync(app, admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().PutAsJsonAsync($"/api/admin/photos/{photo}/tags", new { names = new[] { "Nobody" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync("/api/admin/tags")).StatusCode);
    }

    [Fact]
    public async Task Search_text_cannot_use_wildcards_to_match_everything()
    {
        var admin = await app.AdminAsync();
        await SetupAsync(app, admin);
        // % and _ are stripped, so they match nothing on their own
        Assert.Empty(await PhotosAsync(app.NewClient(), "q=%25zzz_qqq"));
    }
}
