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

/// <summary>Schedule settings the admin can change in the app. Null means "not chosen here": the container variable or the built-in default applies.</summary>
public class TuningSettings
{
    public int? RescanMinutes { get; set; }
    public int? BackupHours { get; set; }
    public int? BackupDaily { get; set; }
    public int? BackupWeekly { get; set; }

    public TuningSettings Sanitized() => new()
    {
        RescanMinutes = RescanMinutes is { } r ? Math.Clamp(r, 0, 1440) : null,
        BackupHours = BackupHours is { } h ? Math.Clamp(h, 0, 720) : null,
        BackupDaily = BackupDaily is { } d ? Math.Clamp(d, 1, 60) : null,
        BackupWeekly = BackupWeekly is { } w ? Math.Clamp(w, 0, 52) : null
    };
}

/// <summary>Follow a player, site-wide: parents may follow a team's player (team and number) and get an email about new photos of them in every gallery of that team. A gallery can say otherwise.</summary>
public class FollowSettings
{
    public bool Enabled { get; set; }
    public FollowSettings Sanitized() => new() { Enabled = Enabled };
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
    public LinkPreviewSettings LinkPreviews { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public SetupSettings Setup { get; set; } = new();
    public SsoSettings Sso { get; set; } = new();
    public TuningSettings Tuning { get; set; } = new();
    public VisionSettings Vision { get; set; } = new();
    public FollowSettings Follow { get; set; } = new();
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
        appCfg = cfg;
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
                data.LinkPreviews = (data.LinkPreviews ?? new()).Sanitized();
                data.Updates = (data.Updates ?? new()).Sanitized();
                data.Privacy = (data.Privacy ?? new()).Sanitized();
                data.Setup = (data.Setup ?? new()).Sanitized();
                data.Sso = (data.Sso ?? new()).Sanitized();
                data.Tuning = (data.Tuning ?? new()).Sanitized();
                data.Vision = (data.Vision ?? new()).Sanitized(cfg.CaptionUrl);
                cfg.ApplyTuning(data.Tuning);
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

    public SsoSettings Sso
    {
        get { lock (gate) return data.Sso.Clone(); }
    }

    public SsoSettings SetSso(SsoSettings s)
    {
        lock (gate)
        {
            data.Sso = s.Sanitized();
            Save();
            return data.Sso.Clone();
        }
    }

    /// <summary>A test sign-in worked as an admin with a verified email.</summary>
    public void MarkSsoTested()
    {
        lock (gate)
        {
            data.Sso.TestedAt = DateTime.UtcNow;
            Save();
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

    public LinkPreviewSettings LinkPreviews
    {
        get { lock (gate) return data.LinkPreviews.Sanitized(); }
    }

    /// <summary>Automatic tagging providers. API keys are included: never send this to a browser (use <see cref="VisionSettings.Public"/>).</summary>
    public VisionSettings Vision
    {
        get { lock (gate) return (data.Vision ?? new()).Sanitized(appCfg.CaptionUrl); }
    }

    public VisionSettings SetVision(VisionSettings s)
    {
        lock (gate)
        {
            data.Vision = s.Sanitized(appCfg.CaptionUrl).WithStoredKeys((data.Vision ?? new()).Sanitized(appCfg.CaptionUrl));
            Save();
            return data.Vision.Clone();
        }
    }

    public PrivacySettings Privacy
    {
        get { lock (gate) return data.Privacy.Sanitized(); }
    }

    readonly AppConfig appCfg;

    public TuningSettings Tuning
    {
        get { lock (gate) return data.Tuning.Sanitized(); }
    }

    /// <summary>Saves the schedule settings and applies them at once. Values a container variable fixes are kept out of the file.</summary>
    public TuningSettings SetTuning(TuningSettings s)
    {
        lock (gate)
        {
            var t = s.Sanitized();
            if (appCfg.Locked.Contains("rescanMinutes")) t.RescanMinutes = data.Tuning.RescanMinutes;
            if (appCfg.Locked.Contains("backupHours")) t.BackupHours = data.Tuning.BackupHours;
            if (appCfg.Locked.Contains("backupDaily")) t.BackupDaily = data.Tuning.BackupDaily;
            if (appCfg.Locked.Contains("backupWeekly")) t.BackupWeekly = data.Tuning.BackupWeekly;
            data.Tuning = t;
            Save();
            appCfg.ApplyTuning(t);
            return t.Sanitized();
        }
    }

    public PrivacySettings SetPrivacy(PrivacySettings s)
    {
        lock (gate)
        {
            data.Privacy = s.Sanitized();
            Save();
            return data.Privacy.Sanitized();
        }
    }

    public SetupSettings Setup
    {
        get { lock (gate) return data.Setup.Sanitized(); }
    }

    public SetupSettings SetSetup(SetupSettings s)
    {
        lock (gate)
        {
            data.Setup = s.Sanitized();
            Save();
            return data.Setup.Sanitized();
        }
    }

    public UpdateSettings Updates
    {
        get { lock (gate) return data.Updates.Sanitized(); }
    }

    public UpdateSettings SetUpdates(UpdateSettings s)
    {
        lock (gate)
        {
            data.Updates = s.Sanitized();
            Save();
            return data.Updates.Sanitized();
        }
    }

    public LinkPreviewSettings SetLinkPreviews(LinkPreviewSettings s)
    {
        lock (gate)
        {
            data.LinkPreviews = s.Sanitized();
            Save();
            return data.LinkPreviews.Sanitized();
        }
    }

    public FollowSettings Follow
    {
        get { lock (gate) return (data.Follow ?? new()).Sanitized(); }
    }

    public FollowSettings SetFollow(FollowSettings s)
    {
        lock (gate)
        {
            data.Follow = s.Sanitized();
            Save();
            return data.Follow.Sanitized();
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

    /// <summary>All settings as JSON for moving to another server. Passwords and secrets are left out, and a sign-in test has to be repeated on the new address.</summary>
    public string Export()
    {
        lock (gate)
        {
            var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(data, JsonOpts), JsonOpts)!;
            copy.Email.Password = "";
            copy.Sso.ClientSecret = "";
            copy.Sso.TestedAt = null;
            foreach (var p in copy.Vision?.Providers ?? new()) p.ApiKey = "";
            return JsonSerializer.Serialize(new { format = "photogallery-settings", version = 1, settings = copy }, JsonOpts);
        }
    }

    /// <summary>Applies an exported file. Returns the names of the sections that were replaced, or null when the file is not a settings export.</summary>
    public List<string>? Import(string json)
    {
        AppSettings? incoming;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var f) || f.GetString() != "photogallery-settings") return null;
            if (!root.TryGetProperty("settings", out var s) || s.ValueKind != JsonValueKind.Object) return null;
            incoming = JsonSerializer.Deserialize<AppSettings>(s.GetRawText(), JsonOpts);
            if (incoming == null) return null;
            var names = new List<string>();
            lock (gate)
            {
                bool Has(string n) { var has = s.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Object; if (has) names.Add(n); return has; }
                if (s.TryGetProperty("SiteName", out var sn) && sn.ValueKind == JsonValueKind.String) { data.SiteName = SanitizeName(sn.GetString()); names.Add("SiteName"); }
                if (Has("Watermark")) data.Watermark = incoming.Watermark.Sanitized();
                if (Has("Home")) data.Home = incoming.Home.Sanitized();
                if (Has("Stats")) data.Stats = incoming.Stats.Sanitized();
                if (Has("Email")) data.Email = incoming.Email.WithStoredPassword(data.Email).Sanitized();
                if (Has("License")) data.License = incoming.License.Sanitized();
                if (Has("Theme")) data.Theme = incoming.Theme.Sanitized();
                if (Has("LinkPreviews")) data.LinkPreviews = incoming.LinkPreviews.Sanitized();
                if (Has("Updates")) data.Updates = incoming.Updates.Sanitized();
                if (Has("Privacy")) data.Privacy = incoming.Privacy.Sanitized();
                if (Has("Follow")) data.Follow = (incoming.Follow ?? new()).Sanitized();
                if (Has("Vision")) data.Vision = (incoming.Vision ?? new()).Sanitized(appCfg.CaptionUrl).WithStoredKeys((data.Vision ?? new()).Sanitized(appCfg.CaptionUrl));
                if (Has("Sso"))
                {
                    var keep = data.Sso.ClientSecret;
                    data.Sso = incoming.Sso.Sanitized();
                    if (data.Sso.ClientSecret.Length == 0) data.Sso.ClientSecret = keep;
                    data.Sso.TestedAt = null;
                    data.Sso.HidePasswordLogin = false;   // only allowed after a test sign-in on this server
                }
                Save();
            }
            return names;
        }
        catch (JsonException) { return null; }
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
