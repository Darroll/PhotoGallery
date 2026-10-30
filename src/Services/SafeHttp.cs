using System.Net;
using System.Net.Sockets;

namespace PhotoGallery;

/// <summary>
/// Fetches web pages and pictures for link previews without letting a link reach the home network.
/// Every connection (including each redirect) is made to an address that was resolved here and checked: loopback,
/// private, link-local and other non-public ranges are refused, so a saved link cannot be used to probe the server's
/// own network. Only ports 80 and 443, a few redirects, a time limit and a size limit.
/// </summary>
/// <summary>Thrown when a connection would go to an address that is not public.</summary>
public class BlockedAddressException() : HttpRequestException("That address is not allowed.");

public static class SafeHttp
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    const int MaxRedirects = 3;

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;
        var b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            if (b[0] is 0 or 10 or 127) return false;                               // this network, private, loopback
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;              // carrier-grade NAT
            if (b[0] == 169 && b[1] == 254) return false;                           // link-local
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;               // private
            if (b[0] == 192 && b[1] == 168) return false;                           // private
            if (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) return false;           // protocol assignments, documentation
            if (b[0] == 198 && b[1] is 18 or 19) return false;                      // benchmarking
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;             // documentation
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;              // documentation
            return b[0] < 224;                                                       // multicast, reserved, broadcast
        }
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && (b[2] == 0x0d && b[3] == 0xb8 || b[2] == 0 && b[3] == 0)) return false; // documentation, Teredo
        if (b[0] == 0x20 && b[1] == 0x02) return false;                             // 6to4 can hide an IPv4 address
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b) return false; // NAT64
        return true;
    }

    static readonly HttpClient client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = (await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct)).Where(IsPublic).ToArray();
            if (addresses.Length == 0) throw new BlockedAddressException();
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public record Response(string ContentType, byte[] Body, Uri FinalUri);

    /// <summary>How a link answered: Ok, Gone (404 or 410), Unreachable (no connection), Unsure (some other answer, for example a site that turns robots away) or NotChecked (not a public address).</summary>
    public enum Reach { Ok, Gone, Unreachable, Unsure, NotChecked }

    /// <summary>Asks a link whether it opens, without downloading it. Same safety rules as <see cref="GetAsync"/>.</summary>
    public static async Task<(Reach reach, int? code)> ProbeAsync(Uri uri)
    {
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            var method = HttpMethod.Head;
            for (var hop = 0; hop <= MaxRedirects + 1; hop++)
            {
                if (uri.Scheme is not ("http" or "https") || uri.Port is not (80 or 443)) return (Reach.NotChecked, null);
                using var req = new HttpRequestMessage(method, uri);
                req.Headers.UserAgent.ParseAdd("PhotoGalleryLinkCheck/1.0");
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var code = (int)resp.StatusCode;
                if (code is >= 300 and < 400 && resp.Headers.Location is { } to) { uri = to.IsAbsoluteUri ? to : new Uri(uri, to); continue; }
                if (method == HttpMethod.Head && code is 400 or 403 or 405 or 501) { method = HttpMethod.Get; hop--; continue; } // some sites refuse HEAD
                if (code is >= 200 and < 300) return (Reach.Ok, code);
                if (code is 404 or 410) return (Reach.Gone, code);
                return (Reach.Unsure, code);
            }
            return (Reach.Unsure, null); // too many redirects
        }
        catch (BlockedAddressException) { return (Reach.NotChecked, null); }
        catch (HttpRequestException e) when (e.InnerException is BlockedAddressException) { return (Reach.NotChecked, null); }
        catch { return (Reach.Unreachable, null); }
    }

    /// <summary>Downloads at most <paramref name="maxBytes"/>; null when blocked, too big, slow or not a success. With <paramref name="truncate"/> a longer body is cut off at the limit instead (a page's title is near the top).</summary>
    public static async Task<Response?> GetAsync(Uri uri, long maxBytes, string accept, bool truncate = false)
    {
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (uri.Scheme is not ("http" or "https") || uri.Port is not (80 or 443)) return null;
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.UserAgent.ParseAdd("PhotoGalleryLinkPreview/1.0");
                req.Headers.Accept.ParseAdd(accept);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } to)
                {
                    uri = to.IsAbsoluteUri ? to : new Uri(uri, to);
                    continue;
                }
                if (!resp.IsSuccessStatusCode) return null;
                if (!truncate && resp.Content.Headers.ContentLength > maxBytes) return null;
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                using var ms = new MemoryStream();
                var buf = new byte[16 * 1024];
                int n;
                while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
                {
                    if (ms.Length + n > maxBytes)
                    {
                        if (!truncate) return null;
                        ms.Write(buf, 0, (int)(maxBytes - ms.Length));
                        break;
                    }
                    ms.Write(buf, 0, n);
                }
                return new Response(resp.Content.Headers.ContentType?.MediaType ?? "", ms.ToArray(), uri);
            }
            return null;
        }
        catch { return null; }
    }
}
