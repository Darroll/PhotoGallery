using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoGallery.Tests;

/// <summary>Downloads are refused until the visitor accepts the current license wording.</summary>
public class LicenseGateTests(AppFactory app) : IClassFixture<AppFactory>
{
    static async Task<bool> AcceptedAsync(HttpClient c) =>
        (await (await c.GetAsync("/api/license")).ReadAsync()).GetProperty("accepted").GetBoolean();

    static async Task<JsonElement> AdminLicenseAsync(HttpClient admin) =>
        (await (await admin.GetAsync("/api/admin/settings/license")).ReadAsync()).GetProperty("license");

    static async Task PutLicenseAsync(HttpClient admin, bool enabled, string title, string text)
    {
        var r = await admin.PutAsJsonAsync("/api/admin/settings/license", new { enabled, requireAcceptance = true, title, holder = "", contact = "", text });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Anonymous_visitors_are_refused_single_downloads_and_zips_until_they_accept()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Gate Refuses", app.NewFolder(2));
        var guest = app.NewClient();
        var photo = (await AppFactory.PhotoIdsAsync(guest, slug))[0];

        var single = await guest.GetAsync($"/dl/{photo}?size=original");
        Assert.Equal(HttpStatusCode.Forbidden, single.StatusCode);
        Assert.True((await single.ReadAsync()).GetProperty("licenseRequired").GetBoolean());

        var zip = await guest.GetAsync($"/api/g/{slug}/zip?size=original");
        Assert.Equal(HttpStatusCode.Forbidden, zip.StatusCode);
        Assert.True((await zip.ReadAsync()).GetProperty("licenseRequired").GetBoolean());
        Assert.False(await AcceptedAsync(guest));
    }

    [Fact]
    public async Task Accepting_a_stale_wording_is_refused_and_the_header_is_required()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await app.NewClient().PostAsJsonAsync("/api/license/accept", new { version = "not-the-current-wording" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.CreateClient().PostAsJsonAsync("/api/license/accept", new { version = "x" })).StatusCode);
    }

    [Fact]
    public async Task Accepting_unlocks_downloads_for_that_browser_and_no_other()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Gate Unlocks", app.NewFolder(2));
        var reader = app.NewClient();
        var photo = (await AppFactory.PhotoIdsAsync(reader, slug))[0];

        Assert.False(await AcceptedAsync(reader));
        await reader.AcceptLicenseAsync();
        Assert.True(await AcceptedAsync(reader));
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/dl/{photo}?size=original")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/g/{slug}/zip?size=original")).StatusCode);

        var stranger = app.NewClient();
        Assert.False(await AcceptedAsync(stranger));
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/dl/{photo}?size=original")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_wording_asks_everyone_to_accept_again()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Gate Rewording", app.NewFolder(1));
        var reader = app.NewClient();
        var photo = (await AppFactory.PhotoIdsAsync(reader, slug))[0];
        await reader.AcceptLicenseAsync();

        var before = await AdminLicenseAsync(admin);
        try
        {
            await PutLicenseAsync(admin, enabled: true, title: "Revised", text: "A new clause that needs fresh consent.");
            Assert.False(await AcceptedAsync(reader));
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/dl/{photo}?size=original")).StatusCode);

            await reader.AcceptLicenseAsync();
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/dl/{photo}?size=original")).StatusCode);
        }
        finally
        {
            await PutLicenseAsync(admin, enabled: true, title: before.GetProperty("title").GetString()!, text: before.GetProperty("text").GetString()!);
        }
    }

    [Fact]
    public async Task Turning_the_license_off_removes_the_requirement()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Gate Off", app.NewFolder(1));
        var before = await AdminLicenseAsync(admin);
        try
        {
            await PutLicenseAsync(admin, enabled: false, title: before.GetProperty("title").GetString()!, text: before.GetProperty("text").GetString()!);
            var guest = app.NewClient();
            var photo = (await AppFactory.PhotoIdsAsync(guest, slug))[0];
            Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/dl/{photo}?size=original")).StatusCode);
        }
        finally
        {
            await PutLicenseAsync(admin, enabled: true, title: before.GetProperty("title").GetString()!, text: before.GetProperty("text").GetString()!);
        }
    }

    [Fact]
    public async Task Admins_download_without_being_asked()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Gate Admin", app.NewFolder(1));
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/dl/{photo}?size=original")).StatusCode);
        Assert.True(await AcceptedAsync(admin));
    }

    [Fact]
    public async Task The_admin_sees_who_accepted_and_whether_it_was_the_current_wording()
    {
        var admin = await app.AdminAsync();
        var before = (await (await admin.GetAsync("/api/admin/license/acceptances")).ReadAsync()).GetProperty("total").GetInt32();

        await app.NewClient().AcceptLicenseAsync();

        var log = await (await admin.GetAsync("/api/admin/license/acceptances")).ReadAsync();
        Assert.Equal(before + 1, log.GetProperty("total").GetInt32());
        var newest = log.GetProperty("recent")[0];
        Assert.True(newest.GetProperty("current").GetBoolean());
        Assert.False(newest.GetProperty("signedIn").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync("/api/admin/license/acceptances")).StatusCode);
    }
}

/// <summary>A signed-in account's acceptance follows the account, not the browser.</summary>
public class LicenseAccountTests(UserFactory app) : IClassFixture<UserFactory>
{
    [Fact]
    public async Task An_account_that_accepted_is_not_asked_again_on_another_device()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Account Gate", app.NewFolder(1));
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];

        var laptop = app.AsUser("license-reader");
        await laptop.AcceptLicenseAsync();

        var phone = app.AsUser("license-reader");      // a different browser, no visitor cookie
        Assert.True((await (await phone.GetAsync("/api/license")).ReadAsync()).GetProperty("accepted").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync($"/dl/{photo}?size=original")).StatusCode);

        var someone = app.AsUser("license-stranger");
        Assert.Equal(HttpStatusCode.Forbidden, (await someone.GetAsync($"/dl/{photo}?size=original")).StatusCode);
    }
}
