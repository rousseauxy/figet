using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class FeedInstructions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientBaseUrl",
                table: "Feeds",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedInstructions",
                table: "Feeds",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileInstructions",
                table: "Feeds",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageInstructions",
                table: "Feeds",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientBaseUrl",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "FeedInstructions",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "FileInstructions",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "PackageInstructions",
                table: "Feeds");
        }
    }
}
