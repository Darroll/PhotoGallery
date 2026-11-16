using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoGallery.Tests;

/// <summary>Events that last several days, found by search on any of their days; and a cover photo kept out of the photo grid.</summary>
public class EventDatesAndCoverTests(AppFactory app) : IClassFixture<AppFactory>
{
    async Task<(string slug, int id, HttpClient admin)> NewGalleryAsync(string title, int photos = 3)
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(photos), visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        return (g.GetProperty("slug").GetString()!, g.GetProperty("id").GetInt32(), admin);
    }

    static async Task<JsonElement> GalleryAsync(HttpClient c, string slug) => await (await c.GetAsync($"/api/g/{slug}")).ReadAsync();

    async Task<string[]> FoundAsync(string query)
    {
        var r = await (await app.NewClient().GetAsync("/api/search?" + query)).ReadAsync();
        return r.GetProperty("galleries").EnumerateArray().Select(g => g.GetProperty("slug").GetString()!).ToArray();
    }

    // ---------- event dates ----------

    [Fact]
    public async Task An_event_can_run_over_several_days_and_the_last_day_is_returned()
    {
        var (slug, id, admin) = await NewGalleryAsync("Weekend");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { eventDate = "2026-03-13", eventEndDate = "2026-03-15" });
        var g = await GalleryAsync(app.NewClient(), slug);
        Assert.StartsWith("2026-03-13", g.GetProperty("eventDate").GetString());
        Assert.StartsWith("2026-03-15", g.GetProperty("eventEndDate").GetString());
    }

    [Fact]
    public async Task A_last_day_that_is_not_after_the_first_day_is_dropped()
    {
        var (slug, id, admin) = await NewGalleryAsync("Bad Range");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { eventDate = "2026-03-13", eventEndDate = "2026-03-13" });
        Assert.Equal(JsonValueKind.Null, (await GalleryAsync(app.NewClient(), slug)).GetProperty("eventEndDate").ValueKind);
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { eventEndDate = "2026-03-10" });
        Assert.Equal(JsonValueKind.Null, (await GalleryAsync(app.NewClient(), slug)).GetProperty("eventEndDate").ValueKind);
        // clearing the first day takes the last day with it
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { eventEndDate = "2026-03-20" });
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { eventDate = "" });
        var g = await GalleryAsync(app.NewClient(), slug);
        Assert.Equal(JsonValueKind.Null, g.GetProperty("eventDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, g.GetProperty("eventEndDate").ValueKind);
    }

    [Fact]
    public async Task Search_finds_a_multi_day_event_on_any_of_its_days_and_not_outside_them()
    {
        var (slug, id, admin) = await NewGalleryAsync("Tournament Weekend");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { eventDate = "2026-05-08", eventEndDate = "2026-05-10" });

        Assert.Contains(slug, await FoundAsync("from=2026-05-09&to=2026-05-09"));   // the middle day
        Assert.Contains(slug, await FoundAsync("from=2026-05-10&to=2026-05-10"));   // the last day
        Assert.Contains(slug, await FoundAsync("from=2026-05-01&to=2026-05-08"));   // a range that ends on the first day
        Assert.Contains(slug, await FoundAsync("from=2026-05-10"));                 // open ended
        Assert.Contains(slug, await FoundAsync("to=2026-05-08"));
        Assert.DoesNotContain(slug, await FoundAsync("from=2026-05-11&to=2026-05-20"));
        Assert.DoesNotContain(slug, await FoundAsync("from=2026-04-01&to=2026-05-07"));
    }

    [Fact]
    public async Task A_day_filter_combines_with_the_text_search()
    {
        var (slug, id, admin) = await NewGalleryAsync("Zebra Cup");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { eventDate = "2026-06-05", eventEndDate = "2026-06-07" });
        Assert.Contains(slug, await FoundAsync("q=zebra&from=2026-06-06&to=2026-06-06"));
        Assert.DoesNotContain(slug, await FoundAsync("q=zebra&from=2026-07-01&to=2026-07-02"));
        Assert.DoesNotContain(slug, await FoundAsync("q=nomatch&from=2026-06-06&to=2026-06-06"));
    }

    [Fact]
    public async Task Searching_a_month_finds_an_event_that_runs_into_it()
    {
        var (slug, id, admin) = await NewGalleryAsync("Month Crossing");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { eventDate = "2026-08-30", eventEndDate = "2026-09-02" });
        Assert.Contains(slug, await FoundAsync("where=August%202026"));
        Assert.Contains(slug, await FoundAsync("where=September%202026"));
        Assert.DoesNotContain(slug, await FoundAsync("where=October%202026"));
    }

    [Fact]
    public async Task A_one_day_event_is_still_found_by_its_day()
    {
        var (slug, id, admin) = await NewGalleryAsync("One Day");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { eventDate = "2026-09-12" });
        Assert.Contains(slug, await FoundAsync("from=2026-09-12&to=2026-09-12"));
        Assert.DoesNotContain(slug, await FoundAsync("from=2026-09-13&to=2026-09-14"));
    }

    [Fact]
    public void Dates_read_naturally_for_one_day_a_weekend_and_longer_spans()
    {
        static DateTime D(string s) => DateTime.Parse(s);
        Assert.Equal("", GalleryRules.DateRangeText(null, null));
        Assert.Equal("March 14, 2026", GalleryRules.DateRangeText(D("2026-03-14"), null));
        Assert.Equal("March 14, 2026", GalleryRules.DateRangeText(D("2026-03-14"), D("2026-03-14")));
        Assert.Equal("March 13-15, 2026", GalleryRules.DateRangeText(D("2026-03-13"), D("2026-03-15")));
        Assert.Equal("March 30 - April 2, 2026", GalleryRules.DateRangeText(D("2026-03-30"), D("2026-04-02")));
        Assert.Equal("December 30, 2025 - January 2, 2026", GalleryRules.DateRangeText(D("2025-12-30"), D("2026-01-02")));
    }

    // ---------- cover photo kept out of the photos ----------

    async Task<(int coverId, string[] allNames)> SetCoverAsync(HttpClient admin, string slug, int id, string name)
    {
        var photos = (await GalleryAsync(app.NewClient(), slug)).GetProperty("photos").EnumerateArray().ToList();
        var cover = photos.Single(p => p.GetProperty("name").GetString() == name).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/cover", new { photoId = cover })).StatusCode);
        return (cover, photos.Select(p => p.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task A_cover_only_photo_is_not_among_the_photos_the_count_or_the_zip()
    {
        var (slug, id, admin) = await NewGalleryAsync("Logo Cover", 3);
        var (cover, _) = await SetCoverAsync(admin, slug, id, "a.jpg");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { coverMode = 1 });

        var guest = app.NewClient();
        var g = await GalleryAsync(guest, slug);
        Assert.Equal(cover, g.GetProperty("cover").GetInt32());
        Assert.Equal(1, g.GetProperty("coverMode").GetInt32());
        Assert.Equal(new[] { "b.jpg", "c.jpg" }, g.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).Order().ToArray());

        var home = await (await guest.GetAsync("/api/galleries")).ReadAsync();
        Assert.Equal(2, home.EnumerateArray().Single(x => x.GetProperty("slug").GetString() == slug).GetProperty("count").GetInt32());

        await guest.AcceptLicenseAsync();
        var zip = await guest.GetAsync($"/api/g/{slug}/zip?size=original");
        using var archive = new ZipArchive(new MemoryStream(await zip.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Equal(new[] { "b.jpg", "c.jpg" }, archive.Entries.Select(e => e.FullName).Order().ToArray());

        // the cover picture itself still opens
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/img/{cover}/thumb")).StatusCode);
    }

    [Fact]
    public async Task A_cover_only_photo_is_not_in_search_results()
    {
        var (slug, id, admin) = await NewGalleryAsync("Search Logo", 2);
        await SetCoverAsync(admin, slug, id, "a.jpg");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { coverMode = 1 });
        var r = await (await app.NewClient().GetAsync("/api/search?q=Search%20Logo&scope=photos")).ReadAsync();
        Assert.Equal(new[] { "b.jpg" }, r.GetProperty("photos").EnumerateArray().Where(p => p.GetProperty("slug").GetString() == slug).Select(p => p.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task Choosing_a_new_cover_puts_the_old_one_back_among_the_photos()
    {
        var (slug, id, admin) = await NewGalleryAsync("Swap Cover", 3);
        await SetCoverAsync(admin, slug, id, "a.jpg");
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { coverMode = 2 });
        Assert.Equal(2, (await GalleryAsync(app.NewClient(), slug)).GetProperty("photos").GetArrayLength());

        var photos = (await GalleryAsync(app.NewClient(), slug)).GetProperty("photos").EnumerateArray().ToList();
        var b = photos.First(p => p.GetProperty("name").GetString() == "b.jpg").GetProperty("id").GetInt32();
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/cover", new { photoId = b });

        var g = await GalleryAsync(app.NewClient(), slug);
        Assert.Equal(0, g.GetProperty("coverMode").GetInt32());
        Assert.Equal(3, g.GetProperty("photos").GetArrayLength());
    }

    [Fact]
    public async Task Unknown_cover_modes_are_ignored()
    {
        var (slug, id, admin) = await NewGalleryAsync("Mode Check", 2);
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { coverMode = 7 });
        Assert.Equal(0, (await GalleryAsync(app.NewClient(), slug)).GetProperty("coverMode").GetInt32());
    }
}
