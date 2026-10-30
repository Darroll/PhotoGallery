using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Keeps dated snapshots of the database in DATA_DIR/backup. A snapshot is a consistent copy taken while the app runs
/// (VACUUM INTO), checked with an integrity check before it is kept. The newest BACKUP_DAILY days are kept, plus the
/// newest older snapshot of each of BACKUP_WEEKLY further weeks. Status is read from the files, so it survives restarts.
/// </summary>
public class BackupService(IServiceScopeFactory scopes, AppConfig cfg, ILogger<BackupService> log) : BackgroundService
{
    public record Snapshot(string Name, DateTime Time, long Bytes);

    static readonly string[] NameFormats = ["yyyyMMdd-HHmmss", "yyyyMMdd-HHmm"];
    readonly SemaphoreSlim gate = new(1, 1);

    public string Dir => Path.Combine(cfg.DataDir, "backup");
    public bool Enabled => cfg.BackupHours > 0;
    /// <summary>The message of the last failed attempt; cleared by the next success.</summary>
    public string? LastError { get; private set; }

    public List<Snapshot> List()
    {
        if (!Directory.Exists(Dir)) return [];
        var found = new List<Snapshot>();
        foreach (var f in new DirectoryInfo(Dir).GetFiles("photogallery-*.db"))
        {
            var stamp = Path.GetFileNameWithoutExtension(f.Name)["photogallery-".Length..];
            var time = DateTime.TryParseExact(stamp, NameFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : f.LastWriteTimeUtc;
            found.Add(new Snapshot(f.Name, DateTime.SpecifyKind(time, DateTimeKind.Utc), f.Length));
        }
        return found.OrderByDescending(s => s.Time).ToList();
    }

    public async Task<Snapshot> RunAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        var tmp = "";
        try
        {
            Directory.CreateDirectory(Dir);
            var final = Path.Combine(Dir, $"photogallery-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            tmp = final + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);

            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{tmp.Replace("'", "''")}'", ct);
            }
            await VerifyAsync(tmp, ct);
            File.Move(tmp, final, overwrite: true);
            Prune(List(), cfg.BackupDaily, cfg.BackupWeekly);
            LastError = null;
            var info = new FileInfo(final);
            log.LogInformation("Database backup {Name} ({Bytes} bytes)", info.Name, info.Length);
            return new Snapshot(info.Name, DateTime.UtcNow, info.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            log.LogWarning(ex, "Database backup failed");
            try { if (tmp != "" && File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
        finally { gate.Release(); }
    }

    static async Task VerifyAsync(string file, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        var result = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
        if (result != "ok") throw new InvalidOperationException("The snapshot failed its integrity check: " + result);
    }

    /// <summary>The snapshots to keep: the newest of each of the last <paramref name="daily"/> days, then the newest of each of the last <paramref name="weekly"/> older weeks.</summary>
    public static HashSet<string> Keep(List<Snapshot> all, int daily, int weekly)
    {
        var newestFirst = all.OrderByDescending(s => s.Time).ToList();
        var days = newestFirst.GroupBy(s => s.Time.Date).Take(Math.Max(1, daily)).ToList();
        var keep = days.Select(g => g.First().Name).ToHashSet();
        var cutoff = days.Last().Key;   // weekly snapshots come only from days before the daily window
        var older = newestFirst.Where(s => s.Time.Date < cutoff)
            .GroupBy(s => (ISOWeek.GetYear(s.Time), ISOWeek.GetWeekOfYear(s.Time)))
            .Take(Math.Max(0, weekly)).Select(g => g.First().Name);
        keep.UnionWith(older);
        return keep;
    }

    void Prune(List<Snapshot> all, int daily, int weekly)
    {
        var keep = Keep(all, daily, weekly);
        foreach (var s in all.Where(s => !keep.Contains(s.Name)))
            try { File.Delete(Path.Combine(Dir, s.Name)); } catch (Exception ex) { log.LogWarning(ex, "Could not remove old backup {Name}", s.Name); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var newest = List().FirstOrDefault();
                if (newest == null || DateTime.UtcNow - newest.Time >= TimeSpan.FromHours(cfg.BackupHours)) await RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* recorded in LastError and logged; retried below */ }
            try { await Task.Delay(TimeSpan.FromMinutes(15), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
