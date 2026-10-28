using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery.Tests;

/// <summary>Alerts that are queued in the outbox when something happens (sending is a background job, so these tests read the outbox).</summary>
public class EmailAlertTests(AppFactory app) : IClassFixture<AppFactory>
{
    static async Task ConfigureAsync(HttpClient admin, bool enabled, bool loginAlerts)
    {
        var r = await admin.PutAsJsonAsync("/api/admin/settings/email", new { enabled, host = "127.0.0.1", port = 25, from = "gallery@example.test", to = "owner@example.test", loginAlerts });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    async Task<int> OutboxCountAsync() => await app.WithDbAsync(db => db.EmailOutbox.CountAsync());

    async Task WrongPasswordAsync()
    {
        var r = await app.NewClient().PostAsJsonAsync("/api/admin/login", new { password = "not-the-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task A_failed_admin_sign_in_queues_an_alert_to_the_owner()
    {
        var admin = await app.AdminAsync();
        await ConfigureAsync(admin, enabled: true, loginAlerts: true);
        var before = await OutboxCountAsync();

        await WrongPasswordAsync();

        Assert.Equal(before + 1, await OutboxCountAsync());
        var mail = await app.WithDbAsync(db => db.EmailOutbox.OrderByDescending(e => e.Id).FirstAsync());
        Assert.Equal("owner@example.test", mail.ToAddr);
        Assert.Contains("Admin login: wrong password", mail.Subject);
        Assert.Contains("failed admin sign-in", mail.Body);
    }

    [Fact]
    public async Task Nothing_is_queued_when_login_alerts_are_off()
    {
        var admin = await app.AdminAsync();
        await ConfigureAsync(admin, enabled: true, loginAlerts: false);
        var before = await OutboxCountAsync();

        await WrongPasswordAsync();

        Assert.Equal(before, await OutboxCountAsync());
    }

    [Fact]
    public async Task Nothing_is_queued_when_email_is_switched_off()
    {
        var admin = await app.AdminAsync();
        await ConfigureAsync(admin, enabled: false, loginAlerts: true);
        var before = await OutboxCountAsync();

        await WrongPasswordAsync();

        Assert.Equal(before, await OutboxCountAsync());
    }
}
