using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PhotoGallery;

/// <summary>
/// "Save to phone": a visitor picks photos and the browser hands them to the phone's share sheet, which saves them
/// to the camera roll. Starting a save counts as one download (like a ZIP), and returns a short-lived ticket that
/// lets the browser fetch each of those photos without counting each one again.
/// </summary>
public class PhoneSaves
{
    public const int MaxPhotos = 500;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    record Ticket(int GalleryId, HashSet<int> PhotoIds, DateTime Expires);
    readonly ConcurrentDictionary<string, Ticket> tickets = new();

    public string Start(int galleryId, IEnumerable<int> photoIds)
    {
        var now = DateTime.UtcNow;
        foreach (var kv in tickets) if (kv.Value.Expires <= now) tickets.TryRemove(kv.Key, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        tickets[token] = new Ticket(galleryId, photoIds.ToHashSet(), now + Lifetime);
        return token;
    }

    /// <summary>True when the ticket is current and covers this photo of this gallery.</summary>
    public bool Covers(string? token, int galleryId, int photoId) =>
        token != null && tickets.TryGetValue(token, out var t) && t.Expires > DateTime.UtcNow && t.GalleryId == galleryId && t.PhotoIds.Contains(photoId);

    /// <summary>Formats a phone's photo app saves as they are. Anything else is sent as a JPEG.</summary>
    public static bool PhoneReady(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".png";

    /// <summary>The size a phone copy is made at: the largest offered size up to 4096 px, or the smallest offered when all are larger.</summary>
    public static int? PhoneSize(int[] sizes) =>
        sizes.Length == 0 ? null : sizes.Where(s => s <= 4096).DefaultIfEmpty(sizes.Min()).Max();
}
