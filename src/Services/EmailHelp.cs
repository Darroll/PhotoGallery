using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;

namespace PhotoGallery;

/// <summary>One-click starting points for the email settings, and plain-language explanations of what went wrong.</summary>
public static class EmailHelp
{
    public record Preset(string Id, string Label, string Host, int Port, string Security, bool NeedsLogin, string Steps, string? Link);

    public static readonly Preset[] Presets =
    [
        new("gmail", "Gmail", "smtp.gmail.com", 587, "starttls", true,
            "Turn on 2-step verification for your Google account, then create an app password (a 16-letter code made just for this app). Use your Gmail address as the user name and the app password as the password. Your normal Gmail password will not work.",
            "https://myaccount.google.com/apppasswords"),
        new("outlook", "Outlook.com or Microsoft 365", "smtp-mail.outlook.com", 587, "starttls", true,
            "Use your full Outlook address as the user name. If you use two-step sign-in, create an app password in your Microsoft account security settings and use that as the password. Some Microsoft 365 work accounts have sending from apps switched off by their administrator.",
            "https://account.live.com/proofs/AppPassword"),
        new("yahoo", "Yahoo Mail", "smtp.mail.yahoo.com", 587, "starttls", true,
            "Create an app password in your Yahoo account security settings and use it as the password, with your Yahoo address as the user name.",
            "https://login.yahoo.com/account/security"),
        new("relay", "A mail relay on my network (no sign-in)", "", 25, "none", false,
            "For a mail server at home or at work that accepts mail from this computer without a password, such as a relay container on your server. Enter its address and port.",
            null),
        new("custom", "Something else", "", 587, "starttls", true,
            "Your email provider's help pages list the outgoing (SMTP) server name, port and whether it needs a secure connection. Most use port 587 with a secure connection and your account's user name and password.",
            null),
    ];

    /// <summary>Turns a failed send into a sentence a non-technical person can act on.</summary>
    public static string Explain(Exception ex, string host, int port, string security, string username)
    {
        var inner = ex;
        while (inner.InnerException != null && inner is not SocketException and not AuthenticationException) inner = inner.InnerException;
        var raw = (ex.InnerException?.Message ?? ex.Message).Trim();
        var where = $"\"{host}\" on port {port}";

        if (string.IsNullOrWhiteSpace(host)) return "No mail server is set yet. Pick your email provider above, or type the server name.";
        if (inner is SocketException se)
            return se.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                    $"The mail server name \"{host}\" was not found. Check the spelling, or choose your provider from the list.",
                SocketError.ConnectionRefused =>
                    $"{where} is not accepting connections. Check the port number, and that the mail server is running.",
                SocketError.TimedOut =>
                    $"{where} did not answer in time. Check the server name and port, and that a firewall is not blocking outgoing mail from this computer (some internet providers block port 25; use 587 with a provider such as Gmail).",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                    $"This computer cannot reach {where}. Check its internet or network connection.",
                _ => $"Could not connect to {where}: {se.Message}"
            };
        if (inner is AuthenticationException)
            return $"A secure connection to {where} could not be set up: its security certificate was not accepted. Check the server name matches your provider, or try the other security choice.";
        if (inner is TimeoutException || raw.Contains("timed out", StringComparison.OrdinalIgnoreCase) || raw.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return security == "none"
                ? $"{where} did not answer in time. If your provider needs a secure connection (most do), turn on \"Use a secure connection\"; otherwise check the server name and port."
                : $"{where} did not answer in time. Check the server name and port, or try again in a moment.";

        var m = raw.ToLowerInvariant();
        if (security == "none" && username.Length > 0 && m.Contains("client was not authenticated") && !m.Contains("starttls"))
            return "The mail server refused the sign-in. Check the user name and password, and turn on \"Use a secure connection\", which most providers need before they accept a password.";
        if (m.Contains("starttls") || security == "none" && (m.Contains("secure connection") || m.Contains("ssl") && m.Contains("required")))
            return "The mail server requires a secure connection. Turn on \"Use a secure connection\" (usually with port 587).";
        var wantsSignIn = m.Contains("authentication required") || m.Contains("authentication is required") || m.Contains("not authenticated") || m.Contains("5.7.0") && m.Contains("auth");
        if (wantsSignIn && string.IsNullOrEmpty(username))
            return "The mail server wants you to sign in first. Enter your user name and password below.";
        if (wantsSignIn || m.Contains("535") || m.Contains("username and password not accepted") || m.Contains("badcredentials") || m.Contains("authentication unsuccessful")
            || m.Contains("authentication failed") || m.Contains("invalid credentials") || m.Contains("5.7.8"))
            return "The mail server did not accept the user name or password. " + (host.Contains("gmail") || host.Contains("google")
                ? "Gmail needs an app password, not your usual password; the steps are shown above."
                : "Check both for typos. Many providers need an app password instead of your usual one.");
        if (m.Contains("sendasdenied") || m.Contains("not authorized to send") || m.Contains("sender address rejected") || m.Contains("5.7.60") || m.Contains("5.2.252") || m.Contains("5.7.1") && m.Contains("sender"))
            return "The mail server will not send as that \"Send from\" address. Use the same address you sign in with.";
        if (m.Contains("relay") && (m.Contains("denied") || m.Contains("not permitted") || m.Contains("prohibited")) || m.Contains("5.7.1"))
            return "The mail server refused to pass this message on. It probably needs you to sign in, or does not allow mail from this computer.";
        if (m.Contains("user unknown") || m.Contains("mailbox unavailable") || m.Contains("no such user") || m.Contains("5.1.1") || m.Contains("recipient") && m.Contains("rejected"))
            return "The mail server says the \"Send alerts to\" address does not exist. Check it for typos.";
        if (ex is FormatException || m.Contains("mailbox syntax") || m.Contains("invalid address"))
            return "One of the email addresses does not look right. Check \"Send from\" and \"Send alerts to\".";
        return $"The mail server said: {raw}";
    }

    public static bool Valid(string? address) => !string.IsNullOrWhiteSpace(address) && MailAddress.TryCreate(address, out _);
}
