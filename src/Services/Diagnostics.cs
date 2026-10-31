using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoGallery;

/// <summary>Keeps the last few warnings and errors in memory so the diagnostics bundle can include them.</summary>
public sealed class RecentLog : ILoggerProvider
{
    public record Entry(DateTime Time, LogLevel Level, string Category, string Message);

    const int Max = 60;
    readonly object gate = new();
    readonly Queue<Entry> items = new();

    public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
    public void Dispose() { }

    public IReadOnlyList<Entry> Snapshot() { lock (gate) return items.ToArray(); }

    void Add(Entry e) { lock (gate) { items.Enqueue(e); while (items.Count > Max) items.Dequeue(); } }

    sealed class L(RecentLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var msg = fmt(state, ex);
            if (ex != null) msg += " | " + ex.GetType().Name + ": " + ex.Message;
            owner.Add(new Entry(DateTime.UtcNow, level, category, msg));
        }
    }
}

public static class Diagnostics
{
    static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    static readonly Regex Secret = new(@"(?i)(password|passwd|secret|token|key|code|authorization|cookie)(\s*[=:]\s*|\s+is\s+)\S+", RegexOptions.Compiled);

    /// <summary>Removes email addresses and anything that looks like a secret, so a pasted bundle is safe to post publicly.</summary>
    public static string Redact(string s)
    {
        s = Email.Replace(s, "<email>");
        s = Secret.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "<removed>");
        return s.Length > 500 ? s[..500] + "..." : s;
    }

    static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : (string.IsNullOrEmpty(url) ? "(none)" : "(set)");

    public static string Build(AppConfig cfg, SettingsService settings, SsoConfig sso, RecentLog log, long uptimeSeconds, int galleries, int photos)
    {
        var sb = new StringBuilder();
        void H(string t) { sb.AppendLine(); sb.AppendLine("## " + t); }
        void L(string k, object? v) => sb.AppendLine($"- {k}: {v}");

        sb.AppendLine("PhotoGallery diagnostics (secrets, passwords and email addresses are removed)");
        sb.AppendLine("Generated " + DateTime.UtcNow.ToString("u"));

        H("App");
        L("Version", cfg.Version); L("Commit", cfg.Commit); L("Built", string.IsNullOrEmpty(cfg.BuildDate) ? "(development)" : cfg.BuildDate);
        L("Running for", TimeSpan.FromSeconds(uptimeSeconds).ToString(@"d\.hh\:mm\:ss"));
        L("Galleries / photos", $"{galleries} / {photos}");

        H("Environment");
        L("OS", RuntimeInformation.OSDescription); L(".NET", RuntimeInformation.FrameworkDescription); L("Architecture", RuntimeInformation.OSArchitecture);
        L("exiftool", cfg.ExifTool != null ? "found" : "not found");
        L("Library folder", cfg.LibraryRoot); L("Data folder", cfg.DataDir);
        var access = FolderAccess.Probe(cfg);
        L("Library readable", access.LibraryReadable + (access.LibraryError != null ? " (" + Redact(access.LibraryError) + ")" : ""));
        L("Data folder writable", access.DataWritable + (access.DataError != null ? " (" + Redact(access.DataError) + ")" : ""));

        H("Settings (container variables and in-app settings)");
        L("Admin password in container", cfg.AdminPasswordSet ? "set" : "not set (chosen in the app)");
        L("Image sizes", string.Join(",", cfg.Sizes)); L("Max upload MB", cfg.MaxUploadMb); L("Rescan minutes", cfg.RescanMinutes);
        L("Backup hours / daily / weekly", $"{cfg.BackupHours} / {cfg.BackupDaily} / {cfg.BackupWeekly}");
        L("Backup copy folder", cfg.BackupCopyDir != null ? "set" : "off");
        L("Trust proxy", cfg.TrustProxy);
        L("Trusted proxies", string.Join(", ", cfg.TrustedNetworks.Select(n => n.Net + "/" + n.Prefix)));
        L("Single sign-on", sso.Enabled ? "on (provider host " + Host(sso.Authority) + ")" : "off");
        L("Email alerts", settings.Email.Enabled ? $"on (server {settings.Email.Host}:{settings.Email.Port}, security {settings.Email.Security}, preset {(settings.Email.Preset == "" ? "none" : settings.Email.Preset)})" : "off");
        L("Automatic captions", cfg.CaptionUrl != null ? "on (model " + cfg.CaptionModel + ")" : "off");
        L("Link previews", settings.LinkPreviews.Enabled);
        L("Visitor statistics", settings.Stats.Enabled);
        L("Watermark", settings.Watermark.Mode);

        H("Recent warnings and errors (newest last)");
        var entries = log.Snapshot();
        if (entries.Count == 0) sb.AppendLine("- none since the app started");
        foreach (var e in entries)
            sb.AppendLine($"- {e.Time:HH:mm:ss}Z {e.Level} {e.Category.Split('.').Last()}: {Redact(e.Message)}");
        return sb.ToString();
    }
}
