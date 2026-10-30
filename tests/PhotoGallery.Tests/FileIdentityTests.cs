using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery.Tests;

public class FileIdentityTests(AppFactory app) : IClassFixture<AppFactory>
{
    async Task<(int id, string folder)> NewGalleryAsync(HttpClient admin, int photos = 2)
    {
        // every photo here has contents no other test uses, since identical files count as the same photo
        var folder = "i" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.Combine(app.Library, folder));
        for (int i = 0; i < photos; i++)
            TestImages.Write(Path.Combine(app.Library, folder, $"{(char)('a' + i)}.jpg"), 300 + Random.Shared.Next(1, 4000), 200 + i, 40 + Random.Shared.Next(0, 150));
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Ident " + Guid.NewGuid().ToString("N")[..6], folder, visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        return (g.GetProperty("id").GetInt32(), folder);
    }

    async Task Rescan(HttpClient admin, int id) { var r = await admin.PostAsync($"/api/admin/galleries/{id}/scan", null); Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync()); }

    /// <summary>The thumbnail builder may still hold a new file for a moment.</summary>
    static void Move(string from, string to)
    {
        for (var i = 0; ; i++)
            try { File.Move(from, to); return; }
            catch (IOException) when (i < 50) { Thread.Sleep(100); }
    }

    Task Heart(int galleryId, int photoId, string visitor) => app.WithDbAsync(async db =>
    {
        db.Favorites.Add(new Favorite { GalleryId = galleryId, PhotoId = photoId, VisitorId = visitor, IdentityKey = "v:" + visitor });
        await db.SaveChangesAsync();
        return 0;
    });

    [Fact]
    public async Task Renaming_and_moving_a_file_keeps_its_photo_with_likes_tags_and_caption()
    {
        var admin = await app.AdminAsync();
        var (id, folder) = await NewGalleryAsync(admin);
        var a = await app.WithDbAsync(db => db.Photos.Where(p => p.GalleryId == id).OrderBy(p => p.Name).FirstAsync());
        Assert.NotEqual("", a.Fingerprint);
        await Heart(id, a.Id, "v1");
        await app.WithDbAsync(async db =>
        {
            var tag = new Tag { Name = "kept-" + Guid.NewGuid().ToString("N")[..5] };
            db.Tags.Add(tag); await db.SaveChangesAsync();
            db.PhotoTags.Add(new PhotoTag { PhotoId = a.Id, TagId = tag.Id });
            var p = await db.Photos.FindAsync(a.Id); p!.Caption = "Cake"; p.AltText = "A cake";
            await db.SaveChangesAsync(); return 0;
        });

        Directory.CreateDirectory(Path.Combine(app.Library, folder, "sub"));
        Move(Path.Combine(app.Library, folder, a.Name), Path.Combine(app.Library, folder, "sub", "renamed.jpg"));
        await Rescan(admin, id);

        var photos = await app.WithDbAsync(db => db.Photos.Where(p => p.GalleryId == id).ToListAsync());
        Assert.Equal(2, photos.Count);
        var moved = photos.Single(p => p.Id == a.Id);
        Assert.Equal("renamed.jpg", moved.Name);
        Assert.Equal(folder + "/sub/renamed.jpg", moved.RelPath);
        Assert.Equal("Cake", moved.Caption);
        Assert.Equal(1, await app.WithDbAsync(db => db.Favorites.CountAsync(f => f.PhotoId == a.Id)));
        Assert.Equal(1, await app.WithDbAsync(db => db.PhotoTags.CountAsync(t => t.PhotoId == a.Id)));
    }

    [Fact]
    public async Task A_file_moved_to_another_gallery_takes_its_likes_and_caption_along_in_either_scan_order()
    {
        var admin = await app.AdminAsync();
        foreach (var otherFirst in new[] { false, true })
        {
            var (idA, folderA) = await NewGalleryAsync(admin);
            var (idB, folderB) = await NewGalleryAsync(admin, 1);
            var a = await app.WithDbAsync(db => db.Photos.Where(p => p.GalleryId == idA).OrderBy(p => p.Name).FirstAsync());
            await Heart(idA, a.Id, "v2");
            await app.WithDbAsync(async db => { (await db.Photos.FindAsync(a.Id))!.Caption = "Travels"; await db.SaveChangesAsync(); return 0; });

            Move(Path.Combine(app.Library, folderA, a.Name), Path.Combine(app.Library, folderB, "arrived.jpg"));
            if (otherFirst) { await Rescan(admin, idB); await Rescan(admin, idA); }
            else { await Rescan(admin, idA); await Rescan(admin, idB); }

            var arrived = await app.WithDbAsync(db => db.Photos.SingleAsync(p => p.GalleryId == idB && p.Name == "arrived.jpg"));
            Assert.Equal("Travels", arrived.Caption);
            Assert.Equal(1, await app.WithDbAsync(db => db.Favorites.CountAsync(f => f.PhotoId == arrived.Id && f.GalleryId == idB)));
        }
    }

    [Fact]
    public async Task A_file_that_disappears_and_comes_back_gets_its_details_back()
    {
        var admin = await app.AdminAsync();
        var (id, folder) = await NewGalleryAsync(admin);
        var a = await app.WithDbAsync(db => db.Photos.Where(p => p.GalleryId == id).OrderBy(p => p.Name).FirstAsync());
        await Heart(id, a.Id, "v3");
        var path = Path.Combine(app.Library, folder, a.Name);
        var hold = Path.Combine(app.Root, "hold-" + a.Name);
        Move(path, hold);
        await Rescan(admin, id);
        var dbg = await app.WithDbAsync(async db => $"retired={await db.PhotosRetired.CountAsync()} photos={await db.Photos.CountAsync(p => p.GalleryId == id)} favs={await db.Favorites.CountAsync(f => f.GalleryId == id)} fp={a.Fingerprint}");
        Assert.True(await app.WithDbAsync(db => db.PhotosRetired.AnyAsync(r => r.Fingerprint == a.Fingerprint)), dbg);

        Move(hold, Path.Combine(app.Library, folder, "back.jpg"));
        await Rescan(admin, id);
        var back = await app.WithDbAsync(db => db.Photos.SingleAsync(p => p.GalleryId == id && p.Name == "back.jpg"));
        Assert.Equal(1, await app.WithDbAsync(db => db.Favorites.CountAsync(f => f.PhotoId == back.Id)));
        Assert.Equal(0, await app.WithDbAsync(db => db.PhotosRetired.CountAsync(r => r.Fingerprint == a.Fingerprint)));
    }

    [Fact]
    public async Task Photos_scanned_before_tracking_get_a_fingerprint_on_the_next_scan()
    {
        var admin = await app.AdminAsync();
        var (id, _) = await NewGalleryAsync(admin);
        await app.WithDbAsync(async db => { await db.Photos.Where(p => p.GalleryId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Fingerprint, "")); return 0; });
        await Rescan(admin, id);
        Assert.All(await app.WithDbAsync(db => db.Photos.Where(p => p.GalleryId == id).ToListAsync()), p => Assert.NotEqual("", p.Fingerprint));
    }
}
