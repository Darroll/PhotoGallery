using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoGallery.Migrations;

/// <inheritdoc />
public partial class TeamLogoAndColors : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LogoFile",
            table: "Teams",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "MainColor",
            table: "Teams",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "SecondColor",
            table: "Teams",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<bool>(
            name: "SiteLook",
            table: "Teams",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LogoFile",
            table: "Teams");

        migrationBuilder.DropColumn(
            name: "MainColor",
            table: "Teams");

        migrationBuilder.DropColumn(
            name: "SecondColor",
            table: "Teams");

        migrationBuilder.DropColumn(
            name: "SiteLook",
            table: "Teams");
    }
}
