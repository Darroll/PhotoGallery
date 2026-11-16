using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoGallery.Tests;

/// <summary>A gallery split into sections, one per sub folder (the games of a tournament), each with its own YouTube link.</summary>
public class SectionTests(AppFactory app) : IClassFixture<AppFactory>
{
    const string Yt1 = "https://www.youtube.com/watch?v=aaaaaaaaaaa";
    const string Yt2 = "https://www.youtube.com/watch?v=bbbbbbbbbbb";

    /// <summary>A library folder with Game_1 (2 photos), Game_2 (1), Game_10 (1) and one photo outside any sub folder.</summary>
    async Task<(string slug, int id, HttpClient admin)> TournamentAsync(string title)
    {
        var folder = "t" + Guid.NewGuid().ToString("N")[..8];
        void Put(string sub, string name, int grey)
        {
            var dir = Path.Combine(app.Library, folder, sub);
            Directory.CreateDirectory(dir);
            TestImages.Write(Path.Combine(dir, name), 400, 300, grey);
        }
        Put("Game_1", "a.jpg", 60); Put("Game_1", "b.jpg", 90);
        Put("Game_2", "c.jpg", 120);
        Put("Game_10", "d.jpg", 150);
        Put("", "loose.jpg", 180);
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder, visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        return (g.GetProperty("slug").GetString()!, g.GetProperty("id").GetInt32(), admin);
    }

    static async Task<JsonElement> GalleryAsync(HttpClient c, string slug) => await (await c.GetAsync($"/api/g/{slug}")).ReadAsync();

    [Fact]
    public async Task A_gallery_with_no_sections_stays_one_flat_grid()
    {
        var (slug, _, _) = await TournamentAsync("Flat");
        var g = await GalleryAsync(app.NewClient(), slug);
        Assert.Equal(0, g.GetProperty("sections").GetArrayLength());
        Assert.Equal(5, g.GetProperty("photos").GetArrayLength());
        Assert.All(g.GetProperty("photos").EnumerateArray(), p => Assert.Equal("", p.GetProperty("section").GetString()));
    }

    [Fact]
    public async Task Subfolders_are_listed_in_natural_order_with_their_photo_counts()
    {
        var (_, id, admin) = await TournamentAsync("Find");
        var r = await (await admin.GetAsync($"/api/admin/galleries/{id}/subfolders")).ReadAsync();
        var folders = r.GetProperty("folders").EnumerateArray().Select(f => (f.GetProperty("folder").GetString()!, f.GetProperty("count").GetInt32())).ToList();
        Assert.Equal(new[] { ("Game_1", 2), ("Game_2", 1), ("Game_10", 1) }, folders);
        Assert.Equal(1, r.GetProperty("loose").GetInt32());
    }

    [Fact]
    public async Task Sections_show_in_the_admins_order_each_with_its_own_video_and_photos()
    {
        var (slug, id, admin) = await TournamentAsync("Tournament");
        var put = await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { sections = new object[] {
            new { folder = "Game_2", title = "Game 2 vs Hawks", links = new[] { new { label = "Full game", url = Yt2 } } },
            new { folder = "Game_1", title = "Game 1 vs Eagles", links = new[] { new { label = "Full game", url = Yt1 } } } } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var g = await GalleryAsync(app.NewClient(), slug);
        var sections = g.GetProperty("sections").EnumerateArray().ToList();
        // the two set up first, then the other sub folder, then the photo outside any folder
        Assert.Equal(new[] { "Game 2 vs Hawks", "Game 1 vs Eagles", "Game 10", "Other photos" }, sections.Select(s => s.GetProperty("title").GetString()).ToArray());
        Assert.Equal(new[] { 1, 2, 1, 1 }, sections.Select(s => s.GetProperty("count").GetInt32()).ToArray());
        var game2 = sections[0].GetProperty("links")[0];
        Assert.Equal("bbbbbbbbbbb", game2.GetProperty("videoId").GetString());
        Assert.Equal("video", game2.GetProperty("category").GetString());

        var bySection = g.GetProperty("photos").EnumerateArray().GroupBy(p => p.GetProperty("section").GetString()!).ToDictionary(x => x.Key, x => x.Count());
        Assert.Equal(2, bySection["Game_1"]); Assert.Equal(1, bySection["Game_2"]); Assert.Equal(1, bySection["Game_10"]); Assert.Equal(1, bySection[""]);
    }

    [Fact]
    public async Task Removing_every_section_goes_back_to_one_grid()
    {
        var (slug, id, admin) = await TournamentAsync("Remove");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { sections = new[] { new { folder = "Game_1", title = "One", links = Array.Empty<object>() } } });
        Assert.Equal(4, (await GalleryAsync(app.NewClient(), slug)).GetProperty("sections").GetArrayLength());
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { sections = Array.Empty<object>() });
        Assert.Equal(0, (await GalleryAsync(app.NewClient(), slug)).GetProperty("sections").GetArrayLength());
    }

    [Fact]
    public async Task Section_names_cannot_point_outside_the_gallery_folder_and_links_must_be_web_addresses()
    {
        var (slug, id, admin) = await TournamentAsync("Safe");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { sections = new object[] {
            new { folder = "../secret", title = "Nope" }, new { folder = "a/b", title = "Nope" }, new { folder = "..", title = "Nope" },
            new { folder = "Game_1", title = "Ok", links = new[] { new { label = "x", url = "javascript:alert(1)" }, new { label = "y", url = Yt1 } } } } });
        var g = await GalleryAsync(app.NewClient(), slug);
        var first = g.GetProperty("sections")[0];
        Assert.Equal("Ok", first.GetProperty("title").GetString());
        Assert.Equal(1, first.GetProperty("links").GetArrayLength());
        Assert.DoesNotContain(g.GetProperty("sections").EnumerateArray(), s => s.GetProperty("folder").GetString()!.Contains(".."));
    }

    [Fact]
    public async Task One_section_can_be_downloaded_as_its_own_zip()
    {
        var (slug, id, admin) = await TournamentAsync("Zip");
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        var r = await guest.GetAsync($"/api/g/{slug}/zip?size=original&section=Game_1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("-Game-1", r.Content.Headers.ContentDisposition!.FileName);
        using var zip = new ZipArchive(new MemoryStream(await r.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Equal(new[] { "a.jpg", "b.jpg" }, zip.Entries.Select(e => e.FullName).Order().ToArray());

        // photos outside any sub folder are their own section
        var loose = await guest.GetAsync($"/api/g/{slug}/zip?size=original&section=");
        using var zip2 = new ZipArchive(new MemoryStream(await loose.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Equal(new[] { "loose.jpg" }, zip2.Entries.Select(e => e.FullName).ToArray());

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/g/{slug}/zip?size=original&section=Nope")).StatusCode);
    }

    [Fact]
    public async Task Links_inside_sections_are_checked_like_the_gallerys_own_links()
    {
        var (_, id, admin) = await TournamentAsync("Health");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { sections = new[] { new { folder = "Game_1", title = "One", links = new[] { new { label = "Game", url = Yt1 } } } } });
        var health = await (await admin.GetAsync($"/api/admin/galleries/{id}/links/health")).ReadAsync();
        Assert.Contains(health.GetProperty("items").EnumerateArray(), i => i.GetProperty("url").GetString() == Yt1);
    }

    [Fact]
    public void Numbers_in_names_sort_by_value()
    {
        var names = new[] { "Game 10", "Game 2", "game 1", "Game 20" };
        Assert.Equal(new[] { "game 1", "Game 2", "Game 10", "Game 20" }, names.OrderBy(n => n, new GalleryRules.NaturalComparer()).ToArray());
    }
}
