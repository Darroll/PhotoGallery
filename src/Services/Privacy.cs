using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>The short privacy notice linked from the footer. Editable in Admin -> Settings -> Privacy.</summary>
public class PrivacySettings
{
    public bool Enabled { get; set; } = true;
    public string Title { get; set; } = "Privacy notice";
    /// <summary>Who to write to for a copy or removal of someone's data (email or web address); optional.</summary>
    public string Contact { get; set; } = "";
    /// <summary>{site}, {contact} and {year} are replaced when it is shown.</summary>
    public string Text { get; set; } = DefaultText;

    public const int MaxText = 20000;

    public const string DefaultText = """
        This site is run privately by its owner. It has no advertising, no social features beyond likes and comments, and nothing here is sold or shared for marketing.

        What is kept, and why
        - Likes (the heart). A like is tied to the sign-in account that gave it, or, if the person was not signed in, to their network address. The address is kept so one person cannot like a photo twice, and is never shown to anyone.
        - Comments. Only people who signed in with a verified email address can comment. Visitors see the display name chosen, never the email address. The owner can also see the email address, the time, the address and the browser used, in a private log kept for moderation.
        - Sign-in. When you sign in, the sign-in provider tells this site your name and verified email address. They are kept so your comments and likes stay yours across devices.
        - Visitor statistics (if switched on). Page and download counts with the network address, an approximate country, the browser type and an anonymous number kept in a cookie. They are deleted automatically after the retention period the owner chose.
        - Email alerts (if you subscribed). Your email address, until you unsubscribe with the link in any message.
        - Guest uploads and caption suggestions (if you used them). The name you gave and what you sent.

        Cookies
        This site uses only cookies it needs: a sign-in cookie, and an anonymous visitor number for likes and statistics. There are no advertising or tracking cookies, and nothing is loaded from advertisers. Videos that the owner embedded load from YouTube when you play them.

        Who sees it
        Only the owner of {site}. It is not given to anyone else, apart from the sign-in provider you chose to sign in with and, if email alerts are on, the mail service that delivers them.

        Your choices
        You can ask for a copy of everything held about you, or for it to be deleted (likes, comments, sign-in record, subscription and any statistics tied to your browser), by writing to {contact}. You can also remove your own likes and unsubscribe at any time yourself.

        Copyright (c) {year} {site}.
        """;

    public PrivacySettings Sanitized()
    {
        static string Clip(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }
        var text = (Text ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length > MaxText) text = text[..MaxText];
        return new PrivacySettings
        {
            Enabled = Enabled,
            Title = Clip(Title, 100) is { Length: > 0 } t ? t : "Privacy notice",
            Contact = Clip(Contact, 200),
            Text = text.Length == 0 ? DefaultText : text
        };
    }

    public object ForVisitors(string siteName)
    {
        var contact = Contact.Length > 0 ? Contact : "the site owner";
        var text = Text.Replace("{site}", siteName).Replace("{contact}", contact).Replace("{year}", DateTime.UtcNow.Year.ToString());
        return new { enabled = Enabled, title = Title, text };
    }
}

/// <summary>Finds, exports and deletes what the app holds about one person, identified by email address.</summary>
public static class Privacy
{
    /// <summary>The identifiers that all belong to one person: their sign-in ids and the anonymous browser ids tied to them.</summary>
    record Person(string Email, List<string> Subs, List<string> VisitorIds);

    public static string Norm(string? email) => (email ?? "").Trim().ToLowerInvariant();

    static async Task<Person> ResolveAsync(AppDb db, string email)
    {
        var subs = new HashSet<string>(await db.Users.Where(u => u.Email.ToLower() == email).Select(u => u.Sub).ToListAsync());
        foreach (var s in await db.Comments.Where(c => c.Email.ToLower() == email && c.Sub != "").Select(c => c.Sub).Distinct().ToListAsync()) subs.Add(s);
        foreach (var s in await db.CommentEvents.Where(c => c.ActorEmail.ToLower() == email && c.ActorSub != "").Select(c => c.ActorSub).Distinct().ToListAsync()) subs.Add(s);
        foreach (var s in await db.CommentBlocks.Where(b => b.Email.ToLower() == email && b.Sub != "").Select(b => b.Sub).Distinct().ToListAsync()) subs.Add(s);
        var keys = subs.Select(s => "u:" + s).ToList();
        var vids = new HashSet<string>();
        foreach (var v in await db.Favorites.Where(f => keys.Contains(f.IdentityKey) && f.VisitorId != "").Select(f => f.VisitorId).Distinct().ToListAsync()) vids.Add(v);
        foreach (var v in await db.LicenseAcceptances.Where(a => subs.Contains(a.Sub) && a.VisitorId != "").Select(a => a.VisitorId).Distinct().ToListAsync()) vids.Add(v);
        return new Person(email, subs.ToList(), vids.ToList());
    }

    /// <summary>People the app knows by email, for the picker in the admin page.</summary>
    public static async Task<List<object>> SearchAsync(AppDb db, string q)
    {
        q = Norm(q);
        var found = new Dictionary<string, (string Name, HashSet<string> Where)>();
        void Add(string email, string name, string where)
        {
            email = Norm(email);
            if (email == "" || (q != "" && !email.Contains(q) && !name.ToLowerInvariant().Contains(q))) return;
            if (!found.TryGetValue(email, out var e)) found[email] = e = (name, new HashSet<string>());
            if (e.Name == "" && name != "") found[email] = (name, e.Where);
            found[email].Where.Add(where);
        }
        foreach (var u in await db.Users.AsNoTracking().Select(u => new { u.Email, u.Name, u.DisplayName }).ToListAsync()) Add(u.Email, u.DisplayName != "" ? u.DisplayName : u.Name, "signed in");
        foreach (var c in await db.Comments.AsNoTracking().Where(c => c.Email != "").Select(c => c.Email).Distinct().ToListAsync()) Add(c, "", "comments");
        foreach (var s in await db.Subscribers.AsNoTracking().Select(s => s.Email).ToListAsync()) Add(s, "", "subscriber");
        foreach (var m in await db.GroupMembers.AsNoTracking().Select(m => m.Email).Distinct().ToListAsync()) Add(m, "", "group");
        foreach (var g in await db.Galleries.AsNoTracking().Where(g => g.AllowedEmails != "").Select(g => g.AllowedEmails).ToListAsync())
            foreach (var e in GalleryPeople.Parse(g)) Add(e, "", "invited");
        return found.OrderBy(x => x.Key).Take(30).Select(x => (object)new { email = x.Key, name = x.Value.Name, where = x.Value.Where.OrderBy(w => w).ToArray() }).ToList();
    }

    /// <summary>Everything held about the person, as plain data. Never includes other people's details.</summary>
    public static async Task<Dictionary<string, object>> ExportAsync(AppDb db, string email)
    {
        email = Norm(email);
        var p = await ResolveAsync(db, email);
        var keys = p.Subs.Select(s => "u:" + s).ToList();
        var galleries = await db.Galleries.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Title);
        var photos = await db.Photos.AsNoTracking().Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, x => x.Name);
        string G(int id) => galleries.GetValueOrDefault(id, "(deleted gallery)");
        string P(int id) => photos.GetValueOrDefault(id, "(removed photo)");

        var commentIds = await db.Comments.Where(c => c.Email.ToLower() == email || p.Subs.Contains(c.Sub)).Select(c => c.Id).ToListAsync();
        var result = new Dictionary<string, object>
        {
            ["email"] = email,
            ["exportedAt"] = DateTime.UtcNow,
            ["accounts"] = await db.Users.AsNoTracking().Where(u => u.Email.ToLower() == email || p.Subs.Contains(u.Sub))
                .Select(u => new { u.Email, u.Name, u.DisplayName, u.FirstSeen, u.LastSeen }).ToListAsync(),
            ["comments"] = (await db.Comments.AsNoTracking().Where(c => commentIds.Contains(c.Id)).OrderBy(c => c.CreatedAt).ToListAsync())
                .Select(c => new { c.Id, gallery = G(c.GalleryId), photo = P(c.PhotoId), c.Author, c.Text, c.CreatedAt, c.EditedAt, c.Deleted, c.Hidden, c.Ip }).ToList(),
            ["commentLog"] = (await db.CommentEvents.AsNoTracking().Where(e => commentIds.Contains(e.CommentId) || e.ActorEmail.ToLower() == email || p.Subs.Contains(e.ActorSub)).OrderBy(e => e.At).ToListAsync())
                .Select(e => new { e.CommentId, e.Kind, e.At, e.Text, e.Ip, e.UserAgent }).ToList(),
            ["likes"] = (await db.Favorites.AsNoTracking().Where(f => keys.Contains(f.IdentityKey) || (f.VisitorId != "" && p.VisitorIds.Contains(f.VisitorId))).OrderBy(f => f.CreatedAt).ToListAsync())
                .Select(f => new { gallery = G(f.GalleryId), photo = P(f.PhotoId), f.CreatedAt, f.Ip }).ToList(),
            ["captionSuggestions"] = await db.CaptionEdits.AsNoTracking().Where(c => p.Subs.Contains(c.Sub)).Select(c => new { c.Field, c.Proposed, c.Status, c.CreatedAt, c.Name }).ToListAsync(),
            ["licenseAcceptances"] = await db.LicenseAcceptances.AsNoTracking().Where(a => p.Subs.Contains(a.Sub)).Select(a => new { a.Name, a.Version, a.AcceptedAt }).ToListAsync(),
            ["subscription"] = await db.Subscribers.AsNoTracking().Where(s => s.Email.ToLower() == email).Select(s => new { s.Email, s.CreatedAt, s.ConfirmedAt, s.Unsubscribed }).ToListAsync(),
            ["commentBlock"] = await db.CommentBlocks.AsNoTracking().Where(b => b.Email.ToLower() == email || p.Subs.Contains(b.Sub)).Select(b => new { b.Name, b.Reason, b.At }).ToListAsync(),
            ["groupMemberships"] = await db.GroupMembers.AsNoTracking().Where(m => m.Email.ToLower() == email)
                .Join(db.PeopleGroups, m => m.GroupId, g => g.Id, (m, g) => g.Name).ToListAsync(),
            ["queuedEmails"] = await db.EmailOutbox.AsNoTracking().Where(m => m.ToAddr.ToLower() == email).Select(m => new { m.Subject, m.CreatedAt, m.SentAt, m.Failed }).ToListAsync(),
        };
        var invited = new List<string>();
        foreach (var g in await db.Galleries.AsNoTracking().Where(g => g.AllowedEmails != "").Select(g => new { g.Title, g.AllowedEmails }).ToListAsync())
            if (GalleryPeople.Parse(g.AllowedEmails).Contains(email)) invited.Add(g.Title);
        result["galleryInvitations"] = invited;
        var visits = await db.Visits.AsNoTracking().Where(v => v.VisitorId != "" && p.VisitorIds.Contains(v.VisitorId)).OrderBy(v => v.Time).Take(2000).ToListAsync();
        result["visits"] = visits.Select(v => new { v.Time, v.Kind, gallery = v.GalleryId == null ? "" : G(v.GalleryId.Value), v.Ip, v.Country, v.UserAgent }).ToList();
        result["note"] = "Likes given while not signed in are tied to a network address, not to a person, so they appear here only when that browser was also used to sign in.";
        return result;
    }

    public record Removed(int Accounts, int Comments, int CommentLog, int Likes, int Visits, int Other, bool BlockKept);

    /// <summary>Removes everything held about the person. Runs in one transaction. A comment block is kept (so a blocked person stays blocked) unless asked.</summary>
    public static async Task<Removed> DeleteAsync(AppDb db, string email, bool removeBlock)
    {
        email = Norm(email);
        if (email == "") throw new ArgumentException("An email address is needed.");
        var p = await ResolveAsync(db, email);
        var keys = p.Subs.Select(s => "u:" + s).ToList();
        await using var tx = await db.Database.BeginTransactionAsync();

        var commentIds = await db.Comments.Where(c => c.Email.ToLower() == email || p.Subs.Contains(c.Sub)).Select(c => c.Id).ToListAsync();
        // photographer replies stay, but no longer point at a comment that is gone
        await db.Comments.Where(c => c.ReplyToId != null && commentIds.Contains(c.ReplyToId.Value) && !commentIds.Contains(c.Id)).ExecuteUpdateAsync(s => s.SetProperty(c => c.ReplyToId, (int?)null));
        var log = await db.CommentEvents.Where(e => commentIds.Contains(e.CommentId) || e.ActorEmail.ToLower() == email || p.Subs.Contains(e.ActorSub)).ExecuteDeleteAsync();
        var comments = await db.Comments.Where(c => commentIds.Contains(c.Id)).ExecuteDeleteAsync();
        var likes = await db.Favorites.Where(f => keys.Contains(f.IdentityKey) || (f.VisitorId != "" && p.VisitorIds.Contains(f.VisitorId))).ExecuteDeleteAsync();
        var owners = p.VisitorIds.Count == 0 ? 0 : await db.FavoriteOwners.Where(o => p.VisitorIds.Contains(o.VisitorId)).ExecuteDeleteAsync();
        var visits = p.VisitorIds.Count == 0 ? 0 : await db.Visits.Where(v => p.VisitorIds.Contains(v.VisitorId)).ExecuteDeleteAsync();
        var other = owners;
        other += await db.LicenseAcceptances.Where(a => p.Subs.Contains(a.Sub)).ExecuteDeleteAsync();
        other += await db.CaptionEdits.Where(c => p.Subs.Contains(c.Sub)).ExecuteDeleteAsync();
        other += await db.Subscribers.Where(s => s.Email.ToLower() == email).ExecuteDeleteAsync();
        other += await db.GroupMembers.Where(m => m.Email.ToLower() == email).ExecuteDeleteAsync();
        other += await db.EmailOutbox.Where(m => m.ToAddr.ToLower() == email).ExecuteDeleteAsync();
        if (removeBlock) other += await db.CommentBlocks.Where(b => b.Email.ToLower() == email || p.Subs.Contains(b.Sub)).ExecuteDeleteAsync();
        var blockKept = !removeBlock && await db.CommentBlocks.AnyAsync(b => b.Email.ToLower() == email || p.Subs.Contains(b.Sub));
        var accounts = await db.Users.Where(u => u.Email.ToLower() == email || p.Subs.Contains(u.Sub)).ExecuteDeleteAsync();

        foreach (var g in await db.Galleries.Where(g => g.AllowedEmails != "").ToListAsync())
        {
            var list = GalleryPeople.Parse(g.AllowedEmails);
            if (list.RemoveAll(x => Norm(x) == email) > 0) { g.AllowedEmails = string.Join('\n', list); other++; }
        }
        // details held for removed photos can carry the person's likes and comment ids
        var deleted = new HashSet<int>(commentIds);
        foreach (var r in await db.PhotosRetired.ToListAsync())
        {
            FileIdentity.Saved? s;
            try { s = JsonSerializer.Deserialize<FileIdentity.Saved>(r.Data); } catch { continue; }
            if (s == null) continue;
            var keep = s.Likes.Where(l => !keys.Contains(l.IdentityKey) && !(l.VisitorId != "" && p.VisitorIds.Contains(l.VisitorId))).ToList();
            var ids = s.CommentIds?.Where(i => !deleted.Contains(i)).ToList();
            if (keep.Count != s.Likes.Count || (ids?.Count ?? 0) != (s.CommentIds?.Count ?? 0))
            {
                r.Data = JsonSerializer.Serialize(s with { Likes = keep, CommentIds = ids });
                other++;
            }
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new Removed(accounts, comments, log, likes, visits, other, blockKept);
    }
}
