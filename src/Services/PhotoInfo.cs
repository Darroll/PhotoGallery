using System.Diagnostics;
using System.Text.Json;

namespace PhotoGallery;

/// <summary>One file that makes up a photo (the processed image, or its RAW), with its metadata grouped the way the file reports it.</summary>
public record InfoVersion(string Role, string File, long Bytes, DateTime Modified, Dictionary<string, Dictionary<string, string>> Groups);

/// <summary>Reads the metadata of every file behind a photo. exiftool gives the full picture when installed; without it, the image library's own fields are used.</summary>
public static class PhotoInfo
{
    static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(20);

    public static async Task<List<InfoVersion>> ReadAsync(AppConfig cfg, Photo p, bool includeLocation)
    {
        var list = new List<InfoVersion>();
        var primary = cfg.ResolvePhoto(p, p.RelPath);
        if (primary != null && File.Exists(primary))
            list.Add(await ReadFileAsync(cfg, primary, p.RawOnly ? "RAW" : "Image", includeLocation));
        if (p.RawRelPath != null && p.RawRelPath != p.RelPath)
        {
            var raw = cfg.ResolvePhoto(p, p.RawRelPath);
            if (raw != null && File.Exists(raw)) list.Add(await ReadFileAsync(cfg, raw, "RAW", includeLocation));
        }
        return list;
    }

    static async Task<InfoVersion> ReadFileAsync(AppConfig cfg, string path, string role, bool includeLocation)
    {
        var groups = cfg.ExifTool != null ? await ExiftoolAsync(cfg.ExifTool, path) : ImageLibraryFields(path);
        if (!includeLocation) StripLocation(groups);
        var fi = new FileInfo(path);
        groups["File"] = new Dictionary<string, string>
        {
            ["Name"] = fi.Name,
            ["Size"] = fi.Length >= 1048576 ? $"{fi.Length / 1048576.0:0.0} MB" : $"{Math.Max(1, fi.Length / 1024)} KB",
            ["Modified"] = fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm") + " UTC",
        };
        return new InfoVersion(role, fi.Name, fi.Length, fi.LastWriteTimeUtc, groups);
    }

    /// <summary>Visitors never see where a photo was taken: GPS tags are removed unless the viewer is the admin.</summary>
    public static void StripLocation(Dictionary<string, Dictionary<string, string>> groups)
    {
        foreach (var g in groups.Values)
            foreach (var key in g.Keys.Where(IsLocation).ToList()) g.Remove(key);
        foreach (var name in groups.Where(x => IsLocation(x.Key) || x.Value.Count == 0).Select(x => x.Key).ToList()) groups.Remove(name);
    }

    static bool IsLocation(string name) =>
        name.Contains("GPS", StringComparison.OrdinalIgnoreCase) || name.Contains("Latitude", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Longitude", StringComparison.OrdinalIgnoreCase) || name.Equals("Position", StringComparison.OrdinalIgnoreCase);

    static async Task<Dictionary<string, Dictionary<string, string>>> ExiftoolAsync(string exe, string path)
    {
        var groups = new Dictionary<string, Dictionary<string, string>>();
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-j");
            psi.ArgumentList.Add("-G");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(path);
            using var proc = Process.Start(psi)!;
            using var limit = new CancellationTokenSource(ReadLimit);
            var text = await proc.StandardOutput.ReadToEndAsync(limit.Token);
            await proc.WaitForExitAsync(limit.Token);
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(text)) return groups;

            using var doc = JsonDocument.Parse(text);
            foreach (var obj in doc.RootElement.EnumerateArray())
            foreach (var prop in obj.EnumerateObject())
            {
                var colon = prop.Name.IndexOf(':');
                if (colon <= 0) continue;                    // SourceFile and similar
                var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                if (string.IsNullOrWhiteSpace(value) || value.Length > 300 || value.StartsWith("(Binary data", StringComparison.Ordinal)) continue;
                var group = prop.Name[..colon];
                if (!groups.TryGetValue(group, out var fields)) groups[group] = fields = new();
                fields[prop.Name[(colon + 1)..]] = value;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            // an unreadable file just shows fewer details
        }
        return groups;
    }

    /// <summary>Fallback without exiftool: the fields the image library reads from the file (JPEG, PNG, TIFF, WebP, HEIC, ...). RAW files need exiftool.</summary>
    static Dictionary<string, Dictionary<string, string>> ImageLibraryFields(string path)
    {
        var groups = new Dictionary<string, Dictionary<string, string>>();
        try
        {
            using var img = NetVips.Image.NewFromFile(path);
            foreach (var name in img.GetFields())
            {
                object? raw = img.Get(name);
                if (raw is not string and not int and not double) continue;
                var value = Convert.ToString(raw) ?? "";
                if (value.Length == 0 || value.Length > 300) continue;
                var parts = name.Split('-', 3);
                var group = parts.Length == 3 ? parts[0] + "-" + parts[1] : parts[0];
                var key = parts.Length == 3 ? parts[2] : name;
                if (!groups.TryGetValue(group, out var fields)) groups[group] = fields = new();
                fields[key] = value;
            }
        }
        catch (NetVips.VipsException)
        {
            // not an image the library can open (for example a RAW file without exiftool)
        }
        return groups;
    }
}
