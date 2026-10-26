using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>Periodically rescans every gallery folder so new files appear without a manual rescan.</summary>
public class RescanService(IServiceScopeFactory scopes, AppConfig cfg, ILogger<RescanService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (cfg.RescanMinutes <= 0) return;
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var scanner = scope.ServiceProvider.GetRequiredService<Scanner>();
                foreach (var id in await db.Galleries.Select(g => g.Id).ToListAsync(ct))
                {
                    var g = await db.Galleries.FindAsync([id], ct);
                    if (g == null) continue;
                    try
                    {
                        var r = await scanner.ScanAsync(db, g);
                        if (r.Added > 0 || r.Removed > 0) log.LogInformation("Rescan {Slug}: +{Added} -{Removed}", g.Slug, r.Added, r.Removed);
                    }
                    catch (DirectoryNotFoundException) { }
                    catch (Exception ex) { log.LogWarning(ex, "Rescan failed for {Slug}", g.Slug); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Rescan loop error"); }
            try { await Task.Delay(TimeSpan.FromMinutes(cfg.RescanMinutes), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
