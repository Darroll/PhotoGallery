using System.Text.RegularExpressions;

namespace PhotoGallery;

/// <summary>
/// Word search for photo descriptions and the rest of a photo's text: "header in the rain" finds "Emma (#10) wins a header
/// as rain pours down". Every word that matters must appear (small words like "the" and "in" are left out), each at the
/// start of a word ("rain" finds "rainy" but not "training"), in any order and in any of the photo's fields. A plural also
/// finds the singular ("headers" finds "header"). app.js has the same rules for the search box on a gallery.
/// </summary>
public static class TextSearch
{
    static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "in", "on", "at", "of", "with", "and", "or", "is", "are", "to", "for", "by", "from", "into", "her", "his", "their", "its",
        "photo", "photos", "picture", "pictures", "pic", "pics",
    };

    /// <summary>The words of a search that matter, in lower case. Empty when there are none (only small words, or nothing typed).</summary>
    public static List<string> Words(string? query) =>
        Regex.Split((query ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}#]+")
            .Select(w => w.Trim('#').Length == 0 ? "" : w)
            .Where(w => w.Length > 0 && !Small.Contains(w)).Distinct().Take(12).ToList();

    /// <summary>Every word appears in these fields together, each at the start of a word.</summary>
    public static bool Matches(IReadOnlyList<string> words, IEnumerable<string?> fields)
    {
        if (words.Count == 0) return false;
        var text = string.Join(" \n ", fields.Where(f => !string.IsNullOrEmpty(f))).ToLowerInvariant();
        return words.All(w => Starts(text, w) || (w.Length > 3 && w.EndsWith('s') && Starts(text, w[..^1])));
    }

    static bool Starts(string text, string word) => Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(word));
}
