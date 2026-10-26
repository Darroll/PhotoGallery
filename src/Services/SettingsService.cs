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
    public WatermarkSettings Watermark { get; set; } = new();
    public HomeSettings Home { get; set; } = new();
    public StatsSettings Stats { get; set; } = new();
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
            }
            else
            {
                // first run: honour the old WATERMARK_TEXT env var once, then it lives in settings.json
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
        get { lock (gate) return new StatsSettings { Enabled = data.Stats.Enabled, RetentionDays = data.Stats.RetentionDays, IncludeAdmin = data.Stats.IncludeAdmin }; }
    }

    public StatsSettings SetStats(StatsSettings s)
    {
        lock (gate)
        {
            data.Stats = s.Sanitized();
            Save();
            return new StatsSettings { Enabled = data.Stats.Enabled, RetentionDays = data.Stats.RetentionDays, IncludeAdmin = data.Stats.IncludeAdmin };
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
