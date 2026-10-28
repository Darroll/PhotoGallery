using System.Text.Json;

namespace PhotoGallery;

/// <summary>Per-user settings saved with a signed-in account, so they follow the person to any device. Stored as JSON in Users.Preferences.</summary>
public class UserPreferences
{
    /// <summary>Theme id; null means "no choice yet" (use the site default). "auto" is a real choice: follow the device.</summary>
    public string? Theme { get; set; }

    static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static UserPreferences Parse(string? json)
    {
        try { return (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<UserPreferences>(json, Opts))?.Sanitized() ?? new(); }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(Sanitized(), Opts);

    /// <summary>Drops values that are no longer valid (for example a theme that was removed).</summary>
    public UserPreferences Sanitized() => new() { Theme = ThemeSettings.IsValid(Theme) ? Theme : null };
}
