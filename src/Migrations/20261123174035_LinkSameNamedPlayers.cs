using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoGallery.Migrations;

/// <inheritdoc />
public partial class LinkSameNamedPlayers : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // every named player on the rosters becomes a girl, and players with the same name on several teams are the same girl
        migrationBuilder.Sql(@"INSERT INTO ""Athletes"" (""Name"", ""CreatedAt"")
            SELECT MIN(trim(""Name"")), datetime('now') FROM ""Players"" WHERE trim(""Name"") <> '' GROUP BY lower(trim(""Name""))");
        migrationBuilder.Sql(@"UPDATE ""Players"" SET ""AthleteId"" = (SELECT a.""Id"" FROM ""Athletes"" a WHERE a.""Name"" = trim(""Players"".""Name""))
            WHERE trim(""Name"") <> ''");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"UPDATE ""Players"" SET ""AthleteId"" = NULL");
        migrationBuilder.Sql(@"DELETE FROM ""Athletes""");
    }
}
