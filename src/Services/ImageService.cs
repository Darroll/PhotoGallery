using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using NetVips;

namespace PhotoGallery;

public class ImageService(AppConfig cfg, SettingsService settings, ILogger<ImageService> log)
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public static readonly string[] LosslessFormats = ["png", "webp", "tif"];

    public static string ContentType(string ext) => ext.ToLowerInvariant() switch
    {
        "png" => "image/png",
        "webp" => "image/webp",
        "tif" or "tiff" => "image/tiff",
        "jpg" or "jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };

    /// <summary>Path of the file used to build derivatives (the image itself, or an extracted RAW preview).</summary>
    public async Task<string?> GetSourceAsync(Photo p)
    {
        if (!p.RawOnly) return cfg.ResolvePhoto(p, p.RelPath);
        var dest = Path.Combine(cfg.CacheDir, "rawprev", $"{p.Id}.jpg");
        if (File.Exists(dest)) return dest;
        var raw = cfg.ResolvePhoto(p, p.RelPath);
        if (raw == null || !File.Exists(raw)) return null;
        return await ExtractRawPreviewAsync(raw, dest) ? dest : null;
    }

    public async Task<bool> ExtractRawPreviewAsync(string raw, string dest)
    {
        if (cfg.ExifTool == null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".tmp";
        foreach (var tag in new[] { "-JpgFromRaw", "-PreviewImage", "-OtherImage" })
        {
            try
            {
                var psi = new ProcessStartInfo(cfg.ExifTool) { RedirectStandardOutput = true, UseShellExecute = false };
                psi.ArgumentList.Add("-b");
                psi.ArgumentList.Add(tag);
                psi.ArgumentList.Add(raw);
                using var proc = Process.Start(psi)!;
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    await using (var fs = File.Create(tmp))
                        await proc.StandardOutput.BaseStream.CopyToAsync(fs, limit.Token);
                    await proc.WaitForExitAsync(limit.Token);
                }
                catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 2000)
                {
                    File.Move(tmp, dest, true);
                    return true;
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "exiftool failed for {Raw}", raw); }
        }
        if (File.Exists(tmp)) File.Delete(tmp);
        return false;
    }

    /// <summary>Largest image accepted (about 150 megapixels); stops tiny files that expand into huge pictures.</summary>
    public const long MaxPixels = 150_000_000;

    public (int w, int h, DateTime? taken) ReadMeta(string path)
    {
        using var img = Image.NewFromFile(path);
        int w = img.Width, h = img.Height;
        if ((long)w * h > MaxPixels) throw new InvalidDataException("image dimensions too large");
        if (img.GetTypeOf("orientation") != 0 && Convert.ToInt32(img.Get("orientation")) >= 5) (w, h) = (h, w);
        DateTime? taken = null;
        try
        {
            if (img.GetTypeOf("exif-ifd2-DateTimeOriginal") != 0)
            {
                var s = Convert.ToString(img.Get("exif-ifd2-DateTimeOriginal")) ?? "";
                if (s.Length >= 19 && DateTime.TryParseExact(s[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    taken = d;
            }
        }
        catch { }
        return (w, h, taken);
    }

    /// <summary>
    /// kind: "thumb" or "view" (WebP for on-screen use) or "size" (lossless export, fmt png|webp|tif).
    /// Returns the cached file path, generating it if needed.
    /// </summary>
    public async Task<(string path, string ext)?> GetVariantAsync(Photo p, string kind, int px, string fmt, bool watermark)
    {
        var src = await GetSourceAsync(p);
        if (src == null || !File.Exists(src)) return null;

        var ext = kind == "size" ? fmt : "webp";
        var stamp = File.GetLastWriteTimeUtc(src).Ticks.ToString("x");
        var wmTag = watermark ? "w" + WatermarkStamp() : "n";
        var dir = Path.Combine(cfg.CacheDir, "variants", p.Id.ToString());
        var path = Path.Combine(dir, $"{kind}{px}_{wmTag}_{stamp}.{ext}");
        if (File.Exists(path)) return (path, ext);

        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (File.Exists(path)) return (path, ext);
            Directory.CreateDirectory(dir);
            var tmp = path + ".tmp." + ext;
            await Task.Run(() => Render(src, tmp, kind, px, fmt, watermark));
            File.Move(tmp, path, true);
            return (path, ext);
        }
        finally
        {
            gate.Release();
        }
    }

    void Render(string src, string dest, string kind, int px, string fmt, bool watermark)
    {
        using var img = Image.Thumbnail(src, px, height: px, size: Enums.Size.Down);
        var result = watermark ? ApplyWatermark(img) : img;
        try
        {
            if (kind == "size")
            {
                switch (fmt)
                {
                    case "png": result.Pngsave(dest, compression: 6); break;
                    case "webp": result.Webpsave(dest, lossless: true); break;
                    case "tif": result.Tiffsave(dest, compression: Enums.ForeignTiffCompression.Lzw); break;
                    default: throw new ArgumentException("bad format");
                }
            }
            else
            {
                result.Webpsave(dest, q: kind == "thumb" ? 80 : 88);
            }
        }
        finally
        {
            if (!ReferenceEquals(result, img)) result.Dispose();
        }
    }

    string WatermarkStamp()
    {
        var w = settings.Watermark;
        var s = $"{w.Mode}|{w.Text}|{w.Position}|{w.Opacity}|{w.SizePercent}";
        if (w.Mode == "png" && File.Exists(cfg.WatermarkImagePath)) s += File.GetLastWriteTimeUtc(cfg.WatermarkImagePath).Ticks;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..8];
    }

    /// <summary>The cache file name tag for watermarked variants made with the current watermark settings.</summary>
    public string CurrentWatermarkTag => "w" + WatermarkStamp();

    /// <summary>Path of the file derivatives are built from, without creating anything (null when unknown).</summary>
    public string? PeekSource(Photo p) =>
        p.RawOnly ? Path.Combine(cfg.CacheDir, "rawprev", $"{p.Id}.jpg") : cfg.ResolvePhoto(p, p.RelPath);

    public bool HasWatermarkPng => File.Exists(cfg.WatermarkImagePath);

    public bool Available(WatermarkSettings w) =>
        (w.Mode == "text" && !string.IsNullOrWhiteSpace(w.Text)) || (w.Mode == "png" && HasWatermarkPng);

    public bool WatermarkAvailable => Available(settings.Watermark);

    /// <summary>Renders a sample with the given (possibly unsaved) settings, for the admin preview.</summary>
    public async Task<byte[]?> PreviewWatermarkAsync(Photo p, WatermarkSettings w)
    {
        var src = await GetSourceAsync(p);
        if (src == null || !File.Exists(src)) return null;
        w = w.Sanitized();
        return await Task.Run(() =>
        {
            using var img = Image.Thumbnail(src, 1200, height: 1200, size: Enums.Size.Down);
            var result = Available(w) ? ApplyWatermark(img, w) : img;
            try { return result.WebpsaveBuffer(q: 85); }
            finally { if (!ReferenceEquals(result, img)) result.Dispose(); }
        });
    }

    Image ApplyWatermark(Image img) => ApplyWatermark(img, settings.Watermark);

    Image ApplyWatermark(Image img, WatermarkSettings w)
    {
        if (!Available(w)) return img;
        var baseImg = img.Bands < 3 ? img.Colourspace(Enums.Interpretation.Srgb) : img;
        double opacity = w.Opacity / 100.0, size = w.SizePercent / 100.0;
        Image? overlay = null;
        try
        {
            if (w.Mode == "png")
            {
                using var raw = Image.NewFromFile(cfg.WatermarkImagePath).Colourspace(Enums.Interpretation.Srgb);
                using var scaled = raw.Resize(baseImg.Width * size / raw.Width);
                var rgb = scaled.ExtractBand(0, 3);
                using var alphaSrc = scaled.HasAlpha() ? scaled.ExtractBand(scaled.Bands - 1) : scaled.NewFromImage(255).Cast(Enums.BandFormat.Uchar);
                using var alpha = (alphaSrc * opacity).Cast(Enums.BandFormat.Uchar);
                overlay = rgb.Cast(Enums.BandFormat.Uchar).Bandjoin(alpha);
            }
            else
            {
                using var mask = Image.Text(w.Text, font: "sans bold 72", dpi: 72);
                var scale = Math.Min(baseImg.Width * size / mask.Width, 4.0);
                using var m2 = mask.Resize(scale);
                using var alpha = (m2 * opacity).Cast(Enums.BandFormat.Uchar);
                using var white = m2.NewFromImage(255, 255, 255).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
                overlay = white.Bandjoin(alpha);
            }
            int margin = (int)(baseImg.Width * 0.02);
            int maxX = Math.Max(0, baseImg.Width - overlay.Width), maxY = Math.Max(0, baseImg.Height - overlay.Height);
            int x = w.Position.EndsWith("left") ? Math.Min(margin, maxX) : w.Position == "center" ? maxX / 2 : Math.Max(0, maxX - margin);
            int y = w.Position.StartsWith("top") ? Math.Min(margin, maxY) : w.Position == "center" ? maxY / 2 : Math.Max(0, maxY - margin);
            return baseImg.Composite2(overlay, Enums.BlendMode.Over, x, y);
        }
        finally
        {
            overlay?.Dispose();
            if (!ReferenceEquals(baseImg, img)) baseImg.Dispose();
        }
    }
}
