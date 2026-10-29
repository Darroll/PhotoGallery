using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

public class GroupTests(UserFactory app) : IClassFixture<UserFactory>
{
    static string Unique(string prefix) => prefix + " " + Guid.NewGuid().ToString("N")[..6];

    static async Task<int> GroupAsync(HttpClient admin, string name, params string[] emails)
    {
        var r = await admin.PostAsJsonAsync("/api/admin/groups", new { name, emails });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.ReadAsync()).GetProperty("id").GetInt32();
    }

    /// <summary>A Specific people gallery with no emails of its own, inviting these groups. Returns slug and id.</summary>
    static async Task<(string slug, int id)> GalleryForAsync(UserFactory app, HttpClient admin, params int[] groupIds)
    {
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = Unique("Groups gallery"), folder = app.NewFolder(1), visibility = 3 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var id = g.GetProperty("id").GetInt32();
        var put = await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/groups", new { ids = groupIds });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return (g.GetProperty("slug").GetString()!, id);
    }

    static async Task<HttpStatusCode> OpenAsync(HttpClient c, string slug) => (await c.GetAsync($"/api/g/{slug}")).StatusCode;

    [Fact]
    public async Task A_group_member_opens_a_gallery_the_group_is_invited_to_and_others_do_not()
    {
        var admin = await app.AdminAsync();
        var group = await GroupAsync(admin, Unique("Family"), "Aunt.May@Example.test");
        var (slug, _) = await GalleryForAsync(app, admin, group);

        Assert.Equal(HttpStatusCode.OK, await OpenAsync(app.AsUser("sub-aunt", "aunt.may@example.test"), slug));
        Assert.Equal(HttpStatusCode.Forbidden, await OpenAsync(app.AsUser("sub-stranger", "stranger@example.test"), slug));
        Assert.Equal(HttpStatusCode.Unauthorized, await OpenAsync(app.NewClient(), slug));
    }

    [Fact]
    public async Task One_person_can_be_in_several_groups_and_each_group_opens_its_own_galleries()
    {
        var admin = await app.AdminAsync();
        var family = await GroupAsync(admin, Unique("Family"));
        var team = await GroupAsync(admin, Unique("Team"));
        var (familySlug, _) = await GalleryForAsync(app, admin, family);
        var (teamSlug, _) = await GalleryForAsync(app, admin, team);

        var put = await admin.PutAsJsonAsync("/api/admin/people/groups", new { email = "Both@Example.test", groupIds = new[] { family, team } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var other = await admin.PutAsJsonAsync("/api/admin/people/groups", new { email = "familyonly@example.test", groupIds = new[] { family } });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        var both = app.AsUser("sub-both", "both@example.test");
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(both, familySlug));
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(both, teamSlug));

        var familyOnly = app.AsUser("sub-familyonly", "familyonly@example.test");
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(familyOnly, familySlug));
        Assert.Equal(HttpStatusCode.Forbidden, await OpenAsync(familyOnly, teamSlug));

        // the people list shows both groups for the person
        var people = await (await admin.GetAsync("/api/admin/people")).ReadAsync();
        var person = people.EnumerateArray().Single(p => p.GetProperty("email").GetString() == "both@example.test");
        Assert.Equal(new[] { family, team }.OrderBy(i => i), person.GetProperty("groupIds").EnumerateArray().Select(i => i.GetInt32()).OrderBy(i => i));
    }

    [Fact]
    public async Task Changing_a_persons_groups_takes_effect_at_once()
    {
        var admin = await app.AdminAsync();
        var a = await GroupAsync(admin, Unique("A group"));
        var b = await GroupAsync(admin, Unique("B group"));
        var (slugA, _) = await GalleryForAsync(app, admin, a);
        var (slugB, _) = await GalleryForAsync(app, admin, b);
        var person = app.AsUser("sub-mover", "mover@example.test");

        await admin.PutAsJsonAsync("/api/admin/people/groups", new { email = "mover@example.test", groupIds = new[] { a, b } });
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(person, slugA));
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(person, slugB));

        // leaving one group keeps the other
        await admin.PutAsJsonAsync("/api/admin/people/groups", new { email = "mover@example.test", groupIds = new[] { b } });
        Assert.Equal(HttpStatusCode.Forbidden, await OpenAsync(person, slugA));
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(person, slugB));

        // an empty list takes them out of every group
        await admin.PutAsJsonAsync("/api/admin/people/groups", new { email = "mover@example.test", groupIds = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.Forbidden, await OpenAsync(person, slugB));
        var people = await (await admin.GetAsync("/api/admin/people")).ReadAsync();
        Assert.DoesNotContain(people.EnumerateArray(), p => p.GetProperty("email").GetString() == "mover@example.test");
    }

    [Fact]
    public async Task Replacing_a_groups_members_adds_and_removes_people()
    {
        var admin = await app.AdminAsync();
        var group = await GroupAsync(admin, Unique("Club"), "old@example.test");
        var (slug, _) = await GalleryForAsync(app, admin, group);
        var oldMember = app.AsUser("sub-old", "old@example.test");
        var newMember = app.AsUser("sub-new", "new@example.test");
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(oldMember, slug));

        var put = await admin.PutAsJsonAsync($"/api/admin/groups/{group}", new { emails = new[] { "new@example.test" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await OpenAsync(oldMember, slug));
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(newMember, slug));

        var read = await (await admin.GetAsync($"/api/admin/groups/{group}")).ReadAsync();
        Assert.Equal(new[] { "new@example.test" }, read.GetProperty("emails").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task Deleting_a_group_closes_its_galleries_but_keeps_people_in_their_other_groups()
    {
        var admin = await app.AdminAsync();
        var doomed = await GroupAsync(admin, Unique("Doomed"), "shared@example.test");
        var kept = await GroupAsync(admin, Unique("Kept"), "shared@example.test");
        var (doomedSlug, doomedId) = await GalleryForAsync(app, admin, doomed);
        var (keptSlug, _) = await GalleryForAsync(app, admin, kept);
        var person = app.AsUser("sub-shared", "shared@example.test");
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(person, doomedSlug));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/groups/{doomed}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await OpenAsync(person, doomedSlug));
        Assert.Equal(HttpStatusCode.OK, await OpenAsync(person, keptSlug));

        var invited = await (await admin.GetAsync($"/api/admin/galleries/{doomedId}/groups")).ReadAsync();
        Assert.Empty(invited.EnumerateArray());
        var groups = await (await admin.GetAsync("/api/admin/groups")).ReadAsync();
        Assert.DoesNotContain(groups.EnumerateArray(), g => g.GetProperty("id").GetInt32() == doomed);
    }

    [Fact]
    public async Task An_unverified_address_does_not_get_in_through_a_group()
    {
        var admin = await app.AdminAsync();
        var group = await GroupAsync(admin, Unique("Checked"), "checked@example.test");
        var (slug, _) = await GalleryForAsync(app, admin, group);
        Assert.Equal(HttpStatusCode.Forbidden, await OpenAsync(app.AsUser("sub-unverified-group", "checked@example.test", verified: false), slug));
    }

    [Fact]
    public async Task Group_names_are_unique_and_addresses_are_checked()
    {
        var admin = await app.AdminAsync();
        var name = Unique("Unique name");
        var id = await GroupAsync(admin, name);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/groups", new { name = name.ToUpperInvariant(), emails = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/groups", new { name = "  ", emails = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/groups", new { name = Unique("Bad"), emails = new[] { "nope" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/people/groups", new { email = "nope", groupIds = new[] { id } })).StatusCode);

        var renamed = Unique("Renamed");
        var put = await admin.PutAsJsonAsync($"/api/admin/groups/{id}", new { name = renamed });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(renamed, (await put.ReadAsync()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Groups_are_for_the_admin_only()
    {
        var admin = await app.AdminAsync();
        var id = await GroupAsync(admin, Unique("Private group"));
        var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/admin/people/groups", new { email = "x@example.test", groupIds = new[] { id } })).StatusCode);
        // a signed-in visitor is known but not an admin, so the answer is "forbidden" rather than "sign in"
        Assert.Equal(HttpStatusCode.Forbidden, (await app.AsUser("sub-visitor", "visitor@example.test").GetAsync("/api/admin/groups")).StatusCode);

        var noHeader = await app.AdminAsync();
        noHeader.DefaultRequestHeaders.Remove("X-Requested-With");
        Assert.Equal(HttpStatusCode.BadRequest, (await noHeader.DeleteAsync($"/api/admin/groups/{id}")).StatusCode);
    }
}
