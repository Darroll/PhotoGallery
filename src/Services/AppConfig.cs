namespace PhotoGallery;

public class AppConfig
{
    public string LibraryRoot { get; }
    public string DataDir { get; }
    public string CacheDir => Path.Combine(DataDir, "cache");
    public string AdminPassword { get; }
    public bool AdminPasswordGenerated { get; }
    /// <summary>Admin user name from ADMIN_USERNAME; null (no user name asked for) when the variable is empty.</summary>
    public string? AdminUsername { get; }
    /// <summary>Password sign-in for the admin; can be switched off once single sign-on works.</summary>
    public bool AdminPasswordLogin { get; }
    // Single sign-on (Keycloak or any OpenID Connect provider); all empty means sign-in is off
    public string? OidcAuthority { get; }
    public string? OidcClientId { get; }
    public string? OidcClientSecret { get; }
    /// <summary>Role (or group) in the provider that makes a user an admin of the gallery.</summary>
    public string OidcAdminRole { get; }
    public bool OidcAllowHttp { get; }
    /// <summary>Address of an Ollama server with a vision model, for automatic captions. Empty means off.</summary>
    public string? CaptionUrl { get; }
    public string CaptionModel { get; }
    public bool OidcEnabled => OidcAuthority != null && OidcClientId != null;
    public int[] Sizes { get; }
    public int MaxUploadMb { get; }
    public int MaxPendingPerGallery { get; }
    public string LegacyWatermarkText { get; }
    public int RescanMinutes { get; }
    /// <summary>Hours between database backups (0 = off), and how many daily and weekly snapshots to keep.</summary>
    public int BackupHours { get; }
    public int BackupDaily { get; }
    public int BackupWeekly { get; }
    /// <summary>A second folder (another disk, a share, a USB drive) that every snapshot is also copied to. Empty means off.</summary>
    public string? BackupCopyDir { get; }
    public string WatermarkImagePath => Path.Combine(DataDir, "watermark.png");
    public string SiteTitle { get; }
    public bool TrustProxy { get; }
    /// <summary>Networks whose X-Forwarded-* and CF-Connecting-IP headers are believed (SWAG's docker network by default).</summary>
    public (System.Net.IPAddress Net, int Prefix)[] TrustedNetworks { get; }
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
        AdminUsername = string.IsNullOrWhiteSpace(c["ADMIN_USERNAME"]) ? null : c["ADMIN_USERNAME"]!.Trim();
        static string? Opt(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        OidcAuthority = Opt(c["OIDC_AUTHORITY"])?.TrimEnd('/');
        OidcClientId = Opt(c["OIDC_CLIENT_ID"]);
        // Once SSO is configured the password form is gone; ADMIN_PASSWORD_LOGIN=true brings it back (e.g. if SSO is down).
        var pwLogin = Opt(c["ADMIN_PASSWORD_LOGIN"]);
        AdminPasswordLogin = pwLogin == null ? !OidcEnabled : !string.Equals(pwLogin, "false", StringComparison.OrdinalIgnoreCase);
        OidcClientSecret = Opt(c["OIDC_CLIENT_SECRET"]);
        OidcAdminRole = Opt(c["OIDC_ADMIN_ROLE"]) ?? "gallery-admin";
        OidcAllowHttp = string.Equals(c["OIDC_ALLOW_HTTP"], "true", StringComparison.OrdinalIgnoreCase);
        CaptionUrl = Opt(c["CAPTION_URL"])?.TrimEnd('/');
        CaptionModel = Opt(c["CAPTION_MODEL"]) ?? "llava";
        Sizes = (c["SIZES"] ?? "1080,1920,2560,4096")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).Where(s => s > 0).Distinct().OrderBy(s => s).ToArray();
        MaxUploadMb = int.TryParse(c["MAX_UPLOAD_MB"], out var m) ? m : 100;
        MaxPendingPerGallery = int.TryParse(c["MAX_PENDING_PER_GALLERY"], out var mp) ? mp : 200;
        LegacyWatermarkText = c["WATERMARK_TEXT"] ?? "";
        RescanMinutes = int.TryParse(c["RESCAN_MINUTES"], out var rm) ? rm : 15;
        BackupHours = int.TryParse(c["BACKUP_HOURS"], out var bh) ? bh : 24;
        BackupDaily = int.TryParse(c["BACKUP_DAILY"], out var bd) ? Math.Max(1, bd) : 7;
        BackupWeekly = int.TryParse(c["BACKUP_WEEKLY"], out var bw) ? Math.Max(0, bw) : 4;
        BackupCopyDir = Opt(c["BACKUP_COPY_DIR"]) is { } bc ? Path.GetFullPath(bc) : null;
        SiteTitle = c["SITE_TITLE"] ?? "Photos";
        TrustProxy = !string.Equals(c["TRUST_PROXY"], "false", StringComparison.OrdinalIgnoreCase);
        var nets = new List<(System.Net.IPAddress, int)>();
        foreach (var part in (c["TRUSTED_PROXIES"] ?? "172.16.0.0/12,127.0.0.0/8,::1/128").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('/');
            if (System.Net.IPAddress.TryParse(bits[0], out var ip) && (bits.Length == 1 || int.TryParse(bits[1], out _)))
                nets.Add((ip, bits.Length == 1 ? (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128) : int.Parse(bits[1])));
        }
        TrustedNetworks = nets.ToArray();
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
    public bool IsTrustedProxy(System.Net.IPAddress? addr)
    {
        if (addr == null) return false;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        foreach (var (net, prefix) in TrustedNetworks)
        {
            var n = net.IsIPv4MappedToIPv6 ? net.MapToIPv4() : net;
            if (n.AddressFamily != addr.AddressFamily) continue;
            var a = addr.GetAddressBytes(); var b = n.GetAddressBytes();
            int full = prefix / 8, rem = prefix % 8; var ok = true;
            for (int i = 0; i < full && ok; i++) ok = a[i] == b[i];
            if (ok && rem > 0) ok = (a[full] >> (8 - rem)) == (b[full] >> (8 - rem));
            if (ok) return true;
        }
        return false;
    }

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
