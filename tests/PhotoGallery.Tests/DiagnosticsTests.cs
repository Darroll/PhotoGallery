using System.Net;
using Microsoft.Extensions.Logging;

namespace PhotoGallery.Tests;

public class DiagnosticsTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Diagnostics_need_an_admin()
    {
        var r = await app.NewClient().GetAsync("/api/admin/diagnostics");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_describe_the_install_without_secrets()
    {
        var admin = await app.AdminAsync();
        var r = await admin.GetAsync("/api/admin/diagnostics");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var text = (await r.ReadAsync()).GetProperty("text").GetString()!;
        Assert.Contains("## App", text);
        Assert.Contains("Library readable: True", text);
        Assert.Contains("Admin password in container: set", text);
        Assert.DoesNotContain(AppFactory.AdminPassword, text);
        Assert.DoesNotContain("gallery@example.com", text);
    }

    [Theory]
    [InlineData("Sign-in failed for jane.doe@example.com today", "<email>")]
    [InlineData("password=hunter2 rejected", "password=<removed>")]
    [InlineData("Setup code is ABC123", "code is <removed>")]
    [InlineData("OIDC client_secret: s3cr3t failed", "secret: <removed>")]
    public void Redact_removes_emails_and_secrets(string input, string expected)
    {
        var o = Diagnostics.Redact(input);
        Assert.Contains(expected, o);
        Assert.DoesNotContain("jane.doe", o); Assert.DoesNotContain("hunter2", o); Assert.DoesNotContain("ABC123", o); Assert.DoesNotContain("s3cr3t", o);
    }

    [Fact]
    public void Recent_log_keeps_only_warnings_and_errors_and_is_capped()
    {
        var log = new RecentLog();
        var l = log.CreateLogger("PhotoGallery.Test");
        l.LogInformation("ignored");
        for (int i = 0; i < 100; i++) l.LogWarning("warning {I}", i);
        l.LogError(new InvalidOperationException("boom"), "failed");
        var all = log.Snapshot();
        Assert.True(all.Count <= 60);
        Assert.DoesNotContain(all, e => e.Message == "ignored");
        Assert.Contains("InvalidOperationException: boom", all.Last().Message);
    }
}
