using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoGallery.Migrations
{
    /// <inheritdoc />
    public partial class TeamKitsAndNewNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TeamKit",
                table: "Galleries",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "NumberSightings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    PhotoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Dismissed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSightings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NumberSightings_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NumberSightings_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NumberSightings_PhotoId",
                table: "NumberSightings",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_NumberSightings_TeamId_Number_PhotoId",
                table: "NumberSightings",
                columns: new[] { "TeamId", "Number", "PhotoId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NumberSightings");

            migrationBuilder.DropColumn(
                name: "TeamKit",
                table: "Galleries");
        }
    }
}
