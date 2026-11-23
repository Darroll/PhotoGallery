using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoGallery.Tests;

public class FollowTests(UserFactory app) : IClassFixture<UserFactory>
{
    static readonly SemaphoreSlim Serial = new(1, 1);   // the email settings are shared by every test in this class

    async Task EmailOnAsync(HttpClient admin)
    {
        var r = await admin.PutAsJsonAsync("/api/admin/settings/email", new { enabled = true, host = "127.0.0.1", port = 25, from = "gallery@example.test", to = "owner@example.test", siteUrl = "http://localhost" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    async Task<(int gid, string slug, int[] photos)> TeamGalleryAsync(HttpClient admin, string title, int photos, string roster = "10 Emma\n7 Ava", int visibility = 0, bool names = false)
    {
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(photos), visibility });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var gid = g.GetProperty("id").GetInt32();
        var slug = g.GetProperty("slug").GetString()!;
        var team = await (await admin.PostAsJsonAsync("/api/admin/teams", new { name = title + " FC", colors = "white shirts, blue numbers", roster, tagWithNames = names })).ReadAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId = team.GetProperty("id").GetInt32() })).StatusCode);
        return (gid, slug, await AppFactory.PhotoIdsAsync(admin, slug));
    }

    Task Tag(HttpClient admin, int photo, params string[] numbers) => admin.PostAsJsonAsync($"/api/admin/photos/{photo}/players", new { add = numbers });

    Task SendAsync(TimeSpan later) => app.WithDbAsync(async db => { await PlayerFollows.SendAsync(db, app.Services.GetRequiredService<SettingsService>(), DateTime.UtcNow + later); return 0; });

    Task<List<EmailOutbox>> MailToAsync(string address) =>
        app.WithDbAsync(db => db.EmailOutbox.AsNoTracking().Where(e => e.ToAddr == address).OrderBy(e => e.Id).ToListAsync());

    static object FollowBody(string tag, bool follow = true) => new { tag, follow };

    [Fact]
    public async Task A_parent_follows_a_player_and_gets_one_email_about_new_photos_only()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            var (gid, slug, photos) = await TeamGalleryAsync(admin, "Follow Cup", 4);
            var tag = "Follow Cup FC 10";
            const string mail = "parent-follow@example.test";
            var parent = app.AsUser("parent-follow-1", mail);

            // off until the admin allows it in the gallery
            await EmailOnAsync(admin);
            Assert.Equal(JsonValueKind.Null, (await (await parent.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("follow").ValueKind);
            Assert.Equal(HttpStatusCode.BadRequest, (await parent.PostAsJsonAsync($"/api/g/{slug}/follow", FollowBody(tag))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/follow", new { allow = true })).StatusCode);
            var players = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            Assert.True(players.GetProperty("allowFollow").GetBoolean());
            Assert.True(players.GetProperty("followMail").GetBoolean());

            // a photo tagged before following is not news
            await Tag(admin, photos[0], "10");

            // signing in with a verified email is needed
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().PostAsJsonAsync($"/api/g/{slug}/follow", FollowBody(tag))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await app.AsUser("parent-follow-unverified", "nv@example.test", verified: false).PostAsJsonAsync($"/api/g/{slug}/follow", FollowBody(tag))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await parent.PostAsJsonAsync($"/api/g/{slug}/follow", FollowBody("Follow Cup FC 99"))).StatusCode);

            var f = await (await parent.PostAsJsonAsync($"/api/g/{slug}/follow", FollowBody(tag))).ReadAsync();
            Assert.Equal(new[] { "10" }, f.GetProperty("following").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal("10", (await (await parent.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("follow").GetProperty("following").EnumerateArray().Single().GetString());
            Assert.Empty((await (await app.NewClient().GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("follow").GetProperty("following").EnumerateArray());

            await Tag(admin, photos[1], "10");
            await Tag(admin, photos[2], "7");   // someone else's player

            // the tags settle first, in case the admin is still reviewing
            await SendAsync(TimeSpan.Zero);
            Assert.Empty(await MailToAsync(mail));

            await SendAsync(TimeSpan.FromMinutes(31));
            var m = Assert.Single(await MailToAsync(mail));
            Assert.Equal("New photos of #10 (Follow Cup FC)", m.Subject);
            Assert.Contains("1 new photo in Follow Cup", m.Body);
            Assert.Contains($"http://localhost/g/{slug}?player=Follow%20Cup%20FC%2010", m.Body);
            Assert.DoesNotContain("Emma", m.Body);   // numbers only: the team does not put names in tags
            var token = Regex.Match(m.Body, @"/follow/stop/([A-Za-z0-9_-]+)").Groups[1].Value;
            Assert.NotEmpty(token);

            // at most one email a day (this tag is dated an hour from now, as the clock was moved on above)
            await Tag(admin, photos[3], "10");
            await app.WithDbAsync(db => db.PhotoTags.Where(pt => pt.PhotoId == photos[3]).ExecuteUpdateAsync(u => u.SetProperty(pt => pt.TaggedAt, DateTime.UtcNow.AddHours(1))));
            await SendAsync(TimeSpan.FromHours(3));
            Assert.Single(await MailToAsync(mail));
            await SendAsync(TimeSpan.FromHours(25));
            var second = (await MailToAsync(mail)).Last();
            Assert.Contains("1 new photo in Follow Cup", second.Body);
            Assert.Equal(2, (await MailToAsync(mail)).Count);
            await SendAsync(TimeSpan.FromHours(50));   // nothing new, nothing sent
            Assert.Equal(2, (await MailToAsync(mail)).Count);

            // the link in the email stops it, without signing in
            var stop = await app.NewClient().GetAsync($"/follow/stop/{token}");
            Assert.Equal("/?unfollowed=1", stop.Headers.Location?.OriginalString);
            Assert.False(await app.WithDbAsync(db => db.PlayerFollows.AnyAsync(x => x.Sub == "parent-follow-1")));
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Only_galleries_that_allow_it_and_that_the_parent_can_open_are_mailed()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await EmailOnAsync(admin);
            var (gid, slug, photos) = await TeamGalleryAsync(admin, "Follow Scope", 1, names: true);
            await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/follow", new { allow = true });
            const string mail = "parent-scope@example.test";
            var parent = app.AsUser("parent-scope-1", mail);
            var f = await parent.PostAsJsonAsync($"/api/g/{slug}/follow", FollowBody("Emma 10"));
            Assert.Equal(HttpStatusCode.OK, f.StatusCode);
            var teamId = await app.WithDbAsync(db => db.Galleries.Where(g => g.Id == gid).Select(g => g.TeamId!.Value).FirstAsync());

            // two more galleries of the same team: one does not allow following, one is for invited people only
            async Task<int> Another(string title, int visibility, bool allow)
            {
                var r = await (await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(1), visibility })).ReadAsync();
                var id = r.GetProperty("gallery").GetProperty("id").GetInt32();
                await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/team", new { teamId });
                if (allow) await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/follow", new { allow = true });
                var p = await app.WithDbAsync(db => db.Photos.Where(x => x.GalleryId == id).Select(x => x.Id).FirstAsync());
                await Tag(admin, p, "10");
                return id;
            }
            await Another("Follow Scope Closed", 0, allow: false);
            await Another("Follow Scope Invited", 3, allow: true);
            await Tag(admin, photos[0], "10");

            await SendAsync(TimeSpan.FromMinutes(31));
            var m = Assert.Single(await MailToAsync(mail));
            Assert.Equal("New photos of Emma 10", m.Subject);   // the team puts names in tags
            Assert.Contains("Follow Scope\n", m.Body);
            Assert.DoesNotContain("Follow Scope Closed", m.Body);
            Assert.DoesNotContain("Follow Scope Invited", m.Body);

            // privacy tools see and remove follows
            var export = await (await admin.GetAsync("/api/admin/privacy/person?email=" + Uri.EscapeDataString(mail))).ReadAsync();
            Assert.Equal("10", export.GetProperty("playerFollows").EnumerateArray().Single().GetProperty("number").GetString());
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/privacy/delete", new { email = mail })).StatusCode);
            Assert.False(await app.WithDbAsync(db => db.PlayerFollows.AnyAsync(x => x.Sub == "parent-scope-1")));
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Stopping_from_the_gallery_and_unknown_links_are_harmless()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await EmailOnAsync(admin);
            var (gid, slug, _) = await TeamGalleryAsync(admin, "Follow Stop", 1);
            await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/follow", new { allow = true });
            var parent = app.AsUser("parent-stop-1", "parent-stop@example.test");
            await parent.PostAsJsonAsync($"/api/g/{slug}/follow", FollowBody("Follow Stop FC 7"));
            await parent.PostAsJsonAsync($"/api/g/{slug}/follow", FollowBody("Follow Stop FC 7"));   // twice is still one follow
            Assert.Equal(1, await app.WithDbAsync(db => db.PlayerFollows.CountAsync(x => x.Sub == "parent-stop-1")));
            var r = await (await parent.PostAsJsonAsync($"/api/g/{slug}/follow", FollowBody("Follow Stop FC 7", false))).ReadAsync();
            Assert.Empty(r.GetProperty("following").EnumerateArray());
            Assert.Equal("/?unfollowed=1", (await app.NewClient().GetAsync("/follow/stop/not-a-real-token")).Headers.Location?.OriginalString);
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task The_site_wide_switch_covers_every_team_gallery_and_my_players_page_follows_from_anywhere()
    {
        await Serial.WaitAsync();
        var admin = await app.AdminAsync();
        try
        {
            await EmailOnAsync(admin);
            var (gid, slug, photos) = await TeamGalleryAsync(admin, "Follow Site", 1);
            var teamId = await app.WithDbAsync(db => db.Galleries.Where(g => g.Id == gid).Select(g => g.TeamId!.Value).FirstAsync());
            const string mail = "parent-site@example.test";
            var parent = app.AsUser("parent-site-1", mail);

            // site switch off: nothing to follow on the page
            Assert.DoesNotContain((await (await parent.GetAsync("/api/me/players")).ReadAsync()).GetProperty("teams").EnumerateArray(), t => t.GetProperty("id").GetInt32() == teamId);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/settings/follow", new { enabled = true })).StatusCode);
            Assert.True((await (await admin.GetAsync("/api/admin/settings/follow")).ReadAsync()).GetProperty("enabled").GetBoolean());
            Assert.True((await (await parent.GetAsync("/api/me")).ReadAsync()).GetProperty("follow").GetBoolean());

            // every gallery of the team offers it now, without its own switch
            Assert.Equal(JsonValueKind.Object, (await (await parent.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("follow").ValueKind);

            // a second gallery of the same team, turned off for itself
            var r = await (await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Follow Site Off", folder = app.NewFolder(1), visibility = 0 })).ReadAsync();
            var offId = r.GetProperty("gallery").GetProperty("id").GetInt32();
            var offSlug = r.GetProperty("gallery").GetProperty("slug").GetString()!;
            await admin.PutAsJsonAsync($"/api/admin/galleries/{offId}/team", new { teamId });
            await admin.PutAsJsonAsync($"/api/admin/galleries/{offId}/follow", new { allow = false });
            Assert.Equal(JsonValueKind.Null, (await (await parent.GetAsync($"/api/g/{offSlug}")).ReadAsync()).GetProperty("follow").ValueKind);
            // and a third, on by default
            r = await (await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Follow Site Next Game", folder = app.NewFolder(1), visibility = 0 })).ReadAsync();
            var nextId = r.GetProperty("gallery").GetProperty("id").GetInt32();
            await admin.PutAsJsonAsync($"/api/admin/galleries/{nextId}/team", new { teamId });

            // follow from the My players page, by team and number
            var page = await (await parent.GetAsync("/api/me/players")).ReadAsync();
            var team = page.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == teamId);
            var ten = team.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("number").GetString() == "10");
            Assert.Equal("#10", ten.GetProperty("label").GetString());   // numbers only
            Assert.Equal(HttpStatusCode.OK, (await parent.PostAsJsonAsync("/api/me/players", new { playerId = ten.GetProperty("id").GetInt32(), follow = true })).StatusCode);
            Assert.Equal("10", (await (await parent.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("follow").GetProperty("following").EnumerateArray().Single().GetString());

            await Tag(admin, photos[0], "10");
            await Tag(admin, await app.WithDbAsync(db => db.Photos.Where(x => x.GalleryId == offId).Select(x => x.Id).FirstAsync()), "10");
            await Tag(admin, await app.WithDbAsync(db => db.Photos.Where(x => x.GalleryId == nextId).Select(x => x.Id).FirstAsync()), "10");
            await SendAsync(TimeSpan.FromMinutes(31));
            var m = Assert.Single(await MailToAsync(mail));
            Assert.Contains("1 new photo in Follow Site\n", m.Body);
            Assert.Contains("1 new photo in Follow Site Next Game", m.Body);
            Assert.DoesNotContain("Follow Site Off", m.Body);
            Assert.Contains("http://localhost/players", m.Body);

            // a player of a team the parent cannot see is refused
            var hidden = await (await admin.PostAsJsonAsync("/api/admin/teams", new { name = "Follow Hidden FC", colors = "red", roster = "4" })).ReadAsync();
            var four = hidden.GetProperty("players").EnumerateArray().Single().GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.BadRequest, (await parent.PostAsJsonAsync("/api/me/players", new { playerId = four, follow = true })).StatusCode);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/settings/follow", new { enabled = false });
            Serial.Release();
        }
    }

    [Fact]
    public async Task Following_a_girl_covers_every_team_she_is_on()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await EmailOnAsync(admin);
            var (gidA, slugA, photosA) = await TeamGalleryAsync(admin, "Follow OCSS", 2, roster: "42 Reagan Q\n9 Mia Q");
            var (gidB, slugB, photosB) = await TeamGalleryAsync(admin, "Follow East", 2, roster: "11 Reagan Q");
            foreach (var g in new[] { gidA, gidB }) await admin.PutAsJsonAsync($"/api/admin/galleries/{g}/follow", new { allow = true });
            const string mail = "parent-reagan@example.test";
            var parent = app.AsUser("parent-reagan", mail);

            Assert.Equal(HttpStatusCode.OK, (await parent.PostAsJsonAsync($"/api/g/{slugA}/follow", FollowBody("Follow OCSS FC 42"))).StatusCode);
            // she shows as followed on the other team too, as that team's number
            Assert.Equal("11", (await (await parent.GetAsync($"/api/g/{slugB}")).ReadAsync()).GetProperty("follow").GetProperty("following").EnumerateArray().Single().GetString());

            await Tag(admin, photosA[0], "42");
            await Tag(admin, photosB[0], "11");
            await Tag(admin, photosB[1], "11");
            await SendAsync(TimeSpan.FromHours(1));
            var m = Assert.Single(await MailToAsync(mail));
            Assert.Contains("1 new photo in Follow OCSS", m.Body);
            Assert.Contains("2 new photos in Follow East", m.Body);
            Assert.Contains("?player=" + Uri.EscapeDataString("Follow East FC 11"), m.Body);

            // stopping on one team stops her everywhere
            Assert.Equal(HttpStatusCode.OK, (await parent.PostAsJsonAsync($"/api/g/{slugB}/follow", FollowBody("Follow East FC 11", false))).StatusCode);
            Assert.Empty((await (await parent.GetAsync($"/api/g/{slugA}")).ReadAsync()).GetProperty("follow").GetProperty("following").EnumerateArray());
        }
        finally { Serial.Release(); }
    }
}
