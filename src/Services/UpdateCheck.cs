using System.Text.Json;

namespace PhotoGallery;

public class UpdateSettings
{
    /// <summary>Off by default: nothing is sent anywhere until the admin turns this on.</summary>
    public bool Enabled { get; set; }
    public UpdateSettings Sanitized() => new() { Enabled = Enabled };
}

/// <summary>Asks GitHub once a day for the newest released version, only when the admin switched the notice on.</summary>
public class UpdateChecker(AppConfig cfg, SettingsService settings, ILogger<UpdateChecker> log)
{
    public const string TagsUrl = "https://api.github.com/repos/Darroll/PhotoGallery/tags?per_page=100";
    public const string ReleasesPage = "https://github.com/Darroll/PhotoGallery/blob/main/CHANGELOG.md";
    static readonly TimeSpan Every = TimeSpan.FromHours(24);

    public record Result(string? Latest, DateTime CheckedAt, string? Error);

    readonly object gate = new();
    Result? last;
    bool running;
    /// <summary>Replaced in tests so no real request is made.</summary>
    public Func<HttpClient> Http { get; set; } = () => new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    public Result? Last { get { lock (gate) return last; } }

    /// <summary>Starts a refresh in the background when the notice is on and the answer is older than a day.</summary>
    public void RefreshIfDue()
    {
        if (!settings.Updates.Enabled) return;
        lock (gate)
        {
            if (running || (last != null && DateTime.UtcNow - last.CheckedAt < Every && last.Error == null)) return;
            if (last != null && DateTime.UtcNow - last.CheckedAt < TimeSpan.FromMinutes(30)) return;   // back off after a failure
            running = true;
        }
        _ = Task.Run(async () => { try { await CheckAsync(); } finally { lock (gate) running = false; } });
    }

    public async Task<Result> CheckAsync()
    {
        Result r;
        try
        {
            using var http = Http();
            using var req = new HttpRequestMessage(HttpMethod.Get, TagsUrl);
            req.Headers.UserAgent.ParseAdd("PhotoGallery/" + cfg.Version);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            r = new Result(Newest(await resp.Content.ReadAsStringAsync()), DateTime.UtcNow, null);
        }
        catch (Exception e)
        {
            log.LogInformation("Update check failed: {Message}", e.Message);
            r = new Result(null, DateTime.UtcNow, "The version list could not be fetched.");
        }
        lock (gate) last = r;
        return r;
    }

    public static bool TryParseVersion(string? tag, out Version v)
    {
        v = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(t, out var p) && (v = p) != null;
    }

    /// <summary>The highest version number among the repository's tags (only plain release tags such as v1.2.0).</summary>
    public static string? Newest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Version? best = null; string? bestName = null;
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var name = t.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name == null || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^v?\d+\.\d+(\.\d+)?$")) continue;
            if (TryParseVersion(name, out var v) && (best == null || v > best)) { best = v; bestName = name.TrimStart('v', 'V'); }
        }
        return bestName;
    }

    public bool IsNewer(string latest) => TryParseVersion(latest, out var l) && TryParseVersion(cfg.Version, out var cur) && l > cur;
}
