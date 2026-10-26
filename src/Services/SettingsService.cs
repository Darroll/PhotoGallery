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

/// <summary>App-level settings kept in DATA_DIR/settings.json (editable from the admin Settings page).</summary>
public class SettingsService
{
    readonly string path;
    readonly object gate = new();
    WatermarkSettings watermark;

    public SettingsService(AppConfig cfg)
    {
        path = Path.Combine(cfg.DataDir, "settings.json");
        try
        {
            if (File.Exists(path))
                watermark = (JsonSerializer.Deserialize<WatermarkSettings>(File.ReadAllText(path), JsonOpts) ?? new()).Sanitized();
            else
            {
                // first run: honour the old WATERMARK_TEXT env var once, then it lives in settings.json
                watermark = string.IsNullOrWhiteSpace(cfg.LegacyWatermarkText)
                    ? new WatermarkSettings()
                    : new WatermarkSettings { Mode = "text", Text = cfg.LegacyWatermarkText };
                Save();
            }
        }
        catch { watermark = new WatermarkSettings(); }
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public WatermarkSettings Watermark
    {
        get { lock (gate) return Clone(watermark); }
    }

    public WatermarkSettings SetWatermark(WatermarkSettings s)
    {
        lock (gate)
        {
            watermark = s.Sanitized();
            Save();
            return Clone(watermark);
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
        File.WriteAllText(tmp, JsonSerializer.Serialize(watermark, JsonOpts));
        File.Move(tmp, path, true);
    }
}
