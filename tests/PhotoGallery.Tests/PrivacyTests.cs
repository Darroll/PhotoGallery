using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery.Tests;

public class PrivacyTests(AppFactory app) : IClassFixture<AppFactory>
{
    record Seeded(int Gallery, int Photo, string Jane, string Joe, string JaneSub, string JoeSub, string JaneVid, string JoeVid);

    /// <summary>Gives two people the same kinds of records, so a delete for one can be checked against the other. Each tag makes a separate pair.</summary>
    async Task<Seeded> SeedAsync(string tag)
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Privacy " + tag, app.NewFolder(1));
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        var gallery = await app.WithDbAsync(db => db.Galleries.Where(g => g.Slug == slug).Select(g => g.Id).FirstAsync());
        var s = new Seeded(gallery, photo, "jane-" + tag + "@example.com", "joe-" + tag + "@example.com", "sub-jane-" + tag, "sub-joe-" + tag, "vid-jane-" + tag, "vid-joe-" + tag);
        await app.WithDbAsync(async db =>
        {
            foreach (var (email, sub, vid) in new[] { (s.Jane, s.JaneSub, s.JaneVid), (s.Joe, s.JoeSub, s.JoeVid) })
            {
                db.Users.Add(new AppUser { Sub = sub, Email = email, Name = email[..4], DisplayName = email[..4] });
                var c = new Comment { GalleryId = gallery, PhotoId = photo, Sub = sub, Email = email, Author = email[..4], Text = "Lovely from " + email };
                db.Comments.Add(c);
                await db.SaveChangesAsync();
                db.CommentEvents.Add(new CommentEvent { CommentId = c.Id, GalleryId = gallery, PhotoId = photo, Kind = "post", ActorSub = sub, ActorEmail = email, Text = c.Text, Ip = "203.0.113.5" });
                db.Favorites.Add(new Favorite { GalleryId = gallery, PhotoId = photo, VisitorId = vid, IdentityKey = "u:" + sub, Ip = "203.0.113.5" });
                db.LicenseAcceptances.Add(new LicenseAcceptance { Sub = sub, VisitorId = vid, Name = email[..4], Version = "v1" });
                db.CaptionEdits.Add(new CaptionEdit { PhotoId = photo, Field = "caption", Proposed = "x", Sub = sub, Name = email[..4] });
                db.Subscribers.Add(new Subscriber { Email = email, Token = Guid.NewGuid().ToString("N") });
                db.Visits.Add(new Visit { Kind = "view_gallery", GalleryId = gallery, Ip = "203.0.113.5", VisitorId = vid });
                db.EmailOutbox.Add(new EmailOutbox { ToAddr = email, Subject = "Hello", Body = "x" });
            }
            var grp = new PeopleGroup { Name = "Family " + tag };
            db.PeopleGroups.Add(grp);
            await db.SaveChangesAsync();
            db.GroupMembers.AddRange(new GroupMember { GroupId = grp.Id, Email = s.Jane }, new GroupMember { GroupId = grp.Id, Email = s.Joe });
            var g = await db.Galleries.FirstAsync(x => x.Id == gallery);
            g.AllowedEmails = s.Jane + "\n" + s.Joe;
            db.CommentBlocks.Add(new CommentBlock { Sub = s.JaneSub, Email = s.Jane, Name = "Jane", Reason = "spam" });
            await db.SaveChangesAsync();
            return 0;
        });
        return s;
    }

    static JsonElement Count(JsonElement j, string k) => j.GetProperty(k);

    [Fact]
    public async Task Privacy_tools_need_an_admin()
    {
        var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/privacy/person?email=a@b.co")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/admin/privacy/delete", new { email = "a@b.co" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/privacy/people")).StatusCode);
    }

    [Fact]
    public async Task Export_lists_what_is_held_about_one_person_only()
    {
        var s = await SeedAsync("exp");
        var admin = await app.AdminAsync();
        var r = await admin.GetAsync("/api/admin/privacy/person?email=" + Uri.EscapeDataString("  " + s.Jane.ToUpperInvariant() + " "));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.ReadAsync();
        Assert.Equal(s.Jane, j.GetProperty("email").GetString());
        Assert.Equal(1, Count(j, "accounts").GetArrayLength());
        Assert.Equal(1, Count(j, "comments").GetArrayLength());
        Assert.All(j.GetProperty("comments").EnumerateArray(), c => Assert.Contains(s.Jane, c.GetProperty("text").GetString()));
        Assert.Equal(1, Count(j, "likes").GetArrayLength());
        Assert.Equal(1, Count(j, "visits").GetArrayLength());
        Assert.Equal(1, Count(j, "subscription").GetArrayLength());
        Assert.Equal(1, Count(j, "groupMemberships").GetArrayLength());
        Assert.Equal(1, Count(j, "galleryInvitations").GetArrayLength());
        Assert.DoesNotContain(s.Joe, j.GetRawText());
    }

    [Fact]
    public async Task Export_can_be_downloaded_as_a_file_and_rejects_bad_addresses()
    {
        var s = await SeedAsync("dl");
        var admin = await app.AdminAsync();
        var r = await admin.GetAsync("/api/admin/privacy/person?download=true&email=" + Uri.EscapeDataString(s.Jane));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cd = r.Content.Headers.ContentDisposition?.ToString() ?? "";
        Assert.Contains("attachment", cd);
        Assert.DoesNotContain("jane", cd);   // the file name must not reveal who it is about
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/privacy/person?email=not-an-email")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/privacy/delete", new { email = "" })).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_one_person_everywhere_and_leaves_everyone_else()
    {
        var s = await SeedAsync("del");
        var admin = await app.AdminAsync();
        // a held copy of removed-photo details, with both people's likes in it
        await app.WithDbAsync(async db =>
        {
            var saved = new FileIdentity.Saved("", "", "", [], [new(s.JaneVid, "u:" + s.JaneSub, "1.1.1.1", DateTime.UtcNow), new(s.JoeVid, "u:" + s.JoeSub, "2.2.2.2", DateTime.UtcNow)]);
            db.PhotosRetired.Add(new PhotoRetired { Fingerprint = "fp-" + Guid.NewGuid().ToString("N"), Name = "gone.jpg", GalleryId = s.Gallery, Data = JsonSerializer.Serialize(saved) });
            await db.SaveChangesAsync();
            return 0;
        });

        var r = await admin.PostAsJsonAsync("/api/admin/privacy/delete", new { email = s.Jane, removeBlock = false });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var removed = await r.ReadAsync();
        Assert.Equal(1, removed.GetProperty("comments").GetInt32());
        Assert.Equal(1, removed.GetProperty("likes").GetInt32());
        Assert.Equal(1, removed.GetProperty("accounts").GetInt32());
        Assert.True(removed.GetProperty("blockKept").GetBoolean());

        await app.WithDbAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.Email == s.Jane));
            Assert.False(await db.Comments.AnyAsync(c => c.Email == s.Jane));
            Assert.False(await db.CommentEvents.AnyAsync(e => e.ActorEmail == s.Jane));
            Assert.False(await db.Favorites.AnyAsync(f => f.IdentityKey == "u:" + s.JaneSub || f.VisitorId == s.JaneVid));
            Assert.False(await db.Visits.AnyAsync(v => v.VisitorId == s.JaneVid));
            Assert.False(await db.LicenseAcceptances.AnyAsync(a => a.Sub == s.JaneSub));
            Assert.False(await db.CaptionEdits.AnyAsync(c => c.Sub == s.JaneSub));
            Assert.False(await db.Subscribers.AnyAsync(x => x.Email == s.Jane));
            Assert.False(await db.GroupMembers.AnyAsync(m => m.Email == s.Jane));
            Assert.False(await db.EmailOutbox.AnyAsync(m => m.ToAddr == s.Jane));
            Assert.DoesNotContain(s.Jane, (await db.Galleries.FirstAsync(g => g.Id == s.Gallery)).AllowedEmails);
            var held = string.Join("", await db.PhotosRetired.Select(x => x.Data).ToListAsync());
            Assert.DoesNotContain("u:" + s.JaneSub, held);
            Assert.True(await db.CommentBlocks.AnyAsync(b => b.Email == s.Jane), "the block stays by default");

            // Joe is untouched
            Assert.True(await db.Users.AnyAsync(u => u.Email == s.Joe));
            Assert.True(await db.Comments.AnyAsync(c => c.Email == s.Joe && c.PhotoId == s.Photo));
            Assert.True(await db.CommentEvents.AnyAsync(e => e.ActorEmail == s.Joe));
            Assert.True(await db.Favorites.AnyAsync(f => f.IdentityKey == "u:" + s.JoeSub));
            Assert.True(await db.Visits.AnyAsync(v => v.VisitorId == s.JoeVid));
            Assert.True(await db.Subscribers.AnyAsync(x => x.Email == s.Joe));
            Assert.True(await db.GroupMembers.AnyAsync(m => m.Email == s.Joe));
            Assert.Contains(s.Joe, (await db.Galleries.FirstAsync(g => g.Id == s.Gallery)).AllowedEmails);
            Assert.Contains("u:" + s.JoeSub, held);
            return 0;
        });

        // a second delete finds nothing, and removing the block on request works
        var again = await (await admin.PostAsJsonAsync("/api/admin/privacy/delete", new { email = s.Jane, removeBlock = true })).ReadAsync();
        Assert.Equal(0, again.GetProperty("comments").GetInt32());
        Assert.False(again.GetProperty("blockKept").GetBoolean());
        await app.WithDbAsync(async db => { Assert.False(await db.CommentBlocks.AnyAsync(b => b.Email == s.Jane)); return 0; });
    }

    [Fact]
    public async Task People_search_finds_known_addresses()
    {
        var s = await SeedAsync("find");
        var admin = await app.AdminAsync();
        var j = await (await admin.GetAsync("/api/admin/privacy/people?q=joe-find")).ReadAsync();
        Assert.Contains(j.EnumerateArray(), p => p.GetProperty("email").GetString() == s.Joe);
        Assert.DoesNotContain(j.EnumerateArray(), p => p.GetProperty("email").GetString() == s.Jane);
    }
}

public class PrivacyNoticeTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task The_notice_is_public_with_placeholders_filled_and_linked_from_the_site()
    {
        var anon = app.NewClient();
        var site = await (await anon.GetAsync("/api/site")).ReadAsync();
        Assert.Equal("Privacy notice", site.GetProperty("privacy").GetString());
        var p = await (await anon.GetAsync("/api/privacy")).ReadAsync();
        Assert.True(p.GetProperty("enabled").GetBoolean());
        var text = p.GetProperty("text").GetString()!;
        Assert.Contains("Test Photos", text);
        Assert.DoesNotContain("{site}", text); Assert.DoesNotContain("{contact}", text); Assert.DoesNotContain("{year}", text);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/privacy")).StatusCode);
    }

    [Fact]
    public async Task The_admin_can_edit_hide_and_restore_the_notice()
    {
        var admin = await app.AdminAsync();
        var put = await admin.PutAsJsonAsync("/api/admin/settings/privacy", new { enabled = true, title = "About your data", contact = "me@example.com", text = "We keep little. Write to {contact}." });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var p = await (await app.NewClient().GetAsync("/api/privacy")).ReadAsync();
        Assert.Equal("About your data", p.GetProperty("title").GetString());
        Assert.Equal("We keep little. Write to me@example.com.", p.GetProperty("text").GetString());

        await admin.PutAsJsonAsync("/api/admin/settings/privacy", new { enabled = false, title = "About your data", contact = "", text = "" });
        var site = await (await app.NewClient().GetAsync("/api/site")).ReadAsync();
        Assert.Equal(JsonValueKind.Null, site.GetProperty("privacy").ValueKind);
        // an empty text falls back to the default wording
        var s = await (await admin.GetAsync("/api/admin/settings/privacy")).ReadAsync();
        Assert.Contains("What is kept, and why", s.GetProperty("privacy").GetProperty("text").GetString());
    }

    [Fact]
    public void Redaction_helper_is_not_confused_by_the_default_text() =>
        Assert.True(PrivacySettings.DefaultText.Length < PrivacySettings.MaxText);
}
