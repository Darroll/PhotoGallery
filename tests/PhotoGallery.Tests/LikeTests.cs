using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PhotoGallery.Tests;

/// <summary>The test server has no network address of its own, so a request may name the one it comes from.</summary>
class TestAddressFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, n) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-Ip", out var v) && IPAddress.TryParse(v, out var ip)) ctx.Connection.RemoteIpAddress = ip;
            return n();
        });
        next(app);
    };
}

public class LikesFactory : AppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddTransient<IStartupFilter, TestAddressFilter>());
    }

    /// <summary>A visitor at a network address; sign in as a provider account by passing a subject.</summary>
    public Visitor VisitorAt(string ip, string? sub = null) => new(this, ip, sub);

    public string SessionCookie(string sub)
    {
        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var id = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, sub), new Claim(ClaimTypes.Name, "Person " + sub)], CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(id), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, CookieAuthenticationDefaults.AuthenticationScheme);
        return "pg_session=" + options.TicketDataFormat.Protect(ticket);
    }
}

public class Visitor
{
    readonly LikesFactory app;
    readonly HttpClient client;
    string? browser;      // the anonymous visitor cookie, kept like a browser would
    string? session;      // the sign-in cookie

    public Visitor(LikesFactory app, string ip, string? sub)
    {
        this.app = app;
        client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Requested-With", "pg");
        client.DefaultRequestHeaders.Add("X-Test-Ip", ip);
        if (sub != null) SignIn(sub);
    }

    public void SignIn(string sub) => session = app.SessionCookie(sub);
    public void ClearCookies() { browser = null; }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        var cookies = new List<string>();
        if (browser != null) cookies.Add(browser);
        if (session != null) cookies.Add(session);
        if (cookies.Count > 0) req.Headers.Add("Cookie", string.Join("; ", cookies));
        if (body != null) req.Content = JsonContent.Create(body);
        var r = await client.SendAsync(req);
        if (r.Headers.TryGetValues("Set-Cookie", out var set))
            foreach (var c in set.Select(v => v.Split(';')[0]).Where(v => v.StartsWith("pgv="))) browser = c;
        return r;
    }

    public async Task<int?> LikeAsync(string slug, int photoId, bool on = true)
    {
        var r = await SendAsync(HttpMethod.Post, $"/api/g/{slug}/favorite/{photoId}", new { on });
        r.EnsureSuccessStatusCode();
        return (await r.ReadAsync()).GetProperty("likes").GetInt32();
    }

    public Task<HttpResponseMessage> LikeRawAsync(string slug, int photoId) => SendAsync(HttpMethod.Post, $"/api/g/{slug}/favorite/{photoId}", new { on = true });

    public async Task<System.Text.Json.JsonElement> GalleryAsync(string slug) => await (await SendAsync(HttpMethod.Get, $"/api/g/{slug}")).ReadAsync();

    public async Task<int> LikesOnAsync(string slug, int photoId) =>
        (await GalleryAsync(slug)).GetProperty("photos").EnumerateArray().First(p => p.GetProperty("id").GetInt32() == photoId).GetProperty("likes").GetInt32();
}

public class LikeTests(LikesFactory app) : IClassFixture<LikesFactory>
{
    async Task<(string slug, int[] photos, int galleryId)> NewGalleryAsync(string title)
    {
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder = app.NewFolder(2), visibility = 0 });
        var g = (await r.ReadAsync()).GetProperty("gallery");
        var slug = g.GetProperty("slug").GetString()!;
        return (slug, await AppFactory.PhotoIdsAsync(admin, slug), g.GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:1:2:3:4", "2001:db8:1:2::/64")]
    public void An_address_counts_as_its_network(string ip, string expected) => Assert.Equal(expected, Likes.Network(ip));

    [Fact]
    public async Task One_anonymous_like_counts_per_address_and_clearing_cookies_does_not_add_another()
    {
        var (slug, photos, _) = await NewGalleryAsync("Likes By Address");
        var photo = photos[0];

        var anna = app.VisitorAt("198.51.100.1");
        Assert.Equal(1, await anna.LikeAsync(slug, photo));

        // a second browser on the same address, and the same browser after clearing its cookies: still one like
        var annaPhone = app.VisitorAt("198.51.100.1");
        Assert.Equal(1, await annaPhone.LikeAsync(slug, photo));
        anna.ClearCookies();
        Assert.Equal(1, await anna.LikeAsync(slug, photo));

        // somebody on another network is another person
        var ben = app.VisitorAt("198.51.100.2");
        Assert.Equal(2, await ben.LikeAsync(slug, photo));

        // the same household taking their like back does not remove the other person's
        Assert.Equal(2, await annaPhone.LikeAsync(slug, photo, on: false)); // anna's other browser still has its heart
        Assert.Equal(2, await ben.LikesOnAsync(slug, photo) + (await anna.LikesOnAsync(slug, photo) - await ben.LikesOnAsync(slug, photo)));
        Assert.Equal(0, await app.VisitorAt("198.51.100.3").LikesOnAsync(slug, photos[1]));
    }

    [Fact]
    public async Task A_signed_in_person_counts_once_on_any_network_and_two_people_on_one_address_count_twice()
    {
        var (slug, photos, _) = await NewGalleryAsync("Likes By Person");
        var photo = photos[0];

        var home = app.VisitorAt("192.0.2.10", sub: "alice");
        var work = app.VisitorAt("192.0.2.77", sub: "alice");
        Assert.Equal(1, await home.LikeAsync(slug, photo));
        Assert.Equal(1, await work.LikeAsync(slug, photo));          // same account, other network

        var bob = app.VisitorAt("192.0.2.10", sub: "bob");           // another account on alice's home address
        Assert.Equal(2, await bob.LikeAsync(slug, photo));

        var guest = app.VisitorAt("192.0.2.10");                     // and a guest on that address
        Assert.Equal(3, await guest.LikeAsync(slug, photo));
    }

    [Fact]
    public async Task A_guest_who_signs_in_keeps_their_like_and_is_not_counted_twice()
    {
        var (slug, photos, _) = await NewGalleryAsync("Likes After Sign In");
        var photo = photos[0];

        var visitor = app.VisitorAt("192.0.2.50");
        Assert.Equal(1, await visitor.LikeAsync(slug, photo));
        visitor.SignIn("carol");                                       // same browser, now signed in
        Assert.Equal(1, await visitor.LikesOnAsync(slug, photo));      // the like moved to the account

        // carol is now one person; a guest on the same address is another
        Assert.Equal(2, await app.VisitorAt("192.0.2.50").LikeAsync(slug, photo));
        // and carol on a new network adds nothing
        Assert.Equal(2, await app.VisitorAt("192.0.2.99", sub: "carol").LikeAsync(slug, photo));
    }

    [Fact]
    public async Task The_gallery_can_switch_hearts_and_the_counts_off()
    {
        var (slug, photos, id) = await NewGalleryAsync("Likes Switches");
        var admin = await app.AdminAsync();
        var photo = photos[0];
        var fan = app.VisitorAt("192.0.2.20");
        await fan.LikeAsync(slug, photo);

        // counts hidden from visitors, still visible to the admin
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { showLikeCounts = false });
        var seen = await fan.GalleryAsync(slug);
        Assert.False(seen.GetProperty("showLikeCounts").GetBoolean());
        Assert.Equal(0, await fan.LikesOnAsync(slug, photo));
        Assert.Equal(0, await app.VisitorAt("192.0.2.21").LikeAsync(slug, photo));   // the answer to a heart does not reveal the count either
        var adminView = await (await admin.GetAsync($"/api/g/{slug}")).ReadAsync();
        Assert.True(adminView.GetProperty("showLikeCounts").GetBoolean());
        Assert.Equal(2, adminView.GetProperty("photos").EnumerateArray().First(p => p.GetProperty("id").GetInt32() == photo).GetProperty("likes").GetInt32());

        // hearts off: nothing can be liked
        await admin.PutAsJsonAsync($"/api/admin/galleries/{id}", new { allowLikes = false });
        Assert.False((await fan.GalleryAsync(slug)).GetProperty("allowLikes").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.VisitorAt("192.0.2.22").LikeRawAsync(slug, photos[1])).StatusCode);
    }

    [Fact]
    public async Task New_galleries_have_hearts_and_counts_on()
    {
        var (slug, _, _) = await NewGalleryAsync("Likes Defaults");
        var g = await app.VisitorAt("192.0.2.30").GalleryAsync(slug);
        Assert.True(g.GetProperty("allowLikes").GetBoolean());
        Assert.True(g.GetProperty("showLikeCounts").GetBoolean());
    }
}
