using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class UpstreamReleaseNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReleaseNotes",
                table: "CachedUpstreamDescriptions",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReleaseNotes",
                table: "CachedUpstreamDescriptions");
        }
    }
}
