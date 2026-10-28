using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

public class PhotoInfoRulesTests
{
    [Fact]
    public void Location_tags_are_removed_and_empty_groups_disappear()
    {
        var groups = new Dictionary<string, Dictionary<string, string>>
        {
            ["EXIF"] = new() { ["Make"] = "Canon", ["GPSLatitude"] = "51 N", ["GPSLongitude"] = "0 W" },
            ["GPS"] = new() { ["GPSAltitude"] = "10 m" },
            ["Composite"] = new() { ["GPSPosition"] = "51 N, 0 W", ["Aperture"] = "2.8" },
            ["XMP"] = new() { ["Latitude"] = "51" },
        };
        PhotoInfo.StripLocation(groups);
        Assert.Equal(new[] { "EXIF", "Composite" }, groups.Keys.ToArray());
        Assert.Equal(new[] { "Make" }, groups["EXIF"].Keys.ToArray());
        Assert.Equal(new[] { "Aperture" }, groups["Composite"].Keys.ToArray());
    }
}

public class PhotoInfoApiTests(AppFactory app) : IClassFixture<AppFactory>
{
    static readonly byte[] FakeRaw = "pretend camera raw bytes"u8.ToArray();

    [Fact]
    public async Task Every_file_behind_a_photo_is_described_with_its_file_details()
    {
        var folder = app.NewFolder(2);
        await File.WriteAllBytesAsync(Path.Combine(app.Library, folder, "a.cr2"), FakeRaw);
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Details Pair", folder, allowRaw: true);
        var id = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "a.jpg").GetProperty("id").GetInt32();

        var info = await (await app.NewClient().GetAsync($"/api/g/{slug}/photo/{id}/info")).ReadAsync();
        Assert.Equal("a.jpg", info.GetProperty("name").GetString());
        var versions = info.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(2, versions.Count);
        Assert.Equal("Image", versions[0].GetProperty("role").GetString());
        Assert.Equal("a.jpg", versions[0].GetProperty("file").GetString());
        Assert.Equal("RAW", versions[1].GetProperty("role").GetString());
        Assert.Equal("a.cr2", versions[1].GetProperty("file").GetString());
        Assert.Equal("a.cr2", versions[1].GetProperty("groups").GetProperty("File").GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Details_follow_the_same_access_rules_as_the_photo()
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Details Private", folder = app.NewFolder(1), visibility = 2, password = "pw-details" });
        var slug = (await r.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;
        var id = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        Assert.Equal(HttpStatusCode.NotFound, (await app.NewClient().GetAsync($"/api/g/{slug}/photo/{id}/info")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.NewClient().GetAsync($"/api/g/{slug}/photo/999999/info")).StatusCode);
    }

    [Fact]
    public async Task Visitors_get_no_location_tags_even_when_the_file_has_them()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Details Public", app.NewFolder(1));
        var id = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        var visitor = await (await app.NewClient().GetAsync($"/api/g/{slug}/photo/{id}/info")).ReadAsync();
        var visitorGroups = visitor.GetProperty("versions")[0].GetProperty("groups").EnumerateObject().Select(g => g.Name).ToList();
        Assert.DoesNotContain(visitorGroups, g => g.Contains("GPS", StringComparison.OrdinalIgnoreCase));
    }
}
