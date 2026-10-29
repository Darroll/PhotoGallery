using System.Net;
using System.Net.Http.Json;

namespace PhotoGallery.Tests;

public class RestrictedGalleryTests(UserFactory app) : IClassFixture<UserFactory>
{
    /// <summary>A restricted gallery with these email addresses on its list. Returns its slug, id and first photo.</summary>
    static async Task<(string slug, int id, int photo)> RestrictedAsync(UserFactory app, HttpClient admin, params string[] emails)
    {
        var title = "Members " + Guid.NewGuid().ToString("N")[..6];
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(1), visibility = 3 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var id = g.GetProperty("id").GetInt32();
        var slug = g.GetProperty("slug").GetString()!;
        var put = await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/people", new { emails });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        return (slug, id, photo);
    }

    [Fact]
    public async Task Anonymous_visitors_are_asked_to_sign_in_and_get_no_photos()
    {
        var admin = await app.AdminAsync();
        var (slug, _, photo) = await RestrictedAsync(app, admin, "member@example.test");
        var anon = app.NewClient();

        var r = await anon.GetAsync($"/api/g/{slug}");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.True((await r.ReadAsync()).GetProperty("needsSignIn").GetBoolean());
        Assert.NotEqual(HttpStatusCode.OK, (await anon.GetAsync($"/img/{photo}/thumb")).StatusCode);
    }

    [Fact]
    public async Task A_listed_verified_email_opens_the_gallery_and_matching_ignores_case()
    {
        var admin = await app.AdminAsync();
        var (slug, _, photo) = await RestrictedAsync(app, admin, "Friend@Example.test");
        var friend = app.AsUser("sub-friend", "FRIEND@example.test");

        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync($"/api/g/{slug}")).StatusCode);
        var image = await friend.GetAsync($"/img/{photo}/thumb");
        Assert.DoesNotContain(image.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task An_unverified_address_and_anyone_else_are_turned_away()
    {
        var admin = await app.AdminAsync();
        var (slug, _, _) = await RestrictedAsync(app, admin, "vetted@example.test");

        var unverified = app.AsUser("sub-unverified", "vetted@example.test", verified: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await unverified.GetAsync($"/api/g/{slug}")).StatusCode);

        var other = app.AsUser("sub-other", "someone@example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/g/{slug}")).StatusCode);
    }

    [Fact]
    public async Task Removing_someone_takes_effect_at_once_and_the_admin_always_gets_in()
    {
        var admin = await app.AdminAsync();
        var (slug, id, _) = await RestrictedAsync(app, admin, "leaver@example.test");
        var leaver = app.AsUser("sub-leaver", "leaver@example.test");
        Assert.Equal(HttpStatusCode.OK, (await leaver.GetAsync($"/api/g/{slug}")).StatusCode);

        var put = await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/people", new { emails = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await leaver.GetAsync($"/api/g/{slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/g/{slug}")).StatusCode);
    }

    [Fact]
    public async Task Restricted_galleries_stay_off_the_home_page_and_out_of_search()
    {
        var admin = await app.AdminAsync();
        var (slug, _, _) = await RestrictedAsync(app, admin, "hidden@example.test");
        var member = app.AsUser("sub-hidden", "hidden@example.test");

        var home = await (await member.GetAsync("/api/galleries")).ReadAsync();
        Assert.DoesNotContain(home.EnumerateArray(), g => g.GetProperty("slug").GetString() == slug);
        var search = await (await member.GetAsync("/api/search?q=members")).ReadAsync();
        Assert.DoesNotContain(search.GetProperty("galleries").EnumerateArray(), g => g.GetProperty("slug").GetString() == slug);
    }

    [Fact]
    public async Task The_list_only_takes_real_email_addresses()
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Checked list " + Guid.NewGuid().ToString("N")[..6], folder = app.NewFolder(1), visibility = 3 });
        var id = (await r.ReadAsync()).GetProperty("gallery").GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/people", new { emails = new[] { "not-an-address" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/people", new { emails = new[] { "ok@example.test", "bad" } })).StatusCode);
    }
}
