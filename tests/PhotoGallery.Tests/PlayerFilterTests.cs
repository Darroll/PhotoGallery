using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoGallery.Tests;

/// <summary>The player filter: a chip per tagged player on a gallery, and one player's photos as a ZIP or a save to phone.</summary>
public class PlayerFilterTests(AppFactory app) : IClassFixture<AppFactory>
{
    /// <summary>A gallery of four photos (a to d) for a new team with players 10, 7, 0 and 00; tags the given photos by hand.</summary>
    async Task<(string slug, int gid, string team, Dictionary<string, int> ids, HttpClient admin)> GalleryAsync(bool names, params (string photo, string number)[] tags) =>
        await GalleryAsync(names, null, tags);

    async Task<(string slug, int gid, string team, Dictionary<string, int> ids, HttpClient admin)> GalleryAsync(bool names, Action<string>? prepare, params (string photo, string number)[] tags)
    {
        var admin = await app.AdminAsync();
        var folder = app.NewFolder(4);
        prepare?.Invoke(Path.Combine(app.Library, folder));
        var slug = await AppFactory.GalleryAsync(admin, "Filter " + Guid.NewGuid().ToString("N")[..6], folder, allowRaw: prepare != null);
        var g = await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync();
        var gid = g.GetProperty("id").GetInt32();
        var ids = g.GetProperty("photos").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("id").GetInt32());
        var team = "Team" + Guid.NewGuid().ToString("N")[..6];
        var t = await (await admin.PostAsJsonAsync("/api/admin/teams", new { name = team, colors = "white shirts", roster = "10 Emma\n7 Ava\n0 Zoe\n00 Lily", tagWithNames = names })).ReadAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId = t.GetProperty("id").GetInt32() })).StatusCode);
        foreach (var (photo, number) in tags)
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/photos/{ids[photo]}/player", new { number })).StatusCode);
        return (slug, gid, team, ids, admin);
    }

    static async Task<List<JsonElement>> ChipsAsync(HttpClient c, string slug) =>
        (await (await c.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("players").EnumerateArray().ToList();

    [Fact]
    public async Task Each_tagged_player_gets_a_chip_in_jersey_order_and_other_tags_do_not()
    {
        var (slug, _, team, ids, _) = await GalleryAsync(false, ("a.jpg", "10"), ("b.jpg", "10"), ("b.jpg", "00"), ("c.jpg", "0"), ("d.jpg", "7"));
        // an ordinary tag is not a player
        await app.WithDbAsync(async db =>
        {
            var tag = new Tag { Name = "Sunset " + Guid.NewGuid().ToString("N")[..4] };
            db.Tags.Add(tag); await db.SaveChangesAsync();
            db.PhotoTags.Add(new PhotoTag { PhotoId = ids["a.jpg"], TagId = tag.Id }); await db.SaveChangesAsync();
            return 0;
        });

        var chips = await ChipsAsync(app.NewClient(), slug);
        Assert.Equal(new[] { "0", "00", "7", "10" }, chips.Select(c => c.GetProperty("number").GetString()));
        Assert.Equal(new[] { "#0", "#00", "#7", "#10" }, chips.Select(c => c.GetProperty("label").GetString()));
        Assert.Equal($"{team} 10", chips[3].GetProperty("tag").GetString());
    }

    [Fact]
    public async Task Named_tags_show_the_name_on_the_chip()
    {
        var (slug, _, _, _, _) = await GalleryAsync(true, ("a.jpg", "10"));
        var chip = Assert.Single(await ChipsAsync(app.NewClient(), slug));
        Assert.Equal("Emma 10", chip.GetProperty("tag").GetString());
        Assert.Equal("Emma 10", chip.GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_gallery_without_player_tags_has_no_chips()
    {
        var (slug, _, _, _, _) = await GalleryAsync(false);
        Assert.Empty(await ChipsAsync(app.NewClient(), slug));
    }

    [Fact]
    public async Task Download_all_of_one_player_zips_only_her_photos()
    {
        var (slug, _, team, _, _) = await GalleryAsync(false, ("a.jpg", "10"), ("c.jpg", "10"), ("b.jpg", "7"));
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        var zip = await guest.GetAsync($"/api/g/{slug}/zip?size=original&player={Uri.EscapeDataString(team.ToLowerInvariant() + " 10")}");
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        Assert.Contains("-10", zip.Content.Headers.ContentDisposition?.FileName ?? "");
        using var archive = new ZipArchive(new MemoryStream(await zip.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Equal(new[] { "a.jpg", "c.jpg" }, archive.Entries.Select(e => e.FullName).Order().ToArray());

        // a player with no photos here is nothing to download
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/g/{slug}/zip?size=original&player={Uri.EscapeDataString(team + " 0")}")).StatusCode);
    }

    [Fact]
    public async Task Save_to_phone_of_one_player_returns_only_her_photos()
    {
        var (slug, _, team, ids, _) = await GalleryAsync(false, ("b.jpg", "7"), ("d.jpg", "7"), ("a.jpg", "10"));
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        var r = await guest.PostAsJsonAsync($"/api/g/{slug}/phone", new { player = $"{team} 7" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var got = (await r.ReadAsync()).GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).Order().ToArray();
        Assert.Equal(new[] { ids["b.jpg"], ids["d.jpg"] }.Order().ToArray(), got);
    }

    [Fact]
    public async Task Only_photos_visitors_can_see_count_for_the_chips_and_the_zip()
    {
        var (slug, _, team, ids, _) = await GalleryAsync(false, ("a.jpg", "10"));
        // the only photo of 10 is waiting for approval (as a guest upload would be)
        await app.WithDbAsync(async db => { (await db.Photos.FindAsync(ids["a.jpg"]))!.Approved = false; await db.SaveChangesAsync(); return 0; });
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        Assert.Empty(await ChipsAsync(guest, slug));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/g/{slug}/zip?size=original&player={Uri.EscapeDataString(team + " 10")}")).StatusCode);
    }

    [Fact]
    public async Task Curated_only_leaves_out_unedited_raw_frames_but_keeps_each_edited_photos_raw()
    {
        // a.jpg has its RAW (a.cr2); c.jpg is made an unedited RAW frame (only a RAW exists for it)
        var (slug, _, team, ids, _) = await GalleryAsync(false, dir => File.WriteAllBytes(Path.Combine(dir, "a.cr2"), "raw bytes"u8.ToArray()), ("a.jpg", "10"), ("c.jpg", "10"));
        await app.WithDbAsync(async db => { var c = (await db.Photos.FindAsync(ids["c.jpg"]))!; c.RawRelPath = c.RelPath; await db.SaveChangesAsync(); return 0; });
        var guest = app.NewClient();
        await guest.AcceptLicenseAsync();
        var who = Uri.EscapeDataString(team + " 10");

        async Task<string[]> NamesAsync(string query)
        {
            var r = await guest.GetAsync($"/api/g/{slug}/zip?size=original&raw=true&player={who}{query}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            using var archive = new ZipArchive(new MemoryStream(await r.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
            return archive.Entries.Select(e => e.FullName).Order().ToArray();
        }
        Assert.Equal(new[] { "a.jpg", "RAW/a.cr2" }, await NamesAsync("&curated=true"));
        Assert.Equal(new[] { "a.jpg", "RAW/a.cr2", "RAW/c.jpg" }, await NamesAsync(""));

        var phone = await guest.PostAsJsonAsync($"/api/g/{slug}/phone", new { player = team + " 10", curated = true });
        Assert.Equal(new[] { ids["a.jpg"] }, (await phone.ReadAsync()).GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToArray());
    }
}
