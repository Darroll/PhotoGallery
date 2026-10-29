using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// The people allowed into a Specific people gallery: email addresses listed on the gallery, plus everyone in the groups invited to it.
/// The addresses can belong to people who have not signed in yet. A visitor gets in when they sign in (single sign-on)
/// with one of these addresses and the provider has verified it.
/// </summary>
public static class GalleryPeople
{
    public const int Max = 200;

    /// <summary>Lower-cases and trims each address and drops repeats. Returns null if any entry is not an email address.</summary>
    public static List<string>? Clean(IEnumerable<string>? input)
    {
        var list = new List<string>();
        foreach (var raw in input ?? [])
        {
            var email = (raw ?? "").Trim().ToLowerInvariant();
            if (email.Length == 0) continue;
            if (!Subscribers.ValidAddress(email)) return null;
            if (!list.Contains(email)) list.Add(email);
        }
        return list;
    }

    /// <summary>The stored list, one address per line.</summary>
    public static List<string> Parse(string stored) =>
        stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The signed-in user's email address (lower case), only when the provider has verified it.</summary>
    public static string? VerifiedEmail(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        if (!user.HasClaim("email_verified", "true")) return null;
        var email = user.FindFirst(ClaimTypes.Email)?.Value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(email) ? null : email;
    }

    /// <summary>True when the user's verified email is on the gallery's own list or in a group invited to it.</summary>
    public static async Task<bool> AllowsAsync(AppDb db, Gallery g, ClaimsPrincipal user)
    {
        var email = VerifiedEmail(user);
        if (email == null) return false;
        if (Parse(g.AllowedEmails).Contains(email)) return true;
        return await db.GalleryGroups.AnyAsync(gg => gg.GalleryId == g.Id
            && db.GroupMembers.Any(m => m.GroupId == gg.GroupId && m.Email == email));
    }
}
