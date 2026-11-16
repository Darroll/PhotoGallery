using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoGallery.Tests;

public class SubscribeTests(AppFactory app) : IClassFixture<AppFactory>
{
    const string Reader = "reader@example.test";

    static async Task ConfigureAsync(HttpClient admin, bool enabled = true, string siteUrl = "http://localhost", string mail = "now")
    {
        var r = await admin.PutAsJsonAsync("/api/admin/settings/email", new { enabled, host = "127.0.0.1", port = 25, from = "gallery@example.test", to = "owner@example.test", siteUrl, subscribers = true, subscriberMail = mail });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    async Task<List<EmailOutbox>> MailToAsync(string address) =>
        await app.WithDbAsync(db => db.EmailOutbox.AsNoTracking().Where(e => e.ToAddr == address).OrderBy(e => e.Id).ToListAsync());

    async Task<(int id, string slug)> CreateAsync(HttpClient admin, string title, string folder, int visibility = 0)
    {
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder, visibility });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var g = (await r.ReadAsync()).GetProperty("gallery");
        return (g.GetProperty("id").GetInt32(), g.GetProperty("slug").GetString()!);
    }

    [Fact]
    public async Task A_reader_subscribes_confirms_gets_notices_and_unsubscribes()
    {
        var admin = await app.AdminAsync();
        await ConfigureAsync(admin);
        var anon = app.NewClient();

        // 1. sign up: the same answer either way, and a confirmation link goes out
        var sub = await anon.PostAsJsonAsync("/api/subscribe", new { email = Reader });
        Assert.Equal(HttpStatusCode.Accepted, sub.StatusCode);
        var confirmMail = (await MailToAsync(Reader)).Single();
        Assert.StartsWith("Confirm your subscription", confirmMail.Subject);
        var token = Regex.Match(confirmMail.Body, @"/subscribe/confirm/([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));

        // 2. unconfirmed readers get nothing
        var (_, _) = await CreateAsync(admin, "Journey Before Confirm", app.NewFolder(1));
        Assert.Empty((await MailToAsync(Reader)).Where(m => m.Subject.Contains("Journey Before Confirm")));

        // 3. confirming turns notices on
        var confirm = await anon.GetAsync($"/subscribe/confirm/{token}");
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Equal("/?subscribed=1", confirm.Headers.Location?.OriginalString);

        var publicFolder = app.NewFolder(1);
        var (publicId, publicSlug) = await CreateAsync(admin, "Journey Public", publicFolder);
        var notice = (await MailToAsync(Reader)).Single(m => m.Subject == "New gallery: Journey Public");
        Assert.Contains($"http://localhost/g/{publicSlug}", notice.Body);
        Assert.Contains($"/unsubscribe/{token}", notice.Body);

        // 4. private galleries are never announced
        await CreateAsync(admin, "Journey Private", app.NewFolder(1), visibility: 2);
        Assert.DoesNotContain((await MailToAsync(Reader)), m => m.Subject.Contains("Journey Private"));

        // 5. new photos in a public gallery are announced once per scan
        TestImages.Write(Path.Combine(app.Library, publicFolder, "new-arrival.jpg"), 320, 240, 150);
        var scan = await admin.PostAsync($"/api/admin/galleries/{publicId}/scan", null);
        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        var added = (await MailToAsync(Reader)).Single(m => m.Subject == "1 new photo in Journey Public");
        Assert.Contains($"http://localhost/g/{publicSlug}", added.Body);

        // 6. unsubscribing stops everything after it
        var unsub = await anon.GetAsync($"/unsubscribe/{token}");
        Assert.Equal("/?unsubscribed=1", unsub.Headers.Location?.OriginalString);
        var before = (await MailToAsync(Reader)).Count;
        await CreateAsync(admin, "Journey After Unsubscribe", app.NewFolder(1));
        Assert.Equal(before, (await MailToAsync(Reader)).Count);
    }

    [Fact]
    public async Task Subscribing_twice_or_with_an_unknown_token_is_harmless()
    {
        var admin = await app.AdminAsync();
        await ConfigureAsync(admin);
        var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/subscribe", new { email = "twice@example.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/subscribe", new { email = "TWICE@example.test" })).StatusCode);
        Assert.Equal("/?subscribed=0", (await anon.GetAsync("/subscribe/confirm/not-a-real-token")).Headers.Location?.OriginalString);
        Assert.Equal("/?unsubscribed=1", (await anon.GetAsync("/unsubscribe/not-a-real-token")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Subscriber_counts_are_for_the_admin_only()
    {
        var admin = await app.AdminAsync();
        var counts = await (await admin.GetAsync("/api/admin/subscribers/count")).ReadAsync();
        Assert.True(counts.GetProperty("confirmed").GetInt32() >= 0);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync("/api/admin/subscribers/count")).StatusCode);
    }

    [Fact]
    public async Task The_site_says_whether_subscriptions_are_offered()
    {
        var admin = await app.AdminAsync();
        await ConfigureAsync(admin);
        Assert.True((await (await app.NewClient().GetAsync("/api/site")).ReadAsync()).GetProperty("subscribe").GetBoolean());
        await ConfigureAsync(admin, enabled: false);
        try
        {
            Assert.False((await (await app.NewClient().GetAsync("/api/site")).ReadAsync()).GetProperty("subscribe").GetBoolean());
        }
        finally
        {
            await ConfigureAsync(admin);
        }
    }
    async Task<string> ConfirmedReaderAsync(string address)
    {
        await app.NewClient().PostAsJsonAsync("/api/subscribe", new { email = address });
        var token = Regex.Match((await MailToAsync(address)).Single().Body, @"/subscribe/confirm/([A-Za-z0-9_-]+)").Groups[1].Value;
        await app.NewClient().GetAsync($"/subscribe/confirm/{token}");
        return token;
    }

    Task DigestAsync() => app.WithDbAsync(async db => { await Subscribers.SendDigestAsync(db, app.Services.GetRequiredService<SettingsService>()); return 0; });

    [Fact]
    public async Task A_daily_digest_is_one_email_with_everything_new_and_not_one_per_change()
    {
        const string who = "digest-daily@example.test";
        var admin = await app.AdminAsync();
        await ConfigureAsync(admin, mail: "daily");
        await ConfirmedReaderAsync(who);

        var folder = app.NewFolder(1);
        var (id, slug) = await CreateAsync(admin, "Digest Alpha", folder);
        var (_, slugB) = await CreateAsync(admin, "Digest Beta", app.NewFolder(1));
        TestImages.Write(Path.Combine(app.Library, folder, "more1.jpg"), 320, 240, 100);
        TestImages.Write(Path.Combine(app.Library, folder, "more2.jpg"), 320, 240, 120);
        await admin.PostAsync($"/api/admin/galleries/{id}/scan", null);
        Assert.Single(await MailToAsync(who));   // only the confirmation: nothing is sent per change

        await DigestAsync();
        var mail = (await MailToAsync(who)).Skip(1).Single();
        Assert.Equal("What's new on " + app.Services.GetRequiredService<SettingsService>().SiteName, mail.Subject);
        Assert.Contains("Digest Alpha", mail.Body);
        Assert.Contains($"http://localhost/g/{slugB}", mail.Body);
        Assert.Contains("/unsubscribe/", mail.Body);

        await DigestAsync();
        Assert.Equal(2, (await MailToAsync(who)).Count);   // nothing new, nothing sent
    }

    [Fact]
    public async Task A_digest_waits_for_its_interval_and_the_weekly_one_waits_longer()
    {
        const string who = "digest-wait@example.test";
        var admin = await app.AdminAsync();
        await ConfigureAsync(admin, mail: "daily");
        await ConfirmedReaderAsync(who);
        var folder = app.NewFolder(1);
        var (id, _) = await CreateAsync(admin, "Digest Wait", folder);
        await DigestAsync();
        var first = (await MailToAsync(who)).Count;

        TestImages.Write(Path.Combine(app.Library, folder, "later.jpg"), 320, 240, 100);
        await admin.PostAsync($"/api/admin/galleries/{id}/scan", null);
        await DigestAsync();
        Assert.Equal(first, (await MailToAsync(who)).Count);   // sent a moment ago: this waits for tomorrow

        await app.WithDbAsync(async db => { (await db.AlertStates.SingleAsync(a => a.Key == "digest-sent")).At = DateTime.UtcNow.AddHours(-25); await db.SaveChangesAsync(); return 0; });
        await DigestAsync();
        Assert.Equal(first + 1, (await MailToAsync(who)).Count);

        await ConfigureAsync(admin, mail: "weekly");
        TestImages.Write(Path.Combine(app.Library, folder, "much-later.jpg"), 320, 240, 90);
        await admin.PostAsync($"/api/admin/galleries/{id}/scan", null);
        await app.WithDbAsync(async db => { (await db.AlertStates.SingleAsync(a => a.Key == "digest-sent")).At = DateTime.UtcNow.AddDays(-3); await db.SaveChangesAsync(); return 0; });
        await DigestAsync();
        Assert.Equal(first + 1, (await MailToAsync(who)).Count);   // three days is not a week
    }

}

public class SubscribeValidationTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Bad_addresses_and_missing_headers_are_refused()
    {
        var admin = await app.AdminAsync();
        await admin.PutAsJsonAsync("/api/admin/settings/email", new { enabled = true, host = "127.0.0.1", port = 25, from = "g@example.test", to = "o@example.test", siteUrl = "http://localhost", subscribers = true });
        var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/subscribe", new { email = "not-an-address" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/subscribe", new { email = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.CreateClient().PostAsJsonAsync("/api/subscribe", new { email = "x@example.test" })).StatusCode);
    }

    [Fact]
    public async Task Nothing_is_offered_until_the_site_address_is_set()
    {
        var admin = await app.AdminAsync();
        await admin.PutAsJsonAsync("/api/admin/settings/email", new { enabled = true, host = "127.0.0.1", port = 25, from = "g@example.test", to = "o@example.test", siteUrl = "", subscribers = true });
        var r = await app.NewClient().PostAsJsonAsync("/api/subscribe", new { email = "needs-url@example.test" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False((await (await app.NewClient().GetAsync("/api/site")).ReadAsync()).GetProperty("subscribe").GetBoolean());
    }

}
