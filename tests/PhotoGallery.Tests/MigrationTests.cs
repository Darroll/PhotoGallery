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
            AsBaseline(db);
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
    public void The_like_migration_keeps_existing_galleries_liking_and_gives_old_hearts_an_owner()
    {
        using (var db = new AppDb(Opts))
        {
            var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
            migrator.Migrate("20261029222553_InitialCreate");
            db.Database.ExecuteSqlRaw("INSERT INTO \"Favorites\" (GalleryId, PhotoId, VisitorId, CreatedAt) VALUES (1, 7, 'visitor-aaaaaaaaaaaaaaaa', '2026-10-01')");
            db.Database.ExecuteSqlRaw("INSERT INTO \"Favorites\" (GalleryId, PhotoId, VisitorId, CreatedAt) VALUES (1, 7, 'visitor-bbbbbbbbbbbbbbbb', '2026-10-02')");
        }
        using (var db = new AppDb(Opts))
        {
            Schema.Migrate(db);
            var keys = db.Favorites.OrderBy(f => f.VisitorId).Select(f => f.IdentityKey).ToList();
            Assert.Equal(["v:visitor-aaaaaaaaaaaaaaaa", "v:visitor-bbbbbbbbbbbbbbbb"], keys);   // each old browser counts once
            var conn = db.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, dflt_value FROM pragma_table_info('Galleries') WHERE name IN ('AllowLikes', 'ShowLikeCounts')";
            using var r = cmd.ExecuteReader();
            var defaults = new Dictionary<string, string>();
            while (r.Read()) defaults[r.GetString(0)] = r.GetString(1);
            Assert.Equal("1", defaults["AllowLikes"]);      // existing galleries keep their hearts
            Assert.Equal("1", defaults["ShowLikeCounts"]);
        }
    }

    /// <summary>Inserts a row into a table as it was at an old schema: every required column gets a plain value of its type, then the given ones override.</summary>
    static void InsertOld(AppDb db, string table, Dictionary<string, object> values)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var cols = new List<(string name, string type)>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"SELECT name, type FROM pragma_table_info('{table}') WHERE (\"notnull\" = 1 AND dflt_value IS NULL) AND pk = 0";
            using var r = c.ExecuteReader();
            while (r.Read()) cols.Add((r.GetString(0), r.GetString(1)));
        }
        var all = cols.ToDictionary(x => x.name, x => x.type == "INTEGER" ? (object)0 : x.name is "CreatedAt" or "FirstSeen" or "LastSeen" or "TakenAt" or "Time" or "UpdatedAt" or "AcceptedAt" or "At" or "AddedAt" ? "2025-01-01 00:00:00" : "x");
        foreach (var (k, v) in values) all[k] = v;
        using var ins = conn.CreateCommand();
        ins.CommandText = $"INSERT INTO \"{table}\" ({string.Join(",", all.Keys.Select(k => $"\"{k}\""))}) VALUES ({string.Join(",", all.Keys.Select((_, i) => "$p" + i))})";
        var n = 0;
        foreach (var v in all.Values) { var p = ins.CreateParameter(); p.ParameterName = "$p" + n++; p.Value = v; ins.Parameters.Add(p); }
        ins.ExecuteNonQuery();
    }

    [Fact]
    public void Upgrading_from_the_first_release_keeps_every_gallery_photo_and_user_and_makes_a_backup()
    {
        using (var db = new AppDb(Opts))
        {
            var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
            migrator.Migrate("20261029222553_InitialCreate");
            InsertOld(db, "Galleries", new() { ["Id"] = 1, ["Slug"] = "old-gallery", ["Title"] = "Old Gallery", ["Folder"] = "Old/Folder", ["Visibility"] = 1 });
            for (var i = 1; i <= 3; i++)
                InsertOld(db, "Photos", new() { ["Id"] = i, ["GalleryId"] = 1, ["RelPath"] = $"p{i}.jpg", ["Name"] = $"p{i}.jpg" });
            InsertOld(db, "Users", new() { ["Sub"] = "old-user", ["Email"] = "o@example.test", ["Name"] = "Old" });
            InsertOld(db, "Tags", new() { ["Id"] = 1, ["Name"] = "keep" });
            InsertOld(db, "Favorites", new() { ["Id"] = 1, ["GalleryId"] = 1, ["PhotoId"] = 2, ["VisitorId"] = "visitor-cccccccccccccccc" });
        }
        using (var db = new AppDb(Opts)) Schema.Migrate(db);
        using (var db = new AppDb(Opts))
        {
            Assert.Empty(db.Database.GetPendingMigrations());
            var g = db.Galleries.Single();
            Assert.Equal(("old-gallery", "Old Gallery", "Old/Folder"), (g.Slug, g.Title, g.Folder));
            Assert.Equal("[]", g.Sections);                       // new settings start at their defaults
            Assert.Equal(CoverMode.WithPhotos, g.CoverMode);
            Assert.Null(g.EventEndDate);
            Assert.Equal(["p1.jpg", "p2.jpg", "p3.jpg"], db.Photos.OrderBy(p => p.RelPath).Select(p => p.RelPath).ToList());
            Assert.All(db.Photos.ToList(), p => Assert.False(p.TakenFromRaw));
            Assert.Equal("Old", db.Users.Single().Name);
            Assert.Equal("keep", db.Tags.Single().Name);
            Assert.Equal(1, db.Favorites.Count());
            Assert.Single(Directory.GetFiles(dir, "*.bak-*"));    // the file was copied before anything changed
        }
        // and the backup is the old database, untouched
        var bak = Directory.GetFiles(dir, "*.bak-*").Single();
        using var old = new SqliteConnection($"Data Source={bak};Pooling=False;Mode=ReadOnly");
        old.Open();
        using var cmd = old.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Photos";
        Assert.Equal(3L, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Galleries') WHERE name = 'Sections'";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    /// <summary>
    /// EnsureCreated builds today's model, but a database from before migrations had the baseline schema. Take away what
    /// later migrations add, so adoption can be tested the way it happens: baseline first, then the newer migrations.
    /// Add a line here whenever a migration adds a column or an index.
    /// </summary>
    static void AsBaseline(AppDb db)
    {
        db.Database.ExecuteSqlRaw("DROP INDEX \"IX_Favorites_PhotoId_IdentityKey\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Favorites\" DROP COLUMN \"IdentityKey\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Favorites\" DROP COLUMN \"Ip\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Galleries\" DROP COLUMN \"AllowLikes\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Galleries\" DROP COLUMN \"ShowLikeCounts\"");
        db.Database.ExecuteSqlRaw("DROP INDEX \"IX_Photos_Fingerprint\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Photos\" DROP COLUMN \"Fingerprint\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"PhotosRetired\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"CommentBlocks\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"CommentEvents\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"Comments\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Galleries\" DROP COLUMN \"AllowComments\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Photos\" DROP COLUMN \"CommentsOff\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" DROP COLUMN \"DisplayName\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Galleries\" DROP COLUMN \"Sections\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Galleries\" DROP COLUMN \"EventEndDate\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Galleries\" DROP COLUMN \"CoverMode\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Photos\" DROP COLUMN \"TakenFromRaw\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"FeedbackMessages\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Galleries\" DROP COLUMN \"AllowFeedback\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"NumberSightings\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Galleries\" DROP COLUMN \"TeamKit\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"PlayerSuggestions\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"Players\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"Teams\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Photos\" DROP COLUMN \"PlayerCheckAttempts\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Photos\" DROP COLUMN \"PlayersCheckedAt\"");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Galleries\" DROP COLUMN \"TeamId\"");
    }

    [Fact]
    public void An_adopted_database_and_a_new_one_end_up_with_the_same_schema()
    {
        string[] legacy, fresh;
        using (var db = new AppDb(Opts)) { db.Database.EnsureCreated(); AsBaseline(db); Schema.Migrate(db); legacy = Shape(db).ToArray(); }
        File.Delete(File_);
        using (var db = new AppDb(Opts)) { Schema.Migrate(db); fresh = Shape(db).ToArray(); }
        Assert.Equal(legacy, fresh);
    }
}
