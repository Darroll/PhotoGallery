namespace PhotoGallery;

public class AppConfig
{
    public string LibraryRoot { get; }
    public string DataDir { get; }
    public string CacheDir => Path.Combine(DataDir, "cache");
    public string AdminPassword { get; }
    public bool AdminPasswordGenerated { get; }
    public int[] Sizes { get; }
    public int MaxUploadMb { get; }
    public int MaxPendingPerGallery { get; }
    public string LegacyWatermarkText { get; }
    public int RescanMinutes { get; }
    public string WatermarkImagePath => Path.Combine(DataDir, "watermark.png");
    public string SiteTitle { get; }
    public bool TrustProxy { get; }
    public string Version { get; }
    public string Commit { get; }
    public string BuildDate { get; }
    public string? ExifTool { get; }

    public static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".webp", ".heic", ".avif"];
    public static readonly string[] RawExts = [".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw"];

    public AppConfig(IConfiguration c)
    {
        LibraryRoot = Path.GetFullPath(c["PHOTO_LIBRARY"] ?? "/photos");
        DataDir = Path.GetFullPath(c["DATA_DIR"] ?? "/data");
        var pw = c["ADMIN_PASSWORD"];
        if (string.IsNullOrWhiteSpace(pw))
        {
            pw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).Replace('+', 'a').Replace('/', 'b').TrimEnd('=');
            AdminPasswordGenerated = true;
        }
        AdminPassword = pw;
        Sizes = (c["SIZES"] ?? "1080,1920,2560,4096")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).Where(s => s > 0).Distinct().OrderBy(s => s).ToArray();
        MaxUploadMb = int.TryParse(c["MAX_UPLOAD_MB"], out var m) ? m : 100;
        MaxPendingPerGallery = int.TryParse(c["MAX_PENDING_PER_GALLERY"], out var mp) ? mp : 200;
        LegacyWatermarkText = c["WATERMARK_TEXT"] ?? "";
        RescanMinutes = int.TryParse(c["RESCAN_MINUTES"], out var rm) ? rm : 15;
        SiteTitle = c["SITE_TITLE"] ?? "Photos";
        TrustProxy = !string.Equals(c["TRUST_PROXY"], "false", StringComparison.OrdinalIgnoreCase);
        Version = (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];
        Commit = c["APP_COMMIT"] ?? "dev";
        BuildDate = c["APP_BUILD_DATE"] ?? "";
        ExifTool = FindExifTool();
    }

    static string? FindExifTool()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "exiftool.exe" } : new[] { "exiftool" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var n in names)
            {
                try { var p = Path.Combine(dir, n); if (File.Exists(p)) return p; } catch { }
            }
        return null;
    }

    public string GuestRoot => Path.Combine(DataDir, "guest");

    /// <summary>Resolve a library-relative path, refusing anything that escapes the library root.</summary>
    public string? Resolve(string rel) => ResolveUnder(LibraryRoot, rel);

    /// <summary>Guest uploads live in the data dir so the photo library can stay read-only.</summary>
    public string? ResolvePhoto(Photo p, string rel) => ResolveUnder(p.IsGuest ? GuestRoot : LibraryRoot, rel);

    static string? ResolveUnder(string rootDir, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        var full = Path.GetFullPath(Path.Combine(rootDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public string Rel(string full) =>
        Path.GetRelativePath(LibraryRoot, full).Replace(Path.DirectorySeparatorChar, '/');
}
