using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public record ScanResult(int Added, int Removed, int Total, int SkippedRaw, int Paired, int RawOnly);

public class Scanner(AppConfig cfg, ImageService images, ThumbnailService thumbs, ILogger<Scanner> log)
{
    static readonly SemaphoreSlim ScanGate = new(1, 1);
    static readonly char[] Separators = ['-', '_', ' ', '(', '.'];

    public static bool IsHidden(string name) =>
        name.StartsWith('.') || name.StartsWith('@') || name.StartsWith('#') || name == "_guest" || name == "Thumbs.db";

    record Entry(string Full, string Dir, string Base);

    public async Task<ScanResult> ScanAsync(AppDb db, Gallery g)
    {
        await ScanGate.WaitAsync();
        try { return await ScanInnerAsync(db, g); }
        finally { ScanGate.Release(); }
    }

    async Task<ScanResult> ScanInnerAsync(AppDb db, Gallery g)
    {
        var dir = cfg.Resolve(g.Folder);
        if (dir == null || !Directory.Exists(dir)) throw new DirectoryNotFoundException($"Folder not found: {g.Folder}");

        var existing = await db.Photos.Where(p => p.GalleryId == g.Id && !p.IsGuest).ToListAsync();
        var byRel = existing.ToDictionary(p => p.RelPath);

        // RAW files already claimed by a hand-made link are not offered to automatic matching
        var lockedRaws = existing.Where(p => p.RawLocked && p.RawRelPath != null && !p.RawOnly)
            .Select(p => p.RawRelPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var imgs = new List<Entry>();
        var raws = new List<Entry>();
        foreach (var file in Walk(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var e = new Entry(file, Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
            if (AppConfig.ImageExts.Contains(ext)) imgs.Add(e);
            else if (AppConfig.RawExts.Contains(ext)) { if (!lockedRaws.Contains(cfg.Rel(file))) raws.Add(e); }
        }

        // one primary image per (dir, base); prefer jpg/jpeg when several formats share a name
        imgs = imgs.GroupBy(i => (i.Dir.ToLowerInvariant(), i.Base.ToLowerInvariant()))
            .Select(grp => grp.OrderBy(i => Path.GetExtension(i.Full).ToLowerInvariant() is ".jpg" or ".jpeg" ? 0 : 1).First())
            .ToList();

        var rawByDirBase = raws.GroupBy(r => (r.Dir.ToLowerInvariant(), r.Base.ToLowerInvariant())).ToDictionary(x => x.Key, x => x.First());
        var rawByBase = raws.GroupBy(r => r.Base.ToLowerInvariant()).ToDictionary(x => x.Key, x => x.ToList());

        Entry? FindRaw(Entry image)
        {
            var b = image.Base.ToLowerInvariant();
            if (rawByDirBase.TryGetValue((image.Dir.ToLowerInvariant(), b), out var same)) return same;
            if (g.RawMatchMode >= RawMatch.Anywhere && rawByBase.TryGetValue(b, out var any)) return any[0];
            if (g.RawMatchMode == RawMatch.Fuzzy)
            {
                // image name = raw name + separator + suffix, e.g. IMG_1234.CR3 <-> IMG_1234-Edit.png ("IMG_12" never matches IMG_1234)
                Entry? best = null;
                foreach (var r in raws)
                {
                    var rb = r.Base;
                    if (image.Base.Length > rb.Length && image.Base.StartsWith(rb, StringComparison.OrdinalIgnoreCase)
                        && Separators.Contains(image.Base[rb.Length]) && (best == null || rb.Length > best.Base.Length
                            || (rb.Length == best.Base.Length && string.Equals(r.Dir, image.Dir, StringComparison.OrdinalIgnoreCase))))
                        best = r;
                }
                return best;
            }
            return null;
        }

        var usedRaw = new HashSet<string>(lockedRaws, StringComparer.OrdinalIgnoreCase);
        var plan = new List<(Entry primary, string? rawRel)>();
        foreach (var image in imgs)
        {
            var rel = cfg.Rel(image.Full);
            if (byRel.TryGetValue(rel, out var ex) && ex.RawLocked) { plan.Add((image, ex.RawRelPath)); if (ex.RawRelPath != null) usedRaw.Add(ex.RawRelPath); continue; }
            var raw = FindRaw(image);
            var rawRel = raw != null ? cfg.Rel(raw.Full) : null;
            if (rawRel != null) usedRaw.Add(rawRel);
            plan.Add((image, rawRel));
        }
        foreach (var r in raws)
            if (!usedRaw.Contains(cfg.Rel(r.Full))) plan.Add((r, cfg.Rel(r.Full)));

        var seen = new HashSet<string>();
        int added = 0, skippedRaw = 0;
        foreach (var (primary, rawRel) in plan)
        {
            var rel = cfg.Rel(primary.Full);
            seen.Add(rel);
            var isRawOnly = rawRel == rel;
            if (byRel.TryGetValue(rel, out var ex))
            {
                if (!ex.RawLocked && ex.RawRelPath != rawRel) ex.RawRelPath = rawRel;
                if (string.IsNullOrEmpty(ex.Name)) ex.Name = Path.GetFileName(primary.Full);
                continue;
            }
            if (isRawOnly && cfg.ExifTool == null) { skippedRaw++; continue; }

            var info = new FileInfo(primary.Full);
            var photo = new Photo
            {
                GalleryId = g.Id,
                RelPath = rel,
                Name = Path.GetFileName(primary.Full),
                RawRelPath = rawRel,
                SizeBytes = info.Length,
                TakenAt = info.LastWriteTimeUtc
            };
            try
            {
                if (!isRawOnly)
                {
                    var (w, h, taken) = images.ReadMeta(primary.Full);
                    photo.Width = w; photo.Height = h;
                    if (taken != null) photo.TakenAt = taken.Value;
                }
                db.Photos.Add(photo);
                await db.SaveChangesAsync();
                if (isRawOnly)
                {
                    var src = await images.GetSourceAsync(photo);
                    if (src == null) { db.Photos.Remove(photo); await db.SaveChangesAsync(); skippedRaw++; continue; }
                    var (w, h, taken) = images.ReadMeta(src);
                    photo.Width = w; photo.Height = h;
                    if (taken != null) photo.TakenAt = taken.Value;
                    await db.SaveChangesAsync();
                }
                added++;
            }
            catch (Exception ex2)
            {
                log.LogWarning(ex2, "Skipping unreadable file {File}", primary.Full);
                if (photo.Id != 0) { db.Photos.Remove(photo); await db.SaveChangesAsync(); }
                else db.Entry(photo).State = EntityState.Detached;
            }
        }

        var gone = existing.Where(p => !seen.Contains(p.RelPath)).ToList();
        foreach (var p in gone)
        {
            var d = Path.Combine(cfg.CacheDir, "variants", p.Id.ToString());
            if (Directory.Exists(d)) try { Directory.Delete(d, true); } catch { }
        }
        var goneIds = gone.Select(p => p.Id).ToList();
        if (goneIds.Count > 0) await db.Favorites.Where(f => goneIds.Contains(f.PhotoId)).ExecuteDeleteAsync();
        db.Photos.RemoveRange(gone);
        await db.SaveChangesAsync();

        if (g.CoverPhotoId == null || !await db.Photos.AnyAsync(p => p.Id == g.CoverPhotoId))
        {
            g.CoverPhotoId = await db.Photos.Where(p => p.GalleryId == g.Id && p.Approved)
                .OrderBy(p => p.TakenAt).Select(p => (int?)p.Id).FirstOrDefaultAsync();
            await db.SaveChangesAsync();
        }
        if (added > 0) thumbs.Enqueue(g.Id);
        var total = await db.Photos.CountAsync(p => p.GalleryId == g.Id);
        var paired = await db.Photos.CountAsync(p => p.GalleryId == g.Id && p.RawRelPath != null && p.RawRelPath != p.RelPath);
        var rawOnly = await db.Photos.CountAsync(p => p.GalleryId == g.Id && p.RawRelPath != null && p.RawRelPath == p.RelPath);
        return new ScanResult(added, gone.Count, total, skippedRaw, paired, rawOnly);
    }

    static IEnumerable<string> Walk(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
            if (!IsHidden(Path.GetFileName(f))) yield return f;
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (!IsHidden(Path.GetFileName(d)))
                foreach (var f in Walk(d)) yield return f;
    }
}
