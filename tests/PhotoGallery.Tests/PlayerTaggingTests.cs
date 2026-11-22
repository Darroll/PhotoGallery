using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoGallery.Tests;

/// <summary>A stand-in for the AI models: answers per provider kind, and records who was asked.</summary>
public class FakeVisionReader : IVisionReader
{
    public ConcurrentQueue<string> Asked { get; } = new();
    public ConcurrentDictionary<string, Func<string>> Answers { get; } = new();
    public string? LastPrompt;

    public Task<VisionAnswer> AskAsync(VisionProvider provider, byte[] jpeg, string prompt, CancellationToken ct)
    {
        Asked.Enqueue(provider.Kind);
        LastPrompt = prompt;
        Assert.True(jpeg.Length > 100 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "a JPEG is sent");
        if (!Answers.TryGetValue(provider.Kind, out var answer)) throw new VisionException(provider.Kind + " is down");
        return Task.FromResult(new VisionAnswer(PlayerPrompt.Parse(answer()), provider.Kind, provider.Model, 1));
    }
}

public class VisionAppFactory : AppFactory
{
    public FakeVisionReader Reader { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IVisionReader>(Reader));
    }
}

public class PlayerTaggingTests(VisionAppFactory app) : IClassFixture<VisionAppFactory>
{
    static readonly SemaphoreSlim Serial = new(1, 1);   // the provider settings are shared by every test in this class

    async Task SetProvidersAsync(HttpClient admin, params object[] providers)
    {
        var r = await admin.PutAsJsonAsync("/api/admin/settings/vision", new { providers, autoApproveAt = 0 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    async Task<(int gid, string slug, int teamId)> GalleryWithTeamAsync(HttpClient admin, string title, int photos, string roster = "10 Emma\n7 Ava\n3")
    {
        var slug = await AppFactory.GalleryAsync(admin, title, app.NewFolder(photos));
        var gid = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("id").GetInt32();
        var team = await (await admin.PostAsJsonAsync("/api/admin/teams", new { name = title + " FC", colors = "white shirts, blue numbers", roster })).ReadAsync();
        var teamId = team.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId })).StatusCode);
        return (gid, slug, teamId);
    }

    static async Task<JsonElement> WaitAsync(HttpClient admin, int gid)
    {
        for (var i = 0; i < 200; i++)
        {
            var s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            if (!s.GetProperty("job").GetProperty("running").GetBoolean()) return s;
            await Task.Delay(50);
        }
        throw new TimeoutException("the check did not finish");
    }

    [Fact]
    public async Task Numbers_on_the_roster_become_suggestions_and_approving_tags_the_photo()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin, new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" });
            var (gid, slug, _) = await GalleryWithTeamAsync(admin, "Tag Flow", 2);
            // 10 is on the roster, 99 is not (the other team), and a reading as "0.9" means 90%
            app.Reader.Answers["ollama"] = () => "Sure! {\"numbers\": [{\"number\": \"10\", \"confidence\": 0.9}, {\"number\": 99, \"confidence\": 95}]}";

            Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { limit = 10 })).StatusCode);
            var s = await WaitAsync(admin, gid);
            Assert.Equal(2, s.GetProperty("checked").GetInt32());
            var pending = s.GetProperty("pending").EnumerateArray().ToList();
            Assert.Equal(2, pending.Count);
            Assert.All(pending, p => { Assert.Equal("10", p.GetProperty("number").GetString()); Assert.Equal(90, p.GetProperty("confidence").GetInt32()); Assert.Equal("Tag Flow FC 10", p.GetProperty("tag").GetString()); });
            Assert.Contains("white shirts, blue numbers", app.Reader.LastPrompt);
            Assert.Contains("10", app.Reader.LastPrompt);

            // nothing is tagged before review
            var photos = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray().ToList();
            Assert.All(photos, p => Assert.Empty(p.GetProperty("tags").EnumerateArray()));

            var first = pending[0].GetProperty("id").GetInt32(); var second = pending[1].GetProperty("id").GetInt32();
            var rv = await (await admin.PostAsJsonAsync("/api/admin/players/review", new { approve = new[] { first }, reject = new[] { second } })).ReadAsync();
            Assert.Equal(1, rv.GetProperty("approved").GetInt32());
            Assert.Equal(1, rv.GetProperty("rejected").GetInt32());
            photos = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray().ToList();
            Assert.Single(photos, p => p.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == "Tag Flow FC 10"));

            // checking again does not bring back the rejected suggestion
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { recheck = true });
            s = await WaitAsync(admin, gid);
            Assert.Empty(s.GetProperty("pending").EnumerateArray());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Without_a_fallback_set_up_only_the_first_provider_is_asked()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            // Claude has a key but is switched off, OpenAI is on but its agreement is not ticked: neither may be used
            await SetProvidersAsync(admin,
                new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" },
                new { kind = "anthropic", enabled = false, model = "claude-opus-5-5", apiKey = "sk-test", sendPhotosAgreed = true },
                new { kind = "openai", enabled = true, model = "gpt-test", apiKey = "sk-test", sendPhotosAgreed = false });
            var (gid, _, _) = await GalleryWithTeamAsync(admin, "No Fallback", 1);
            app.Reader.Answers.TryRemove("ollama", out _);
            app.Reader.Asked.Clear();

            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            var s = await WaitAsync(admin, gid);
            Assert.Equal(new[] { "ollama" }, app.Reader.Asked.ToArray());
            Assert.Equal(1, s.GetProperty("job").GetProperty("failed").GetInt32());
            Assert.Contains("ollama is down", s.GetProperty("job").GetProperty("lastError").GetString());
            Assert.Equal(0, s.GetProperty("checked").GetInt32());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Providers_are_tried_in_the_order_chosen_until_one_answers()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin,
                new { kind = "openai", enabled = true, model = "gpt-test", apiKey = "sk-o", sendPhotosAgreed = true },
                new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" },
                new { kind = "anthropic", enabled = true, model = "claude-opus-5-5", apiKey = "sk-a", sendPhotosAgreed = true });
            var (gid, _, _) = await GalleryWithTeamAsync(admin, "Order", 1);
            app.Reader.Answers.TryRemove("openai", out _);
            app.Reader.Answers.TryRemove("ollama", out _);
            app.Reader.Answers["anthropic"] = () => "{\"numbers\": [{\"number\": \"7\", \"confidence\": 80}]}";
            app.Reader.Asked.Clear();

            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            var s = await WaitAsync(admin, gid);
            Assert.Equal(new[] { "openai", "ollama", "anthropic" }, app.Reader.Asked.ToArray());
            var p = s.GetProperty("pending").EnumerateArray().Single();
            Assert.Equal("7", p.GetProperty("number").GetString());
            Assert.StartsWith("anthropic", p.GetProperty("source").GetString());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Api_keys_are_kept_but_never_sent_back_or_exported()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin, new { kind = "anthropic", enabled = true, model = "claude-opus-5-5", apiKey = "sk-secret-123", sendPhotosAgreed = true });
            var text = await (await admin.GetAsync("/api/admin/settings/vision")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("sk-secret-123", text);
            Assert.Contains("\"keySet\":true", text);
            // saving again with an empty key keeps the stored one
            await SetProvidersAsync(admin, new { kind = "anthropic", enabled = true, model = "claude-opus-5-5", apiKey = "", sendPhotosAgreed = true });
            text = await (await admin.GetAsync("/api/admin/settings/vision")).Content.ReadAsStringAsync();
            Assert.Contains("\"keySet\":true", text);
            Assert.DoesNotContain("sk-secret-123", await (await admin.GetAsync("/api/admin/onboarding/export")).Content.ReadAsStringAsync());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Names_go_in_tags_only_when_the_team_chooses_it()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin, new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" });
            var (gid, _, teamId) = await GalleryWithTeamAsync(admin, "Named", 1);
            await admin.PutAsJsonAsync($"/api/admin/teams/{teamId}", new { name = "Named FC", colors = "red", tagWithNames = true, roster = "10 Emma\n7 Ava" });
            app.Reader.Answers["ollama"] = () => "{\"numbers\": [\"7\"]}";
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            var s = await WaitAsync(admin, gid);
            Assert.Equal("Ava 7", s.GetProperty("pending").EnumerateArray().Single().GetProperty("tag").GetString());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task A_check_needs_a_provider_a_team_and_players()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin, new { kind = "ollama", enabled = false, url = "http://ollama.local:11434", model = "qwen3-vl:8b" });
            var slug = await AppFactory.GalleryAsync(admin, "Not Ready", app.NewFolder(1));
            var gid = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("id").GetInt32();
            var r = await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("No AI provider", await r.Content.ReadAsStringAsync());

            await SetProvidersAsync(admin, new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" });
            r = await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            Assert.Contains("Choose the team", await r.Content.ReadAsStringAsync());
            Assert.NotEqual(HttpStatusCode.OK, (await app.NewClient().GetAsync($"/api/admin/galleries/{gid}/players")).StatusCode);   // admin only
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public void Replies_and_rosters_are_read_carefully()
    {
        Assert.Equal(new[] { "10", "7" }, PlayerPrompt.Parse("```json\n{\"numbers\":[{\"number\":\"#10\",\"confidence\":\"85\"},{\"number\":7},{\"number\":\"ten\"},{\"number\":\"1234\"}]}\n```").Select(x => x.Number));
        Assert.Empty(PlayerPrompt.Parse("{\"numbers\": []}"));
        Assert.Throws<VisionException>(() => PlayerPrompt.Parse("I can't see any numbers."));
        var best = PlayerPrompt.Parse("{\"numbers\":[{\"number\":\"5\",\"confidence\":40},{\"number\":\"5\",\"confidence\":90}]}").Single();
        Assert.Equal(90, best.Confidence);

        var roster = PlayerTags.ParseRoster("10 Emma\n#7 - Ava\n03\nnot a player\n10 Again");
        Assert.Equal(new[] { ("10", "Emma"), ("7", "Ava"), ("3", "") }, roster);
        Assert.Contains("other team", PlayerPrompt.Build("blue", new[] { "3", "10" }));
    }
}
