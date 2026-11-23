using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoGallery.Tests;

public class DescriptionUnitTests
{
    [Fact]
    public void The_prompt_names_only_the_tagged_players_and_keeps_opponents_general()
    {
        var p = PhotoDescriptions.Prompt(true, "white shirts with blue numbers", new[] { "#10", "Ava" }, "Hurricanes", "Rovers");
        Assert.Contains("#10, Ava", p);
        Assert.Contains("Our team is Hurricanes", p);
        Assert.Contains("The other team is Rovers", p);
        Assert.Contains("never by its jersey color", p);
        Assert.Contains("white shirts with blue numbers", p);
        Assert.Contains("a teammate", p);
        Assert.Contains("never by number or name", p);
        Assert.Contains("\"description\"", p);
        var none = PhotoDescriptions.Prompt(true, "", Array.Empty<string>());
        Assert.DoesNotContain("These players", none);
        var plain = PhotoDescriptions.Prompt(false, "", Array.Empty<string>());
        Assert.DoesNotContain("our team", plain, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Numbers_that_are_not_our_tagged_players_are_taken_out()
    {
        Assert.Equal("#10 shoots past an opponent in the rain.", PhotoDescriptions.Scrub("#10 shoots past an opponent #12 in the rain.", new[] { "10" }));
        Assert.Equal("Emma (#10) heads the ball as a defender closes in.", PhotoDescriptions.Scrub("Emma (#10) heads the ball as a defender number 4 closes in.", new[] { "10" }));
        Assert.Equal("A goalkeeper dives.", PhotoDescriptions.Scrub("A goalkeeper No. 1 dives.", Array.Empty<string>()));
        Assert.Equal("Reagan passes to a teammate.", PhotoDescriptions.Scrub("Reagan passes to a teammate Player 11.", Array.Empty<string>()));
    }

    [Fact]
    public void The_opponent_comes_from_the_title()
    {
        Assert.Equal("Rovers", PhotoDescriptions.Opponent("Hurricanes vs Rovers", "Hurricanes"));
        Assert.Equal("Rovers", PhotoDescriptions.Opponent("Rovers v. Hurricanes", "Hurricanes"));
        Assert.Equal("East Ridge", PhotoDescriptions.Opponent("vs East Ridge (home)", "OCSS"));
        Assert.Equal("Rovers", PhotoDescriptions.Opponent("Game 2 against Rovers - 11/23", "OCSS"));
        Assert.Equal("", PhotoDescriptions.Opponent("Fall tournament", "OCSS"));
        Assert.Equal("Rovers", PhotoDescriptions.Opponent("ocss vs Rovers", "OCSS"));
    }

    [Fact]
    public void The_reply_is_read_from_JSON_or_plain_words()
    {
        Assert.Equal("A header in the rain.", PhotoDescriptions.Parse("Sure: {\"description\": \"A header\\nin the rain.\"}"));
        Assert.Equal("A header in the rain.", PhotoDescriptions.Parse("A header in the rain."));
        Assert.Throws<VisionException>(() => PhotoDescriptions.Parse("{\"caption\": \"x\"}"));
        Assert.True(PhotoDescriptions.Clean(string.Join(" ", Enumerable.Repeat("word", 200))).Length <= PhotoDescriptions.MaxLength);
    }

    [Fact]
    public void Search_words_match_in_any_order_at_the_start_of_a_word()
    {
        var d = new[] { "Emma (#10) wins a header as rain pours down." };
        Assert.True(TextSearch.Matches(TextSearch.Words("header in the rain"), d));
        Assert.True(TextSearch.Matches(TextSearch.Words("headers rain"), d));
        Assert.True(TextSearch.Matches(TextSearch.Words("#10 header"), d));
        Assert.True(TextSearch.Matches(TextSearch.Words("rainy"[..4]), d));
        Assert.False(TextSearch.Matches(TextSearch.Words("train"), new[] { "rain" }));
        Assert.False(TextSearch.Matches(TextSearch.Words("rain"), new[] { "training session" }));
        Assert.False(TextSearch.Matches(TextSearch.Words("header save"), d));
        Assert.Empty(TextSearch.Words("the in a"));
    }

    [Fact]
    public void A_label_is_the_number_unless_the_team_uses_names()
    {
        var p = new Player { Number = "10", Name = "Emma" };
        Assert.Equal("Emma", PhotoDescriptions.Label(new Team { Name = "FC" }, p));   // known players are named, whatever the tag setting
        Assert.Equal("Emma", PhotoDescriptions.Label(new Team { Name = "FC", TagWithNames = true }, p));
        Assert.Equal("#3", PhotoDescriptions.Label(new Team { Name = "FC", TagWithNames = true }, new Player { Number = "3" }));
    }
}

public class DescriptionTests(VisionAppFactory app) : IClassFixture<VisionAppFactory>
{
    static readonly SemaphoreSlim Serial = new(1, 1);

    async Task<HttpClient> SetupAsync()
    {
        app.Services.GetRequiredService<DescriptionService>().Paused = true;
        var admin = await app.AdminAsync();
        var r = await admin.PutAsJsonAsync("/api/admin/settings/vision", new
        {
            providers = new[] { new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b-instruct" } },
            autoApproveAt = 0, autoCheckNew = false, describePhotos = true,
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return admin;
    }

    async Task<(int gid, string slug, int teamId, int[] photos)> GalleryAsync(HttpClient admin, string title, int photos, bool names = false)
    {
        var slug = await AppFactory.GalleryAsync(admin, title, app.NewFolder(photos));
        var gid = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("id").GetInt32();
        var team = await (await admin.PostAsJsonAsync("/api/admin/teams", new { name = title + " FC", colors = "white shirts with blue numbers", roster = "10 Emma\n7 Ava\n3", tagWithNames = names })).ReadAsync();
        var teamId = team.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId })).StatusCode);
        return (gid, slug, teamId, await AppFactory.PhotoIdsAsync(admin, slug));
    }

    async Task<(int Written, int Failed)> RoundAsync()
    {
        using var scope = app.Services.CreateScope();
        return await PhotoDescriptions.RunRoundAsync(scope.ServiceProvider, 50, CancellationToken.None);
    }

    static async Task<JsonElement> PhotoAsync(HttpClient c, string slug, int id) =>
        (await (await c.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray().First(p => p.GetProperty("id").GetInt32() == id);

    /// <summary>The model writes back the players it was told about, so a test can see what it was told.</summary>
    static string Echo(string prompt)
    {
        var m = System.Text.RegularExpressions.Regex.Match(prompt, @"These players from our team are in the photo: (.+?)\. Refer");
        var who = m.Success ? m.Groups[1].Value : "A teammate";
        return JsonSerializer.Serialize(new { description = $"{who} wins a header in the rain while an opponent #12 watches." });
    }

    [Fact]
    public async Task A_description_names_the_tagged_players_and_is_rewritten_when_the_photo_is_retagged()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await SetupAsync();
            app.Reader.TextAnswers["ollama"] = Echo;
            var (_, slug, _, photos) = await GalleryAsync(admin, "Describe One", 1);
            var id = photos[0];
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/photos/{id}/players", new { add = new[] { "10" } })).StatusCode);

            await RoundAsync();
            var p = await PhotoAsync(admin, slug, id);
            Assert.Equal("Emma wins a header in the rain while an opponent watches.", p.GetProperty("description").GetString());   // the opponent's number is gone
            Assert.Equal("ai", p.GetProperty("descriptionSource").GetString());
            Assert.Contains(app.Reader.TextPrompts, x => x.Contains("in the photo: Emma.") && x.Contains("3 = #3, 7 = Ava, 10 = Emma"));

            // nothing changed: not asked again
            DateTime? At() => app.WithDbAsync(db => Task.FromResult(db.Photos.First(x => x.Id == id).DescribedAt)).Result;
            var at = At();
            await RoundAsync();
            Assert.Equal(at, At());

            // a player identified: written again with both
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/photos/{id}/players", new { add = new[] { "7" } })).StatusCode);
            await RoundAsync();
            Assert.StartsWith("Ava, Emma wins", (await PhotoAsync(admin, slug, id)).GetProperty("description").GetString());

            // a number corrected (10 was really 3): written again without #10
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/photos/{id}/players", new { remove = new[] { "10" }, add = new[] { "3" } })).StatusCode);
            await RoundAsync();
            var fixedText = (await PhotoAsync(admin, slug, id)).GetProperty("description").GetString()!;
            Assert.StartsWith("#3, Ava wins", fixedText);
            Assert.DoesNotContain("Emma", fixedText);

            // the settings page lists it, marked as a rewrite after a retag
            var activity = await (await admin.GetAsync("/api/admin/settings/vision/descriptions/activity")).ReadAsync();
            Assert.True(activity.GetProperty("on").GetBoolean());
            Assert.Contains(activity.GetProperty("recent").EnumerateArray(), e => e.GetProperty("photoId").GetInt32() == id && e.GetProperty("retag").GetBoolean() && e.GetProperty("text").GetString() == fixedText && e.GetProperty("slug").GetString() == slug);

            // parents find it by searching in their own words
            var found = await (await app.NewClient().GetAsync("/api/search?q=" + Uri.EscapeDataString("header in the rain"))).ReadAsync();
            Assert.Contains(found.GetProperty("photos").EnumerateArray(), x => x.GetProperty("id").GetInt32() == id && x.GetProperty("description").GetString() == fixedText);
            var missed = await (await app.NewClient().GetAsync("/api/search?q=" + Uri.EscapeDataString("save in the snow"))).ReadAsync();
            Assert.DoesNotContain(missed.GetProperty("photos").EnumerateArray(), x => x.GetProperty("id").GetInt32() == id);
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task A_description_you_wrote_is_never_replaced_and_clearing_it_hands_it_back()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await SetupAsync();
            app.Reader.TextAnswers["ollama"] = Echo;
            var (_, slug, _, photos) = await GalleryAsync(admin, "Describe Two", 1);
            var id = photos[0];
            await RoundAsync();
            Assert.StartsWith("A teammate wins", (await PhotoAsync(admin, slug, id)).GetProperty("description").GetString());

            var r = await admin.PostAsJsonAsync($"/api/g/{slug}/photo/{id}/suggest", new { description = "Ava scores the winner in the last minute" });
            Assert.Equal("admin", (await r.ReadAsync()).GetProperty("descriptionSource").GetString());
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/photos/{id}/players", new { add = new[] { "7" } })).StatusCode);
            await RoundAsync();
            var p = await PhotoAsync(admin, slug, id);
            Assert.Equal("Ava scores the winner in the last minute", p.GetProperty("description").GetString());
            Assert.Equal("admin", p.GetProperty("descriptionSource").GetString());

            // saving the caption alone leaves your description as it is
            await admin.PostAsJsonAsync($"/api/g/{slug}/photo/{id}/suggest", new { caption = "Winner", description = "Ava scores the winner in the last minute" });
            Assert.Equal("admin", (await PhotoAsync(admin, slug, id)).GetProperty("descriptionSource").GetString());

            await admin.PostAsJsonAsync($"/api/g/{slug}/photo/{id}/suggest", new { description = "" });
            await RoundAsync();
            p = await PhotoAsync(admin, slug, id);
            Assert.StartsWith("Ava wins", p.GetProperty("description").GetString());
            Assert.Equal("ai", p.GetProperty("descriptionSource").GetString());

            // visitors never see who wrote it
            Assert.Equal(JsonValueKind.Null, (await PhotoAsync(app.NewClient(), slug, id)).GetProperty("descriptionSource").ValueKind);
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Players_with_a_roster_name_are_named()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await SetupAsync();
            app.Reader.TextAnswers["ollama"] = Echo;
            var (_, slug, _, photos) = await GalleryAsync(admin, "Describe Three", 1, names: true);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/photos/{photos[0]}/players", new { add = new[] { "10" } })).StatusCode);
            await RoundAsync();
            Assert.StartsWith("Emma wins", (await PhotoAsync(admin, slug, photos[0])).GetProperty("description").GetString());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Failures_stop_the_round_and_are_counted_per_photo()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await SetupAsync();
            app.Reader.TextAnswers.TryRemove("ollama", out _);
            var (_, slug, _, photos) = await GalleryAsync(admin, "Describe Four", 5);
            var (written, failed) = await RoundAsync();
            Assert.Equal(0, written);
            Assert.Equal(3, failed);   // three in a row, then it waits
            var counts = await (await admin.GetAsync("/api/admin/settings/vision/descriptions")).ReadAsync();
            Assert.True(counts.GetProperty("total").GetInt32() >= 5);

            // switched off: nothing is asked
            await admin.PutAsJsonAsync("/api/admin/settings/vision", new { providers = new[] { new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b-instruct" } }, describePhotos = false });
            Assert.Equal((0, 0), await RoundAsync());
        }
        finally { Serial.Release(); }
    }
}
