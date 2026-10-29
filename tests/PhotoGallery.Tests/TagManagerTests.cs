using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoGallery.Tests;

public class TagManagerTests(AppFactory app) : IClassFixture<AppFactory>
{
    static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    static async Task<JsonElement> TagAsync(HttpClient admin, string name) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/admin/tags")).EnumerateArray().First(t => t.GetProperty("name").GetString() == name);

    static async Task<int[]> NewPhotosAsync(AppFactory app, HttpClient admin)
    {
        var slug = await AppFactory.GalleryAsync(admin, Unique("Tag gallery "), app.NewFolder(3));
        return await AppFactory.PhotoIdsAsync(admin, slug);
    }

    [Fact]
    public async Task A_tag_can_be_created_on_its_own_and_creating_it_twice_is_harmless()
    {
        var admin = await app.AdminAsync();
        var name = Unique("Created");
        var r = await admin.PostAsJsonAsync("/api/admin/tags", new { name = "#" + name });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await r.ReadAsync()).GetProperty("created").GetBoolean());
        var again = await admin.PostAsJsonAsync("/api/admin/tags", new { name = name.ToUpper() });
        Assert.False((await again.ReadAsync()).GetProperty("created").GetBoolean());
        var t = await TagAsync(admin, name);
        Assert.Equal(0, t.GetProperty("photos").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/tags", new { name = "  " })).StatusCode);
    }

    [Fact]
    public async Task Tags_can_be_renamed_but_not_to_a_name_already_taken()
    {
        var admin = await app.AdminAsync();
        var a = Unique("Alpha"); var b = Unique("Beta");
        var id = (await (await admin.PostAsJsonAsync("/api/admin/tags", new { name = a })).ReadAsync()).GetProperty("id").GetInt32();
        await admin.PostAsJsonAsync("/api/admin/tags", new { name = b });
        var renamed = Unique("Gamma");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/tags/{id}", new { name = renamed })).StatusCode);
        Assert.Equal(id, (await TagAsync(admin, renamed)).GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/admin/tags/{id}", new { name = b.ToLower() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/admin/tags/999999", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Bulk_tagging_adds_and_removes_without_touching_other_tags()
    {
        var admin = await app.AdminAsync();
        var photos = await NewPhotosAsync(app, admin);
        Assert.True(photos.Length >= 2);
        var pair = photos.Take(2).ToArray();
        var keep = Unique("Keep"); var add = Unique("Added"); var gone = Unique("Gone");
        await admin.PutAsJsonAsync($"/api/admin/photos/{pair[0]}/tags", new { names = new[] { keep, gone } });

        var r = await admin.PostAsJsonAsync("/api/admin/photos/tags", new { photoIds = pair, add = new[] { add, "#" + add.ToUpper() }, remove = new[] { gone } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.ReadAsync()).GetProperty("photos").GetInt32());

        Assert.Equal(2, (await TagAsync(admin, add)).GetProperty("photos").GetInt32());
        Assert.Equal(1, (await TagAsync(admin, keep)).GetProperty("photos").GetInt32());
        Assert.Equal(0, (await TagAsync(admin, gone)).GetProperty("photos").GetInt32());

        // doing it again does not duplicate anything
        await admin.PostAsJsonAsync("/api/admin/photos/tags", new { photoIds = pair, add = new[] { add } });
        Assert.Equal(2, (await TagAsync(admin, add)).GetProperty("photos").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/photos/tags", new { photoIds = Array.Empty<int>(), add = new[] { "x" } })).StatusCode);
    }

    [Fact]
    public async Task Merging_moves_photos_without_duplicates_and_removes_the_old_tag()
    {
        var admin = await app.AdminAsync();
        var photos = await NewPhotosAsync(app, admin);
        var from = Unique("From"); var into = Unique("Into");
        await admin.PutAsJsonAsync($"/api/admin/photos/{photos[0]}/tags", new { names = new[] { from, into } });   // already has both
        await admin.PutAsJsonAsync($"/api/admin/photos/{photos[1]}/tags", new { names = new[] { from } });
        var fromId = (await TagAsync(admin, from)).GetProperty("id").GetInt32();
        var intoId = (await TagAsync(admin, into)).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/tags/{fromId}/merge", new { intoId = fromId })).StatusCode);
        var r = await admin.PostAsJsonAsync($"/api/admin/tags/{fromId}/merge", new { intoId });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.Equal(2, (await TagAsync(admin, into)).GetProperty("photos").GetInt32());
        Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/admin/tags")).EnumerateArray(), t => t.GetProperty("name").GetString() == from);
    }

    [Fact]
    public async Task Tag_management_needs_the_admin()
    {
        var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/admin/tags", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/admin/photos/tags", new { photoIds = new[] { 1 }, add = new[] { "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/admin/tags/1", new { name = "x" })).StatusCode);
    }
}
