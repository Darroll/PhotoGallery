using System.Net.Http.Json;

namespace PhotoGallery.Tests;

/// <summary>Photos with a RAW file are ordered by when the camera took them, whether the photo is the edited export or the RAW itself.</summary>
public class ShootingTimeTests(AppFactory app) : IClassFixture<AppFactory>
{
    static readonly byte[] FakeRaw = "pretend camera raw bytes"u8.ToArray();

    /// <summary>Runs the body with the RAW time reader replaced by one that answers from <paramref name="times"/> (RAW file name -> time).</summary>
    static async Task WithRawTimesAsync(string folder, Dictionary<string, DateTime?> times, List<string> asked, Func<Task> body)
    {
        ImageService.RawTimesOverride = paths =>
        {
            var result = new Dictionary<string, DateTime?>();
            foreach (var p in paths.Where(p => p.Contains(folder)))   // other tests scan galleries at the same time; only this folder counts
            {
                lock (asked) asked.Add(Path.GetFileName(p));
                if (times.TryGetValue(Path.GetFileName(p), out var t)) result[p] = t;   // a name not listed counts as unreadable
            }
            return Task.FromResult(result);
        };
        try { await body(); }
        finally { ImageService.RawTimesOverride = null; }
    }

    /// <summary>A folder of a.jpg b.jpg c.jpg, each with a RAW partner a.cr2 b.cr2 c.cr2, as a gallery.</summary>
    string RawPairsFolder()
    {
        var folder = app.NewFolder(3);
        foreach (var n in new[] { "a", "b", "c" }) File.WriteAllBytes(Path.Combine(app.Library, folder, n + ".cr2"), FakeRaw);
        return folder;
    }

    async Task<(string slug, int id, HttpClient admin)> RawPairsAsync(string title, string folder)
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder, visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        return (g.GetProperty("slug").GetString()!, g.GetProperty("id").GetInt32(), admin);
    }

    static async Task<string[]> OrderAsync(HttpClient c, string slug) =>
        (await (await c.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public async Task Edited_photos_are_ordered_by_the_time_in_their_RAW_file()
    {
        var times = new Dictionary<string, DateTime?>
        {
            ["a.cr2"] = new DateTime(2026, 11, 14, 12, 0, 3), ["b.cr2"] = new DateTime(2026, 11, 14, 12, 0, 1), ["c.cr2"] = new DateTime(2026, 11, 14, 12, 0, 2)
        };
        var folder = RawPairsFolder();
        await WithRawTimesAsync(folder, times, new List<string>(), async () =>
        {
            var (slug, id, admin) = await RawPairsAsync("Shot Order", folder);
            Assert.Equal(new[] { "b.jpg", "c.jpg", "a.jpg" }, await OrderAsync(app.NewClient(), slug));

            // the order can be reversed like any other
            await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { sortDesc = true });
            Assert.Equal(new[] { "a.jpg", "c.jpg", "b.jpg" }, await OrderAsync(app.NewClient(), slug));
        });
    }

    [Fact]
    public async Task Frames_of_a_burst_keep_their_order_by_the_hundredths_of_a_second()
    {
        var times = new Dictionary<string, DateTime?>
        {
            ["a.cr2"] = ImageService.ParseShot("2026-11-14T12:00:00", "30"), ["b.cr2"] = ImageService.ParseShot("2026-11-14T12:00:00", "05"), ["c.cr2"] = ImageService.ParseShot("2026-11-14T12:00:00", "17")
        };
        var folder = RawPairsFolder();
        await WithRawTimesAsync(folder, times, new List<string>(), async () =>
        {
            var (slug, _, _) = await RawPairsAsync("Burst", folder);
            Assert.Equal(new[] { "b.jpg", "c.jpg", "a.jpg" }, await OrderAsync(app.NewClient(), slug));
        });
    }

    [Fact]
    public async Task A_photo_is_read_once_and_one_that_could_not_be_read_is_tried_again()
    {
        var times = new Dictionary<string, DateTime?>
        {
            ["a.cr2"] = new DateTime(2026, 11, 14, 12, 0, 3), ["b.cr2"] = new DateTime(2026, 11, 14, 12, 0, 1)   // c.cr2 is unreadable
        };
        var asked = new List<string>();
        var folder = RawPairsFolder();
        await WithRawTimesAsync(folder, times, asked, async () =>
        {
            var (slug, id, admin) = await RawPairsAsync("Read Once", folder);
            lock (asked) { Assert.Contains("c.cr2", asked); asked.Clear(); }

            var scan = await (await admin.PostAsync($"/api/admin/galleries/{id}/scan", null)).ReadAsync();
            lock (asked) Assert.Equal(new[] { "c.cr2" }, asked.ToArray());   // a and b are done; only the unreadable one is asked again
            Assert.Equal(0, scan.GetProperty("retimed").GetInt32());

            // now it can be read
            times["c.cr2"] = new DateTime(2026, 11, 14, 12, 0, 2);
            var again = await (await admin.PostAsync($"/api/admin/galleries/{id}/scan", null)).ReadAsync();
            Assert.Equal(1, again.GetProperty("retimed").GetInt32());
            Assert.Equal(new[] { "b.jpg", "c.jpg", "a.jpg" }, await OrderAsync(app.NewClient(), slug));
            asked.Clear();
            await admin.PostAsync($"/api/admin/galleries/{id}/scan", null);
            lock (asked) Assert.Empty(asked);
        });
    }

    [Fact]
    public async Task A_RAW_file_without_a_time_keeps_the_file_date_and_is_not_asked_again()
    {
        var times = new Dictionary<string, DateTime?> { ["a.cr2"] = null, ["b.cr2"] = null, ["c.cr2"] = null };
        var asked = new List<string>();
        var folder = RawPairsFolder();
        await WithRawTimesAsync(folder, times, asked, async () =>
        {
            var (slug, id, admin) = await RawPairsAsync("No Time", folder);
            Assert.Equal(3, (await OrderAsync(app.NewClient(), slug)).Length);
            asked.Clear();
            await admin.PostAsync($"/api/admin/galleries/{id}/scan", null);
            lock (asked) Assert.Empty(asked);
        });
    }

    [Fact]
    public async Task Photos_without_a_RAW_partner_are_left_alone()
    {
        var asked = new List<string>();
        var folder = app.NewFolder(2);
        await WithRawTimesAsync(folder, new Dictionary<string, DateTime?>(), asked, async () =>
        {
            var admin = await app.AdminAsync();
            var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "No RAW", folder, visibility = 0 });
            Assert.True(r.IsSuccessStatusCode);
            lock (asked) Assert.Empty(asked);
        });
    }

    [Theory]
    [InlineData("2026-11-14T10:53:37", "", 0)]
    [InlineData("2026-11-14T10:53:37", "00", 0)]
    [InlineData("2026-11-14T10:53:37", "15", 150)]
    [InlineData("2026-11-14T10:53:37", "5", 500)]
    [InlineData("2026-11-14T10:53:37", "125", 125)]
    public void The_shooting_time_includes_the_fraction_of_a_second(string time, string subsec, int milliseconds)
    {
        var t = ImageService.ParseShot(time, subsec)!.Value;
        Assert.Equal(new DateTime(2026, 11, 14, 10, 53, 37).AddMilliseconds(milliseconds), t);
    }

    [Theory]
    [InlineData("-", "-")]
    [InlineData("", "")]
    [InlineData("not a time", "10")]
    public void A_missing_or_unreadable_time_is_null(string time, string subsec) => Assert.Null(ImageService.ParseShot(time, subsec));
}
