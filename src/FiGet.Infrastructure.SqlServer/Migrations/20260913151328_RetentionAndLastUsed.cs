using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RetentionAndLastUsed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastUsedUtc",
                table: "PackageVersions",
                type: "datetime2",
                nullable: false,
                // Every version already stored counts as used on the day this arrives, not in the year 1: cache pruning measures
                // "unused" from this column, and a zero date would prune every existing cached copy on its first run.
                defaultValue: new DateTime(2026, 9, 13, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<int>(
                name: "PruneCachedAfterDays",
                table: "Feeds",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetainIfUsedWithinDays",
                table: "Feeds",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RetainPerMajorVersion",
                table: "Feeds",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RetainPrereleaseVersions",
                table: "Feeds",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetainStableVersions",
                table: "Feeds",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUsedUtc",
                table: "PackageVersions");

            migrationBuilder.DropColumn(
                name: "PruneCachedAfterDays",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "RetainIfUsedWithinDays",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "RetainPerMajorVersion",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "RetainPrereleaseVersions",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "RetainStableVersions",
                table: "Feeds");
        }
    }
}
