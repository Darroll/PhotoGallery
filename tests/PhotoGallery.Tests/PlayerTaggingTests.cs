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
    public ConcurrentQueue<string> Prompts { get; } = new();
    /// <summary>When set, every request hangs until cancelled, like a model that is still thinking.</summary>
    public volatile bool Hang;

    public async Task<VisionAnswer> AskAsync(VisionProvider provider, byte[] jpeg, string prompt, CancellationToken ct)
    {
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
        Asked.Enqueue(provider.Kind);
        LastPrompt = prompt;
        Prompts.Enqueue(prompt);
        Assert.True(jpeg.Length > 100 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "a JPEG is sent");
        if (!Answers.TryGetValue(provider.Kind, out var answer)) throw new VisionException(provider.Kind + " is down");
        return new VisionAnswer(PlayerPrompt.Parse(answer()), provider.Kind, provider.Model, 1);
    }

    /// <summary>Answers for questions other than numbers (scene descriptions), per provider kind; the function sees the prompt.</summary>
    public ConcurrentDictionary<string, Func<string, string>> TextAnswers { get; } = new();
    public ConcurrentQueue<string> TextPrompts { get; } = new();

    public Task<VisionText> AskTextAsync(VisionProvider provider, byte[] jpeg, string prompt, CancellationToken ct)
    {
        TextPrompts.Enqueue(prompt);
        Assert.True(jpeg.Length > 100 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "a JPEG is sent");
        if (!TextAnswers.TryGetValue(provider.Kind, out var answer)) throw new VisionException(provider.Kind + " is down");
        return Task.FromResult(new VisionText(answer(prompt), provider.Kind, provider.Model, 1));
    }
}

public class VisionAppFactory : UserFactory
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
    public async Task The_admin_sees_each_photo_checked_and_every_gallery_in_the_activity_list()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin, new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" });
            var (gid, _, _) = await GalleryWithTeamAsync(admin, "Watch Flow", 2);
            app.Reader.Answers["ollama"] = () => "{\"numbers\": [{\"number\": \"7\", \"confidence\": 80}, {\"number\": \"42\", \"confidence\": 90}]}";
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            var job = (await WaitAsync(admin, gid)).GetProperty("job");
            var recent = job.GetProperty("recent").EnumerateArray().ToList();
            Assert.Equal(2, recent.Count);
            Assert.All(recent, r =>
            {
                Assert.True(r.GetProperty("ok").GetBoolean());
                Assert.Contains("#7 (80%)", r.GetProperty("result").GetString());
                Assert.Contains("not on the roster: #42", r.GetProperty("result").GetString());
                Assert.Equal("Ollama (your own server)", r.GetProperty("provider").GetString());
            });
            Assert.Equal("", job.GetProperty("currentPhoto").GetString());

            var all = await (await admin.GetAsync("/api/admin/players/jobs")).ReadAsync();
            var mine = all.EnumerateArray().Single(x => x.GetProperty("job").GetProperty("galleryId").GetInt32() == gid);
            Assert.Equal("Watch Flow", mine.GetProperty("job").GetProperty("gallery").GetString());
            Assert.Equal(2, mine.GetProperty("pending").GetInt32());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task A_running_check_shows_the_photo_being_read_and_can_be_stopped()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin, new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" });
            var (gid, _, _) = await GalleryWithTeamAsync(admin, "Stop Flow", 3);
            app.Reader.Answers["ollama"] = () => "{\"numbers\": []}";
            app.Reader.Hang = true;
            try
            {
                await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
                JsonElement job = default;
                for (var i = 0; i < 200; i++)
                {
                    job = (await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync()).GetProperty("job");
                    if (job.GetProperty("currentProvider").GetString() != "") break;
                    await Task.Delay(50);
                }
                Assert.True(job.GetProperty("running").GetBoolean());
                Assert.NotEqual("", job.GetProperty("currentPhoto").GetString());
                Assert.True(job.GetProperty("currentPhotoId").GetInt32() > 0);
                Assert.Equal("Ollama (your own server)", job.GetProperty("currentProvider").GetString());

                Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/admin/galleries/{gid}/players/stop", null)).StatusCode);
                var s = await WaitAsync(admin, gid);
                Assert.Equal("Stopped by you.", s.GetProperty("job").GetProperty("lastError").GetString());
                Assert.Equal(0, s.GetProperty("checked").GetInt32());
                Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/galleries/{gid}/players/stop", null)).StatusCode);
            }
            finally { app.Reader.Hang = false; }

            // the photos left unchecked are picked up by the next check
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            Assert.Equal(3, (await WaitAsync(admin, gid)).GetProperty("checked").GetInt32());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Numbers_not_on_the_roster_can_be_added_as_players_without_checking_again()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin, new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" });
            var (gid, slug, teamId) = await GalleryWithTeamAsync(admin, "New Flow", 2);
            app.Reader.Answers["ollama"] = () => "{\"numbers\": [{\"number\": \"88\", \"confidence\": 70}, {\"number\": \"21\", \"confidence\": 85}, {\"number\": \"9\", \"confidence\": 60}]}";
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            var s = await WaitAsync(admin, gid);
            Assert.Empty(s.GetProperty("pending").EnumerateArray());
            Assert.Equal(2, s.GetProperty("checked").GetInt32());
            var unknown = s.GetProperty("unknown").EnumerateArray().ToList();
            Assert.Equal(new[] { "9", "21", "88" }, unknown.Select(u => u.GetProperty("number").GetString()).ToArray());   // in number order
            Assert.All(unknown, u => Assert.Equal(2, u.GetProperty("photos").GetInt32()));
            // the Teams page offers the numbers found in the team's photos for its roster
            async Task<string[]> FoundAsync() => (await (await admin.GetAsync("/api/admin/teams")).ReadAsync()).EnumerateArray()
                .Single(t => t.GetProperty("id").GetInt32() == teamId).GetProperty("found").EnumerateArray()
                .Select(f => f.GetProperty("number").GetString() + ":" + f.GetProperty("photos").GetInt32()).ToArray();
            Assert.Equal(new[] { "9:2", "21:2", "88:2" }, await FoundAsync());

            // one of the two photos is unticked: only the other becomes a suggestion
            var skip = unknown[1].GetProperty("photoIds")[0].GetInt32();
            var add = await (await admin.PostAsJsonAsync($"/api/admin/teams/{teamId}/players", new { number = "21", name = "Mia", skip = new[] { skip } })).ReadAsync();
            Assert.Equal(1, add.GetProperty("suggestions").GetInt32());
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/teams/{teamId}/players", new { number = "21" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/teams/{teamId}/numbers/dismiss", new { number = "88" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/teams/{teamId}/numbers/dismiss", new { number = "9" })).StatusCode);

            Assert.Equal(new[] { "21" }, (await FoundAsync()).Select(f => f.Split(':')[0]).ToArray());   // numbers said not to be ours are not offered
            s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            Assert.Empty(s.GetProperty("unknown").EnumerateArray());
            var pending = s.GetProperty("pending").EnumerateArray().ToList();
            Assert.Equal(1, pending.Count);
            Assert.NotEqual(skip, pending[0].GetProperty("photoId").GetInt32());
            Assert.All(pending, p => { Assert.Equal("21", p.GetProperty("number").GetString()); Assert.Equal(85, p.GetProperty("confidence").GetInt32()); });

            // a dismissed number stays dismissed when the photos are read again
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { recheck = true });
            Assert.Empty((await WaitAsync(admin, gid)).GetProperty("unknown").EnumerateArray());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task A_gallery_tells_the_model_only_the_jersey_set_it_uses()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin, new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" });
            var (gid, _, teamId) = await GalleryWithTeamAsync(admin, "Kit Flow", 1);
            var team = await (await admin.PutAsJsonAsync($"/api/admin/teams/{teamId}", new { name = "Kit Flow FC", colors = "Home: white shirts with blue numbers\nAway: blue shirts with white numbers\nThird: black shirts with gold numbers", roster = "10 Emma" })).ReadAsync();
            Assert.Equal(new[] { "Home", "Away", "Third" }, team.GetProperty("kits").EnumerateArray().Select(k => k.GetProperty("name").GetString()).ToArray());
            var away = team.GetProperty("kits")[1];
            Assert.Equal("blue", away.GetProperty("shirt").GetString());
            Assert.Equal("white", away.GetProperty("number").GetString());
            app.Reader.Answers["ollama"] = () => "{\"numbers\": []}";

            // no set chosen: every set is described
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { recheck = true });
            await WaitAsync(admin, gid);
            Assert.Contains("white shirts with blue numbers; or blue shirts with white numbers; or black shirts with gold numbers", app.Reader.LastPrompt);

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId, kit = "away" })).StatusCode);
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { recheck = true });
            var s = await WaitAsync(admin, gid);
            Assert.Equal("away", s.GetProperty("teamKit").GetString());
            Assert.Contains("Our team wears: blue shirts with white numbers.", app.Reader.LastPrompt);
            Assert.DoesNotContain("gold", app.Reader.LastPrompt);

            // colours typed for this gallery alone, not one of the team's sets
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId, kit = "white shirts with black numbers" })).StatusCode);
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { recheck = true });
            await WaitAsync(admin, gid);
            Assert.Contains("Our team wears: white shirts with black numbers.", app.Reader.LastPrompt);
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task Each_folder_of_a_tournament_can_have_its_own_jersey()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            await SetProvidersAsync(admin, new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" });
            // a tournament: one game per sub folder
            var folder = "t" + Guid.NewGuid().ToString("N")[..8];
            foreach (var (game, n) in new[] { ("Game_1", 2), ("Game_2", 1) })
            {
                Directory.CreateDirectory(Path.Combine(app.Library, folder, game));
                for (var i = 0; i < n; i++) TestImages.Write(Path.Combine(app.Library, folder, game, $"{game}{i}.jpg"), 400 + i * 40, 300, 60 + i * 30);
            }
            var slug = await AppFactory.GalleryAsync(admin, "Cup", folder);
            var gid = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("id").GetInt32();
            var team = await (await admin.PostAsJsonAsync("/api/admin/teams", new { name = "Cup FC", colors = "Home: white shirts with blue numbers\nAway: blue shirts with white numbers\nThird: black shirts with gold numbers", roster = "10 Emma" })).ReadAsync();
            var teamId = team.GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId, kit = "Home" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId, folder = "game_2", kit = "Away" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId, folder = "../x", kit = "Away" })).StatusCode);

            var s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            var folders = s.GetProperty("folders").EnumerateArray().ToList();
            Assert.Equal(new[] { "Game_1", "Game_2" }, folders.Select(f => f.GetProperty("folder").GetString()).ToArray());
            Assert.Equal(new[] { "", "Away" }, folders.Select(f => f.GetProperty("kit").GetString()).ToArray());
            Assert.Equal(new[] { 2, 1 }, folders.Select(f => f.GetProperty("photos").GetInt32()).ToArray());

            app.Reader.Answers["ollama"] = () => "{\"numbers\": []}";
            app.Reader.Prompts.Clear();
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { recheck = true });
            await WaitAsync(admin, gid);
            var prompts = app.Reader.Prompts.ToList();
            Assert.Equal(3, prompts.Count);
            Assert.Equal(2, prompts.Count(p => p.Contains("Our team wears: white shirts with blue numbers.")));
            Assert.Equal(1, prompts.Count(p => p.Contains("Our team wears: blue shirts with white numbers.")));

            // back to the gallery's jersey, and a new team starts over
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId, folder = "Game_2", kit = "" })).StatusCode);
            s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            Assert.All(s.GetProperty("folders").EnumerateArray(), f => Assert.Equal("", f.GetProperty("kit").GetString()));
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task New_photos_are_checked_automatically_when_the_gallery_or_main_setting_says_so()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            async Task Vision(bool autoNew) => Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/settings/vision", new { providers = new[] { new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" } }, autoApproveAt = 0, autoCheckNew = autoNew })).StatusCode);
            await Vision(true);
            app.Reader.Answers["ollama"] = () => "{\"numbers\": [{\"number\": \"10\", \"confidence\": 90}]}";
            var folder = app.NewFolder(1);
            var slug = await AppFactory.GalleryAsync(admin, "Auto Flow", folder);
            var gid = (await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("id").GetInt32();
            // no team yet: the first scan queues nothing
            Assert.False((await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync()).GetProperty("job").GetProperty("running").GetBoolean());
            var team = await (await admin.PostAsJsonAsync("/api/admin/teams", new { name = "Auto FC", colors = "red shirts", roster = "10 Emma" })).ReadAsync();
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/team", new { teamId = team.GetProperty("id").GetInt32() })).StatusCode);
            var s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            Assert.Equal(JsonValueKind.Null, s.GetProperty("autoTag").ValueKind);   // a new gallery follows the main setting
            Assert.True(s.GetProperty("autoTagDefault").GetBoolean());

            int n = 1;
            async Task<JsonElement> AddPhotoAndScan()
            {
                TestImages.Write(Path.Combine(app.Library, folder, $"new{n++}.jpg"), 500, 300, 90 + n * 10);
                Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/galleries/{gid}/scan", null)).StatusCode);
                return await WaitAsync(admin, gid);
            }
            s = await AddPhotoAndScan();
            Assert.Equal(2, s.GetProperty("checked").GetInt32());   // the first photo was waiting too: every unchecked photo is checked
            Assert.True(s.GetProperty("job").GetProperty("automatic").GetBoolean());
            Assert.Equal(2, s.GetProperty("pending").GetArrayLength());

            // this gallery says no: new photos wait
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/autotag", new { autoTag = false })).StatusCode);
            s = await AddPhotoAndScan();
            Assert.Equal(2, s.GetProperty("checked").GetInt32());

            // main setting off, but this gallery says yes
            await Vision(false);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/autotag", new { autoTag = true })).StatusCode);
            s = await AddPhotoAndScan();
            Assert.Equal(4, s.GetProperty("checked").GetInt32());

            // back to the main setting, which is off
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/autotag", new { autoTag = (bool?)null })).StatusCode);
            s = await AddPhotoAndScan();
            Assert.Equal(4, s.GetProperty("checked").GetInt32());
            Assert.Equal(5, s.GetProperty("photos").GetInt32());
        }
        finally { Serial.Release(); }
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_in_time_fails_with_the_setting_named()
    {
        // a server that accepts the connection and never answers, like a stalled Ollama
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var held = new List<System.Net.Sockets.TcpClient>();
        _ = Task.Run(async () => { try { while (true) held.Add(await listener.AcceptTcpClientAsync()); } catch { } });
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var p = new VisionProvider { Kind = "ollama", Enabled = true, Url = $"http://127.0.0.1:{port}", Model = "m", TimeoutSeconds = 1 };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<VisionException>(() => new VisionReader().AskAsync(p, new byte[] { 0xFF, 0xD8 }, "q", CancellationToken.None));
            Assert.Contains("No answer within 1 seconds", ex.Message);
            Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 30);
        }
        finally { listener.Stop(); held.ForEach(c => c.Dispose()); }
    }

    /// <summary>Answers every request with one fixed HTTP 200 JSON body, like an Ollama server would.</summary>
    static (System.Net.Sockets.TcpListener listener, string url) FakeOllama(string body)
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var c = await listener.AcceptTcpClientAsync();
                    var stream = c.GetStream();
                    var buf = new byte[1 << 16]; var seen = new System.Text.StringBuilder();
                    // read the headers and the whole (possibly chunked) body before answering
                    while (true) { var n = await stream.ReadAsync(buf); if (n == 0) break; seen.Append(System.Text.Encoding.Latin1.GetString(buf, 0, n)); var t = seen.ToString(); if (t.Contains("\r\n0\r\n\r\n") || (t.Contains("Content-Length") && t.IndexOf("\r\n\r\n") > 0 && t.Length - t.IndexOf("\r\n\r\n") - 4 >= int.Parse(System.Text.RegularExpressions.Regex.Match(t, @"Content-Length: (\d+)").Groups[1].Value))) break; }
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(bytes);
                }
            }
            catch { }
        });
        return (listener, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
    }

    [Fact]
    public async Task A_thinking_model_that_runs_out_of_room_is_explained()
    {
        var (listener, url) = FakeOllama("{\"response\":\"\",\"thinking\":\"Got it, let's look at the image. The white jerseys...\",\"done\":true,\"done_reason\":\"length\"}");
        try
        {
            var p = new VisionProvider { Kind = "ollama", Enabled = true, Url = url, Model = "qwen3-vl:8b", TimeoutSeconds = 20 };
            var ex = await Assert.ThrowsAsync<VisionException>(() => new VisionReader().AskAsync(p, new byte[] { 0xFF, 0xD8 }, "q", CancellationToken.None));
            Assert.Contains("spent its whole answer thinking", ex.Message);
            Assert.Contains("qwen3-vl:8b-instruct", ex.Message);
        }
        finally { listener.Stop(); }

        (listener, url) = FakeOllama("{\"response\":\"I can see number 10.\",\"done\":true,\"done_reason\":\"stop\"}");
        try
        {
            var p = new VisionProvider { Kind = "ollama", Enabled = true, Url = url, Model = "m", TimeoutSeconds = 20 };
            var ex = await Assert.ThrowsAsync<VisionException>(() => new VisionReader().AskAsync(p, new byte[] { 0xFF, 0xD8 }, "q", CancellationToken.None));
            Assert.Contains("it answered without JSON", ex.Message);
            Assert.Contains("I can see number 10.", ex.Message);
        }
        finally { listener.Stop(); }
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

            // a photo that failed (even three times) is tried again by the next check, for example after changing the model
            for (var i = 0; i < 3; i++) { await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { }); await WaitAsync(admin, gid); }
            app.Reader.Answers["ollama"] = () => "{\"numbers\": []}";
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            s = await WaitAsync(admin, gid);
            Assert.Equal(1, s.GetProperty("job").GetProperty("done").GetInt32());
            Assert.Equal(1, s.GetProperty("checked").GetInt32());
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

        var roster = PlayerTags.ParseRoster("10 Emma\n#7 - Ava\n03\nnot a player\n10 Again\n0 Zoe\n00 Lily\n7 Again");
        Assert.Equal(new[] { ("10", "Emma"), ("7", "Ava"), ("03", ""), ("0", "Zoe"), ("00", "Lily") }, roster);   // 0, 00 and 03 kept as written
        Assert.Contains("other team", PlayerPrompt.Build("blue", new[] { "3", "10" }));
        Assert.Contains("0 and 00 are different", PlayerPrompt.Build("blue", new[] { "3", "10" }));

        // a hidden digit: "?" (or x, _) and the partial flag
        var hidden = PlayerPrompt.Parse("{\"numbers\":[{\"number\":\"1?\",\"confidence\":70},{\"number\":\"2x\"},{\"number\":\"4\",\"partial\":true},{\"number\":\"??\"},{\"number\":\"00\"}]}");
        Assert.Equal(new[] { ("1?", true), ("2?", true), ("4", true), ("00", false) }, hidden.Select(x => (x.Number, x.Partial)));
        Assert.Equal(new[] { "10", "19" }, PlayerTags.Candidates("1?", true, new[] { "1", "10", "19", "21" }));
        Assert.Equal(new[] { "1" }, PlayerTags.Candidates("1", true, new[] { "1", "10", "19", "21", "32" }));   // no guessing at hidden digits without a "?"
    }

    [Fact]
    public async Task Partly_hidden_numbers_are_offered_as_choices_and_never_approved_automatically()
    {
        await Serial.WaitAsync();
        try
        {
            var admin = await app.AdminAsync();
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/settings/vision", new { providers = new[] { new { kind = "ollama", enabled = true, url = "http://ollama.local:11434", model = "qwen3-vl:8b" } }, autoApproveAt = 50 })).StatusCode);
            var (gid, _, teamId) = await GalleryWithTeamAsync(admin, "Hidden Flow", 1, "0 Zoe\n00 Lily\n7 Ava\n07 Mae\n12 Kim\n19 Emma\n15 Ivy");
            // 1? (the 9 covered), 07 read clearly, 0 the model says may be partly hidden; a clear 1 not on the roster is not guessed to be 19
            app.Reader.Answers["ollama"] = () => "{\"numbers\": [{\"number\": \"1?\", \"confidence\": 90}, {\"number\": \"07\", \"confidence\": 95}, {\"number\": \"0\", \"confidence\": 92, \"partial\": true}, {\"number\": \"1\", \"confidence\": 80}]}";
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { });
            var s = await WaitAsync(admin, gid);
            var pending = s.GetProperty("pending").EnumerateArray().ToList();
            string[] Nums(IEnumerable<JsonElement> l) => l.Select(p => p.GetProperty("number").GetString()!).Order(StringComparer.Ordinal).ToArray();
            // 07 is clear and approved automatically (not 7); the rest wait, marked unsure
            Assert.Equal(new[] { "0", "12", "15", "19" }, Nums(pending));
            Assert.Equal("1", s.GetProperty("unknown").EnumerateArray().Single().GetProperty("number").GetString());
            Assert.All(pending, p => Assert.True(p.GetProperty("unsure").GetBoolean()));
            Assert.Equal(1, s.GetProperty("approved").GetInt32());
            Assert.Contains("partly hidden", pending.First(p => p.GetProperty("number").GetString() == "19").GetProperty("note").GetString());
            Assert.Contains("may be partly hidden", pending.First(p => p.GetProperty("number").GetString() == "0").GetProperty("note").GetString());

            // picking 19 rejects 12 and 15 from the same reading, and leaves the 0 waiting
            var id19 = pending.First(p => p.GetProperty("number").GetString() == "19").GetProperty("id").GetInt32();
            var r = await (await admin.PostAsJsonAsync("/api/admin/players/review", new { approve = new[] { id19 } })).ReadAsync();
            Assert.Equal(1, r.GetProperty("approved").GetInt32());
            Assert.Equal(2, r.GetProperty("rejected").GetInt32());
            s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            Assert.Equal(new[] { "0" }, Nums(s.GetProperty("pending").EnumerateArray()));

            // a clear reading later settles the unsure one
            app.Reader.Answers["ollama"] = () => "{\"numbers\": [{\"number\": \"0\", \"confidence\": 97}, {\"number\": \"1\", \"confidence\": 80}]}";
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { recheck = true });
            s = await WaitAsync(admin, gid);
            var zoe = s.GetProperty("pending").EnumerateArray().Single();
            Assert.False(zoe.GetProperty("unsure").GetBoolean());

            // fixing one photo by hand: the 0 was really 00, and the 1 not on the roster was really 19 (already tagged) - so "not a player"
            var pid = zoe.GetProperty("photoId").GetInt32();
            var fix = await (await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/photos/{pid}/player", new { number = "00", suggestion = zoe.GetProperty("id").GetInt32() })).ReadAsync();
            Assert.Equal("Hidden Flow FC 00", fix.GetProperty("tag").GetString());
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/photos/{pid}/player", new { sighting = "1" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/photos/{pid}/player", new { number = "abc" })).StatusCode);
            s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            Assert.Empty(s.GetProperty("pending").EnumerateArray());
            Assert.Empty(s.GetProperty("unknown").EnumerateArray());
            Assert.Equal(3, s.GetProperty("approved").GetInt32());   // 07, 19, 00

            // checking again does not bring the fixed readings back
            await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/players/check", new { recheck = true });
            s = await WaitAsync(admin, gid);
            Assert.Empty(s.GetProperty("pending").EnumerateArray());
            Assert.Empty(s.GetProperty("unknown").EnumerateArray());

            // a number not on the roster yet (the name is not known): the photo waits under it, then is tagged once the player is added
            var moved = await (await admin.PostAsJsonAsync($"/api/admin/galleries/{gid}/photos/{pid}/player", new { number = "44" })).ReadAsync();
            Assert.Equal("44", moved.GetProperty("number").GetString());
            s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            var u44 = s.GetProperty("unknown").EnumerateArray().Single();
            Assert.Equal("44", u44.GetProperty("number").GetString());
            Assert.Equal(pid, u44.GetProperty("photoIds")[0].GetInt32());
            var added = await (await admin.PostAsJsonAsync($"/api/admin/teams/{teamId}/players", new { number = "44", name = "Noa" })).ReadAsync();
            Assert.Equal(1, added.GetProperty("suggestions").GetInt32());
            s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
            Assert.Empty(s.GetProperty("unknown").EnumerateArray());
            Assert.Empty(s.GetProperty("pending").EnumerateArray());   // set by hand, so tagged straight away
            Assert.Equal(4, s.GetProperty("approved").GetInt32());
        }
        finally { Serial.Release(); }
    }
    [Fact]
    public async Task The_admin_tags_several_players_at_once_from_the_photo()
    {
        var admin = await app.AdminAsync();
        var (gid, slug, _) = await GalleryWithTeamAsync(admin, "Viewer Tags", 1);
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];

        var view = await (await admin.GetAsync($"/api/g/{slug}/photo/{photo}/players")).ReadAsync();
        Assert.Equal("admin", view.GetProperty("can").GetString());
        Assert.Equal(new[] { "3", "7", "10" }, view.GetProperty("roster").EnumerateArray().Select(x => x.GetProperty("number").GetString()).ToArray());

        var r = await (await admin.PostAsJsonAsync($"/api/admin/photos/{photo}/players", new { add = new[] { "10", "7", "44" } })).ReadAsync();
        Assert.Equal(new[] { "7", "10" }, r.GetProperty("players").EnumerateArray().Select(x => x.GetProperty("number").GetString()).OrderBy(x => x.Length).ThenBy(x => x).ToArray());
        Assert.Equal("44", r.GetProperty("held").EnumerateArray().Single().GetString());   // not on the roster: waits under #44

        r = await (await admin.PostAsJsonAsync($"/api/admin/photos/{photo}/players", new { remove = new[] { "7" } })).ReadAsync();
        Assert.Equal("10", r.GetProperty("players").EnumerateArray().Single().GetProperty("number").GetString());

        // everyone sees who is tagged, without the roster or the tagging buttons
        var anon = await (await app.NewClient().GetAsync($"/api/g/{slug}/photo/{photo}/players")).ReadAsync();
        Assert.Equal(JsonValueKind.Null, anon.GetProperty("can").ValueKind);
        Assert.Equal("#10", anon.GetProperty("players").EnumerateArray().Single().GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, anon.GetProperty("roster").ValueKind);
        var s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
        Assert.Contains("44", s.GetProperty("unknown").EnumerateArray().Select(x => x.GetProperty("number").GetString()));
    }

    [Fact]
    public async Task Signed_in_parents_suggest_players_only_when_the_gallery_allows_it()
    {
        var admin = await app.AdminAsync();
        var (gid, slug, _) = await GalleryWithTeamAsync(admin, "Parent Tags", 1);
        var photo = (await AppFactory.PhotoIdsAsync(admin, slug))[0];
        var parent = app.AsUser("parent-tagger-1");

        // off by default
        Assert.Equal(JsonValueKind.Null, (await (await parent.GetAsync($"/api/g/{slug}/photo/{photo}/players")).ReadAsync()).GetProperty("can").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/players/suggest", new { numbers = new[] { "7" } })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/galleries/{gid}/playertags", new { allow = true })).StatusCode);
        var view = await (await parent.GetAsync($"/api/g/{slug}/photo/{photo}/players")).ReadAsync();
        Assert.Equal("suggest", view.GetProperty("can").GetString());
        Assert.Equal("#7", view.GetProperty("roster").EnumerateArray().First(x => x.GetProperty("number").GetString() == "7").GetProperty("label").GetString());   // numbers only, no names
        Assert.Equal("suggest", (await (await app.NewClient().GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("playerTagging").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/players/suggest", new { numbers = new[] { "7" } })).StatusCode);
        var r = await (await parent.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/players/suggest", new { numbers = new[] { "7", "10", "99" } })).ReadAsync();
        Assert.Equal(2, r.GetProperty("suggested").GetInt32());   // 99 is not on the roster
        Assert.Equal(0, (await (await parent.PostAsJsonAsync($"/api/g/{slug}/photo/{photo}/players/suggest", new { numbers = new[] { "7" } })).ReadAsync()).GetProperty("suggested").GetInt32());

        // nothing is tagged until the admin approves
        Assert.Empty((await (await app.NewClient().GetAsync($"/api/g/{slug}/photo/{photo}/players")).ReadAsync()).GetProperty("players").EnumerateArray());
        var s = await (await admin.GetAsync($"/api/admin/galleries/{gid}/players")).ReadAsync();
        var mine = s.GetProperty("pending").EnumerateArray().Where(x => x.GetProperty("photoId").GetInt32() == photo).ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, x => Assert.StartsWith("parent", x.GetProperty("source").GetString()));
        var seven = mine.First(x => x.GetProperty("number").GetString() == "7").GetProperty("id").GetInt32();
        Assert.Equal(2, (await (await admin.GetAsync($"/api/g/{slug}/photo/{photo}/players")).ReadAsync()).GetProperty("pending").GetArrayLength());
        await admin.PostAsJsonAsync("/api/admin/players/review", new { approve = new[] { seven } });
        Assert.Equal("#7", (await (await app.NewClient().GetAsync($"/api/g/{slug}/photo/{photo}/players")).ReadAsync()).GetProperty("players").EnumerateArray().Single().GetProperty("label").GetString());
    }
    [Fact]
    public async Task A_gallery_tag_box_offers_only_its_own_team_players()
    {
        var admin = await app.AdminAsync();
        var (gid, slug, _) = await GalleryWithTeamAsync(admin, "Box Ours", 1, "10 Emma");
        var (otherGid, otherSlug, _) = await GalleryWithTeamAsync(admin, "Box Theirs", 1, "9 Zoe");
        await admin.PostAsJsonAsync($"/api/admin/photos/{(await AppFactory.PhotoIdsAsync(admin, slug))[0]}/players", new { add = new[] { "10" } });
        await admin.PostAsJsonAsync($"/api/admin/photos/{(await AppFactory.PhotoIdsAsync(admin, otherSlug))[0]}/players", new { add = new[] { "9" } });
        await admin.PutAsJsonAsync($"/api/admin/photos/{(await AppFactory.PhotoIdsAsync(admin, slug))[0]}/tags", new { names = new[] { "Box Ours FC 10", "Sunset" } });
        var noTeamSlug = await AppFactory.GalleryAsync(admin, "Box None", app.NewFolder(1));
        var noTeam = (await (await admin.GetAsync($"/api/g/{noTeamSlug}")).ReadAsync()).GetProperty("id").GetInt32();

        async Task<string[]> Offered(int? g) => (await (await admin.GetAsync("/api/admin/tags" + (g == null ? "" : $"?gallery={g}"))).ReadAsync()).EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
        var ours = await Offered(gid);
        Assert.Contains("Box Ours FC 10", ours);
        Assert.Contains("Sunset", ours);
        Assert.DoesNotContain("Box Theirs FC 9", ours);
        var none = await Offered(noTeam);
        Assert.Contains("Sunset", none);
        Assert.DoesNotContain("Box Ours FC 10", none);
        Assert.DoesNotContain("Box Theirs FC 9", none);
        Assert.Contains("Box Theirs FC 9", await Offered(null));   // the Tags settings page still lists every tag
        _ = otherGid;
    }
}
