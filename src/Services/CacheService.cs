using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public record CacheStats(long Bytes, int Files, int StaleFiles, long StaleBytes, int Missing);
public record CleanResult(int Files, long Bytes);

/// <summary>Reports on and cleans the derived-image cache (never touches the photo library).</summary>
public class CacheService(AppConfig cfg, ImageService images, IServiceScopeFactory scopes)
{
    public async Task<CacheStats> StatsAsync()
    {
        long bytes = 0; int files = 0;
        if (Directory.Exists(cfg.CacheDir))
            foreach (var f in Directory.EnumerateFiles(cfg.CacheDir, "*", SearchOption.AllDirectories))
                try { bytes += new FileInfo(f).Length; files++; } catch { }
        var stale = await FindStaleAsync();
        return new CacheStats(bytes, files, stale.Count, stale.Sum(s => s.size), await MissingAsync(null));
    }

    /// <summary>Number of photos whose grid thumbnail or viewing image has not been built yet.</summary>
    public async Task<int> MissingAsync(int? galleryId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var galleries = await db.Galleries.AsNoTracking().ToDictionaryAsync(g => g.Id);
        var photos = await db.Photos.AsNoTracking().Where(p => p.Approved && (galleryId == null || p.GalleryId == galleryId)).ToListAsync();
        var wmAvailable = images.WatermarkAvailable;
        int missing = 0;
        foreach (var p in photos)
        {
            var src = images.PeekSource(p);
            if (src == null || !File.Exists(src)) { if (p.RawOnly) missing++; continue; }   // RAW preview not extracted yet
            var stamp = File.GetLastWriteTimeUtc(src).Ticks.ToString("x");
            var dir = Path.Combine(cfg.CacheDir, "variants", p.Id.ToString());
            var wm = galleries.TryGetValue(p.GalleryId, out var g) && g.WatermarkView && wmAvailable;
            var viewTag = wm ? images.CurrentWatermarkTag : "n";
            if (!File.Exists(Path.Combine(dir, $"thumb480_n_{stamp}.webp")) || !File.Exists(Path.Combine(dir, $"view2048_{viewTag}_{stamp}.webp"))) missing++;
        }
        return missing;
    }

    public async Task<CleanResult> CleanAsync()
    {
        var stale = await FindStaleAsync();
        int n = 0; long freed = 0;
        foreach (var (path, size, isDir) in stale)
        {
            try
            {
                if (isDir) Directory.Delete(path, true); else File.Delete(path);
                n++; freed += size;
            }
            catch { }
        }
        return new CleanResult(n, freed);
    }

    /// <summary>Stale = belongs to a deleted photo, or was built from an older source file / older watermark, or is a leftover temp file.</summary>
    async Task<List<(string path, long size, bool isDir)>> FindStaleAsync()
    {
        var result = new List<(string, long, bool)>();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var photos = await db.Photos.AsNoTracking().ToDictionaryAsync(p => p.Id);

        static long DirSize(string d) => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });

        var variants = Path.Combine(cfg.CacheDir, "variants");
        if (Directory.Exists(variants))
            foreach (var dir in Directory.EnumerateDirectories(variants))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var id) || !photos.TryGetValue(id, out var photo))
                { result.Add((dir, DirSize(dir), true)); continue; }

                var src = images.PeekSource(photo);
                if (src == null || !File.Exists(src)) { result.Add((dir, DirSize(dir), true)); continue; }
                var stamp = File.GetLastWriteTimeUtc(src).Ticks.ToString("x");
                var wmNow = images.CurrentWatermarkTag;

                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    var size = new FileInfo(f).Length;
                    if (name.Contains(".tmp."))
                    {
                        if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) result.Add((f, size, false));
                        continue;
                    }
                    var parts = Path.GetFileNameWithoutExtension(name).Split('_');   // kindPx _ wmTag _ stamp
                    if (parts.Length != 3) { result.Add((f, size, false)); continue; }
                    var wmTag = parts[1];
                    if (parts[2] != stamp || (wmTag.StartsWith('w') && wmTag != wmNow)) result.Add((f, size, false));
                }
            }

        var rawPrev = Path.Combine(cfg.CacheDir, "rawprev");
        if (Directory.Exists(rawPrev))
            foreach (var f in Directory.EnumerateFiles(rawPrev, "*.jpg"))
                if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out var id) || !photos.TryGetValue(id, out var p) || !p.RawOnly)
                    result.Add((f, new FileInfo(f).Length, false));
        return result;
    }
}
