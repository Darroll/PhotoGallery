using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoGallery.Migrations
{
    /// <inheritdoc />
    public partial class CoverModeAndEventEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CoverMode",
                table: "Galleries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "EventEndDate",
                table: "Galleries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverMode",
                table: "Galleries");

            migrationBuilder.DropColumn(
                name: "EventEndDate",
                table: "Galleries");
        }
    }
}
