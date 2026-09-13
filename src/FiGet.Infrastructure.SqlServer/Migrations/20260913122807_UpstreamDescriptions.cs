using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class UpstreamDescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedUpstreamDescriptions",
                columns: table => new
                {
                    Key = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FeedUpstreamKey = table.Column<int>(type: "int", nullable: false),
                    IdLower = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NormalizedVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Authors = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ProjectUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    IconUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    LicenseUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Downloads = table.Column<long>(type: "bigint", nullable: false),
                    TagSetHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedUpstreamDescriptions", x => x.Key);
                    table.ForeignKey(
                        name: "FK_CachedUpstreamDescriptions_FeedUpstreams_FeedUpstreamKey",
                        column: x => x.FeedUpstreamKey,
                        principalTable: "FeedUpstreams",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CachedUpstreamTagSets",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Tags = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedUpstreamTagSets", x => x.Hash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedUpstreamDescriptions_FeedUpstreamKey_IdLower_NormalizedVersion",
                table: "CachedUpstreamDescriptions",
                columns: new[] { "FeedUpstreamKey", "IdLower", "NormalizedVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CachedUpstreamDescriptions_TagSetHash",
                table: "CachedUpstreamDescriptions",
                column: "TagSetHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedUpstreamDescriptions");

            migrationBuilder.DropTable(
                name: "CachedUpstreamTagSets");
        }
    }
}
