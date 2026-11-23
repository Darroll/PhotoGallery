using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoGallery.Migrations;

/// <inheritdoc />
public partial class FollowSiteWide : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // the per-gallery switch of the 2.3.0 test build: on stays on; off meant "not chosen", so it follows the site-wide setting
        migrationBuilder.AddColumn<bool>(
            name: "FollowPlayers",
            table: "Galleries",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.Sql("UPDATE \"Galleries\" SET \"FollowPlayers\" = 1 WHERE \"AllowFollow\" = 1");
        // SQLite's own DROP COLUMN, not EF's table rebuild, which would lose the other columns' defaults
        migrationBuilder.Sql("ALTER TABLE \"Galleries\" DROP COLUMN \"AllowFollow\"");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FollowPlayers",
            table: "Galleries");

        migrationBuilder.AddColumn<bool>(
            name: "AllowFollow",
            table: "Galleries",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }
}
