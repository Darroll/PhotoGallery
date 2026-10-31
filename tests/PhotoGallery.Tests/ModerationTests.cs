using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery.Tests;

public class ModerationTests(UserFactory app) : IClassFixture<UserFactory>
{
    record Setup(int Id, string Slug, int Photo);

    async Task<Setup> GalleryAsync(HttpClient admin)
    {
        var folder = app.NewFolder(2);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Mod " + Guid.NewGuid().ToString("N")[..6], folder, visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var id = g.GetProperty("id").GetInt32();
        var slug = g.GetProperty("slug").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { allowComments = true })).StatusCode);
        return new(id, slug, (await AppFactory.PhotoIdsAsync(admin, slug))[0]);
    }

    static string Url(Setup s, string tail = "") => $"/api/g/{s.Slug}/photo/{s.Photo}/comments{tail}";

    async Task<HttpClient> MemberAsync(string sub, string name)
    {
        var c = app.AsUser(sub, sub + "@example.test");
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/me/display-name", new { name })).StatusCode);
        return c;
    }

    static async Task<int> PostAsync(HttpClient c, Setup s, string text)
    {
        var r = await c.PostAsJsonAsync(Url(s), new { text });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.ReadAsync()).GetProperty("id").GetInt32();
    }

    static async Task<JsonElement> List(HttpClient c, Setup s) => await (await c.GetAsync(Url(s))).ReadAsync();

    [Fact]
    public async Task Hiding_takes_a_comment_off_the_page_for_visitors_but_the_admin_still_sees_it_and_can_show_it_again()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var member = await MemberAsync("mod-h1", "Hana Hide");
        var id = await PostAsync(member, s, "Please hide me");

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/comments/{id}/hidden", new { hidden = true })).StatusCode);

        var visitor = app.NewClient();
        Assert.Empty((await List(visitor, s)).GetProperty("comments").EnumerateArray());
        Assert.Empty((await List(member, s)).GetProperty("comments").EnumerateArray());
        var gallery = await visitor.GetAsync($"/api/g/{s.Slug}").ReadAsyncJson();
        Assert.DoesNotContain("Please hide me", gallery.ToString());

        var seenByAdmin = (await List(admin, s)).GetProperty("comments").EnumerateArray().ToList();
        Assert.Single(seenByAdmin);
        Assert.True(seenByAdmin[0].GetProperty("hidden").GetBoolean());

        // a hidden comment cannot be edited or answered by the author any more
        Assert.Equal(HttpStatusCode.NotFound, (await member.PutAsJsonAsync(Url(s, "/" + id), new { text = "sneaky" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/comments/{id}/hidden", new { hidden = false })).StatusCode);
        Assert.Single((await List(visitor, s)).GetProperty("comments").EnumerateArray());

        var log = await admin.GetAsync("/api/admin/comments/log?kind=hide").ReadAsyncJson();
        Assert.Contains(log.GetProperty("items").EnumerateArray(), e => e.GetProperty("commentId").GetInt32() == id);
        Assert.NotEmpty((await admin.GetAsync("/api/admin/comments/log?kind=unhide").ReadAsyncJson()).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_admin_can_delete_any_comment_and_the_log_keeps_it()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var member = await MemberAsync("mod-d1", "Dana Delete");
        var id = await PostAsync(member, s, "Remove this one please");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/comments/{id}")).StatusCode);
        Assert.Empty((await List(app.NewClient(), s)).GetProperty("comments").EnumerateArray());
        Assert.Empty((await List(admin, s)).GetProperty("comments").EnumerateArray());

        var all = await admin.GetAsync("/api/admin/comments?status=deleted&q=Remove%20this").ReadAsyncJson();
        var row = Assert.Single(all.GetProperty("items").EnumerateArray());
        Assert.Equal("deleted", row.GetProperty("status").GetString());
        Assert.Equal("Remove this one please", row.GetProperty("text").GetString());
        Assert.Equal("mod-d1@example.test", row.GetProperty("email").GetString());

        var log = await admin.GetAsync("/api/admin/comments/log?q=Remove%20this").ReadAsyncJson();
        var kinds = log.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        Assert.Contains("post", kinds);
        Assert.Contains("delete", kinds);

        // deleting twice is harmless
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/comments/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_blocked_person_cannot_comment_or_edit_and_unblocking_lets_them_back_in()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var troll = await MemberAsync("mod-b1", "Bo Block");
        var first = await PostAsync(troll, s, "First comment");
        await PostAsync(troll, s, "Second comment");
        var other = await MemberAsync("mod-b2", "Bea Bystander");
        await PostAsync(other, s, "A kind word");

        var r = await admin.PostAsJsonAsync($"/api/admin/comments/{first}/block", new { reason = "spam", hideAll = true });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.ReadAsync()).GetProperty("hidden").GetInt32());

        Assert.Equal(HttpStatusCode.Forbidden, (await troll.PostAsJsonAsync(Url(s), new { text = "I am back" })).StatusCode);
        var me = (await List(troll, s)).GetProperty("me");
        Assert.True(me.GetProperty("blocked").GetBoolean());
        Assert.False(me.GetProperty("canPost").GetBoolean());

        var visible = (await List(app.NewClient(), s)).GetProperty("comments").EnumerateArray().Select(c => c.GetProperty("author").GetString()).ToList();
        Assert.Equal(new[] { "Bea Bystander" }, visible);
        Assert.True((await List(other, s)).GetProperty("me").GetProperty("canPost").GetBoolean());

        // blocked by email too: a new sign-in id with the same verified email is still blocked
        var sameEmail = app.AsUser("mod-b1-other-id", "mod-b1@example.test");
        Assert.Equal(HttpStatusCode.OK, (await sameEmail.PutAsJsonAsync("/api/me/display-name", new { name = "Bo Again" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sameEmail.PostAsJsonAsync(Url(s), new { text = "new name, same person" })).StatusCode);

        var blocks = await admin.GetAsync("/api/admin/comments/blocks").ReadAsyncJson();
        var b = Assert.Single(blocks.EnumerateArray(), x => x.GetProperty("email").GetString() == "mod-b1@example.test");
        Assert.Equal("spam", b.GetProperty("reason").GetString());

        // blocking again does not add a second row
        await admin.PostAsJsonAsync($"/api/admin/comments/{first}/block", new { reason = "", hideAll = false });
        Assert.Single((await admin.GetAsync("/api/admin/comments/blocks").ReadAsyncJson()).EnumerateArray(), x => x.GetProperty("email").GetString() == "mod-b1@example.test");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/comments/blocks/{b.GetProperty("id").GetInt32()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await troll.PostAsJsonAsync(Url(s), new { text = "Sorry, I will behave" })).StatusCode);

        var log = await admin.GetAsync("/api/admin/comments/log?q=mod-b1%40example.test").ReadAsyncJson();
        var kinds = log.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        Assert.Contains("block", kinds);
        Assert.Contains("unblock", kinds);
    }

    [Fact]
    public async Task The_photographer_cannot_be_blocked()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var id = await PostAsync(admin, s, "From the photographer");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/comments/{id}/block", new { reason = "", hideAll = false })).StatusCode);
    }

    [Fact]
    public async Task Moderation_and_the_log_are_for_the_admin_only_and_never_show_the_address()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var member = await MemberAsync("mod-p1", "Pia Private");
        var id = await PostAsync(member, s, "Searchable words here");

        foreach (var client in new[] { app.NewClient(), member })
        {
            Assert.Contains((await client.GetAsync("/api/admin/comments")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
            Assert.Contains((await client.GetAsync("/api/admin/comments/log")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
            Assert.Contains((await client.PutAsJsonAsync($"/api/admin/comments/{id}/hidden", new { hidden = true })).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
            Assert.Contains((await client.DeleteAsync($"/api/admin/comments/{id}")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
            Assert.Contains((await client.PostAsJsonAsync($"/api/admin/comments/{id}/block", new { })).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }

        foreach (var url in new[] { "/api/admin/comments?q=Searchable", "/api/admin/comments/log?q=Searchable" })
        {
            var raw = await (await admin.GetAsync(url)).Content.ReadAsStringAsync();
            Assert.Contains("Searchable words here", raw);
            Assert.DoesNotContain("\"ip\"", raw, StringComparison.OrdinalIgnoreCase);
        }

        // search by person and by gallery
        Assert.Contains((await admin.GetAsync($"/api/admin/comments?q=pia%20private&gallery={s.Id}").ReadAsyncJson()).GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetInt32() == id);
        Assert.Empty((await admin.GetAsync($"/api/admin/comments?q=Searchable&gallery={s.Id + 9999}").ReadAsyncJson()).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Hidden_comments_do_not_count_and_a_deleted_gallery_takes_its_log_with_it()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var member = await MemberAsync("mod-c1", "Cy Count");
        var id = await PostAsync(member, s, "Counted until hidden");
        var counts = async () => (await app.NewClient().GetAsync($"/api/g/{s.Slug}").ReadAsyncJson()).GetProperty("photos").EnumerateArray()
            .Where(p => p.GetProperty("id").GetInt32() == s.Photo).Select(p => p.GetProperty("comments").GetInt32()).Single();
        Assert.Equal(1, await counts());
        await admin.PutAsJsonAsync($"/api/admin/comments/{id}/hidden", new { hidden = true });
        Assert.Equal(0, await counts());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/galleries/{s.Id}")).StatusCode);
        var log = await admin.GetAsync("/api/admin/comments/log?q=Counted%20until").ReadAsyncJson();
        Assert.Empty(log.GetProperty("items").EnumerateArray());
    }
}
