using System.Text.Json;

namespace PhotoGallery;

public class WatermarkSettings
{
    /// <summary>none | text | png</summary>
    public string Mode { get; set; } = "none";
    public string Text { get; set; } = "";
    /// <summary>top-left | top-right | bottom-left | bottom-right | center</summary>
    public string Position { get; set; } = "bottom-right";
    public int Opacity { get; set; } = 55;
    public int SizePercent { get; set; } = 30;

    public static readonly string[] Modes = ["none", "text", "png"];
    public static readonly string[] Positions = ["top-left", "top-right", "bottom-left", "bottom-right", "center"];

    public WatermarkSettings Sanitized()
    {
        return new WatermarkSettings
        {
            Mode = Modes.Contains(Mode) ? Mode : "none",
            Text = (Text ?? "").Trim() is { Length: > 80 } t ? t[..80] : (Text ?? "").Trim(),
            Position = Positions.Contains(Position) ? Position : "bottom-right",
            Opacity = Math.Clamp(Opacity, 10, 100),
            SizePercent = Math.Clamp(SizePercent, 5, 80)
        };
    }
}

public class HomeSettings
{
    /// <summary>event-desc | event-asc | name-asc | name-desc | created-desc | created-asc | manual</summary>
    public string Order { get; set; } = "created-desc";

    public static readonly string[] Orders = ["event-desc", "event-asc", "name-asc", "name-desc", "created-desc", "created-asc", "manual"];

    public HomeSettings Sanitized() => new() { Order = Orders.Contains(Order) ? Order : "created-desc" };
}

class AppSettings
{
    public string SiteName { get; set; } = "";
    public WatermarkSettings Watermark { get; set; } = new();
    public HomeSettings Home { get; set; } = new();
    public StatsSettings Stats { get; set; } = new();
    public EmailSettings Email { get; set; } = new();
    public LicenseSettings License { get; set; } = new();
    public ThemeSettings Theme { get; set; } = new();
}

/// <summary>App-level settings kept in DATA_DIR/settings.json (editable from the admin Settings page).</summary>
public class SettingsService
{
    readonly string path;
    readonly object gate = new();
    AppSettings data = new();

    public SettingsService(AppConfig cfg)
    {
        path = Path.Combine(cfg.DataDir, "settings.json");
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("Watermark", out _) || doc.RootElement.TryGetProperty("Home", out _))
                    data = JsonSerializer.Deserialize<AppSettings>(text, JsonOpts) ?? new();
                else   // first release stored the watermark settings at the top level
                    data = new AppSettings { Watermark = JsonSerializer.Deserialize<WatermarkSettings>(text, JsonOpts) ?? new() };
                data.Watermark = data.Watermark.Sanitized();
                data.Home = data.Home.Sanitized();
                data.Stats = (data.Stats ?? new()).Sanitized();
                data.Email = (data.Email ?? new()).Sanitized();
                data.License = (data.License ?? new()).Sanitized();
                data.Theme = (data.Theme ?? new()).Sanitized();
                if (string.IsNullOrWhiteSpace(data.SiteName)) { data.SiteName = SanitizeName(cfg.SiteTitle); Save(); }
            }
            else
            {
                // first run: honour the old WATERMARK_TEXT env var once, then it lives in settings.json
                data.SiteName = SanitizeName(cfg.SiteTitle);
                if (!string.IsNullOrWhiteSpace(cfg.LegacyWatermarkText))
                    data.Watermark = new WatermarkSettings { Mode = "text", Text = cfg.LegacyWatermarkText };
                Save();
            }
        }
        catch { data = new AppSettings(); }
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public WatermarkSettings Watermark
    {
        get { lock (gate) return Clone(data.Watermark); }
    }

    public HomeSettings Home
    {
        get { lock (gate) return new HomeSettings { Order = data.Home.Order }; }
    }

    public StatsSettings Stats
    {
        get { lock (gate) return new StatsSettings { Enabled = data.Stats.Enabled, RetentionDays = data.Stats.RetentionDays, IncludeAdmin = data.Stats.IncludeAdmin, UseCookie = data.Stats.UseCookie }; }
    }

    public StatsSettings SetStats(StatsSettings s)
    {
        lock (gate)
        {
            data.Stats = s.Sanitized();
            Save();
            return new StatsSettings { Enabled = data.Stats.Enabled, RetentionDays = data.Stats.RetentionDays, IncludeAdmin = data.Stats.IncludeAdmin, UseCookie = data.Stats.UseCookie };
        }
    }

    public EmailSettings Email
    {
        get { lock (gate) return data.Email.Clone(); }
    }

    public EmailSettings SetEmail(EmailSettings s)
    {
        lock (gate)
        {
            data.Email = s.Sanitized();
            Save();
            return data.Email.Clone();
        }
    }

    public ThemeSettings Theme
    {
        get { lock (gate) return data.Theme.Clone(); }
    }

    public ThemeSettings SetTheme(ThemeSettings s)
    {
        lock (gate)
        {
            data.Theme = s.Sanitized();
            Save();
            return data.Theme.Clone();
        }
    }

    public LicenseSettings License
    {
        get { lock (gate) return data.License.Clone(); }
    }

    public LicenseSettings SetLicense(LicenseSettings s)
    {
        lock (gate)
        {
            data.License = s.Sanitized();
            Save();
            return data.License.Clone();
        }
    }

    static string SanitizeName(string? n)
    {
        n = (n ?? "").Trim();
        return n.Length == 0 ? "Photos" : n.Length > 80 ? n[..80] : n;
    }

    /// <summary>The site name shown in the header, the home page and the browser tab.</summary>
    public string SiteName
    {
        get { lock (gate) return string.IsNullOrWhiteSpace(data.SiteName) ? "Photos" : data.SiteName; }
    }

    public string SetSiteName(string? name)
    {
        lock (gate)
        {
            data.SiteName = SanitizeName(name);
            Save();
            return data.SiteName;
        }
    }

    public WatermarkSettings SetWatermark(WatermarkSettings s)
    {
        lock (gate)
        {
            data.Watermark = s.Sanitized();
            Save();
            return Clone(data.Watermark);
        }
    }

    public HomeSettings SetHome(HomeSettings s)
    {
        lock (gate)
        {
            data.Home = s.Sanitized();
            Save();
            return new HomeSettings { Order = data.Home.Order };
        }
    }

    static WatermarkSettings Clone(WatermarkSettings w) => new()
    {
        Mode = w.Mode, Text = w.Text, Position = w.Position, Opacity = w.Opacity, SizePercent = w.SizePercent
    };

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
        File.Move(tmp, path, true);
    }
}
