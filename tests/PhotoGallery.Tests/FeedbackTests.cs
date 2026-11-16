using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

/// <summary>Visitors can send the photographer a message from a gallery that allows it; the admin reads and clears them.</summary>
public class FeedbackTests(AppFactory app) : IClassFixture<AppFactory>
{
    async Task<(string slug, int id, HttpClient admin)> NewGalleryAsync(string title, bool allow, int visibility = 0)
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(1), visibility });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var id = g.GetProperty("id").GetInt32();
        if (allow) await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { allowFeedback = true });
        return (g.GetProperty("slug").GetString()!, id, admin);
    }

    static Task<HttpResponseMessage> SendAsync(HttpClient c, string slug, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/g/{slug}/feedback") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Requested-With", "pg");
        return c.SendAsync(req);
    }

    static async Task<System.Text.Json.JsonElement> InboxAsync(HttpClient admin) => await (await admin.GetAsync("/api/admin/feedback")).ReadAsync();

    [Fact]
    public async Task A_visitor_message_reaches_the_admin_and_can_be_read_and_deleted()
    {
        var (slug, _, admin) = await NewGalleryAsync("Fan Mail", allow: true);
        var r = await SendAsync(app.NewClient(), slug, new { name = "Aunt May", contact = "may@example.test", text = "Loved the third one!" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var inbox = await InboxAsync(admin);
        var m = inbox.GetProperty("messages").EnumerateArray().First(x => x.GetProperty("text").GetString() == "Loved the third one!");
        Assert.Equal("Aunt May", m.GetProperty("name").GetString());
        Assert.Equal("Fan Mail", m.GetProperty("gallery").GetString());
        Assert.False(m.GetProperty("read").GetBoolean());
        var id = m.GetProperty("id").GetInt32();

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/feedback/{id}/read") ; put.Headers.Add("X-Requested-With", "pg");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(put)).StatusCode);
        Assert.True((await InboxAsync(admin)).GetProperty("messages").EnumerateArray().First(x => x.GetProperty("id").GetInt32() == id).GetProperty("read").GetBoolean());

        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/feedback/{id}"); del.Headers.Add("X-Requested-With", "pg");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(del)).StatusCode);
        Assert.DoesNotContain(id, (await InboxAsync(admin)).GetProperty("messages").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task A_gallery_that_has_not_turned_messages_on_refuses_them_and_says_so_in_its_payload()
    {
        var (slug, _, _) = await NewGalleryAsync("Quiet", allow: false);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app.NewClient(), slug, new { text = "hello there" })).StatusCode);
        Assert.False((await (await app.NewClient().GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("allowFeedback").GetBoolean());
    }

    [Fact]
    public async Task Empty_messages_are_refused_and_a_filled_trap_field_is_dropped_silently()
    {
        var (slug, _, admin) = await NewGalleryAsync("Spam Guard", allow: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(app.NewClient(), slug, new { text = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app.NewClient(), slug, new { text = "buy cheap watches", website = "http://spam.test" })).StatusCode);
        Assert.DoesNotContain("buy cheap watches", (await InboxAsync(admin)).GetProperty("messages").EnumerateArray().Select(x => x.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Messages_need_the_request_header_and_access_to_the_gallery()
    {
        var (open, _, admin) = await NewGalleryAsync("Open Mail", allow: true);
        var (slug, _, _) = await NewGalleryAsync("Locked Mail", allow: true, visibility: 2);
        var c = app.NewClient();
        c.DefaultRequestHeaders.Remove("X-Requested-With");
        Assert.False((await c.PostAsJsonAsync($"/api/g/{open}/feedback", new { text = "no header here" })).IsSuccessStatusCode);
        Assert.DoesNotContain("no header here", (await InboxAsync(admin)).GetProperty("messages").EnumerateArray().Select(x => x.GetProperty("text").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app.NewClient(), slug, new { text = "not allowed in" })).StatusCode);
    }

    [Fact]
    public async Task Only_the_admin_can_read_the_messages()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync("/api/admin/feedback")).StatusCode);
    }
}
