using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery.Tests;

public class CommentTests(UserFactory app) : IClassFixture<UserFactory>
{
    record Setup(int Id, string Slug, string Folder, int Photo);

    async Task<Setup> GalleryAsync(HttpClient admin, bool comments = true, int photos = 2)
    {
        var folder = app.NewFolder(photos);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Talk " + Guid.NewGuid().ToString("N")[..6], folder, visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var id = g.GetProperty("id").GetInt32();
        var slug = g.GetProperty("slug").GetString()!;
        if (comments) Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { allowComments = true })).StatusCode);
        return new(id, slug, folder, (await AppFactory.PhotoIdsAsync(admin, slug))[0]);
    }

    static string Url(Setup s, string tail = "") => $"/api/g/{s.Slug}/photo/{s.Photo}/comments{tail}";

    async Task<HttpClient> MemberAsync(string sub, string name)
    {
        var c = app.AsUser(sub, sub + "@example.test");
        var r = await c.PutAsJsonAsync("/api/me/display-name", new { name });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    static async Task<JsonElement> List(HttpClient c, Setup s) => await (await c.GetAsync(Url(s))).ReadAsync();

    [Fact]
    public async Task Comments_are_off_until_the_gallery_turns_them_on()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin, comments: false);
        var member = await MemberAsync("off-1", "Olive Off");
        Assert.False((await List(member, s)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync(Url(s), new { text = "hi" })).StatusCode);
        Assert.False((await app.NewClient().GetAsync($"/api/g/{s.Slug}").ReadAsyncJson()).GetProperty("allowComments").GetBoolean());
    }

    [Fact]
    public async Task Only_verified_signed_in_people_with_a_display_name_can_post_and_the_email_never_shows()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);

        var anon = app.NewClient();
        var seen = await List(anon, s);
        Assert.True(seen.GetProperty("enabled").GetBoolean());
        Assert.False(seen.GetProperty("me").GetProperty("canPost").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync(Url(s), new { text = "hello" })).StatusCode);

        var unverified = app.AsUser("unv-1", "unv@example.test", verified: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await unverified.PostAsJsonAsync(Url(s), new { text = "hello" })).StatusCode);

        var fresh = app.AsUser("fresh-1", "fresh@example.test");
        var me = (await List(fresh, s)).GetProperty("me");
        Assert.False(me.GetProperty("canPost").GetBoolean());
        Assert.Equal("Test fresh-1", me.GetProperty("suggestedName").GetString());
        var noName = await fresh.PostAsJsonAsync(Url(s), new { text = "hello" });
        Assert.Equal(HttpStatusCode.Conflict, noName.StatusCode);
        Assert.True((await noName.ReadAsync()).GetProperty("needsName").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await fresh.PutAsJsonAsync("/api/me/display-name", new { name = "Fran" })).StatusCode);
        var posted = await fresh.PostAsJsonAsync(Url(s), new { text = "Lovely light here" });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var raw = await (await anon.GetAsync(Url(s))).Content.ReadAsStringAsync();
        Assert.Contains("Lovely light here", raw);
        Assert.Contains("Fran", raw);
        Assert.DoesNotContain("fresh@example.test", raw);
        Assert.DoesNotContain("fresh-1", raw);
        Assert.DoesNotContain("\"ip\"", raw);
    }

    [Fact]
    public async Task A_display_name_is_a_name_not_an_email_and_cannot_pose_as_the_photographer_or_repeat()
    {
        var c = app.AsUser("name-1", "name1@example.test");
        foreach (var bad in new[] { "me@example.test", "x", "Photographer", "admin", "http://spam.test", new string('a', 41) })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/me/display-name", new { name = bad })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/me/display-name", new { name = "Unique  Una" })).StatusCode);
        var other = app.AsUser("name-2", "name2@example.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PutAsJsonAsync("/api/me/display-name", new { name = "unique una" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().PutAsJsonAsync("/api/me/display-name", new { name = "Nobody" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.AsUser("name-3", "n3@example.test", verified: false).PutAsJsonAsync("/api/me/display-name", new { name = "Valid Name" })).StatusCode);
    }

    [Fact]
    public async Task Renaming_yourself_renames_your_earlier_comments()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var c = await MemberAsync("ren-1", "Old Name");
        await c.PostAsJsonAsync(Url(s), new { text = "First" });
        await c.PutAsJsonAsync("/api/me/display-name", new { name = "New Name" });
        var list = (await List(c, s)).GetProperty("comments");
        Assert.Equal("New Name", list[0].GetProperty("author").GetString());
    }

    [Fact]
    public async Task Replies_are_flat_and_the_photographer_replies_marked_as_such()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var fan = await MemberAsync("rep-1", "Rae Fan");
        var first = await (await fan.PostAsJsonAsync(Url(s), new { text = "Who took this?" })).ReadAsync();
        var firstId = first.GetProperty("id").GetInt32();

        var answer = await (await admin.PostAsJsonAsync(Url(s), new { text = "I did, last June.", replyTo = firstId })).ReadAsync();
        Assert.True(answer.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("Photographer", answer.GetProperty("author").GetString());
        Assert.Equal("Rae Fan", answer.GetProperty("replyTo").GetProperty("author").GetString());
        Assert.True(answer.GetProperty("mine").GetBoolean());

        var seenByFan = (await List(fan, s)).GetProperty("comments");
        Assert.Equal(2, seenByFan.GetArrayLength());
        Assert.True(seenByFan[0].GetProperty("mine").GetBoolean());
        Assert.False(seenByFan[1].GetProperty("mine").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await fan.PostAsJsonAsync(Url(s), new { text = "Reply to nothing", replyTo = 999999 })).StatusCode);
    }

    [Fact]
    public async Task You_can_change_or_remove_your_own_comment_for_fifteen_minutes_and_the_log_keeps_everything()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var me = await MemberAsync("own-1", "Owen Own");
        var other = await MemberAsync("own-2", "Olga Other");
        var posted = await (await me.PostAsJsonAsync(Url(s), new { text = "Original words" })).ReadAsync();
        var id = posted.GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(Url(s, "/" + id), new { text = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync(Url(s, "/" + id))).StatusCode);

        var edited = await (await me.PutAsJsonAsync(Url(s, "/" + id), new { text = "Better words" })).ReadAsync();
        Assert.Equal("Better words", edited.GetProperty("text").GetString());
        Assert.True(edited.GetProperty("edited").GetBoolean());
        var events = await app.WithDbAsync(db => db.CommentEvents.Where(e => e.CommentId == id).OrderBy(e => e.Id).ToListAsync());
        Assert.Equal(["post", "edit"], events.Select(e => e.Kind));
        Assert.Equal("Original words", events[1].Text);   // the earlier text
        Assert.Equal("own-1@example.test", events[0].ActorEmail);

        // after the window, no more changes
        await app.WithDbAsync(async db => { await db.Comments.Where(c => c.Id == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.CreatedAt, DateTime.UtcNow.AddMinutes(-16))); return 0; });
        var late = await me.PutAsJsonAsync(Url(s, "/" + id), new { text = "Too late" });
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
        Assert.Contains("15 minutes", await late.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await me.DeleteAsync(Url(s, "/" + id))).StatusCode);

        // a fresh one can be removed: it disappears from the page but stays in the log
        var second = (await (await me.PostAsJsonAsync(Url(s), new { text = "Oops, wrong photo" })).ReadAsync()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync(Url(s, "/" + second))).StatusCode);
        Assert.DoesNotContain(second, (await List(me, s)).GetProperty("comments").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()));
        var row = await app.WithDbAsync(db => db.Comments.SingleAsync(c => c.Id == second));
        Assert.True(row.Deleted);
        Assert.Equal("Oops, wrong photo", row.Text);
        Assert.Contains(await app.WithDbAsync(db => db.CommentEvents.Where(e => e.CommentId == second).Select(e => e.Kind).ToListAsync()), k => k == "delete");
    }

    [Fact]
    public async Task The_log_row_records_where_the_comment_came_from_without_showing_it()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var me = await MemberAsync("log-1", "Lou Log");
        me.DefaultRequestHeaders.Add("X-Test-Ip", "203.0.113.9");
        me.DefaultRequestHeaders.UserAgent.ParseAdd("TestBrowser/1.0");
        var id = (await (await me.PostAsJsonAsync(Url(s), new { text = "For the record" })).ReadAsync()).GetProperty("id").GetInt32();
        var row = await app.WithDbAsync(db => db.Comments.SingleAsync(c => c.Id == id));
        Assert.Equal("log-1@example.test", row.Email);
        Assert.Equal("log-1", row.Sub);
        Assert.Contains("TestBrowser", row.UserAgent);
        var shown = await (await me.GetAsync(Url(s))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("203.0.113.9", shown);
        Assert.DoesNotContain("TestBrowser", shown);
    }

    [Fact]
    public async Task The_photo_switch_and_the_gallery_switch_turn_comments_off()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var me = await MemberAsync("sw-1", "Sam Switch");
        await me.PostAsJsonAsync(Url(s), new { text = "Visible for now" });

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/photos/{s.Photo}/comments", new { off = true })).StatusCode);
        Assert.False((await List(me, s)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync(Url(s), new { text = "More" })).StatusCode);
        var page = await (await app.NewClient().GetAsync($"/api/g/{s.Slug}")).ReadAsync();
        Assert.True(page.GetProperty("photos").EnumerateArray().First(p => p.GetProperty("id").GetInt32() == s.Photo).GetProperty("commentsOff").GetBoolean());

        await admin.PutAsJsonAsync($"/api/admin/photos/{s.Photo}/comments", new { off = false });
        Assert.True((await List(me, s)).GetProperty("enabled").GetBoolean());
        Assert.Single((await List(me, s)).GetProperty("comments").EnumerateArray());   // earlier comments come back

        await admin.PutAsJsonAsync($"/api/admin/galleries/{s.Id}", new { allowComments = false });
        Assert.False((await List(me, s)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().PutAsJsonAsync($"/api/admin/photos/{s.Photo}/comments", new { off = true })).StatusCode);
    }

    [Fact]
    public async Task Text_rules_rate_limit_and_duplicates_keep_it_tidy()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var me = await MemberAsync("tidy-1", "Tia Tidy");
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync(Url(s), new { text = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync(Url(s), new { text = new string('x', 1001) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync(Url(s), new { text = "http://a.test http://b.test https://c.test" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await me.PostAsJsonAsync(Url(s), new { text = "One" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await me.PostAsJsonAsync(Url(s), new { text = "One" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await me.PostAsJsonAsync(Url(s), new { text = "Two" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await me.PostAsJsonAsync(Url(s), new { text = "Three" })).StatusCode);
        var fourth = await me.PostAsJsonAsync(Url(s), new { text = "Four" });
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Contains("quickly", await fourth.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_gallery_page_carries_comment_counts_and_hidden_galleries_stay_closed()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var me = await MemberAsync("cnt-1", "Cleo Count");
        await me.PostAsJsonAsync(Url(s), new { text = "Counted" });
        var page = await (await app.NewClient().GetAsync($"/api/g/{s.Slug}")).ReadAsync();
        Assert.True(page.GetProperty("allowComments").GetBoolean());
        Assert.Equal(1, page.GetProperty("photos").EnumerateArray().First(p => p.GetProperty("id").GetInt32() == s.Photo).GetProperty("comments").GetInt32());

        var folder = app.NewFolder(1);
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Closed " + Guid.NewGuid().ToString("N")[..6], folder, visibility = 2, password = "pw-for-this" });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var id = g.GetProperty("id").GetInt32(); var slug = g.GetProperty("slug").GetString()!;
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { allowComments = true });
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        Assert.Equal(HttpStatusCode.Forbidden, (await me.GetAsync($"/api/g/{slug}/photo/{photo}/comments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/comments", new { text = "let me in" })).StatusCode);
    }

    [Fact]
    public async Task Comments_stay_with_the_photo_when_its_file_is_renamed_or_moved_to_another_gallery()
    {
        var admin = await app.AdminAsync();
        // own images, so the file fingerprints are unique
        string Folder()
        {
            var f = "c" + Guid.NewGuid().ToString("N")[..8];
            Directory.CreateDirectory(Path.Combine(app.Library, f));
            TestImages.Write(Path.Combine(app.Library, f, "a.jpg"), 300 + Random.Shared.Next(1, 4000), 210, 40 + Random.Shared.Next(0, 150));
            return f;
        }
        async Task<(int id, string slug)> Make(string folder)
        {
            var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Move " + Guid.NewGuid().ToString("N")[..6], folder, visibility = 0 });
            var g = (await r.ReadAsync()).GetProperty("gallery");
            var id = g.GetProperty("id").GetInt32();
            await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { allowComments = true });
            return (id, g.GetProperty("slug").GetString()!);
        }
        void Move(string from, string to)
        {
            for (var i = 0; ; i++)
                try { File.Move(from, to); return; }
                catch (IOException) when (i < 50) { Thread.Sleep(100); }
        }
        var fromFolder = Folder(); var toFolder = Folder();
        var (idA, slugA) = await Make(fromFolder);
        var (idB, slugB) = await Make(toFolder);
        var photoA = (await AppFactory.PhotoIdsAsync(admin, slugA))[0];
        var me = await MemberAsync("mv-1", "Mo Mover");
        var cid = (await (await me.PostAsJsonAsync($"/api/g/{slugA}/photo/{photoA}/comments", new { text = "Keep me" })).ReadAsync()).GetProperty("id").GetInt32();

        // renamed inside the gallery: same photo, same comment
        Move(Path.Combine(app.Library, fromFolder, "a.jpg"), Path.Combine(app.Library, fromFolder, "renamed.jpg"));
        await admin.PostAsync($"/api/admin/galleries/{idA}/scan", null);
        Assert.Single((await (await me.GetAsync($"/api/g/{slugA}/photo/{photoA}/comments")).ReadAsync()).GetProperty("comments").EnumerateArray());

        // moved to another gallery: the comment follows, with its log
        Move(Path.Combine(app.Library, fromFolder, "renamed.jpg"), Path.Combine(app.Library, toFolder, "arrived.jpg"));
        await admin.PostAsync($"/api/admin/galleries/{idB}/scan", null);
        await admin.PostAsync($"/api/admin/galleries/{idA}/scan", null);
        var arrived = await app.WithDbAsync(db => db.Photos.Where(p => p.GalleryId == idB && p.Name == "arrived.jpg").Select(p => p.Id).SingleAsync());
        var shown = (await (await me.GetAsync($"/api/g/{slugB}/photo/{arrived}/comments")).ReadAsync()).GetProperty("comments");
        Assert.Equal("Keep me", shown[0].GetProperty("text").GetString());
        var row = await app.WithDbAsync(db => db.Comments.SingleAsync(c => c.Id == cid));
        Assert.Equal((idB, arrived), (row.GalleryId, row.PhotoId));
        Assert.All(await app.WithDbAsync(db => db.CommentEvents.Where(e => e.CommentId == cid).ToListAsync()), e => Assert.Equal(arrived, e.PhotoId));
    }

    [Fact]
    public async Task Deleting_a_gallery_removes_its_comments_and_log()
    {
        var admin = await app.AdminAsync();
        var s = await GalleryAsync(admin);
        var me = await MemberAsync("gone-1", "Gail Gone");
        var cid = (await (await me.PostAsJsonAsync(Url(s), new { text = "Short lived" })).ReadAsync()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/galleries/{s.Id}")).StatusCode);
        Assert.False(await app.WithDbAsync(db => db.Comments.AnyAsync(c => c.Id == cid)));
        Assert.False(await app.WithDbAsync(db => db.CommentEvents.AnyAsync(e => e.CommentId == cid)));
    }
}

static class JsonTaskExtensions
{
    public static async Task<JsonElement> ReadAsyncJson(this Task<HttpResponseMessage> t) => await (await t).ReadAsync();
}
