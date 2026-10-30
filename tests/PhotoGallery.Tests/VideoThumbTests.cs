using System.Net.Http.Json;

namespace PhotoGallery.Tests;

public class VideoThumbTests(AppFactory app) : IClassFixture<AppFactory>
{
    const string Vid = "abcdefghijk";

    [Fact]
    public async Task A_changed_YouTube_thumbnail_is_picked_up_when_refreshed_by_the_admin()
    {
        var original = VideoThumbs.Fetch;
        try
        {
            var current = new byte[] { 1, 1, 1 };
            VideoThumbs.Fetch = _ => Task.FromResult<byte[]?>(current);

            var admin = await app.AdminAsync();
            var links = new[] { new { label = "Game", url = "https://www.youtube.com/watch?v=" + Vid } };
            var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Video Thumbs", folder = app.NewFolder(1), visibility = 0, footageLinks = links });
            var id = (await r.ReadAsync()).GetProperty("gallery").GetProperty("id").GetInt32();
            await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { footageLinks = links });

            var visitor = app.NewClient();
            Assert.Equal(new byte[] { 1, 1, 1 }, await visitor.GetByteArrayAsync("/vthumb/" + Vid));

            // YouTube now serves a new image; the cached one is still fresh, so visitors still get the old one
            current = [2, 2, 2];
            Assert.Equal(new byte[] { 1, 1, 1 }, await visitor.GetByteArrayAsync("/vthumb/" + Vid));

            // the admin button fetches it again
            var body = await (await admin.PostAsync($"/api/admin/galleries/{id}/video-thumbnails/refresh", null)).ReadAsync();
            Assert.Equal(1, body.GetProperty("videos").GetInt32());
            Assert.Equal(1, body.GetProperty("refreshed").GetInt32());
            Assert.Equal(new byte[] { 2, 2, 2 }, await visitor.GetByteArrayAsync("/vthumb/" + Vid));

            // a failed fetch keeps the image we have
            VideoThumbs.Fetch = _ => Task.FromResult<byte[]?>(null);
            var failed = await (await admin.PostAsync($"/api/admin/galleries/{id}/video-thumbnails/refresh", null)).ReadAsync();
            Assert.Equal(0, failed.GetProperty("refreshed").GetInt32());
            Assert.Equal(new byte[] { 2, 2, 2 }, await visitor.GetByteArrayAsync("/vthumb/" + Vid));
        }
        finally { VideoThumbs.Fetch = original; }
    }
}
