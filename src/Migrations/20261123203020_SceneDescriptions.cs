using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoGallery.Migrations;

/// <inheritdoc />
public partial class SceneDescriptions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "DescribedAt",
            table: "Photos",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Description",
            table: "Photos",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "DescriptionAttempts",
            table: "Photos",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "DescriptionPlayers",
            table: "Photos",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "DescriptionSource",
            table: "Photos",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DescribedAt",
            table: "Photos");

        migrationBuilder.DropColumn(
            name: "Description",
            table: "Photos");

        migrationBuilder.DropColumn(
            name: "DescriptionAttempts",
            table: "Photos");

        migrationBuilder.DropColumn(
            name: "DescriptionPlayers",
            table: "Photos");

        migrationBuilder.DropColumn(
            name: "DescriptionSource",
            table: "Photos");
    }
}
