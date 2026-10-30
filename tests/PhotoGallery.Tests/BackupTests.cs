using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task Restoring_a_backup_brings_back_what_was_there_and_saves_the_current_state_first()
    {
        var admin = await app.AdminAsync();
        var before = "Before " + Guid.NewGuid().ToString("N")[..6];
        var keptSlug = await AppFactory.GalleryAsync(admin, before, app.NewFolder(1));
        var snap = (await (await admin.PostAsync("/api/admin/backups/run", null)).ReadAsync()).GetProperty("name").GetString()!;
        var laterSlug = await AppFactory.GalleryAsync(admin, "Later " + Guid.NewGuid().ToString("N")[..6], app.NewFolder(1));

        var r = await admin.PostAsJsonAsync("/api/admin/backups/restore", new { name = snap });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var safety = (await r.ReadAsync()).GetProperty("safetyCopy").GetString()!;

        var slugs = await app.WithDbAsync(db => db.Galleries.Select(g => g.Slug).ToListAsync());
        Assert.Contains(keptSlug, slugs);
        Assert.DoesNotContain(laterSlug, slugs);      // created after the backup, gone after the restore

        // the state from just before the restore is kept, listed, downloadable, and itself restorable
        var list = await (await admin.GetAsync("/api/admin/backups")).ReadAsync();
        Assert.Contains(list.GetProperty("safety").EnumerateArray(), s => s.GetProperty("name").GetString() == safety);
        var dl = await admin.GetAsync($"/api/admin/backups/{safety}/download");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        var undo = await admin.PostAsJsonAsync("/api/admin/backups/restore", new { name = safety });
        if (undo.StatusCode != HttpStatusCode.OK) Assert.Fail(await undo.Content.ReadAsStringAsync());
        Assert.Contains(laterSlug, await app.WithDbAsync(db => db.Galleries.Select(g => g.Slug).ToListAsync()));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/g/{laterSlug}")).StatusCode);   // the app keeps working
    }

    [Fact]
    public async Task A_downloaded_backup_can_be_uploaded_to_restore_and_junk_is_refused()
    {
        var admin = await app.AdminAsync();
        var slug = await AppFactory.GalleryAsync(admin, "Upload " + Guid.NewGuid().ToString("N")[..6], app.NewFolder(1));
        var snap = (await (await admin.PostAsync("/api/admin/backups/run", null)).ReadAsync()).GetProperty("name").GetString()!;
        var bytes = await (await admin.GetAsync($"/api/admin/backups/{snap}/download")).Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
        await admin.DeleteAsync($"/api/admin/galleries/{await app.WithDbAsync(db => db.Galleries.Where(g => g.Slug == slug).Select(g => g.Id).FirstAsync())}");
        Assert.DoesNotContain(slug, await app.WithDbAsync(db => db.Galleries.Select(g => g.Slug).ToListAsync()));

        var ok = await admin.PostAsync("/api/admin/backups/restore-upload", new ByteArrayContent(bytes));
        if (ok.StatusCode != HttpStatusCode.OK) Assert.Fail(await ok.Content.ReadAsStringAsync());
        Assert.Contains(slug, await app.WithDbAsync(db => db.Galleries.Select(g => g.Slug).ToListAsync()));
        Assert.Empty(Directory.GetFiles(Path.Combine(app.Data, "backup"), "upload-*"));

        var junk = await admin.PostAsync("/api/admin/backups/restore-upload", new ByteArrayContent("this is not a database"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
        Assert.Contains(slug, await app.WithDbAsync(db => db.Galleries.Select(g => g.Slug).ToListAsync()));   // untouched
    }

    [Fact]
    public async Task Restore_and_download_only_accept_known_backup_names_and_the_admin()
    {
        var admin = await app.AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/backups/..%2Fphotogallery.db/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/admin/backups/restore", new { name = "../photogallery.db" })).StatusCode);
        var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/admin/backups/restore", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/admin/backups/restore-upload", new ByteArrayContent([1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/backups/x.db/download")).StatusCode);
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

public class CopyFactory : AppFactory
{
    public string CopyDir { get; } = Path.Combine(Path.GetTempPath(), "pgcopy-" + Guid.NewGuid().ToString("N"));
    public CopyFactory() : base(null) { }
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("BACKUP_COPY_DIR", CopyDir);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(CopyDir, true); } catch { }
    }
}

public class BackupCopyTests(CopyFactory app) : IClassFixture<CopyFactory>
{
    [Fact]
    public async Task Every_backup_is_also_copied_to_the_second_folder()
    {
        var admin = await app.AdminAsync();
        var name = (await (await admin.PostAsync("/api/admin/backups/run", null)).ReadAsync()).GetProperty("name").GetString()!;
        Assert.True(File.Exists(Path.Combine(app.CopyDir, name)));
        Assert.Empty(Directory.GetFiles(app.CopyDir, "*.tmp"));
        var list = await (await admin.GetAsync("/api/admin/backups")).ReadAsync();
        Assert.Equal(Path.GetFullPath(app.CopyDir), list.GetProperty("copyDir").GetString());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("copyError").ValueKind);
    }

    [Fact]
    public async Task A_copy_that_cannot_be_written_is_reported_but_the_backup_still_succeeds()
    {
        var admin = await app.AdminAsync();
        // a file where the folder should be
        var blocker = Path.Combine(Path.GetTempPath(), "pgblock-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            using var broken = new BrokenCopyFactory(blocker);
            var a = await broken.AdminAsync();
            var run = await a.PostAsync("/api/admin/backups/run", null);
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            var list = await (await a.GetAsync("/api/admin/backups")).ReadAsync();
            Assert.Equal(JsonValueKind.String, list.GetProperty("copyError").ValueKind);
            Assert.NotEmpty(list.GetProperty("snapshots").EnumerateArray());
        }
        finally { try { File.Delete(blocker); } catch { } }
    }

    class BrokenCopyFactory(string blocker) : AppFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("BACKUP_COPY_DIR", Path.Combine(blocker, "sub"));
        }
    }
}
