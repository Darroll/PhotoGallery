using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace PhotoGallery.Tests;

public class ReachabilityTests(AppFactory app) : IClassFixture<AppFactory>
{
    static readonly IPAddress PublicIp = IPAddress.Parse("93.184.216.34");

    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }

    sealed class Throws(Exception e) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw e;
    }

    static HttpResponseMessage Ours(HttpStatusCode code = HttpStatusCode.OK)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("ok") };
        r.Headers.Add(Reachability.Header, "1");
        return r;
    }

    Reachability Svc(HttpMessageHandler handler, IPAddress[]? addrs = null)
    {
        var svc = (Reachability)app.Services.GetService(typeof(Reachability))!;
        svc.Http = () => new HttpClient(handler);
        svc.Resolve = _ => Task.FromResult(addrs ?? [PublicIp]);
        return svc;
    }

    [Theory]
    [InlineData("photos.example.com", "https://photos.example.com")]
    [InlineData("  https://photos.example.com/  ", "https://photos.example.com")]
    [InlineData("http://192.168.1.5:5000", "http://192.168.1.5:5000")]
    [InlineData("https://example.com/gallery/", "https://example.com/gallery")]
    [InlineData("", null)]
    [InlineData("ftp://example.com", null)]
    [InlineData("https://user:pw@example.com", null)]
    [InlineData("https://example.com/?x=1", null)]
    [InlineData("not a url", null)]
    public void Normalize_accepts_only_plain_web_addresses(string input, string? expected) =>
        Assert.Equal(expected, Reachability.Normalize(input));

    [Fact]
    public async Task Healthz_says_it_is_this_app()
    {
        var r = await app.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(r.Headers.Contains(Reachability.Header));
    }

    [Fact]
    public async Task An_address_that_reaches_the_app_over_https_is_green()
    {
        var r = await Svc(new Stub(_ => Ours())).CheckAsync("https://photos.example.com");
        Assert.Equal("ok", r.Status);
    }

    [Fact]
    public async Task Another_site_answering_is_red()
    {
        var r = await Svc(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello") })).CheckAsync("https://photos.example.com");
        Assert.Equal("bad", r.Status);
        Assert.Equal("A different site answers", r.Value);
    }

    [Fact]
    public async Task A_proxy_error_is_explained()
    {
        var r = await Svc(new Stub(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))).CheckAsync("https://photos.example.com");
        Assert.Equal("bad", r.Status);
        Assert.Contains("proxy", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_name_that_does_not_exist_is_red()
    {
        var svc = Svc(new Stub(_ => Ours()));
        svc.Resolve = _ => throw new SocketException((int)SocketError.HostNotFound);
        var r = await svc.CheckAsync("https://nope.example.com");
        Assert.Equal("Name not found", r.Value);
        Assert.Equal("bad", r.Status);
    }

    [Fact]
    public async Task A_home_address_works_but_only_at_home()
    {
        var r = await Svc(new Stub(_ => Ours()), [IPAddress.Parse("192.168.2.3")]).CheckAsync("https://photos.example.com");
        Assert.Equal("warn", r.Status);
        Assert.Equal("Home network only", r.Value);
    }

    [Fact]
    public async Task Plain_http_on_a_public_address_is_amber()
    {
        var r = await Svc(new Stub(_ => Ours())).CheckAsync("http://photos.example.com");
        Assert.Equal("warn", r.Status);
        Assert.Equal("Works, not encrypted", r.Value);
    }

    [Fact]
    public async Task Nothing_listening_and_a_timeout_are_told_apart()
    {
        var refused = await Svc(new Throws(new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)))).CheckAsync("https://photos.example.com");
        Assert.Equal("Connection refused", refused.Value);
        var timeout = await Svc(new Throws(new TaskCanceledException())).CheckAsync("https://photos.example.com");
        Assert.Equal("No answer", timeout.Value);
        var cert = await Svc(new Throws(new HttpRequestException("ssl", new System.Security.Authentication.AuthenticationException("bad certificate")))).CheckAsync("https://photos.example.com");
        Assert.Equal("Certificate problem", cert.Value);
    }
}

public class OnboardingTests(AppFactory app) : IClassFixture<AppFactory>
{
    static async Task<System.Text.Json.JsonElement> StateAsync(HttpClient admin) => await (await admin.GetAsync("/api/admin/onboarding")).ReadAsync();
    static bool Done(System.Text.Json.JsonElement s, string id) => s.GetProperty("steps").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id).GetProperty("done").GetBoolean();

    [Fact]
    public async Task The_checklist_and_the_guide_follow_what_has_been_done()
    {
        var admin = await app.AdminAsync();

        var fresh = await StateAsync(admin);
        Assert.True(fresh.GetProperty("wizardPending").GetBoolean());
        Assert.True(fresh.GetProperty("showChecklist").GetBoolean());
        Assert.False(Done(fresh, "gallery"));
        Assert.True(fresh.GetProperty("library").GetProperty("readable").GetBoolean());

        await AppFactory.GalleryAsync(admin, "First", "Album");
        var after = await StateAsync(admin);
        Assert.False(after.GetProperty("wizardPending").GetBoolean());
        Assert.True(Done(after, "gallery"));
        Assert.True(Done(after, "share"));   // the helper makes a public gallery

        var hidden = await (await admin.PutAsJsonAsync("/api/admin/onboarding", new { checklistHidden = true })).ReadAsync();
        Assert.True(hidden.GetProperty("checklistHidden").GetBoolean());
        Assert.False((await StateAsync(admin)).GetProperty("showChecklist").GetBoolean());
        await admin.PutAsJsonAsync("/api/admin/onboarding", new { checklistHidden = false, wizardDone = true });
        Assert.True((await StateAsync(admin)).GetProperty("wizardDone").GetBoolean());
    }

    [Fact]
    public async Task The_site_address_is_validated_saved_and_shown_on_the_overview()
    {
        var admin = await app.AdminAsync();

        var bad = await admin.PutAsJsonAsync("/api/admin/onboarding/address", new { url = "ftp://x" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await admin.PutAsJsonAsync("/api/admin/onboarding/address", new { url = "Photos.Example.com/" });
        Assert.Equal("https://photos.example.com", (await ok.ReadAsync()).GetProperty("url").GetString());
        Assert.True(Done(await StateAsync(admin), "address"));
        Assert.Equal("https://photos.example.com", ((SettingsService)app.Services.GetService(typeof(SettingsService))!).Email.SiteUrl);

        var check = await ReachCheckAsync(admin);
        Assert.NotEqual("off", check.GetProperty("status").GetString());

        await admin.PutAsJsonAsync("/api/admin/onboarding/address", new { url = "" });
        Assert.Equal("off", (await ReachCheckAsync(admin)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Testing_an_address_needs_one_and_changes_need_the_header()
    {
        var admin = await app.AdminAsync();
        var none = await admin.PostAsJsonAsync("/api/admin/onboarding/test", new { url = "not an address at all" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/onboarding")).StatusCode);

        var noHeader = app.CreateClient();
        var r = await noHeader.PutAsJsonAsync("/api/admin/onboarding", new { wizardDone = true });
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    static async Task<System.Text.Json.JsonElement> ReachCheckAsync(HttpClient admin) =>
        (await (await admin.GetAsync("/api/admin/health")).ReadAsync()).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "reach");
}

public class EmailReadyTests
{
    [Theory]
    [InlineData(true, "smtp.example.com", "a@example.com", true)]
    [InlineData(true, "", "a@example.com", false)]
    [InlineData(true, "smtp.example.com", "", false)]
    [InlineData(false, "smtp.example.com", "a@example.com", false)]
    public void Email_is_ready_only_when_on_and_complete(bool on, string host, string from, bool ready) =>
        Assert.Equal(ready, new EmailSettings { Enabled = on, Host = host, From = from }.Ready);
}
