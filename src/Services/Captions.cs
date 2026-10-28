using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>A proposed change to a photo's caption or alt text, from a signed-in visitor. Applied only when the admin approves it.</summary>
public class CaptionEdit
{
    public int Id { get; set; }
    public int PhotoId { get; set; }
    /// <summary>"caption" or "alt".</summary>
    public string Field { get; set; } = "";
    /// <summary>The value the suggestion was written against, like the base of a merge. A different current value is a conflict.</summary>
    public string Base { get; set; } = "";
    public string Proposed { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>pending, approved or rejected.</summary>
    public string Status { get; set; } = "pending";
    public DateTime? ReviewedAt { get; set; }
}

public static class Captions
{
    public const int MaxCaption = 120;
    public const int MaxAlt = 300;

    public static string Clean(string? s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>The field's value by name ("caption" or "alt").</summary>
    public static string Get(Photo p, string field) => field == "alt" ? p.AltText : p.Caption;

    public static void Set(Photo p, string field, string value, string source)
    {
        if (field == "alt") p.AltText = Clean(value, MaxAlt);
        else p.Caption = Clean(value, MaxCaption);
        p.CaptionSource = source;
    }
}

/// <summary>
/// Writes captions and alt text with a local vision model (Ollama) for photos that have none. Off unless CAPTION_URL is set,
/// so photos never leave the server unless you point it at a model you run yourself.
/// </summary>
public class CaptionService(IServiceScopeFactory scopes, AppConfig cfg, ILogger<CaptionService> log) : BackgroundService
{
    public const int MaxAttempts = 3;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (cfg.CaptionUrl == null) return;
        await Task.Delay(TimeSpan.FromSeconds(90), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var written = await CaptionPendingAsync(scope.ServiceProvider.GetRequiredService<AppDb>(), scope.ServiceProvider.GetRequiredService<ImageService>(), cfg, log, 20, ct);
                if (written > 0) log.LogInformation("Wrote {Count} automatic captions", written);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Automatic captions stopped for this round");
            }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    /// <summary>Captions up to <paramref name="max"/> approved photos that have neither a caption nor alt text. Returns how many were written.</summary>
    public static async Task<int> CaptionPendingAsync(AppDb db, ImageService images, AppConfig cfg, ILogger log, int max, CancellationToken ct = default)
    {
        if (cfg.CaptionUrl == null) return 0;
        var todo = await db.Photos.Where(p => p.Caption == "" && p.AltText == "" && p.Approved && p.CaptionAttempts < MaxAttempts)
            .OrderBy(p => p.Id).Take(max).ToListAsync(ct);
        var written = 0;
        foreach (var p in todo)
        {
            ct.ThrowIfCancellationRequested();
            p.CaptionAttempts++;
            try
            {
                var v = await images.GetVariantAsync(p, "thumb", 480, "webp", false);
                if (v == null) continue;
                var (caption, alt) = await AskAsync(cfg, JpegBytes(v.Value.path), ct);
                if (caption.Length > 0 || alt.Length > 0)
                {
                    Captions.Set(p, "caption", caption, "auto");
                    Captions.Set(p, "alt", alt, "auto");
                    written++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Caption attempt {Attempt} failed for photo {Id}", p.CaptionAttempts, p.Id);
            }
            await db.SaveChangesAsync(ct);
        }
        return written;
    }

    static byte[] JpegBytes(string path)
    {
        using var img = NetVips.Image.NewFromFile(path);
        return img.JpegsaveBuffer(q: 80);
    }

    /// <summary>Asks the model for a short caption and an alt text. Ollama's JSON mode keeps the reply parseable.</summary>
    public static async Task<(string caption, string alt)> AskAsync(AppConfig cfg, byte[] jpeg, CancellationToken ct)
    {
        var body = new
        {
            model = cfg.CaptionModel,
            prompt = "Describe this photo for a photo gallery. Reply only with JSON: {\"caption\": a short title of at most 8 words, " +
                     "\"alt\": one sentence of at most 25 words describing what is shown, for screen readers}. " +
                     "Do not name or guess people, and do not guess where the photo was taken.",
            images = new[] { Convert.ToBase64String(jpeg) },
            stream = false,
            format = "json",
        };
        using var resp = await Http.PostAsJsonAsync($"{cfg.CaptionUrl}/api/generate", body, ct);
        resp.EnsureSuccessStatusCode();
        using var outer = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        using var reply = JsonDocument.Parse(outer.RootElement.GetProperty("response").GetString() ?? "{}");
        var root = reply.RootElement;
        var caption = root.TryGetProperty("caption", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        var alt = root.TryGetProperty("alt", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
        return (Captions.Clean(caption, Captions.MaxCaption), Captions.Clean(alt, Captions.MaxAlt));
    }
}
