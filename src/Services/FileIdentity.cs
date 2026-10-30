using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// What a photo's likes, tags and caption were attached to when its file disappeared from the library. It is held for
/// <see cref="FileIdentity.KeepDays"/> days so the data comes back if the same file turns up again (moved to another
/// folder or gallery, or put back after a mistake). Matching is by <see cref="Photo.Fingerprint"/>, never by name.
/// </summary>
public class PhotoRetired
{
    public int Id { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Name { get; set; } = "";
    public int GalleryId { get; set; }
    /// <summary>JSON, see <see cref="FileIdentity.Saved"/>.</summary>
    public string Data { get; set; } = "{}";
    public DateTime RetiredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Gives a photo an identity that survives a rename or move. The fingerprint is the file size plus a hash of its first and
/// last 64 KB: cheap to read on a network share, and two different photos do not share it in practice. Changing the file's
/// contents (editing it in place) gives it a new fingerprint, so an edited file is treated as a new photo, as before.
/// </summary>
public static class FileIdentity
{
    public const int KeepDays = 90;
    const int RecentDays = 7;
    const int Chunk = 64 * 1024;

    public static string Fingerprint(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = fs.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[Chunk];
        var n = fs.Read(buf, 0, (int)Math.Min(Chunk, len));
        sha.AppendData(buf, 0, n);
        if (len > Chunk)
        {
            fs.Seek(Math.Max(Chunk, len - Chunk), SeekOrigin.Begin);
            n = fs.Read(buf, 0, buf.Length);
            sha.AppendData(buf, 0, n);
        }
        return len + ":" + Convert.ToHexString(sha.GetHashAndReset())[..32];
    }

    public record SavedLike(string VisitorId, string IdentityKey, string Ip, DateTime CreatedAt);
    /// <summary>CommentIds are the comments written on the photo; they are re-pointed, not copied, so the log stays intact.</summary>
    public record Saved(string Caption, string AltText, string CaptionSource, List<int> TagIds, List<SavedLike> Likes, List<int>? CommentIds = null)
    {
        public bool IsEmpty => Caption == "" && AltText == "" && TagIds.Count == 0 && Likes.Count == 0 && (CommentIds?.Count ?? 0) == 0;
    }

    /// <summary>Takes what a visitor or the admin attached to these photos, so it can be put back on the same file elsewhere.</summary>
    public static async Task<Dictionary<int, Saved>> CollectAsync(AppDb db, IReadOnlyCollection<Photo> photos)
    {
        var ids = photos.Select(p => p.Id).ToList();
        var tags = (await db.PhotoTags.AsNoTracking().Where(t => ids.Contains(t.PhotoId)).ToListAsync())
            .GroupBy(t => t.PhotoId).ToDictionary(x => x.Key, x => x.Select(t => t.TagId).ToList());
        var likes = (await db.Favorites.AsNoTracking().Where(f => ids.Contains(f.PhotoId)).ToListAsync())
            .GroupBy(f => f.PhotoId).ToDictionary(x => x.Key, x => x.Select(f => new SavedLike(f.VisitorId, f.IdentityKey, f.Ip, f.CreatedAt)).ToList());
        var comments = (await db.Comments.AsNoTracking().Where(c => ids.Contains(c.PhotoId)).Select(c => new { c.PhotoId, c.Id }).ToListAsync())
            .GroupBy(c => c.PhotoId).ToDictionary(x => x.Key, x => x.Select(c => c.Id).ToList());
        return photos.ToDictionary(p => p.Id, p => new Saved(p.Caption, p.AltText, p.CaptionSource,
            tags.GetValueOrDefault(p.Id) ?? [], likes.GetValueOrDefault(p.Id) ?? [], comments.GetValueOrDefault(p.Id) ?? []));
    }

    /// <summary>Adds saved data to a photo without overwriting anything it already has.</summary>
    public static async Task ApplyAsync(AppDb db, Photo target, Saved s)
    {
        if (target.Caption == "" && s.Caption != "") { target.Caption = s.Caption; target.CaptionSource = s.CaptionSource; }
        if (target.AltText == "" && s.AltText != "") target.AltText = s.AltText;

        var haveTags = (await db.PhotoTags.Where(t => t.PhotoId == target.Id).Select(t => t.TagId).ToListAsync()).ToHashSet();
        var liveTags = (await db.Tags.Where(t => s.TagIds.Contains(t.Id)).Select(t => t.Id).ToListAsync()).ToHashSet();
        foreach (var tagId in s.TagIds)
            if (liveTags.Contains(tagId) && haveTags.Add(tagId)) db.PhotoTags.Add(new PhotoTag { PhotoId = target.Id, TagId = tagId });

        var haveLikes = (await db.Favorites.Where(f => f.PhotoId == target.Id).Select(f => f.VisitorId).ToListAsync()).ToHashSet();
        foreach (var l in s.Likes)
            if (haveLikes.Add(l.VisitorId))
                db.Favorites.Add(new Favorite { GalleryId = target.GalleryId, PhotoId = target.Id, VisitorId = l.VisitorId, IdentityKey = l.IdentityKey, Ip = l.Ip, CreatedAt = l.CreatedAt });
        if (s.CommentIds is { Count: > 0 } moving)
        {
            await db.Comments.Where(c => moving.Contains(c.Id)).ExecuteUpdateAsync(u => u.SetProperty(c => c.PhotoId, target.Id).SetProperty(c => c.GalleryId, target.GalleryId));
            await db.CommentEvents.Where(e => moving.Contains(e.CommentId)).ExecuteUpdateAsync(u => u.SetProperty(e => e.PhotoId, target.Id).SetProperty(e => e.GalleryId, target.GalleryId));
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The files behind these photos are gone. Where the same file now sits under another photo (moved to another gallery) the data
    /// goes there; otherwise it is held for a while in case the file comes back.
    /// </summary>
    public static async Task RetireAsync(AppDb db, IReadOnlyCollection<Photo> gone)
    {
        var withPrint = gone.Where(p => p.Fingerprint != "").ToList();
        if (withPrint.Count == 0) return;
        var saved = await CollectAsync(db, withPrint);
        foreach (var p in withPrint)
        {
            var s = saved[p.Id];
            if (s.IsEmpty) continue;
            // the heir is a recent, still bare copy in another gallery; an established copy with details of its own is a duplicate, not a move
            var since = DateTime.UtcNow.AddDays(-RecentDays);
            var candidates = await db.Photos.Where(x => x.Fingerprint == p.Fingerprint && x.Id != p.Id && !x.IsGuest && x.GalleryId != p.GalleryId && x.AddedAt > since)
                .OrderByDescending(x => x.AddedAt).ToListAsync();
            Photo? heir = null;
            if (candidates.Count > 0)
            {
                var theirs = await CollectAsync(db, candidates);
                heir = candidates.FirstOrDefault(c => theirs[c.Id].IsEmpty);
            }
            if (heir != null) { await ApplyAsync(db, heir, s); continue; }
            db.PhotosRetired.Add(new PhotoRetired { Fingerprint = p.Fingerprint, Name = p.Name, GalleryId = p.GalleryId, Data = JsonSerializer.Serialize(s) });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>A newly found file: give it back what its earlier life had, if that was kept.</summary>
    public static async Task<bool> ReclaimAsync(AppDb db, Photo fresh)
    {
        if (fresh.Fingerprint == "") return false;
        var held = await db.PhotosRetired.Where(r => r.Fingerprint == fresh.Fingerprint).OrderByDescending(r => r.RetiredAt).ToListAsync();
        if (held.Count == 0) return false;
        foreach (var r in held)
            if (JsonSerializer.Deserialize<Saved>(r.Data) is { } s) await ApplyAsync(db, fresh, s);
        db.PhotosRetired.RemoveRange(held);
        await db.SaveChangesAsync();
        return true;
    }

    public static Task ForgetOldAsync(AppDb db) =>
        db.PhotosRetired.Where(r => r.RetiredAt < DateTime.UtcNow.AddDays(-KeepDays)).ExecuteDeleteAsync();
}
