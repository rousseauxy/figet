using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Upstreams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedUpstreams",
                columns: table => new
                {
                    Key = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FeedKey = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Allow = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Deny = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CredentialRef = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedUpstreams", x => x.Key);
                    table.ForeignKey(
                        name: "FK_FeedUpstreams_Feeds_FeedKey",
                        column: x => x.FeedKey,
                        principalTable: "Feeds",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CachedUpstreamIndexes",
                columns: table => new
                {
                    Key = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FeedUpstreamKey = table.Column<int>(type: "int", nullable: false),
                    IdLower = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Versions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SemVer2Versions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FetchedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Stale = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedUpstreamIndexes", x => x.Key);
                    table.ForeignKey(
                        name: "FK_CachedUpstreamIndexes_FeedUpstreams_FeedUpstreamKey",
                        column: x => x.FeedUpstreamKey,
                        principalTable: "FeedUpstreams",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedUpstreamIndexes_FeedUpstreamKey_IdLower",
                table: "CachedUpstreamIndexes",
                columns: new[] { "FeedUpstreamKey", "IdLower" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeedUpstreams_FeedKey_Name",
                table: "FeedUpstreams",
                columns: new[] { "FeedKey", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedUpstreamIndexes");

            migrationBuilder.DropTable(
                name: "FeedUpstreams");
        }
    }
}
