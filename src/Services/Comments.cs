using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// A comment on one photo. Comments are flat (no threads); a reply only names the comment it answers. The email address is the
/// verified one from the sign-in provider and is kept for the log; it is never sent to a browser. A deleted comment keeps its
/// row, so the admin log can still show what was said.
/// </summary>
public class Comment
{
    public int Id { get; set; }
    public int GalleryId { get; set; }
    public int PhotoId { get; set; }
    /// <summary>The provider's id of the person. Empty for the photographer when signed in with the admin password.</summary>
    public string Sub { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>What visitors see: the person's chosen display name, or "Photographer".</summary>
    public string Author { get; set; } = "";
    /// <summary>A reply from the photographer (shown with a badge).</summary>
    public bool IsAdmin { get; set; }
    public int? ReplyToId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
    /// <summary>Removed by the author (or by the admin). Not shown, still logged.</summary>
    public bool Deleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    /// <summary>Hidden by the admin: not shown to visitors, can be shown again.</summary>
    public bool Hidden { get; set; }
    /// <summary>Where it was written from. Stored for the log, never shown on any page.</summary>
    public string Ip { get; set; } = "";
    public string UserAgent { get; set; } = "";
}

/// <summary>One line of the comment log: who did what to which comment, and what the text was.</summary>
public class CommentEvent
{
    public long Id { get; set; }
    public int CommentId { get; set; }
    public int GalleryId { get; set; }
    public int PhotoId { get; set; }
    /// <summary>post, edit, delete, hide, unhide, block, unblock</summary>
    public string Kind { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string ActorSub { get; set; } = "";
    public string ActorEmail { get; set; } = "";
    public string ActorName { get; set; } = "";
    /// <summary>The text written (post), the text before the change (edit), or the text that was removed (delete, hide).</summary>
    public string Text { get; set; } = "";
    public string Ip { get; set; } = "";
    public string UserAgent { get; set; } = "";
}

public static class Comments
{
    public const int MaxLength = 1000;
    public const int MaxName = 40;
    public static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(15);
    public const string AdminName = "Photographer";

    /// <summary>Signed in through the provider with an email address the provider has verified.</summary>
    public static bool IsVerifiedMember(HttpContext ctx) =>
        AuthApi.Sub(ctx) is { Length: > 0 } && ctx.User.HasClaim("email_verified", "true") && ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value is { Length: > 0 };

    public static string Email(HttpContext ctx) => (ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "").ToLowerInvariant();

    public static string CleanText(string? s)
    {
        s = (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var kept = new string(s.Where(c => c == '\n' || !char.IsControl(c)).ToArray());
        while (kept.Contains("\n\n\n")) kept = kept.Replace("\n\n\n", "\n\n");
        return kept;
    }

    /// <summary>null when the text is fine, otherwise what to tell the person.</summary>
    public static string? Problem(string text)
    {
        if (text.Length == 0) return "Write something first.";
        if (text.Length > MaxLength) return $"Comments can be up to {MaxLength} characters.";
        var links = System.Text.RegularExpressions.Regex.Matches(text, @"https?://|www\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        if (links > 2) return "Comments can have at most two links.";
        return null;
    }

    /// <summary>A name made of letters and spaces that cannot be mistaken for an email address or for the photographer.</summary>
    public static string? CleanName(string? s, out string? problem)
    {
        problem = null;
        var name = string.Join(' ', new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length < 2) { problem = "Choose a name of at least two letters."; return null; }
        if (name.Length > MaxName) { problem = $"A name can be up to {MaxName} characters."; return null; }
        if (name.Contains('@') || name.Contains("://") || name.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) { problem = "Use a name, not an email address or a web address."; return null; }
        if (name.Equals(AdminName, StringComparison.OrdinalIgnoreCase) || name.Equals("Admin", StringComparison.OrdinalIgnoreCase) || name.Equals("Administrator", StringComparison.OrdinalIgnoreCase))
        { problem = "That name is kept for the photographer."; return null; }
        return name;
    }

    /// <summary>A starting point for the name box: the account name, unless it is (or looks like) an email address.</summary>
    public static string SuggestName(string? accountName)
    {
        var n = CleanName(accountName, out _);
        return n ?? "";
    }

    public static CommentEvent Event(string kind, Comment c, string actorSub, string actorEmail, string actorName, string text, HttpContext ctx) => new()
    {
        CommentId = c.Id, GalleryId = c.GalleryId, PhotoId = c.PhotoId, Kind = kind, ActorSub = actorSub, ActorEmail = actorEmail, ActorName = actorName,
        Text = text, Ip = ActivityLog.ClientIp(ctx), UserAgent = Agent(ctx)
    };

    public static string Agent(HttpContext ctx)
    {
        var ua = ctx.Request.Headers.UserAgent.ToString();
        return ua.Length > 300 ? ua[..300] : ua;
    }

    /// <summary>Photo id to the number of comments people can see, for the gallery page.</summary>
    public static async Task<Dictionary<int, int>> CountsAsync(AppDb db, int galleryId) =>
        await db.Comments.AsNoTracking().Where(c => c.GalleryId == galleryId && !c.Deleted && !c.Hidden)
            .GroupBy(c => c.PhotoId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
}
