using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoGallery.Migrations
{
    /// <inheritdoc />
    public partial class LikeCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowLikes",
                table: "Galleries",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowLikeCounts",
                table: "Galleries",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityKey",
                table: "Favorites",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Ip",
                table: "Favorites",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // hearts given before likes were counted have no address on record: each browser counts once
            migrationBuilder.Sql("UPDATE Favorites SET IdentityKey = 'v:' || VisitorId WHERE IdentityKey = ''");

            migrationBuilder.CreateIndex(
                name: "IX_Favorites_PhotoId_IdentityKey",
                table: "Favorites",
                columns: new[] { "PhotoId", "IdentityKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Favorites_PhotoId_IdentityKey",
                table: "Favorites");

            migrationBuilder.DropColumn(
                name: "AllowLikes",
                table: "Galleries");

            migrationBuilder.DropColumn(
                name: "ShowLikeCounts",
                table: "Galleries");

            migrationBuilder.DropColumn(
                name: "IdentityKey",
                table: "Favorites");

            migrationBuilder.DropColumn(
                name: "Ip",
                table: "Favorites");
        }
    }
}
