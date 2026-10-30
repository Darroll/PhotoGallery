using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace PhotoGallery.Tests;

/// <summary>A tiny SMTP server that asks for AUTH LOGIN and records what it was given.</summary>
sealed class StubSmtp : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public string? User, Password, Rcpt;
    public bool Delivered;
    public bool AcceptPassword = true;
    readonly Task loop;

    public StubSmtp()
    {
        listener.Start();
        loop = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        });
    }

    async Task Serve(TcpClient c)
    {
        using var _ = c;
        using var stream = c.GetStream();
        using var r = new StreamReader(stream, Encoding.ASCII);
        using var w = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await w.WriteLineAsync("220 stub ready");
        while (await r.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase)) { await w.WriteLineAsync("250-stub"); await w.WriteLineAsync("250 AUTH LOGIN"); }
            else if (line.StartsWith("AUTH LOGIN", StringComparison.OrdinalIgnoreCase))
            {
                var inline = line.Length > 11 ? line[11..].Trim() : "";   // .NET may send the user name along with the command
                if (inline.Length == 0) { await w.WriteLineAsync("334 VXNlcm5hbWU6"); inline = (await r.ReadLineAsync())!; }
                User = Encoding.UTF8.GetString(Convert.FromBase64String(inline));
                await w.WriteLineAsync("334 UGFzc3dvcmQ6");
                Password = Encoding.UTF8.GetString(Convert.FromBase64String((await r.ReadLineAsync())!));
                await w.WriteLineAsync(AcceptPassword ? "235 2.7.0 Accepted" : "535 5.7.8 Username and Password not accepted");
            }
            else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase)) await w.WriteLineAsync(AcceptPassword ? "250 ok" : "530 5.7.0 Authentication Required");   // .NET carries on after a refused sign-in
            else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase)) { Rcpt = line; await w.WriteLineAsync("250 ok"); }
            else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
            {
                await w.WriteLineAsync("354 go");
                while (await r.ReadLineAsync() is { } d && d != ".") { }
                Delivered = true;
                await w.WriteLineAsync("250 queued");
            }
            else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase)) { await w.WriteLineAsync("221 bye"); return; }
            else await w.WriteLineAsync("250 ok");
        }
    }

    public void Dispose() => listener.Stop();
}

public class EmailSetupTests(AppFactory app) : IClassFixture<AppFactory>
{
    static object Body(string host, int port, string user = "", string password = "", string security = "none", string preset = "") =>
        new { enabled = true, host, port, security, username = user, password, preset, from = "gallery@example.test", to = "owner@example.test" };

    [Fact]
    public async Task Presets_cover_the_common_providers_with_instructions()
    {
        var admin = await app.AdminAsync();
        var list = await (await admin.GetAsync("/api/admin/settings/email/presets")).ReadAsync();
        var gmail = list.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "gmail");
        Assert.Equal("smtp.gmail.com", gmail.GetProperty("host").GetString());
        Assert.Equal(587, gmail.GetProperty("port").GetInt32());
        Assert.Equal("starttls", gmail.GetProperty("security").GetString());
        Assert.Contains("app password", gmail.GetProperty("steps").GetString());
        Assert.Contains(list.EnumerateArray(), p => p.GetProperty("id").GetString() == "outlook");
        Assert.Contains(list.EnumerateArray(), p => p.GetProperty("id").GetString() == "relay" && !p.GetProperty("needsLogin").GetBoolean());
    }

    [Fact]
    public async Task The_password_is_stored_but_never_sent_back_and_an_empty_one_keeps_it()
    {
        var admin = await app.AdminAsync();
        var save = await admin.PutAsJsonAsync("/api/admin/settings/email", Body("smtp.example.test", 587, "me@example.test", "abcd efgh ijkl mnop", "starttls", "custom"));
        var saved = await save.ReadAsync();
        Assert.True(saved.GetProperty("passwordSet").GetBoolean());
        Assert.False(saved.TryGetProperty("password", out _));

        var again = await admin.PutAsJsonAsync("/api/admin/settings/email", Body("smtp.example.test", 587, "me@example.test", "", "starttls", "custom"));
        Assert.True((await again.ReadAsync()).GetProperty("passwordSet").GetBoolean());
        var read = await (await admin.GetAsync("/api/admin/settings/email")).ReadAsync();
        Assert.True(read.GetProperty("passwordSet").GetBoolean());
        Assert.Equal("starttls", read.GetProperty("security").GetString());
        Assert.DoesNotContain("abcd", await (await admin.GetAsync("/api/admin/settings/email")).Content.ReadAsStringAsync());

        // a different user name must not inherit the old password
        var other = await admin.PutAsJsonAsync("/api/admin/settings/email", Body("smtp.example.test", 587, "someone@else.test", "", "starttls"));
        Assert.False((await other.ReadAsync()).GetProperty("passwordSet").GetBoolean());
    }

    [Fact]
    public async Task The_test_button_signs_in_and_delivers()
    {
        using var smtp = new StubSmtp();
        var admin = await app.AdminAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/settings/email/test", Body("127.0.0.1", smtp.Port, "me@example.test", "secret-pass"));
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        Assert.Equal("me@example.test", smtp.User);
        Assert.Equal("secret-pass", smtp.Password);
        Assert.True(smtp.Delivered);
        Assert.Contains("owner@example.test", smtp.Rcpt);
    }

    [Fact]
    public async Task The_test_button_can_use_the_saved_password_and_explains_a_refusal()
    {
        using var smtp = new StubSmtp { AcceptPassword = false };
        var admin = await app.AdminAsync();
        await admin.PutAsJsonAsync("/api/admin/settings/email", Body("127.0.0.1", smtp.Port, "me@example.test", "saved-pass"));
        var r = await admin.PostAsJsonAsync("/api/admin/settings/email/test", Body("127.0.0.1", smtp.Port, "me@example.test", ""));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("saved-pass", smtp.Password);
        Assert.Contains("user name and password", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_test_button_explains_problems_before_and_while_connecting()
    {
        var admin = await app.AdminAsync();
        var none = await admin.PostAsJsonAsync("/api/admin/settings/email/test", Body("", 25));
        Assert.Contains("No mail server is set", await none.Content.ReadAsStringAsync());

        var noPass = await admin.PostAsJsonAsync("/api/admin/settings/email/test", Body("127.0.0.1", 9, "who@example.test", ""));
        Assert.Contains("Enter the password", await noPass.Content.ReadAsStringAsync());

        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var closed = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var refused = await admin.PostAsJsonAsync("/api/admin/settings/email/test", Body("127.0.0.1", closed));
        Assert.Contains("is not accepting connections", await refused.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("The SMTP server requires a secure connection or the client was not authenticated. The server response was: Must issue a STARTTLS command first", "none", "me@x.test", "secure connection")]
    [InlineData("The SMTP server requires a secure connection or the client was not authenticated. The server response was: 5.7.0 Authentication Required", "starttls", "", "sign in first")]
    [InlineData("535-5.7.8 Username and Password not accepted", "starttls", "me@gmail.com", "app password")]
    [InlineData("5.7.60 SMTP; Client does not have permissions to send as this sender", "starttls", "a@b.test", "same address you sign in with")]
    [InlineData("5.7.1 Relay access denied", "none", "", "refused to pass this message on")]
    [InlineData("Mailbox unavailable. The server response was: 5.1.1 User unknown", "none", "", "does not exist")]
    [InlineData("something very odd", "none", "", "The mail server said: something very odd")]
    public void Server_replies_are_explained_in_plain_words(string reply, string security, string user, string expected)
    {
        var host = user.EndsWith("gmail.com") ? "smtp.gmail.com" : "smtp.example.test";
        var text = EmailHelp.Explain(new System.Net.Mail.SmtpException(reply), host, 587, security, user);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void Network_failures_are_explained_in_plain_words()
    {
        var notFound = EmailHelp.Explain(new System.Net.Mail.SmtpException("Failure", new SocketException((int)SocketError.HostNotFound)), "smtp.nowhere.test", 587, "starttls", "");
        Assert.Contains("\"smtp.nowhere.test\" was not found", notFound);
        var timeout = EmailHelp.Explain(new System.Net.Mail.SmtpException("Failure", new SocketException((int)SocketError.TimedOut)), "mail.example.test", 25, "none", "");
        Assert.Contains("did not answer in time", timeout);
        var cert = EmailHelp.Explain(new System.Net.Mail.SmtpException("Failure", new System.Security.Authentication.AuthenticationException("bad cert")), "mail.example.test", 587, "starttls", "");
        Assert.Contains("security certificate", cert);
    }

    [Fact]
    public void Google_app_passwords_lose_the_spaces_they_are_shown_with()
    {
        var s = new EmailSettings { Host = "smtp.gmail.com", Password = "abcd efgh ijkl mnop" }.Sanitized();
        Assert.Equal("abcdefghijklmnop", s.Password);
        Assert.Equal("a b", new EmailSettings { Host = "mail.example.test", Password = "a b" }.Sanitized().Password);
    }
}
