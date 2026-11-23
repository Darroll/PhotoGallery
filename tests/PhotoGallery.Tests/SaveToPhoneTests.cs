using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoGallery.Tests;

/// <summary>Save to phone: starting a save counts as one download, and each photo then comes in a format a camera roll accepts.</summary>
public class SaveToPhoneTests(AppFactory app) : IClassFixture<AppFactory>
{
    static async Task<(string token, int[] ids)> StartAsync(HttpClient c, string slug, object body)
    {
        var r = await c.PostAsJsonAsync($"/api/g/{slug}/phone", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.ReadAsync();
        return (j.GetProperty("token").GetString()!, j.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Picked_photos_come_back_as_the_untouched_jpegs()
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder(3);
        var slug = await AppFactory.GalleryAsync(admin, "Phone Picks", folder);
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        var all = await AppFactory.PhotoIdsAsync(guest, slug);

        var (token, ids) = await StartAsync(guest, slug, new { ids = new[] { all[0], all[2] } });
        Assert.Equal(new[] { all[0], all[2] }.OrderBy(x => x), ids.OrderBy(x => x));

        var r = await guest.GetAsync($"/dl/{ids[0]}/phone?t={token}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/jpeg", r.Content.Headers.ContentType?.MediaType);
        var name = r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(app.Library, folder, name!)), await r.Content.ReadAsByteArrayAsync());

        // the ticket only covers the photos that were picked
        Assert.Equal(HttpStatusCode.Gone, (await guest.GetAsync($"/dl/{all[1]}/phone?t={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await guest.GetAsync($"/dl/{ids[0]}/phone?t=made-up")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await guest.GetAsync($"/dl/{ids[0]}/phone")).StatusCode);
    }

    [Fact]
    public async Task A_whole_section_can_be_saved()
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder(1);
        Directory.CreateDirectory(Path.Combine(app.Library, folder, "Game 1"));
        TestImages.Write(Path.Combine(app.Library, folder, "Game 1", "g1.jpg"), 400, 300, 120);
        TestImages.Write(Path.Combine(app.Library, folder, "Game 1", "g2.jpg"), 400, 300, 130);
        var slug = await AppFactory.GalleryAsync(admin, "Phone Section", folder);
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();

        var (_, ids) = await StartAsync(guest, slug, new { section = "Game 1" });
        Assert.Equal(2, ids.Length);
    }

    [Fact]
    public async Task Watermarked_galleries_get_a_watermarked_jpeg_never_the_original()
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder(1);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Phone Watermark", folder, visibility = 0, watermarkDownload = true });
        var slug = (await r.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        var id = (await AppFactory.PhotoIdsAsync(guest, slug)).Single();

        var (token, _) = await StartAsync(guest, slug, new { ids = new[] { id } });
        var dl = await guest.GetAsync($"/dl/{id}/phone?t={token}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("image/jpeg", dl.Content.Headers.ContentType?.MediaType);
        var bytes = await dl.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(await File.ReadAllBytesAsync(Path.Combine(app.Library, folder, "a.jpg")), bytes);
        Assert.Equal(0xFF, bytes[0]); Assert.Equal(0xD8, bytes[1]);   // a JPEG
    }

    [Fact]
    public async Task The_license_must_be_accepted_and_downloads_allowed()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Phone License", app.NewFolder(1));
        var guest = app.NewClient();
        var id = (await AppFactory.PhotoIdsAsync(guest, slug)).Single();
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"/api/g/{slug}/phone", new { ids = new[] { id } })).StatusCode);

        var folder = app.NewFolder(1);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Phone View Only", folder, visibility = 0, allowDownload = false });
        var slug2 = (await r.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;
        await guest.AcceptLicenseAsync();
        var id2 = (await AppFactory.PhotoIdsAsync(guest, slug2)).Single();
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"/api/g/{slug2}/phone", new { ids = new[] { id2 } })).StatusCode);
    }

    [Fact]
    public async Task Several_photos_at_once_follow_the_zip_switch()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Phone No Zip", app.NewFolder(2), allowZip: false);
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        var ids = await AppFactory.PhotoIdsAsync(guest, slug);

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"/api/g/{slug}/phone", new { ids })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsJsonAsync($"/api/g/{slug}/phone", new { ids = new[] { ids[0] } })).StatusCode);
    }

    [Fact]
    public async Task A_save_counts_as_one_download_on_a_share_link()
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder(3);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Phone Link", folder, visibility = 2, password = "secret" });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var gid = g.GetProperty("id").GetInt32(); var slug = g.GetProperty("slug").GetString()!;
        var link = await (await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/links", new { label = "Parents", maxDownloads = 1 })).ReadAsync();

        var guest = app.NewClient();
        await guest.GetAsync($"/s/{link.GetProperty("token").GetString()}");
        await guest.AcceptLicenseAsync();
        var (token, ids) = await StartAsync(guest, slug, new { ids = await AppFactory.PhotoIdsAsync(guest, slug) });
        Assert.Equal(3, ids.Length);
        foreach (var id in ids) Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/dl/{id}/phone?t={token}")).StatusCode);

        // the link's one download is used up now
        Assert.Equal(HttpStatusCode.Gone, (await guest.PostAsJsonAsync($"/api/g/{slug}/phone", new { ids = new[] { ids[0] } })).StatusCode);
    }

    [Fact]
    public void The_phone_copy_uses_the_largest_offered_size_up_to_4096()
    {
        Assert.Equal(2560, PhoneSaves.PhoneSize([1080, 1920, 2560]));
        Assert.Equal(4096, PhoneSaves.PhoneSize([1080, 4096, 6000]));
        Assert.Equal(6000, PhoneSaves.PhoneSize([6000, 8000]));
        Assert.Null(PhoneSaves.PhoneSize([]));
        Assert.True(PhoneSaves.PhoneReady("IMG_1.JPG"));
        Assert.True(PhoneSaves.PhoneReady("x.heic"));
        Assert.False(PhoneSaves.PhoneReady("x.tif"));
        Assert.False(PhoneSaves.PhoneReady("x.webp"));
        Assert.False(PhoneSaves.PhoneReady("edit.png"));   // edited PNGs are often too big for a phone's share sheet: sent as a JPEG copy
    }
}
