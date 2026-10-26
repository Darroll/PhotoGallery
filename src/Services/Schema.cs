using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>Adds columns introduced after the first release to databases created by EnsureCreated.</summary>
public static class Schema
{
    public static void Upgrade(AppDb db)
    {
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Visits"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Visits"" PRIMARY KEY AUTOINCREMENT,
            ""Time"" TEXT NOT NULL, ""Kind"" TEXT NOT NULL, ""GalleryId"" INTEGER NULL, ""PhotoId"" INTEGER NULL,
            ""Ip"" TEXT NOT NULL, ""Country"" TEXT NOT NULL, ""UserAgent"" TEXT NOT NULL, ""IsBot"" INTEGER NOT NULL, ""Detail"" TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Visits_Time"" ON ""Visits"" (""Time"")");
        db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Visits_GalleryId_Time"" ON ""Visits"" (""GalleryId"", ""Time"")");
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
