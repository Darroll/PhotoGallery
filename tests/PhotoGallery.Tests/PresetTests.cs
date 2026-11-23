using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery.Tests;

// presets are site-wide, so this class has its own server and runs its tests one at a time
public class PresetTests(AppFactory app) : IClassFixture<AppFactory>
{
    static readonly SemaphoreSlim Serial = new(1, 1);

    async Task<int> GalleryAsync(HttpClient admin, string title, object? extra = null, string? presetId = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = title, ["folder"] = app.NewFolder(2) };
        if (presetId != null) body["presetId"] = presetId;
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.ReadAsync()).GetProperty("gallery").GetProperty("id").GetInt32();
    }

    Task<Gallery> LoadAsync(int id) => app.WithDbAsync(db => db.Galleries.AsNoTracking().FirstAsync(g => g.Id == id));

    async Task<(int gid, int teamId, int groupId)> SetUpSourceAsync(HttpClient admin, string title)
    {
        var gid = await GalleryAsync(admin, title, presetId: "");
        var team = await (await admin.PostAsJsonAsync("/api/admin/teams", new { name = title + " FC", colors = "Home: white shirts, blue numbers\nAway: blue shirts", roster = "10 Emma" })).ReadAsync();
        var teamId = team.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId, kit = "Home" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/autotag", new { autoTag = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/follow", new { allow = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/playertags", new { allow = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}", new { visibility = 3, allowRaw = true, allowZip = false, hideRawOnly = false, allowComments = true, location = "Field 3", password = "kickoff" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/tags", new { names = new[] { "U12", "League" } })).StatusCode);
        var group = await (await admin.PostAsJsonAsync("/api/admin/groups", new { name = title + " parents", emails = new[] { "p@example.test" } })).ReadAsync();
        var groupId = group.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/groups", new { ids = new[] { groupId } })).StatusCode);
        return (gid, teamId, groupId);
    }

    [Fact]
    public async Task A_new_gallery_starts_with_the_settings_of_the_preset()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            var (source, teamId, groupId) = await SetUpSourceAsync(admin, "Preset Source");

            var r = await admin.PostAsJsonAsync("/api/admin/presets", new { galleryId = source, name = "Saturday game" });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var list = await r.ReadAsync();
            var p = list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Saturday game");
            var id = p.GetProperty("id").GetString()!;
            Assert.Equal("Preset Source FC", p.GetProperty("team").GetString());
            Assert.True(p.GetProperty("hasPassword").GetBoolean());
            Assert.False(p.TryGetProperty("passwordHash", out _));   // the hash never leaves the server

            var gid = await GalleryAsync(admin, "Game vs Rovers", presetId: id);
            var g = await LoadAsync(gid);
            var src = await LoadAsync(source);
            Assert.Equal(Visibility.Restricted, g.Visibility);
            Assert.Equal(src.PasswordHash, g.PasswordHash);
            Assert.True(g.AllowRaw);
            Assert.False(g.AllowZip);
            Assert.False(g.HideRawOnly);
            Assert.True(g.AllowComments);
            Assert.Equal("Field 3", g.Location);
            Assert.Equal(teamId, g.TeamId);
            Assert.Equal("Home", g.TeamKit);
            Assert.True(g.AutoTag);
            Assert.False(g.FollowPlayers);
            Assert.True(g.AllowPlayerTags);
            // per-event things are not copied
            Assert.Equal("Game vs Rovers", g.Title);
            Assert.NotEqual(src.Folder, g.Folder);
            Assert.Equal(new[] { "League", "U12" }, (await (await admin.GetAsync($"/api/admin/galleries/{gid}/tags")).ReadAsync()).EnumerateArray().Select(x => x.GetString()).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { groupId }, (await (await admin.GetAsync($"/api/admin/galleries/{gid}/groups")).ReadAsync()).EnumerateArray().Select(x => x.GetInt32()).ToArray());

            // what the request sends wins over the preset
            var r2 = await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Open game", folder = app.NewFolder(1), visibility = 0, presetId = id });
            var open = await LoadAsync((await r2.ReadAsync()).GetProperty("gallery").GetProperty("id").GetInt32());
            Assert.Equal(Visibility.Public, open.Visibility);
            Assert.Equal(teamId, open.TeamId);

            // no preset: the standard settings
            var plain = await LoadAsync(await GalleryAsync(admin, "Plain gallery", presetId: ""));
            Assert.Null(plain.TeamId);
            Assert.Equal(Visibility.Protected, plain.Visibility);
            Assert.True(plain.HideRawOnly);

            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/galleries", new { title = "Gone", folder = app.NewFolder(1), presetId = "nosuchpreset" })).StatusCode);
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task The_default_preset_applies_when_none_is_picked_and_presets_can_be_renamed_replaced_and_deleted()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            var (source, teamId, _) = await SetUpSourceAsync(admin, "Default Source");
            var made = await (await admin.PostAsJsonAsync("/api/admin/presets", new { galleryId = source, name = "League default", makeDefault = true })).ReadAsync();
            var id = made.GetProperty("defaultId").GetString()!;

            // a request without a preset (the setup guide, the API) gets the default one
            var g = await LoadAsync(await GalleryAsync(admin, "Default game"));
            Assert.Equal(teamId, g.TeamId);

            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/presets", new { galleryId = source, name = "league DEFAULT" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/presets", new { galleryId = source, name = "  " })).StatusCode);

            // replacing keeps the id and the default, and takes the gallery's new settings
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{source}", new { allowRaw = false })).StatusCode);
            var replaced = await (await admin.PostAsJsonAsync("/api/admin/presets", new { galleryId = source, replaceId = id })).ReadAsync();
            var item = replaced.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
            Assert.Equal("League default", item.GetProperty("name").GetString());
            Assert.False(item.GetProperty("allowRaw").GetBoolean());
            Assert.True(item.GetProperty("isDefault").GetBoolean());

            var renamed = await (await admin.PutAsJsonAsync($"/api/admin/presets/{id}", new { name = "Weekend league", isDefault = false })).ReadAsync();
            Assert.Equal(JsonValueKind.Null, renamed.GetProperty("defaultId").ValueKind);
            Assert.Equal("Weekend league", renamed.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id).GetProperty("name").GetString());
            Assert.Null((await LoadAsync(await GalleryAsync(admin, "After default off"))).TeamId);

            // deleting a team leaves presets that used it working, without the team
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/presets/{id}", new { isDefault = true })).StatusCode);
            Assert.True((await admin.DeleteAsync($"/api/admin/teams/{teamId}")).IsSuccessStatusCode);
            var noTeam = await LoadAsync(await GalleryAsync(admin, "Team gone"));
            Assert.Null(noTeam.TeamId);
            Assert.Equal("", noTeam.TeamKit);
            Assert.Equal(Visibility.Restricted, noTeam.Visibility);

            var after = await (await admin.DeleteAsync($"/api/admin/presets/{id}")).ReadAsync();
            Assert.DoesNotContain(after.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetString() == id);
            Assert.Equal(JsonValueKind.Null, after.GetProperty("defaultId").ValueKind);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/presets/{id}")).StatusCode);
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public void Presets_are_cleaned_when_read_from_the_settings_file()
    {
        var s = new PresetSettings
        {
            DefaultId = "missing",
            Items = [new GalleryPreset { Id = "a/b", Name = "  Game\u0007  ", Tags = ["U12", "u12", ""], TeamKit = "Home" }, new GalleryPreset { Name = "" }]
        }.Sanitized();
        var p = Assert.Single(s.Items);
        Assert.Equal("ab", p.Id);
        Assert.Equal("Game", p.Name);
        Assert.Equal(new[] { "U12" }, p.Tags);
        Assert.Equal("", p.TeamKit);   // a jersey means nothing without a team
        Assert.Null(s.DefaultId);
    }
}
