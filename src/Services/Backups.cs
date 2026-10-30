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

    /// <summary>The message of the last failed copy to BACKUP_COPY_DIR; cleared by the next success.</summary>
    public string? CopyError { get; private set; }

    public List<Snapshot> List() => ListIn(Dir, "photogallery-");

    /// <summary>The copies made just before a restore, so a restore can itself be undone. Newest first.</summary>
    public List<Snapshot> SafetyCopies() => ListIn(Dir, "before-restore-");

    /// <summary>The file of a snapshot or safety copy, by the name the lists show; null when there is no such backup.</summary>
    public string? PathOf(string name) =>
        List().Concat(SafetyCopies()).FirstOrDefault(s => s.Name == name) is { } s ? Path.Combine(Dir, s.Name) : null;

    static List<Snapshot> ListIn(string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return [];
        var found = new List<Snapshot>();
        foreach (var f in new DirectoryInfo(dir).GetFiles(prefix + "*.db"))
        {
            var stamp = Path.GetFileNameWithoutExtension(f.Name)[prefix.Length..];
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
            Prune(Dir, List(), cfg.BackupDaily, cfg.BackupWeekly);
            LastError = null;
            CopyOut(final);
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

    /// <summary>Puts a copy of a new snapshot in BACKUP_COPY_DIR. A failure there is reported but never fails the backup itself.</summary>
    void CopyOut(string file)
    {
        if (cfg.BackupCopyDir is not { } dir) return;
        var tmp = Path.Combine(dir, Path.GetFileName(file) + ".tmp");
        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(file, tmp, overwrite: true);
            File.Move(tmp, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            Prune(dir, ListIn(dir, "photogallery-"), cfg.BackupDaily, cfg.BackupWeekly);
            CopyError = null;
        }
        catch (Exception ex)
        {
            CopyError = ex.Message;
            log.LogWarning(ex, "Could not copy the backup to {Dir}", dir);
            try { File.Delete(tmp); } catch { }
        }
    }

    public record RestoreResult(string SafetyCopy);

    /// <summary>
    /// Replaces the live database with the contents of a snapshot file. The current database is first saved as a
    /// before-restore copy, so nothing is lost; the snapshot must pass an integrity check, look like a gallery database
    /// and not come from a newer version of the app. The swap uses SQLite's own backup, which is safe while the app runs.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(string source, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await VerifyAsync(source, ct);
            using (var scope = scopes.CreateScope())
            {
                var known = scope.ServiceProvider.GetRequiredService<AppDb>().Database.GetMigrations().ToHashSet();
                await using var src = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False");
                await src.OpenAsync(ct);
                await using var cmd = src.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Galleries', 'Photos')";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) != 2) throw new InvalidOperationException("That file is not a backup of this gallery.");
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0)
                {
                    cmd.CommandText = "SELECT MigrationId FROM \"__EFMigrationsHistory\"";
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        if (!known.Contains(r.GetString(0))) throw new InvalidOperationException("That backup was made by a newer version of the gallery. Update the app first.");
                }
            }

            Directory.CreateDirectory(Dir);
            var safety = $"before-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.db";
            using (var scope = scopes.CreateScope())
                await scope.ServiceProvider.GetRequiredService<AppDb>().Database.ExecuteSqlRawAsync($"VACUUM INTO '{Path.Combine(Dir, safety).Replace("'", "''")}'", ct);
            foreach (var old in SafetyCopies().Skip(5))
                try { File.Delete(Path.Combine(Dir, old.Name)); } catch { }

            var live = Path.Combine(cfg.DataDir, "photogallery.db");
            await using (var from = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False"))
            await using (var to = new SqliteConnection($"Data Source={live};Pooling=False;Default Timeout=30"))
            {
                await from.OpenAsync(ct);
                await to.OpenAsync(ct);
                from.BackupDatabase(to);
            }
            SqliteConnection.ClearAllPools();
            using (var scope = scopes.CreateScope()) Schema.Migrate(scope.ServiceProvider.GetRequiredService<AppDb>());
            log.LogWarning("Database restored from a backup; the previous state is saved as {Safety}", safety);
            return new RestoreResult(safety);
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

    void Prune(string dir, List<Snapshot> all, int daily, int weekly)
    {
        if (all.Count == 0) return;
        var keep = Keep(all, daily, weekly);
        foreach (var s in all.Where(s => !keep.Contains(s.Name)))
            try { File.Delete(Path.Combine(dir, s.Name)); } catch (Exception ex) { log.LogWarning(ex, "Could not remove old backup {Name}", s.Name); }
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
