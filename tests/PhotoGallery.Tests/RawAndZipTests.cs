using System.IO.Compression;
using System.Net;

namespace PhotoGallery.Tests;

/// <summary>RAW pairing, RAW downloads and ZIP contents. Each test uses its own folder, so the galleries never clash.</summary>
public class RawAndZipTests(AppFactory app) : IClassFixture<AppFactory>
{
    static readonly byte[] FakeRaw = "pretend camera raw bytes"u8.ToArray();

    /// <summary>A gallery whose a.jpg has a RAW partner, a.cr2, in the same folder.</summary>
    async Task<(string slug, int photo)> RawGalleryAsync(string title, bool allowRaw)
    {
        var folder = app.NewFolder(2);
        await File.WriteAllBytesAsync(Path.Combine(app.Library, folder, "a.cr2"), FakeRaw);
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, title, folder, allowRaw: allowRaw);
        var photos = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray();
        var a = photos.First(p => p.GetProperty("name").GetString() == "a.jpg");
        return (slug, a.GetProperty("id").GetInt32());
    }

    static async Task<List<(string Name, byte[] Data)>> EntriesAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/zip", r.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await r.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        var list = new List<(string Name, byte[] Data)>();
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            list.Add((e.FullName, ms.ToArray()));
        }
        return list;
    }

    [Fact]
    public async Task The_raw_partner_is_paired_with_its_photo_and_not_listed_on_its_own()
    {
        var (slug, _) = await RawGalleryAsync("Raw Pairs", allowRaw: true);
        var photos = (await (await app.NewClient().GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray().ToList();
        Assert.Equal(2, photos.Count);
        Assert.Contains(photos, p => p.GetProperty("name").GetString() == "a.jpg" && p.GetProperty("raw").GetBoolean());
        Assert.Contains(photos, p => p.GetProperty("name").GetString() == "b.jpg" && !p.GetProperty("raw").GetBoolean());
    }

    [Fact]
    public async Task The_raw_file_downloads_unchanged_only_after_accepting_the_license()
    {
        var (_, photo) = await RawGalleryAsync("Raw Download", allowRaw: true);
        var guest = app.NewClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/dl/{photo}/raw")).StatusCode);

        await guest.AcceptLicenseAsync();
        var ok = await guest.GetAsync($"/dl/{photo}/raw");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(FakeRaw, await ok.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RAW_downloads_are_refused_when_the_gallery_does_not_allow_them()
    {
        var (_, photo) = await RawGalleryAsync("Raw Locked", allowRaw: false);
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/dl/{photo}/raw")).StatusCode);
    }

    [Fact]
    public async Task A_sized_zip_holds_every_photo_at_that_size()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Zip Sized", app.NewFolder(2));
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();

        var entries = await EntriesAsync(await guest.GetAsync($"/api/g/{slug}/zip?size=1080&fmt=png"));
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.EndsWith("_1080.png", e.Name));
    }

    [Fact]
    public async Task A_zip_of_selected_photos_holds_only_those()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Zip Selected", app.NewFolder(2));
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        var first = (await AppFactory.PhotoIdsAsync(guest, slug))[0];

        var entries = await EntriesAsync(await guest.GetAsync($"/api/g/{slug}/zip?size=1080&fmt=png&ids={first}"));
        Assert.Single(entries);
    }

    [Fact]
    public async Task A_zip_of_originals_contains_the_untouched_files()
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder(2);
        var slug = await AppFactory.GalleryAsync(admin, "Zip Originals", folder);
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();

        var entries = await EntriesAsync(await guest.GetAsync($"/api/g/{slug}/zip?size=original"));
        Assert.Equal(new[] { "a.jpg", "b.jpg" }, entries.Select(e => e.Name).OrderBy(n => n).ToArray());
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(app.Library, folder, "a.jpg")), entries.Single(e => e.Name == "a.jpg").Data);
    }

    [Fact]
    public async Task A_zip_can_include_the_raw_files_when_the_gallery_allows_them()
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder(2);
        await File.WriteAllBytesAsync(Path.Combine(app.Library, folder, "b.cr2"), FakeRaw);
        var slug = await AppFactory.GalleryAsync(admin, "Zip Raw", folder, allowRaw: true);
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();

        var entries = await EntriesAsync(await guest.GetAsync($"/api/g/{slug}/zip?size=1080&fmt=png&raw=true"));
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Name == "RAW/b.cr2" && e.Data.SequenceEqual(FakeRaw));
    }

    [Fact]
    public async Task Zips_are_refused_without_acceptance_when_disabled_or_for_unknown_sizes()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Zip Refusals", app.NewFolder(1));
        var closed = await AppFactory.GalleryAsync(admin, "Zip Closed", app.NewFolder(1), allowZip: false);
        var guest = app.NewClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/api/g/{slug}/zip?size=1080&fmt=png")).StatusCode);

        await guest.AcceptLicenseAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/api/g/{closed}/zip?size=1080&fmt=png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.GetAsync($"/api/g/{slug}/zip?size=123456&fmt=png")).StatusCode);
    }
}
