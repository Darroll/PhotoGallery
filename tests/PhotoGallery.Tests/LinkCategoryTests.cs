using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoGallery.Tests;

public class LinkCategoryTests(AppFactory app) : IClassFixture<AppFactory>
{
    const string Yt = "https://www.youtube.com/watch?v=abcdefghijk";

    async Task<(HttpClient admin, string slug, int id)> NewGalleryAsync(string title)
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(1), visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        return (admin, g.GetProperty("slug").GetString()!, g.GetProperty("id").GetInt32());
    }

    static async Task<List<JsonElement>> LinksAsync(HttpClient client, string slug) =>
        (await (await client.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("links").EnumerateArray().ToList();

    [Fact]
    public async Task Links_saved_without_a_category_keep_their_old_behaviour()
    {
        var (admin, slug, id) = await NewGalleryAsync("Legacy Links");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { footageLinks = new object[] {
            new { label = "Game", url = Yt },
            new { label = "Article", url = "https://example.com/story" } } });

        var links = await LinksAsync(app.NewClient(), slug);
        var game = links.Single(l => l.GetProperty("label").GetString() == "Game");
        var article = links.Single(l => l.GetProperty("label").GetString() == "Article");
        Assert.Equal("video", game.GetProperty("category").GetString());
        Assert.False(game.GetProperty("button").GetBoolean());
        Assert.Equal("external", article.GetProperty("category").GetString());
        Assert.True(article.GetProperty("button").GetBoolean());
    }

    [Fact]
    public async Task The_admin_chooses_the_category_and_whether_a_link_is_also_a_button()
    {
        var (admin, slug, id) = await NewGalleryAsync("Chosen Links");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { footageLinks = new object[] {
            new { label = "Game", url = Yt, category = "external", button = true },
            new { label = "Article", url = "https://example.com/story", category = "video", button = false },
            new { label = "Other", url = "https://example.com/other", category = "external", button = false } } });

        var links = await LinksAsync(app.NewClient(), slug);
        // a YouTube link can be moved to External; only YouTube links can be Video, so a plain link stays External
        Assert.Equal("external", links.Single(l => l.GetProperty("label").GetString() == "Game").GetProperty("category").GetString());
        Assert.True(links.Single(l => l.GetProperty("label").GetString() == "Game").GetProperty("button").GetBoolean());
        var article = links.Single(l => l.GetProperty("label").GetString() == "Article");
        Assert.Equal("external", article.GetProperty("category").GetString());
        Assert.False(article.GetProperty("button").GetBoolean());
        Assert.False(links.Single(l => l.GetProperty("label").GetString() == "Other").GetProperty("button").GetBoolean());

        // the admin page gets the same choices back, so saving again changes nothing
        var back = (await (await admin.GetAsync("/api/admin/galleries")).ReadAsync()).EnumerateArray().First(x => x.GetProperty("id").GetInt32() == id);
        var game = back.GetProperty("footageLinks").EnumerateArray().First(l => l.GetProperty("label").GetString() == "Game");
        Assert.Equal("external", game.GetProperty("category").GetString());
        Assert.True(game.GetProperty("button").GetBoolean());
    }
}
