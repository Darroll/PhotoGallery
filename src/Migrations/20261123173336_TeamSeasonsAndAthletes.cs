using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoGallery.Migrations;

/// <inheritdoc />
public partial class TeamSeasonsAndAthletes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Season",
            table: "Teams",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "AthleteId",
            table: "Players",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "Athletes",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Athletes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Players_AthleteId",
            table: "Players",
            column: "AthleteId");

        migrationBuilder.CreateIndex(
            name: "IX_Athletes_Name",
            table: "Athletes",
            column: "Name",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_Players_Athletes_AthleteId",
            table: "Players",
            column: "AthleteId",
            principalTable: "Athletes",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Players_Athletes_AthleteId",
            table: "Players");

        migrationBuilder.DropTable(
            name: "Athletes");

        migrationBuilder.DropIndex(
            name: "IX_Players_AthleteId",
            table: "Players");

        migrationBuilder.DropColumn(
            name: "Season",
            table: "Teams");

        migrationBuilder.DropColumn(
            name: "AthleteId",
            table: "Players");
    }
}
