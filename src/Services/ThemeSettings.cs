namespace PhotoGallery;

public record ThemeInfo(string Id, string Label);

/// <summary>Site-wide default theme and whether visitors may pick their own (the pick is kept in their browser).</summary>
public class ThemeSettings
{
    /// <summary>"auto" follows the visitor's device (light or dark); the others are fixed. Each non-auto theme has a block in style.css.</summary>
    public static readonly ThemeInfo[] All =
    [
        new("auto", "Auto (device)"), new("light", "Light"), new("dark", "Dark"), new("midnight", "Midnight"),
        new("white", "Gallery White"), new("warm", "Warm"), new("contrast", "High contrast")
    ];

    public string Default { get; set; } = "auto";
    public bool AllowVisitorChoice { get; set; } = true;

    public static bool IsValid(string? id) => id != null && All.Any(t => t.Id == id);

    public ThemeSettings Sanitized() => new() { Default = IsValid(Default) ? Default : "auto", AllowVisitorChoice = AllowVisitorChoice };

    public ThemeSettings Clone() => new() { Default = Default, AllowVisitorChoice = AllowVisitorChoice };

    public object ForVisitors() => new { @default = Default, allowChoice = AllowVisitorChoice, themes = All };
}
