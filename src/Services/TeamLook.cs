using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// A team's logo and colours, set on the Teams page and shown on the site without editing CSS: its galleries use them, and
/// one team can lend them to every other page. They belong to the team by name, so every season of a team shares them.
/// </summary>
public static partial class TeamLook
{
    public const int LogoMaxBytes = 5_000_000;
    /// <summary>Logos are kept at most this many pixels on the long side: big enough for a sharp header on a phone.</summary>
    public const int LogoSize = 512;

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex HexRx();
    [GeneratedRegex("^[0-9a-f]{32}\\.png$")]
    private static partial Regex FileRx();

    public static string Dir(AppConfig cfg) => Path.Combine(cfg.DataDir, "team-logos");

    /// <summary>"#1E40AF", "1e40af" or "#14a" become "#1e40af"; anything else is no colour ("").</summary>
    public static string CleanColor(string? s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        if (s.Length > 0 && s[0] != '#') s = "#" + s;
        if (s.Length == 4 && s.Skip(1).All(Uri.IsHexDigit)) s = "#" + string.Concat(s.Skip(1).Select(c => $"{c}{c}"));
        return HexRx().IsMatch(s) ? s : "";
    }

    public static bool IsLogoFile(string? name) => name != null && FileRx().IsMatch(name);

    /// <summary>Where a logo is served. The file name changes with every upload, so it can be cached for good.</summary>
    public static string? LogoUrl(Team t) => IsLogoFile(t.LogoFile) ? "/team-logo/" + t.LogoFile : null;

    public static bool Has(Team t) => IsLogoFile(t.LogoFile) || t.MainColor.Length > 0 || t.SecondColor.Length > 0;

    /// <summary>What visitors' pages need to dress themselves in a team's look; null when the team has none set.</summary>
    public static object? ForVisitors(Team? t) => t == null || !Has(t) ? null : new { name = t.Name, logo = LogoUrl(t), main = NullIfEmpty(t.MainColor), second = NullIfEmpty(t.SecondColor) };

    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>
    /// Reads an uploaded picture (PNG, JPEG, WebP, GIF and the rest libvips reads), shrinks it to <see cref="LogoSize"/> and
    /// writes it as a new PNG, keeping transparency. Re-encoding means only plain pixels are ever served. Returns the file name.
    /// </summary>
    public static string SaveLogo(AppConfig cfg, byte[] bytes)
    {
        using var img = NetVips.Image.ThumbnailBuffer(bytes, LogoSize, height: LogoSize, size: NetVips.Enums.Size.Down);
        using var srgb = img.Interpretation == NetVips.Enums.Interpretation.Srgb || img.Interpretation == NetVips.Enums.Interpretation.Bw ? img.Copy() : img.Colourspace(NetVips.Enums.Interpretation.Srgb);
        var png = srgb.PngsaveBuffer(compression: 9, keep: NetVips.Enums.ForeignKeep.None);
        Directory.CreateDirectory(Dir(cfg));
        var name = Guid.NewGuid().ToString("N") + ".png";
        File.WriteAllBytes(Path.Combine(Dir(cfg), name), png);
        return name;
    }

    /// <summary>Every season of the team <paramref name="t"/> belongs to (the same name), itself included.</summary>
    public static Task<List<Team>> SeasonsAsync(AppDb db, Team t)
    {
        var name = t.Name.ToLower();
        return db.Teams.Where(x => x.Name.ToLower() == name).ToListAsync();
    }

    /// <summary>A new season, or a season renamed to another team's name, takes on that team's look.</summary>
    public static async Task AdoptAsync(AppDb db, Team t)
    {
        var name = t.Name.ToLower();
        var other = await db.Teams.AsNoTracking().Where(x => x.Id != t.Id && x.Name.ToLower() == name).OrderByDescending(x => x.CreatedAt).ToListAsync();
        var from = other.FirstOrDefault(Has);
        if (from == null) return;
        t.LogoFile = from.LogoFile; t.MainColor = from.MainColor; t.SecondColor = from.SecondColor; t.SiteLook = from.SiteLook;
    }

    /// <summary>Removes logo files no team uses any more (after a new upload, a removal or a deleted team).</summary>
    public static async Task CleanUpAsync(AppDb db, AppConfig cfg)
    {
        var dir = Dir(cfg);
        if (!Directory.Exists(dir)) return;
        var used = (await db.Teams.Select(t => t.LogoFile).ToListAsync()).ToHashSet();
        foreach (var f in Directory.GetFiles(dir, "*.png"))
            if (!used.Contains(Path.GetFileName(f))) try { File.Delete(f); } catch { }
    }
}
