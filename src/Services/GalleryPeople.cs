using System.Security.Claims;

namespace PhotoGallery;

/// <summary>
/// The people allowed into a Restricted gallery. The admin lists email addresses, which can be people who have not signed in yet.
/// A visitor gets in when they sign in (single sign-on) with one of these addresses and the provider has verified it.
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

    /// <summary>True when the signed-in user has a verified email address that is on the gallery's list.</summary>
    public static bool Allows(Gallery g, ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        if (!user.HasClaim("email_verified", "true")) return false;
        var email = user.FindFirst(ClaimTypes.Email)?.Value?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(email) && Parse(g.AllowedEmails).Contains(email);
    }
}
