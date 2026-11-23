using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoGallery.Migrations;

/// <inheritdoc />
public partial class FollowAPlayer : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "TaggedAt",
            table: "PhotoTags",
            type: "TEXT",
            nullable: false,
            defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

        migrationBuilder.AddColumn<bool>(
            name: "AllowFollow",
            table: "Galleries",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "PlayerFollows",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Sub = table.Column<string>(type: "TEXT", nullable: false),
                Email = table.Column<string>(type: "TEXT", nullable: false),
                PlayerId = table.Column<int>(type: "INTEGER", nullable: false),
                Token = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                NotifiedUpTo = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlayerFollows", x => x.Id);
                table.ForeignKey(
                    name: "FK_PlayerFollows_Players_PlayerId",
                    column: x => x.PlayerId,
                    principalTable: "Players",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PlayerFollows_PlayerId",
            table: "PlayerFollows",
            column: "PlayerId");

        migrationBuilder.CreateIndex(
            name: "IX_PlayerFollows_Sub_PlayerId",
            table: "PlayerFollows",
            columns: new[] { "Sub", "PlayerId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PlayerFollows_Token",
            table: "PlayerFollows",
            column: "Token",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PlayerFollows");

        migrationBuilder.DropColumn(
            name: "TaggedAt",
            table: "PhotoTags");

        migrationBuilder.DropColumn(
            name: "AllowFollow",
            table: "Galleries");
    }
}
