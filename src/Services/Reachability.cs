using System.Net;
using System.Net.Sockets;

namespace PhotoGallery;

/// <summary>Asks "does the address people are meant to use actually reach this app?" and explains a "no" in plain words.</summary>
public class Reachability(AppConfig cfg, SettingsService settings)
{
    /// <summary>Sent on /healthz so a test can tell this app apart from some other site on the same address.</summary>
    public const string Header = "X-PhotoGallery";
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    /// <summary>status is ok | warn | bad | off, the same words the Overview uses.</summary>
    public record Result(string Status, string Value, string Detail, DateTime CheckedAt, string Url);

    readonly object gate = new();
    Result? last;
    bool running;
    /// <summary>Replaced in tests so no real request is made.</summary>
    public Func<HttpClient> Http { get; set; } = () => new HttpClient(new HttpClientHandler { MaxAutomaticRedirections = 3 }) { Timeout = TimeSpan.FromSeconds(6) };
    public Func<string, Task<IPAddress[]>> Resolve { get; set; } = host => Dns.GetHostAddressesAsync(host);

    /// <summary>The saved address, or null when none is set.</summary>
    public string? Saved => settings.Email.SiteUrl is { Length: > 0 } u ? u : null;

    /// <summary>The newest answer for the saved address, started in the background when it is older than a few minutes.</summary>
    public Result? Cached()
    {
        var url = Saved;
        if (url == null) return null;
        lock (gate)
        {
            if (last != null && last.Url == url && DateTime.UtcNow - last.CheckedAt < Fresh) return last;
            if (!running)
            {
                running = true;
                _ = Task.Run(async () => { try { await CheckAsync(url); } finally { lock (gate) running = false; } });
            }
            return last != null && last.Url == url ? last : null;
        }
    }

    /// <summary>An address that can be saved: http or https, a real host, no password, query or fragment. Null when it is not.</summary>
    public static string? Normalize(string? input)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0 || t.Length > 200) return null;
        if (!t.Contains("://")) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        if (u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0 || u.Host.Length == 0) return null;
        return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public async Task<Result> CheckAsync(string url)
    {
        var r = await RunAsync(url);
        lock (gate) if (url == Saved) last = r;
        return r;
    }

    async Task<Result> RunAsync(string url)
    {
        Result R(string s, string v, string d) => new(s, v, d, DateTime.UtcNow, url);
        if (Normalize(url) is not { } clean) return R("bad", "Not an address", "That is not a web address. Use something like https://photos.example.com.");
        url = clean;
        var uri = new Uri(url);
        var host = uri.Host;
        var localName = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') && !IPAddress.TryParse(host, out _);

        IPAddress[] addrs;
        try { addrs = IPAddress.TryParse(host, out var literal) ? [literal] : await Resolve(host); }
        catch { return R("bad", "Name not found", $"The name {host} does not point anywhere. Check the spelling, and that the DNS record for it exists (at your domain provider, or in your Cloudflare dashboard)."); }
        if (addrs.Length == 0) return R("bad", "Name not found", $"The name {host} does not point anywhere. Check that its DNS record exists.");
        var homeOnly = addrs.All(ProxyWatch.IsPrivate) || localName;

        try
        {
            using var http = Http();
            using var req = new HttpRequestMessage(HttpMethod.Get, url + "/healthz");
            req.Headers.UserAgent.ParseAdd("PhotoGallery/" + cfg.Version);
            using var resp = await http.SendAsync(req);
            var code = (int)resp.StatusCode;
            var ours = resp.Headers.Contains(Header);
            if (code is 502 or 503 or 504)
                return R("bad", "Proxy cannot reach the app", $"The address answers, but with error {code}: your proxy or tunnel is running, yet it cannot reach this app. Check that it points at this container's address and port, and that the container is running.");
            if (code == 403 || code == 401)
                return R("warn", "Blocked", $"The address answered with {code}. A proxy, firewall or sign-in page in front of the app is stopping the request. Visitors may be stopped too.");
            if (!resp.IsSuccessStatusCode || !ours)
                return R("bad", "A different site answers", $"{url} answers, but it is not this app (reply {code}). The address probably points at another service, or the proxy forwards to the wrong place.");
            if (homeOnly)
                return R("warn", "Home network only", $"The address reaches this app, but {host} is a private address, so it only works inside your home network. Friends and family outside will not be able to open it. To share outside, see Getting started, step 5.");
            if (uri.Scheme == Uri.UriSchemeHttp && resp.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                return R("warn", "Works, not encrypted", "The address reaches this app, but over plain http, so anything typed on it (such as gallery passwords) travels unprotected. Put https in front, for example with a Cloudflare Tunnel or SWAG.");
            return R("ok", "Answers", $"{url} reaches this app. This was tested from the server itself, so it proves the name, certificate and proxy are set up; ask a friend on mobile data to open it once to be certain it works from outside.");
        }
        catch (TaskCanceledException)
        {
            return R("bad", "No answer", $"{host} did not answer within a few seconds. The proxy, tunnel or port forward is probably switched off, or something is blocking the connection.");
        }
        catch (HttpRequestException e)
        {
            var inner = e.GetBaseException();
            if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return R("bad", "Connection refused", $"{host} is reachable, but nothing is listening on that port. Is the proxy or tunnel running?");
            if (inner is System.Security.Authentication.AuthenticationException || e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                return R("bad", "Certificate problem", $"The connection to {host} could not be made secure: the certificate is missing, expired, or issued for a different name. Fix the certificate in your proxy, or use the address its certificate was issued for.");
            return R("bad", "Cannot connect", $"Could not connect to {host}. The proxy or tunnel may be off, or a firewall may be in the way.");
        }
        catch
        {
            return R("bad", "Cannot connect", $"Could not connect to {host}.");
        }
    }
}
