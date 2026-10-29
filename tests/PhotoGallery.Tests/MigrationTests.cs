using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery.Tests;

public class MigrationTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "pgmig-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(dir, "photogallery.db");
    DbContextOptions<AppDb> Opts => new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={File_};Pooling=False").Options;

    public MigrationTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    static List<string> Shape(AppDb db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var rows = new List<string>();
        using var tables = conn.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name";
        var names = new List<string>();
        using (var r = tables.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
        foreach (var t in names)
        {
            using var cols = conn.CreateCommand();
            cols.CommandText = $"SELECT name, type, \"notnull\", pk FROM pragma_table_info('{t}') ORDER BY name";
            using (var r = cols.ExecuteReader()) while (r.Read()) rows.Add($"{t}.{r.GetString(0)} {r.GetString(1)} nn={r.GetInt32(2)} pk={r.GetInt32(3)}");
            using var idx = conn.CreateCommand();
            idx.CommandText = $"SELECT name, \"unique\" FROM pragma_index_list('{t}') WHERE origin = 'c' ORDER BY name";
            using (var r = idx.ExecuteReader()) while (r.Read()) rows.Add($"{t} index {r.GetString(0)} unique={r.GetInt32(1)}");
        }
        return rows;
    }

    [Fact]
    public void The_model_has_no_changes_missing_from_the_migrations()
    {
        using var db = new AppDb(Opts);
        Assert.False(db.Database.HasPendingModelChanges(), "Add a migration: dotnet ef migrations add <Name> (from src/)");
    }

    [Fact]
    public void A_new_database_is_built_by_the_migrations()
    {
        using (var db = new AppDb(Opts)) Schema.Migrate(db);
        using (var db = new AppDb(Opts))
        {
            Assert.Empty(db.Database.GetPendingMigrations());
            Assert.Equal(db.Database.GetMigrations().ToList(), db.Database.GetAppliedMigrations().ToList());
            Assert.Empty(Directory.GetFiles(dir, "*.bak-*"));   // nothing to protect yet
        }
    }

    [Fact]
    public void A_pre_migration_database_is_adopted_with_its_data_and_backed_up()
    {
        using (var db = new AppDb(Opts))
        {
            db.Database.EnsureCreated();   // how every database was created before migrations
            db.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" DROP COLUMN \"Preferences\"");
            db.Database.ExecuteSqlRaw("INSERT INTO \"Users\" VALUES ('old-user', 'o@example.test', 'Old', '2025-01-01', '2025-01-02')");
        }
        using (var db = new AppDb(Opts)) { Schema.Migrate(db); Schema.Migrate(db); }   // twice: repeatable
        using (var db = new AppDb(Opts))
        {
            var u = db.Users.Single(x => x.Sub == "old-user");
            Assert.Equal("{}", u.Preferences);
            Assert.Empty(db.Database.GetPendingMigrations());
            Assert.Single(Directory.GetFiles(dir, "*.bak-*"));
        }
    }

    [Fact]
    public void An_adopted_database_and_a_new_one_end_up_with_the_same_schema()
    {
        string[] legacy, fresh;
        using (var db = new AppDb(Opts)) { db.Database.EnsureCreated(); Schema.Migrate(db); legacy = Shape(db).ToArray(); }
        File.Delete(File_);
        using (var db = new AppDb(Opts)) { Schema.Migrate(db); fresh = Shape(db).ToArray(); }
        Assert.Equal(legacy, fresh);
    }
}
