using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class UpstreamCatalogueFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Dependencies",
                table: "CachedUpstreamIndexes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UnlistedVersions",
                table: "CachedUpstreamIndexes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Dependencies",
                table: "CachedUpstreamIndexes");

            migrationBuilder.DropColumn(
                name: "UnlistedVersions",
                table: "CachedUpstreamIndexes");
        }
    }
}
