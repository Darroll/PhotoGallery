using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhotoGallery.Tests;

public class SeasonRuleTests
{
    static Gallery G(string? eventDate, string created = "2026-01-01", string season = "") => new()
    {
        Title = eventDate ?? created, EventDate = eventDate == null ? null : DateTime.Parse(eventDate), CreatedAt = DateTime.Parse(created), Season = season
    };

    [Theory]
    [InlineData("2026-09-12", "fall-spring", "Fall 2026")]
    [InlineData("2026-08-01", "fall-spring", "Fall 2026")]
    [InlineData("2026-07-31", "fall-spring", "Spring 2026")]
    [InlineData("2027-03-04", "fall-spring", "Spring 2027")]
    [InlineData("2026-09-12", "school-year", "2026-27 season")]
    [InlineData("2027-03-04", "school-year", "2026-27 season")]
    [InlineData("2000-01-04", "school-year", "1999-00 season")]
    [InlineData("2026-09-12", "year", "2026")]
    [InlineData("2026-09-12", "off", "")]
    public void The_automatic_season_comes_from_the_event_date(string date, string mode, string expected) =>
        Assert.Equal(expected, GalleryRules.AutoSeason(G(date), mode));

    [Fact]
    public void Without_an_event_date_the_day_it_was_made_counts_and_typed_seasons_win()
    {
        Assert.Equal("Fall 2025", GalleryRules.SeasonOf(G(null, "2025-10-02"), "fall-spring"));
        Assert.Equal("Indoor 2026", GalleryRules.SeasonOf(G("2026-09-12", season: "  Indoor 2026 "), "fall-spring"));
        Assert.Equal("", GalleryRules.SeasonOf(G("2026-09-12", season: "Indoor 2026"), "off"));
        Assert.Equal(GalleryRules.MaxSeason, GalleryRules.CleanSeason(new string('x', 100)).Length);
    }

    [Fact]
    public void Seasons_are_listed_newest_first_keeping_the_order_inside_each()
    {
        var a = G("2026-09-05"); var b = G("2026-03-01"); var c = G("2026-10-20"); var d = G("2026-04-11", season: "fall 2026");
        // name order: a, b, c, d; d is typed into the fall season (other letter case) although its date is in spring
        var groups = GalleryRules.GroupBySeason([a, b, c, d], "fall-spring");
        Assert.Equal(["Fall 2026", "Spring 2026"], groups.Select(x => x.Season));
        Assert.Equal([a, c, d], groups[0].Galleries);
        Assert.Equal([b], groups[1].Galleries);
        var off = GalleryRules.GroupBySeason([a, b, c], "off");
        Assert.Single(off);
        Assert.Equal("", off[0].Season);
    }
}

// the home page settings are site-wide, so this class has its own server and runs its tests one at a time
public class SeasonTests(AppFactory app) : IClassFixture<AppFactory>
{
    static readonly SemaphoreSlim Serial = new(1, 1);

    async Task<int> PublicGalleryAsync(HttpClient admin, string title, string eventDate, int? teamId = null, string? season = null)
    {
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(1), presetId = "" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await r.ReadAsync()).GetProperty("gallery").GetProperty("id").GetInt32();
        var body = new Dictionary<string, object?> { ["visibility"] = 0, ["eventDate"] = eventDate };
        if (season != null) body["season"] = season;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", body)).StatusCode);
        if (teamId != null) Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{id}/team", new { teamId, kit = "" })).StatusCode);
        return id;
    }

    async Task<int> TeamAsync(HttpClient admin, string name) =>
        (await (await admin.PostAsJsonAsync("/api/admin/teams", new { name, colors = "red shirts", roster = "7" })).ReadAsync()).GetProperty("id").GetInt32();

    static async Task<List<JsonElement>> HomeAsync(HttpClient c) => (await (await c.GetAsync("/api/galleries")).ReadAsync()).EnumerateArray().ToList();

    [Fact]
    public async Task The_home_page_lists_galleries_by_season_with_their_team()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/settings/home", new { order = "name-asc", seasons = "fall-spring" })).StatusCode);
            var hurricanes = await TeamAsync(admin, "Season Hurricanes");
            var lightning = await TeamAsync(admin, "Season Lightning");
            await PublicGalleryAsync(admin, "S-A spring game", "2026-04-11", hurricanes);
            await PublicGalleryAsync(admin, "S-B fall game", "2026-09-12", lightning);
            await PublicGalleryAsync(admin, "S-C fall game", "2026-10-03", hurricanes);
            await PublicGalleryAsync(admin, "S-D banquet", "2026-06-01", season: "Fall 2026");

            var visitor = app.NewClient();
            var home = (await HomeAsync(visitor)).Where(x => x.GetProperty("title").GetString()!.StartsWith("S-")).ToList();
            Assert.Equal(["S-B fall game", "S-C fall game", "S-D banquet", "S-A spring game"], home.Select(x => x.GetProperty("title").GetString()));
            Assert.Equal(["Fall 2026", "Fall 2026", "Fall 2026", "Spring 2026"], home.Select(x => x.GetProperty("season").GetString()));
            Assert.Equal("Season Lightning", home[0].GetProperty("team").GetProperty("name").GetString());
            Assert.Equal(hurricanes, home[1].GetProperty("team").GetProperty("id").GetInt32());
            Assert.Equal(JsonValueKind.Null, home[2].GetProperty("team").ValueKind);

            // the public team list only names teams that have a public gallery
            var teams = (await (await visitor.GetAsync("/api/teams")).ReadAsync()).EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
            Assert.Contains("Season Hurricanes", teams);
            Assert.Contains("Season Lightning", teams);
            await TeamAsync(admin, "Season Unused");
            Assert.DoesNotContain("Season Unused", (await (await visitor.GetAsync("/api/teams")).ReadAsync()).EnumerateArray().Select(x => x.GetProperty("name").GetString()));

            // search narrows to one team's galleries
            var found = await (await visitor.GetAsync($"/api/search?q=S-&scope=galleries&team={hurricanes}")).ReadAsync();
            Assert.Equal(["S-A spring game", "S-C fall game"], found.GetProperty("galleries").EnumerateArray().Select(x => x.GetProperty("title").GetString()).OrderBy(x => x));

            // seasons off: one list in the chosen order, no season names
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/settings/home", new { order = "name-asc", seasons = "off" })).StatusCode);
            home = (await HomeAsync(visitor)).Where(x => x.GetProperty("title").GetString()!.StartsWith("S-")).ToList();
            Assert.Equal(["S-A spring game", "S-B fall game", "S-C fall game", "S-D banquet"], home.Select(x => x.GetProperty("title").GetString()));
            Assert.All(home, x => Assert.Equal("", x.GetProperty("season").GetString()));

            // an unknown mode falls back to fall and spring
            var saved = await (await admin.PutAsJsonAsync("/api/admin/settings/home", new { order = "name-asc", seasons = "winter" })).ReadAsync();
            Assert.Equal("fall-spring", saved.GetProperty("seasons").GetString());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task The_admin_sees_the_season_and_a_preset_carries_a_typed_one()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/settings/home", new { order = "created-desc", seasons = "school-year" })).StatusCode);
            var id = await PublicGalleryAsync(admin, "P-source", "2027-02-06", season: "Indoor 2027");
            var list = (await (await admin.GetAsync("/api/admin/galleries")).ReadAsync()).EnumerateArray().ToList();
            var g = list.Single(x => x.GetProperty("id").GetInt32() == id);
            Assert.Equal("Indoor 2027", g.GetProperty("season").GetString());
            Assert.Equal("2026-27 season", g.GetProperty("autoSeason").GetString());
            Assert.Equal("Indoor 2027", g.GetProperty("seasonShown").GetString());

            var presets = await (await admin.PostAsJsonAsync("/api/admin/presets", new { galleryId = id, name = "Indoor" })).ReadAsync();
            var p = presets.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Indoor");
            Assert.Equal("Indoor 2027", p.GetProperty("season").GetString());
            var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "P-new", folder = app.NewFolder(1), presetId = p.GetProperty("id").GetString() });
            Assert.Equal("Indoor 2027", (await r.ReadAsync()).GetProperty("gallery").GetProperty("season").GetString());

            // clearing the box goes back to the automatic season
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { season = "" })).StatusCode);
            g = (await (await admin.GetAsync("/api/admin/galleries")).ReadAsync()).EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id);
            Assert.Equal("2026-27 season", g.GetProperty("seasonShown").GetString());
        }
        finally { Serial.Release(); }
    }
}

public class TeamCopyTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task A_team_is_copied_for_a_new_season_with_its_jerseys_and_roster()
    {
        var admin = await app.AdminAsync();
        var src = await (await admin.PostAsJsonAsync("/api/admin/teams", new { name = "Copy FC Fall 2026", colors = "Home: white shirts with blue numbers\nAway: blue shirts", tagWithNames = true, roster = "10 Emma\n7 Ava\n3" })).ReadAsync();
        var srcId = src.GetProperty("id").GetInt32();
        var gid = (await (await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Copy FC game", folder = app.NewFolder(1), presetId = "" })).ReadAsync()).GetProperty("gallery").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId = srcId, kit = "Home" })).StatusCode);

        var r = await admin.PostAsJsonAsync($"/api/admin/teams/{srcId}/copy", new { name = "Copy FC Spring 2027" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var copy = await r.ReadAsync();
        Assert.NotEqual(srcId, copy.GetProperty("id").GetInt32());
        Assert.Equal("Copy FC Spring 2027", copy.GetProperty("name").GetString());
        Assert.Equal(src.GetProperty("colors").GetString(), copy.GetProperty("colors").GetString());
        Assert.Equal(src.GetProperty("roster").GetString(), copy.GetProperty("roster").GetString());
        Assert.True(copy.GetProperty("tagWithNames").GetBoolean());
        Assert.Equal(0, copy.GetProperty("galleries").GetInt32());
        // the gallery stays with the old team
        Assert.Equal(srcId, (await app.WithDbAsync(db => db.Galleries.FindAsync(gid).AsTask()))!.TeamId);

        // the name must be new
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/teams/{srcId}/copy", new { name = "copy fc fall 2026" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/admin/teams/999999/copy", new { name = "Nobody" })).StatusCode);
    }
}
