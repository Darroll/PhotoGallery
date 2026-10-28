using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

public class SearchTests(AppFactory app) : IClassFixture<AppFactory>
{
    static async Task<(string slug, int photo)> SetupAsync(AppFactory app, HttpClient admin)
    {
        var title = "Harbour Walk " + Guid.NewGuid().ToString("N")[..6];   // titles must be unique within the app
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(2), visibility = 0, location = "Dover" });
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

        Assert.Contains((await PhotosAsync(anon, "q=dawn")), p => p.GetProperty("slug").GetString() == slug);
        Assert.Contains((await PhotosAsync(anon, "q=grey")), p => p.GetProperty("slug").GetString() == slug);
        Assert.Contains((await PhotosAsync(anon, "q=harbour")), p => p.GetProperty("slug").GetString() == slug);
        Assert.Contains((await PhotosAsync(anon, "where=dover")), p => p.GetProperty("slug").GetString() == slug);
        Assert.Empty(await PhotosAsync(anon, "where=paris"));
        Assert.Empty(await PhotosAsync(anon, "q=zzz-nothing-matches"));
    }

    [Fact]
    public async Task Date_range_filters_by_when_the_photo_was_taken()
    {
        var admin = await app.AdminAsync();
        var (slug, _) = await SetupAsync(app, admin);
        var anon = app.NewClient();

        Assert.Contains((await PhotosAsync(anon, "q=dawn&from=2000-01-01&to=2100-01-01")), p => p.GetProperty("slug").GetString() == slug);
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
    public async Task People_are_searchable_and_shown_only_once_the_admin_makes_them_public()
    {
        var admin = await app.AdminAsync();
        var (slug, photo) = await SetupAsync(app, admin);
        var anon = app.NewClient();

        var tag = await admin.PutAsJsonAsync($"/api/admin/photos/{photo}/people", new { names = new[] { "Alex Rivera", "alex rivera" } });
        Assert.Equal(HttpStatusCode.OK, tag.StatusCode);
        Assert.Equal(new[] { "Alex Rivera" }, (await tag.ReadAsync()).EnumerateArray().Select(n => n.GetString()).ToArray());

        // a name that is not public is neither found nor shown
        Assert.Empty(await PhotosAsync(anon, "q=Rivera"));

        var person = (await (await admin.GetAsync("/api/admin/people")).ReadAsync()).EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Alex Rivera");
        var personId = person.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/people/{personId}", new { @public = true })).StatusCode);

        var found = (await PhotosAsync(anon, "q=Rivera")).Single();
        Assert.Equal(slug, found.GetProperty("slug").GetString());
        Assert.Equal("Alex Rivera", found.GetProperty("people")[0].GetString());

        // removing the tag takes the person off the photo
        await admin.PutAsJsonAsync($"/api/admin/photos/{photo}/people", new { names = Array.Empty<string>() });
        Assert.Empty(await PhotosAsync(anon, "q=Rivera"));
    }

    [Fact]
    public async Task Tagging_people_is_for_the_admin_only()
    {
        var admin = await app.AdminAsync();
        var (_, photo) = await SetupAsync(app, admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().PutAsJsonAsync($"/api/admin/photos/{photo}/people", new { names = new[] { "Nobody" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync("/api/admin/people")).StatusCode);
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
