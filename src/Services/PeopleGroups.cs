using System.Text.RegularExpressions;

namespace PhotoGallery;

/// <summary>
/// A named list of people, kept in the app (not in the identity provider), for inviting many people to Specific people galleries at once.
/// A person (an email address) can belong to any number of groups.
/// </summary>
public class PeopleGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One person (lower-case email address) in a group.</summary>
public class GroupMember
{
    public int GroupId { get; set; }
    public string Email { get; set; } = "";
}

/// <summary>Invites a whole group to a Specific people gallery.</summary>
public class GalleryGroup
{
    public int GalleryId { get; set; }
    public int GroupId { get; set; }
}

public static class PeopleGroups
{
    public const int MaxNameLength = 60;
    public const int MaxMembers = 1000;

    /// <summary>A group name as typed: spaces collapsed and clipped.</summary>
    public static string CleanName(string? s)
    {
        var t = Regex.Replace((s ?? "").Trim(), @"\s+", " ");
        return t.Length > MaxNameLength ? t[..MaxNameLength].TrimEnd() : t;
    }
}
