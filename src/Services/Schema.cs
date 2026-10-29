using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Brings the database to the current schema with EF Core migrations. Databases created before migrations existed
/// (by EnsureCreated plus the hand-written Upgrade below) are caught up once and then adopted into the migration
/// history at the baseline. Do not add to Upgrade: new schema changes belong in a migration.
/// </summary>
public static class Schema
{
    public static void Migrate(AppDb db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var hasTables = TableExists(conn, "Galleries");
        var hasHistory = TableExists(conn, "__EFMigrationsHistory");

        if (hasTables && !hasHistory)
        {
            Upgrade(db);   // legacy databases: catch up to the baseline shape, then stamp it as applied
            BackupFile(conn);
            db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY, ""ProductVersion"" TEXT NOT NULL)");
            db.Database.ExecuteSql($"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({db.Database.GetMigrations().First()}, '9.0.0')");
        }
        else if (hasHistory && db.Database.GetPendingMigrations().Any())
            BackupFile(conn);

        db.Database.Migrate();
        // EF only sets WAL when it creates the file itself, which the probes above pre-empt; it persists in the file
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode = 'wal'");
    }

    static bool TableExists(System.Data.Common.DbConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $n";
        var p = cmd.CreateParameter(); p.ParameterName = "$n"; p.Value = name; cmd.Parameters.Add(p);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>Copies the database file beside itself before a schema change, keeping the three most recent copies.</summary>
    static void BackupFile(System.Data.Common.DbConnection conn)
    {
        var file = conn.DataSource;
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
        using (var cp = conn.CreateCommand()) { cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; cp.ExecuteNonQuery(); }
        File.Copy(file, $"{file}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        foreach (var old in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(file))!, Path.GetFileName(file) + ".bak-*").OrderDescending().Skip(3))
            try { File.Delete(old); } catch { }
    }

    /// <summary>Frozen: the pre-migration catch-up for databases created by EnsureCreated.</summary>
    public static void Upgrade(AppDb db)
    {
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Visits"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Visits"" PRIMARY KEY AUTOINCREMENT,
            ""Time"" TEXT NOT NULL, ""Kind"" TEXT NOT NULL, ""GalleryId"" INTEGER NULL, ""PhotoId"" INTEGER NULL,
            ""Ip"" TEXT NOT NULL, ""Country"" TEXT NOT NULL, ""UserAgent"" TEXT NOT NULL, ""IsBot"" INTEGER NOT NULL, ""Detail"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Visits_Time"" ON ""Visits"" (""Time"")");
        db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Visits_GalleryId_Time"" ON ""Visits"" (""GalleryId"", ""Time"")");
        AddColumn(db, "Galleries", "FootageLinks", "TEXT NOT NULL DEFAULT '[]'");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Favorites"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Favorites"" PRIMARY KEY AUTOINCREMENT,
            ""GalleryId"" INTEGER NOT NULL, ""PhotoId"" INTEGER NOT NULL, ""VisitorId"" TEXT NOT NULL, ""CreatedAt"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Favorites_GalleryId_PhotoId_VisitorId"" ON ""Favorites"" (""GalleryId"", ""PhotoId"", ""VisitorId"")");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""FavoriteOwners"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_FavoriteOwners"" PRIMARY KEY AUTOINCREMENT,
            ""GalleryId"" INTEGER NOT NULL, ""VisitorId"" TEXT NOT NULL, ""Name"" TEXT NOT NULL, ""UpdatedAt"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_FavoriteOwners_GalleryId_VisitorId"" ON ""FavoriteOwners"" (""GalleryId"", ""VisitorId"")");
        AddColumn(db, "Visits", "VisitorId", "TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Visits_VisitorId"" ON ""Visits"" (""VisitorId"")");
        AddColumn(db, "Photos", "RawLocked", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "Galleries", "RawMatchMode", "INTEGER NOT NULL DEFAULT 1");
        AddColumn(db, "Galleries", "HideRawOnly", "INTEGER NOT NULL DEFAULT 1");
        AddColumn(db, "Galleries", "SortOrder", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "Galleries", "SortMode", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "Galleries", "SortDesc", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "Galleries", "EventDate", "TEXT NULL");
        AddColumn(db, "Galleries", "Location", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Galleries", "AllowedSizes", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Galleries", "AllowedFormats", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Galleries", "ExpiresAt", "TEXT NULL");
        AddColumn(db, "Galleries", "AllowedEmails", "TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""EmailOutbox"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_EmailOutbox"" PRIMARY KEY AUTOINCREMENT,
            ""CreatedAt"" TEXT NOT NULL, ""ToAddr"" TEXT NOT NULL, ""Subject"" TEXT NOT NULL, ""Body"" TEXT NOT NULL,
            ""Attempts"" INTEGER NOT NULL, ""NextTry"" TEXT NOT NULL, ""SentAt"" TEXT NULL, ""Error"" TEXT NOT NULL, ""Failed"" INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""AlertStates"" (
            ""Key"" TEXT NOT NULL CONSTRAINT ""PK_AlertStates"" PRIMARY KEY, ""Value"" TEXT NOT NULL, ""At"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Users"" (
            ""Sub"" TEXT NOT NULL CONSTRAINT ""PK_Users"" PRIMARY KEY, ""Email"" TEXT NOT NULL, ""Name"" TEXT NOT NULL,
            ""FirstSeen"" TEXT NOT NULL, ""LastSeen"" TEXT NOT NULL)");
        AddColumn(db, "Users", "Preferences", "TEXT NOT NULL DEFAULT '{}'");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""LicenseAcceptances"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_LicenseAcceptances"" PRIMARY KEY AUTOINCREMENT,
            ""Sub"" TEXT NOT NULL, ""VisitorId"" TEXT NOT NULL, ""Name"" TEXT NOT NULL, ""Version"" TEXT NOT NULL, ""AcceptedAt"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Subscribers"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Subscribers"" PRIMARY KEY AUTOINCREMENT,
            ""Email"" TEXT NOT NULL, ""Token"" TEXT NOT NULL, ""CreatedAt"" TEXT NOT NULL, ""ConfirmedAt"" TEXT NULL, ""Unsubscribed"" INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Subscribers_Token"" ON ""Subscribers"" (""Token"")");
        AddColumn(db, "Photos", "Caption", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Photos", "AltText", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Photos", "CaptionSource", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Photos", "CaptionAttempts", "INTEGER NOT NULL DEFAULT 0");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""CaptionEdits"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_CaptionEdits"" PRIMARY KEY AUTOINCREMENT,
            ""PhotoId"" INTEGER NOT NULL, ""Field"" TEXT NOT NULL, ""Base"" TEXT NOT NULL, ""Proposed"" TEXT NOT NULL,
            ""Sub"" TEXT NOT NULL, ""Name"" TEXT NOT NULL, ""CreatedAt"" TEXT NOT NULL, ""Status"" TEXT NOT NULL, ""ReviewedAt"" TEXT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Tags"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Tags"" PRIMARY KEY AUTOINCREMENT,
            ""Name"" TEXT NOT NULL, ""CreatedAt"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Tags_Name"" ON ""Tags"" (""Name"" COLLATE NOCASE)");
        AddColumn(db, "Tags", "Color", "TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""PhotoTags"" (
            ""PhotoId"" INTEGER NOT NULL, ""TagId"" INTEGER NOT NULL, CONSTRAINT ""PK_PhotoTags"" PRIMARY KEY (""PhotoId"", ""TagId""))");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""GalleryTags"" (
            ""GalleryId"" INTEGER NOT NULL, ""TagId"" INTEGER NOT NULL, CONSTRAINT ""PK_GalleryTags"" PRIMARY KEY (""GalleryId"", ""TagId""))");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""PeopleGroups"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_PeopleGroups"" PRIMARY KEY AUTOINCREMENT,
            ""Name"" TEXT NOT NULL, ""CreatedAt"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PeopleGroups_Name"" ON ""PeopleGroups"" (""Name"" COLLATE NOCASE)");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""GroupMembers"" (
            ""GroupId"" INTEGER NOT NULL, ""Email"" TEXT NOT NULL, CONSTRAINT ""PK_GroupMembers"" PRIMARY KEY (""GroupId"", ""Email""))");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""GalleryGroups"" (
            ""GalleryId"" INTEGER NOT NULL, ""GroupId"" INTEGER NOT NULL, CONSTRAINT ""PK_GalleryGroups"" PRIMARY KEY (""GalleryId"", ""GroupId""))");
    }

    static void AddColumn(AppDb db, string table, string column, string definition)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        alter.ExecuteNonQuery();
    }
}
