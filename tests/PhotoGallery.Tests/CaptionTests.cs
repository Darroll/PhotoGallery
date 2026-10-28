using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoGallery.Tests;

/// <summary>A tiny stand-in for an Ollama server: answers /api/generate with a fixed caption and alt text.</summary>
static class FakeOllama
{
    static readonly Lazy<string> address = new(Start);
    public static string Url => address.Value;

    static string Start()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => Serve(client));
            }
        });
        return $"http://127.0.0.1:{port}";
    }

    static void Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var data = new List<byte>();
            var buf = new byte[8192];
            int headerEnd = -1, bodyLength = 0;
            var chunked = false;
            while (true)
            {
                var n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                data.AddRange(buf.Take(n));
                if (headerEnd < 0)
                {
                    var text = Encoding.ASCII.GetString(data.ToArray());
                    var i = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        headerEnd = i + 4;
                        var line = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        bodyLength = line == null ? 0 : int.Parse(line.Split(':')[1].Trim());
                        chunked = text.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase);
                    }
                }
                if (headerEnd >= 0)
                {
                    var end = Encoding.ASCII.GetString(data.ToArray());
                    if (chunked ? end.EndsWith("0\r\n\r\n", StringComparison.Ordinal) : data.Count >= headerEnd + bodyLength) break;
                }
            }

            var reply = JsonSerializer.Serialize(new
            {
                response = JsonSerializer.Serialize(new { caption = "Golden hour at the pier", alt = "A pier at sunset with the sea below." })
            });
            var payload = Encoding.UTF8.GetBytes(reply);
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            stream.Write(Encoding.ASCII.GetBytes(head));
            stream.Write(payload);
        }
    }
}

public class CaptionFactory() : AppFactory(new() { ["CAPTION_URL"] = FakeOllama.Url });

public class AutoCaptionTests(CaptionFactory app) : IClassFixture<CaptionFactory>
{
    [Fact]
    public async Task A_vision_model_writes_caption_and_alt_text_for_photos_that_have_none()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Auto Captions", app.NewFolder(2));

        using var scope = app.Services.CreateScope();
        var written = await CaptionService.CaptionPendingAsync(
            scope.ServiceProvider.GetRequiredService<AppDb>(),
            scope.ServiceProvider.GetRequiredService<ImageService>(),
            scope.ServiceProvider.GetRequiredService<AppConfig>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, 50);
        Assert.True(written >= 2);

        var photos = (await (await app.NewClient().GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray().ToList();
        Assert.All(photos, p =>
        {
            Assert.Equal("Golden hour at the pier", p.GetProperty("caption").GetString());
            Assert.Equal("A pier at sunset with the sea below.", p.GetProperty("altText").GetString());
        });

        // nothing is captioned twice
        Assert.Equal(0, await CaptionService.CaptionPendingAsync(
            scope.ServiceProvider.GetRequiredService<AppDb>(),
            scope.ServiceProvider.GetRequiredService<ImageService>(),
            scope.ServiceProvider.GetRequiredService<AppConfig>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, 50));
    }
}

public class CaptionSuggestionTests(UserFactory app) : IClassFixture<UserFactory>
{
    static async Task<int> SuggestAsync(HttpClient c, string slug, int photo, string? caption = null, string? alt = null)
    {
        var r = await c.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/suggest", new { caption, alt });
        return (int)r.StatusCode;
    }

    static async Task<int> EditIdAsync(HttpClient admin, int photo, string proposed)
    {
        var list = await (await admin.GetAsync("/api/admin/caption-edits")).ReadAsync();
        return list.EnumerateArray().Last(e => e.GetProperty("photoId").GetInt32() == photo && e.GetProperty("proposed").GetString() == proposed).GetProperty("id").GetInt32();
    }

    async Task<(string caption, string alt)> PublicTextAsync(string slug, int photo)
    {
        var p = (await (await app.NewClient().GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray()
            .First(x => x.GetProperty("id").GetInt32() == photo);
        return (p.GetProperty("caption").GetString()!, p.GetProperty("altText").GetString()!);
    }

    [Fact]
    public async Task Anonymous_visitors_cannot_suggest()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Suggest Anon", app.NewFolder(1));
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        Assert.Equal((int)HttpStatusCode.Unauthorized, await SuggestAsync(app.NewClient(), slug, photo, caption: "Nope"));
    }

    [Fact]
    public async Task A_signed_in_suggestion_waits_for_approval_and_then_appears()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Suggest Approve", app.NewFolder(1));
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];

        var reader = app.AsUser("caption-reader-1");
        var r = await reader.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/suggest", new { caption = "Sunset over the bay", alt = "The bay at dusk" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.ReadAsync()).GetProperty("suggested").GetInt32());

        Assert.Equal(("", ""), await PublicTextAsync(slug, photo));
        var id = await EditIdAsync(admin, photo, "Sunset over the bay");
        var approve = await admin.PostAsync($"/api/admin/caption-edits/{id}/approve", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal("Sunset over the bay", (await PublicTextAsync(slug, photo)).caption);
    }

    [Fact]
    public async Task Approving_over_changed_text_is_a_conflict_until_the_admin_forces_it()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Suggest Conflict", app.NewFolder(1));
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];

        await app.AsUser("caption-reader-2").PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/suggest", new { caption = "Second take" });
        var id = await EditIdAsync(admin, photo, "Second take");

        // the photographer changes the caption meanwhile (an admin suggestion applies straight away)
        await admin.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/suggest", new { caption = "Admin wins" });

        var listed = (await (await admin.GetAsync("/api/admin/caption-edits")).ReadAsync()).EnumerateArray().First(e => e.GetProperty("id").GetInt32() == id);
        Assert.True(listed.GetProperty("conflict").GetBoolean());
        Assert.Equal("Admin wins", listed.GetProperty("current").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/caption-edits/{id}/approve", new { })).StatusCode);
        Assert.Equal("Admin wins", (await PublicTextAsync(slug, photo)).caption);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/caption-edits/{id}/approve", new { force = true })).StatusCode);
        Assert.Equal("Second take", (await PublicTextAsync(slug, photo)).caption);
    }

    [Fact]
    public async Task Rejected_suggestions_never_appear_and_repeats_are_not_queued_twice()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Suggest Reject", app.NewFolder(1));
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        var reader = app.AsUser("caption-reader-3");

        var first = await reader.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/suggest", new { alt = "Rejected description" });
        Assert.Equal(1, (await first.ReadAsync()).GetProperty("suggested").GetInt32());
        var again = await reader.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/suggest", new { alt = "Rejected description" });
        Assert.Equal(0, (await again.ReadAsync()).GetProperty("suggested").GetInt32());

        var id = await EditIdAsync(admin, photo, "Rejected description");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/caption-edits/{id}/reject", new { })).StatusCode);
        Assert.DoesNotContain("Rejected description", (await PublicTextAsync(slug, photo)).alt);
    }

    [Fact]
    public async Task The_photographer_edits_apply_straight_away_and_the_review_list_is_admin_only()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Suggest Admin", app.NewFolder(1));
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];

        var r = await admin.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/suggest", new { caption = "Mine", alt = "My words" });
        Assert.True((await r.ReadAsync()).GetProperty("applied").GetBoolean());
        Assert.Equal(("Mine", "My words"), await PublicTextAsync(slug, photo));

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync("/api/admin/caption-edits")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await app.AsUser("caption-reader-4").GetAsync("/api/admin/caption-edits")).StatusCode);
    }
}
