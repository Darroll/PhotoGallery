using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoGallery.Tests;

public class TeamLookRuleTests
{
    [Theory]
    [InlineData("#1E40AF", "#1e40af")]
    [InlineData("1e40af", "#1e40af")]
    [InlineData(" #14a ", "#1144aa")]
    [InlineData("", "")]
    [InlineData("navy", "")]
    [InlineData("#12345", "")]
    [InlineData("url(x)", "")]
    public void Colours_are_kept_as_six_digit_hex_or_nothing(string typed, string expected) => Assert.Equal(expected, TeamLook.CleanColor(typed));

    [Fact]
    public void Only_logo_names_the_site_made_are_served()
    {
        Assert.True(TeamLook.IsLogoFile(Guid.NewGuid().ToString("N") + ".png"));
        Assert.False(TeamLook.IsLogoFile("../photogallery.db"));
        Assert.False(TeamLook.IsLogoFile("logo.svg"));
        Assert.False(TeamLook.IsLogoFile(null));
    }
}

// "use on every page" is site-wide, so this class has its own server and runs its tests one at a time
public class TeamLookTests(AppFactory app) : IClassFixture<AppFactory>
{
    static readonly SemaphoreSlim Serial = new(1, 1);

    static async Task<JsonElement> TeamAsync(HttpClient admin, string name, string season = "")
    {
        var r = await admin.PostAsJsonAsync("/api/admin/teams", new { name, season, colors = "red shirts", roster = "7" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.ReadAsync();
    }

    static MultipartFormDataContent Picture(int w = 900, int h = 600)
    {
        using var img = (NetVips.Image.Black(w, h, bands: 3) + new double[] { 20, 60, 200 }).Cast(NetVips.Enums.BandFormat.Uchar);
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(img.JpegsaveBuffer()), "file", "logo.jpg");
        return content;
    }

    static async Task<List<JsonElement>> TeamsAsync(HttpClient admin) => (await (await admin.GetAsync("/api/admin/teams")).ReadAsync()).EnumerateArray().ToList();

    [Fact]
    public async Task A_logo_is_shrunk_to_a_png_shared_by_every_season_and_served_to_anyone()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            var fall = await TeamAsync(admin, "Comets", "Fall 2026");
            var spring = await TeamAsync(admin, "Comets", "Spring 2027");
            var r = await admin.PostAsync($"/api/admin/teams/{fall.GetProperty("id").GetInt32()}/logo", Picture());
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var url = (await r.ReadAsync()).GetProperty("logo").GetString()!;

            var teams = await TeamsAsync(admin);
            Assert.All(teams.Where(t => t.GetProperty("name").GetString() == "Comets"), t => Assert.Equal(url, t.GetProperty("logo").GetString()));

            var visitor = app.NewClient();
            var img = await visitor.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, img.StatusCode);
            Assert.Equal("image/png", img.Content.Headers.ContentType!.MediaType);
            using (var png = NetVips.Image.NewFromBuffer(await img.Content.ReadAsByteArrayAsync()))
                Assert.Equal(TeamLook.LogoSize, Math.Max(png.Width, png.Height));

            // a new season copied from the team keeps the logo
            var copy = await admin.PostAsJsonAsync($"/api/admin/teams/{spring.GetProperty("id").GetInt32()}/copy", new { season = "Fall 2027" });
            Assert.Equal(url, (await copy.ReadAsync()).GetProperty("logo").GetString());

            // a new logo replaces the old file; removing it takes it off every season
            var r2 = await admin.PostAsync($"/api/admin/teams/{spring.GetProperty("id").GetInt32()}/logo", Picture(300, 300));
            var url2 = (await r2.ReadAsync()).GetProperty("logo").GetString()!;
            Assert.NotEqual(url, url2);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/teams/{fall.GetProperty("id").GetInt32()}/logo")).StatusCode);
            Assert.All((await TeamsAsync(admin)).Where(t => t.GetProperty("name").GetString() == "Comets"), t => Assert.Equal(JsonValueKind.Null, t.GetProperty("logo").ValueKind));
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(url2)).StatusCode);
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_is_refused()
    {
        var admin = await app.AdminAsync();
        var t = await TeamAsync(admin, "Rockets");
        var content = new MultipartFormDataContent { { new ByteArrayContent("<svg onload=alert(1)>"u8.ToArray()), "file", "x.svg" } };
        var r = await admin.PostAsync($"/api/admin/teams/{t.GetProperty("id").GetInt32()}/logo", content);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        // only the admin can change a logo
        Assert.NotEqual(HttpStatusCode.OK, (await app.NewClient().PostAsync($"/api/admin/teams/{t.GetProperty("id").GetInt32()}/logo", Picture())).StatusCode);
    }

    [Fact]
    public async Task Colours_go_on_every_season_and_one_team_can_dress_the_whole_site()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            var a = await TeamAsync(admin, "Hornets", "Fall 2026");
            await TeamAsync(admin, "Hornets", "Spring 2027");
            var b = await TeamAsync(admin, "Owls");

            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/teams/{a.GetProperty("id").GetInt32()}/look", new { mainColor = "navy" })).StatusCode);
            var r = await admin.PutAsJsonAsync($"/api/admin/teams/{a.GetProperty("id").GetInt32()}/look", new { mainColor = "#1E40AF", secondColor = "facc15", siteLook = true });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var hornets = (await TeamsAsync(admin)).Where(t => t.GetProperty("name").GetString() == "Hornets").ToList();
            Assert.Equal(2, hornets.Count);
            Assert.All(hornets, t => { Assert.Equal("#1e40af", t.GetProperty("mainColor").GetString()); Assert.Equal("#facc15", t.GetProperty("secondColor").GetString()); Assert.True(t.GetProperty("siteLook").GetBoolean()); });

            var site = await (await app.NewClient().GetAsync("/api/site")).ReadAsync();
            Assert.Equal("Hornets", site.GetProperty("siteTeam").GetProperty("name").GetString());
            Assert.Equal("#1e40af", site.GetProperty("siteTeam").GetProperty("main").GetString());

            // a new season added by hand under the same name takes on the team's colours
            var winter = await TeamAsync(admin, "Hornets", "Winter 2027");
            Assert.Equal("#1e40af", winter.GetProperty("mainColor").GetString());

            // another team taking over the whole site turns it off for the first
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/teams/{b.GetProperty("id").GetInt32()}/look", new { mainColor = "#0a0", siteLook = true })).StatusCode);
            site = await (await app.NewClient().GetAsync("/api/site")).ReadAsync();
            Assert.Equal("Owls", site.GetProperty("siteTeam").GetProperty("name").GetString());
            Assert.All((await TeamsAsync(admin)).Where(t => t.GetProperty("name").GetString() == "Hornets"), t => Assert.False(t.GetProperty("siteLook").GetBoolean()));

            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/teams/{b.GetProperty("id").GetInt32()}/look", new { mainColor = "#0a0", siteLook = false })).StatusCode);
            site = await (await app.NewClient().GetAsync("/api/site")).ReadAsync();
            Assert.Equal(JsonValueKind.Null, site.GetProperty("siteTeam").ValueKind);
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task A_gallery_and_the_home_page_carry_their_team_look()
    {
        var admin = await app.AdminAsync();
        var t = await TeamAsync(admin, "Falcons");
        var id = t.GetProperty("id").GetInt32();
        await admin.PutAsJsonAsync($"/api/admin/teams/{id}/look", new { mainColor = "#7c3aed" });
        var slug = await AppFactory.GalleryAsync(admin, "Falcons game", app.NewFolder(1));
        var gid = (await (await admin.GetAsync("/api/galleries")).ReadAsync()).EnumerateArray().First(g => g.GetProperty("slug").GetString() == slug).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId = id, kit = "" })).StatusCode);

        var visitor = app.NewClient();
        var g = await (await visitor.GetAsync("/api/g/" + slug)).ReadAsync();
        Assert.Equal("Falcons", g.GetProperty("team").GetProperty("name").GetString());
        Assert.Equal("#7c3aed", g.GetProperty("team").GetProperty("main").GetString());
        var home = (await (await visitor.GetAsync("/api/galleries")).ReadAsync()).EnumerateArray().First(x => x.GetProperty("slug").GetString() == slug);
        Assert.Equal("#7c3aed", home.GetProperty("team").GetProperty("main").GetString());
    }
}
