using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ChangeReportIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_PackageKey_PublishedUtc",
                table: "PackageVersions",
                columns: new[] { "PackageKey", "PublishedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CachedUpstreamDescriptions_FeedUpstreamKey_PublishedUtc",
                table: "CachedUpstreamDescriptions",
                columns: new[] { "FeedUpstreamKey", "PublishedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PackageVersions_PackageKey_PublishedUtc",
                table: "PackageVersions");

            migrationBuilder.DropIndex(
                name: "IX_CachedUpstreamDescriptions_FeedUpstreamKey_PublishedUtc",
                table: "CachedUpstreamDescriptions");
        }
    }
}
