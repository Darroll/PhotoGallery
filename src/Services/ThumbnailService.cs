using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public record ThumbProgress(int Total, int Done, bool Running, bool Queued);

/// <summary>
/// Builds the grid thumbnail and viewing image for every photo in the background, so galleries are
/// already fast when the first visitor opens them. Work is queued per gallery.
/// </summary>
public class ThumbnailService(IServiceScopeFactory scopes, ImageService images, AppConfig cfg, ILogger<ThumbnailService> log) : BackgroundService
{
    readonly Channel<(int id, bool force)> queue = Channel.CreateUnbounded<(int, bool)>();
    // per gallery: [total, done, running(0/1), queued(0/1)]
    readonly ConcurrentDictionary<int, int[]> progress = new();
    readonly HashSet<int> pending = new();
    readonly object gate = new();

    public void Enqueue(int galleryId, bool force = false)
    {
        lock (gate)
        {
            if (!force && pending.Contains(galleryId)) return;
            pending.Add(galleryId);
        }
        progress.GetOrAdd(galleryId, _ => new int[4])[3] = 1;
        queue.Writer.TryWrite((galleryId, force));
    }

    public ThumbProgress Status(int galleryId) =>
        progress.TryGetValue(galleryId, out var p) ? new(p[0], p[1], p[2] == 1, p[3] == 1) : new(0, 0, false, false);

    public int BusyGalleries => progress.Values.Count(p => p[2] == 1 || p[3] == 1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // warm the cache for existing galleries shortly after start
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                foreach (var id in await db.Galleries.Select(g => g.Id).ToListAsync(ct)) Enqueue(id);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogWarning(ex, "Thumbnail warm-up failed"); }
        }, ct);

        try
        {
            await foreach (var (id, force) in queue.Reader.ReadAllAsync(ct))
            {
                lock (gate) pending.Remove(id);
                try { await ProcessAsync(id, force, ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { log.LogWarning(ex, "Thumbnail job for gallery {Id} failed", id); }
                finally { if (progress.TryGetValue(id, out var p)) { p[2] = 0; lock (gate) { if (!pending.Contains(id)) p[3] = 0; } } }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task ProcessAsync(int id, bool force, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var g = await db.Galleries.FindAsync([id], ct);
        if (g == null) return;
        var photos = await db.Photos.Where(p => p.GalleryId == id && p.Approved).OrderBy(p => p.Id).ToListAsync(ct);

        var prog = progress.GetOrAdd(id, _ => new int[4]);
        prog[0] = photos.Count; prog[1] = 0; prog[2] = 1; prog[3] = 0;

        if (force)
            foreach (var p in photos)
            {
                var dir = Path.Combine(cfg.CacheDir, "variants", p.Id.ToString());
                if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
                var prev = Path.Combine(cfg.CacheDir, "rawprev", $"{p.Id}.jpg");
                if (p.RawOnly && File.Exists(prev)) try { File.Delete(prev); } catch { }
            }

        var wm = g.WatermarkView && images.WatermarkAvailable;
        await Parallel.ForEachAsync(photos, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (p, token) =>
        {
            try
            {
                await images.GetVariantAsync(p, "thumb", 480, "webp", false);
                await images.GetVariantAsync(p, "view", 2048, "webp", wm);
            }
            catch (Exception ex) { log.LogWarning(ex, "Could not build thumbnails for photo {Id}", p.Id); }
            Interlocked.Increment(ref prog[1]);
        });
        log.LogInformation("Thumbnails ready for gallery {Slug} ({Count} photos)", g.Slug, photos.Count);
    }
}
