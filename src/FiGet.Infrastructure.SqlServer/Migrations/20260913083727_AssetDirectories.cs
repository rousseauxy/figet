using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AssetDirectories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssetItems",
                columns: table => new
                {
                    Key = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FeedKey = table.Column<int>(type: "int", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PathLower = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ParentLower = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    IsDirectory = table.Column<bool>(type: "bit", nullable: false),
                    BlobId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Md5 = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Sha1 = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Sha512 = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    UserMetadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CacheHeaderType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CacheHeaderValue = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetItems", x => x.Key);
                    table.ForeignKey(
                        name: "FK_AssetItems_Feeds_FeedKey",
                        column: x => x.FeedKey,
                        principalTable: "Feeds",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetItems_FeedKey_ParentLower",
                table: "AssetItems",
                columns: new[] { "FeedKey", "ParentLower" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetItems_FeedKey_PathLower",
                table: "AssetItems",
                columns: new[] { "FeedKey", "PathLower" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetItems");
        }
    }
}
