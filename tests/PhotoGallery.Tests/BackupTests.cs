using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoGallery.Tests;

public class BackupRetentionTests
{
    static BackupService.Snapshot Snap(string stamp) =>
        new($"photogallery-{stamp}.db", DateTime.ParseExact(stamp, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal), 1);

    [Fact]
    public void Keeps_the_newest_of_each_recent_day_then_one_per_older_week()
    {
        var all = new List<BackupService.Snapshot>();
        // two snapshots a day for 40 days ending 2026-10-30
        for (int d = 0; d < 40; d++)
        {
            var day = new DateTime(2026, 10, 30).AddDays(-d);
            all.Add(Snap(day.ToString("yyyyMMdd") + "-030000"));
            all.Add(Snap(day.ToString("yyyyMMdd") + "-150000"));
        }
        var keep = BackupService.Keep(all, daily: 7, weekly: 4);

        // the last 7 days: only the later snapshot of each day
        for (int d = 0; d < 7; d++)
        {
            var day = new DateTime(2026, 10, 30).AddDays(-d).ToString("yyyyMMdd");
            Assert.Contains($"photogallery-{day}-150000.db", keep);
            Assert.DoesNotContain($"photogallery-{day}-030000.db", keep);
        }
        Assert.Equal(7 + 4, keep.Count);
        Assert.Contains(all.OrderByDescending(s => s.Time).First().Name, keep);   // the newest is never pruned
    }

    [Fact]
    public void A_single_snapshot_is_always_kept_even_with_silly_settings()
    {
        var one = new List<BackupService.Snapshot> { Snap("20261030-030000") };
        Assert.Single(BackupService.Keep(one, daily: 0, weekly: -3));
    }
}

public class BackupTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task A_snapshot_restores_into_a_working_database_with_its_data()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Backed up " + Guid.NewGuid().ToString("N")[..6], app.NewFolder(2));

        var backups = app.Services.GetRequiredService<BackupService>();
        var snap = await backups.RunAsync();
        Assert.True(snap.Bytes > 0);
        Assert.Contains(backups.List(), s => s.Name == snap.Name);
        Assert.Empty(Directory.GetFiles(backups.Dir, "*.tmp"));
        Assert.Null(backups.LastError);

        // restore: copy the snapshot somewhere else and open it the way the app does at startup
        var restored = Path.Combine(Path.GetTempPath(), "pgrestore-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            File.Copy(Path.Combine(backups.Dir, snap.Name), restored);
            var opts = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={restored};Pooling=False").Options;
            using var db = new AppDb(opts);
            Schema.Migrate(db);
            Assert.Contains(await db.Galleries.Select(g => g.Slug).ToListAsync(), s => s == slug);
            Assert.Equal(2, await db.Photos.CountAsync(p => p.Gallery!.Slug == slug));
            Assert.Empty(db.Database.GetPendingMigrations());
        }
        finally { try { File.Delete(restored); } catch { } }
    }

    [Fact]
    public async Task The_admin_can_list_and_take_backups_and_the_health_overview_reports_them()
    {
        var admin = await app.AdminAsync();
        var run = await admin.PostAsync("/api/admin/backups/run", null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var name = (await run.ReadAsync()).GetProperty("name").GetString();

        var list = await (await admin.GetAsync("/api/admin/backups")).ReadAsync();
        Assert.True(list.GetProperty("enabled").GetBoolean());
        Assert.Contains(list.GetProperty("snapshots").EnumerateArray(), s => s.GetProperty("name").GetString() == name);

        var health = await (await admin.GetAsync("/api/admin/health")).ReadAsync();
        var check = health.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == "backup");
        Assert.Equal("ok", check.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Backups_are_for_the_admin_only_and_taking_one_needs_the_csrf_header()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync("/api/admin/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().PostAsync("/api/admin/backups/run", null)).StatusCode);
        var admin = await app.AdminAsync();
        admin.DefaultRequestHeaders.Remove("X-Requested-With");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/backups/run", null)).StatusCode);
    }
}
