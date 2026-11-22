namespace PhotoGallery;

/// <summary>
/// One AI model that can look at photos (for reading jersey numbers). Kinds: "ollama" (a model you run yourself),
/// "anthropic" (Claude, with an API key) and "openai" (with an API key). Consumer subscriptions such as Claude Max cannot
/// be used here: the providers' terms only allow API keys in other apps.
/// </summary>
public class VisionProvider
{
    public string Kind { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Ollama only: the server address, such as http://192.168.1.20:11434.</summary>
    public string Url { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Cloud providers only. Kept in settings.json, never sent to a browser and left out of settings exports.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>Cloud providers only: the admin ticked that photos (of children, often) will be sent to this company.</summary>
    public bool SendPhotosAgreed { get; set; }
    /// <summary>How long to wait for this provider's answer on one photo, in seconds. 0 = no limit (Stop still ends it).</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    public const int DefaultTimeoutSeconds = 300;

    public static readonly string[] Kinds = ["ollama", "anthropic", "openai"];
    public bool IsCloud => Kind != "ollama";

    public static string DefaultModel(string kind) => kind switch
    {
        "ollama" => "qwen3-vl:8b",
        "anthropic" => "claude-opus-5-5",
        _ => "",
    };

    public static string Label(string kind) => kind switch
    {
        "ollama" => "Ollama (your own server)",
        "anthropic" => "Claude (Anthropic API key)",
        "openai" => "OpenAI (API key)",
        _ => kind,
    };

    /// <summary>Set up enough to be tried: switched on, with an address or key, a model, and (for cloud) the agreement ticked.</summary>
    public bool Ready => Enabled && Model.Length > 0 && (IsCloud ? ApiKey.Length > 0 && SendPhotosAgreed : Url.Length > 0);

    public VisionProvider Clone() => (VisionProvider)MemberwiseClone();
}

/// <summary>Automatic tagging settings: the providers in the order they are tried, and how suggestions are handled.</summary>
public class VisionSettings
{
    /// <summary>Tried top to bottom. Only providers that are <see cref="VisionProvider.Ready"/> are tried, so with one set up there is no fallback.</summary>
    public List<VisionProvider> Providers { get; set; } = new();
    /// <summary>Suggestions at or above this confidence (0-100) are tagged straight away. 0 = everything waits for review (the default).</summary>
    public int AutoApproveAt { get; set; }

    public VisionSettings Sanitized(string? captionUrl = null)
    {
        static string T(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }
        var list = new List<VisionProvider>();
        foreach (var p in Providers ?? new())
        {
            var kind = (p?.Kind ?? "").Trim().ToLowerInvariant();
            if (p == null || !VisionProvider.Kinds.Contains(kind) || list.Any(x => x.Kind == kind)) continue;
            var url = T(p.Url, 200).TrimEnd('/');
            if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))) url = "";
            list.Add(new VisionProvider
            {
                Kind = kind, Enabled = p.Enabled, Url = kind == "ollama" ? url : "",
                Model = T(p.Model, 100), ApiKey = kind == "ollama" ? "" : T(p.ApiKey, 300),
                SendPhotosAgreed = kind != "ollama" && p.SendPhotosAgreed,
                TimeoutSeconds = Math.Clamp(p.TimeoutSeconds, 0, 86400),
            });
        }
        // every kind is always listed, so the settings page can show all of them; new ones go at the end, switched off
        foreach (var kind in VisionProvider.Kinds)
            if (!list.Any(x => x.Kind == kind))
                list.Add(new VisionProvider { Kind = kind, Model = VisionProvider.DefaultModel(kind), Url = kind == "ollama" ? (captionUrl ?? "").TrimEnd('/') : "" });
        return new VisionSettings { Providers = list, AutoApproveAt = Math.Clamp(AutoApproveAt, 0, 100) };
    }

    public VisionSettings Clone() => new() { Providers = Providers.Select(p => p.Clone()).ToList(), AutoApproveAt = AutoApproveAt };

    /// <summary>Keeps the stored API key when the browser sends the key field empty (the page never sees the stored key).</summary>
    public VisionSettings WithStoredKeys(VisionSettings stored)
    {
        var s = Clone();
        foreach (var p in s.Providers)
            if (p.IsCloud && p.ApiKey.Length == 0 && stored.Providers.FirstOrDefault(x => x.Kind == p.Kind) is { } old) p.ApiKey = old.ApiKey;
        return s;
    }

    /// <summary>The providers that will be tried, in order.</summary>
    public List<VisionProvider> Chain => Providers.Where(p => p.Ready).ToList();

    /// <summary>What the settings page may see: everything except the keys.</summary>
    public object Public() => new
    {
        autoApproveAt = AutoApproveAt,
        providers = Providers.Select(p => new { p.Kind, label = VisionProvider.Label(p.Kind), p.Enabled, p.Url, p.Model, keySet = p.ApiKey.Length > 0, p.SendPhotosAgreed, p.TimeoutSeconds, cloud = p.IsCloud, p.Ready }),
    };
}
