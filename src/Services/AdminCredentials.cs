using System.Net;
using System.Net.Sockets;

namespace PhotoGallery;

/// <summary>
/// The admin password. ADMIN_PASSWORD in the container always wins. Otherwise the admin chooses one on first
/// visit; it is stored hashed (PBKDF2) in DATA_DIR/admin-password. Deleting that file starts setup again.
/// </summary>
public class AdminCredentials
{
    public const int MinLength = 10, MaxLength = 200;
    readonly AppConfig cfg;
    readonly string path;
    readonly object gate = new();
    string? hash;

    /// <summary>Shown in the container log; lets someone who is not on the home network prove they own the server.</summary>
    public string SetupCode { get; } = Passwords.NewToken()[..12];

    public AdminCredentials(AppConfig cfg)
    {
        this.cfg = cfg;
        path = Path.Combine(cfg.DataDir, "admin-password");
        try { if (File.Exists(path)) hash = File.ReadAllText(path).Trim(); } catch { }
    }

    public bool FromContainer => cfg.AdminPasswordSet;
    public bool SetupRequired => !FromContainer && string.IsNullOrEmpty(hash);

    public bool Verify(string? password)
    {
        password ??= "";
        if (FromContainer) return Passwords.FixedEquals(password, cfg.AdminPassword);
        string? h; lock (gate) h = hash;
        return Passwords.Verify(password, h);
    }

    /// <summary>null when acceptable, otherwise a plain-language reason.</summary>
    public static string? Problem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"Use at least {MinLength} characters.";
        if (password.Length > MaxLength) return $"Use at most {MaxLength} characters.";
        if (password.Trim().Length == 0) return "The password cannot be only spaces.";
        return null;
    }

    public void Set(string password)
    {
        lock (gate)
        {
            Directory.CreateDirectory(cfg.DataDir);
            var h = Passwords.Hash(password);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, h);
            File.Move(tmp, path, true);
            hash = h;
        }
    }

    /// <summary>
    /// True when the request plainly comes from the home network: a private address and no proxy headers.
    /// Anything else needs the setup code, so a server exposed to the internet cannot be claimed by a stranger.
    /// </summary>
    public static bool IsLocalRequest(HttpContext ctx)
    {
        var h = ctx.Request.Headers;
        if (h.ContainsKey("X-Forwarded-For") || h.ContainsKey("CF-Connecting-IP") || h.ContainsKey("Forwarded") || h.ContainsKey("X-Real-IP")) return false;
        var ip = ctx.Connection.RemoteIpAddress;
        return ip != null && IsPrivate(ip);
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && ((b[0] & 0xFE) == 0xFC || ip.IsIPv6LinkLocal);
    }
}
