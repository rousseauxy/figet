using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SharedFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AnonymousList",
                table: "Feeds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FolderRoot",
                table: "Feeds",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FolderWritable",
                table: "Feeds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AssetCachePolicies",
                columns: table => new
                {
                    Key = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FeedKey = table.Column<int>(type: "INTEGER", nullable: false),
                    PathLower = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MaxAgeSeconds = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetCachePolicies", x => x.Key);
                    table.ForeignKey(
                        name: "FK_AssetCachePolicies_Feeds_FeedKey",
                        column: x => x.FeedKey,
                        principalTable: "Feeds",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetCachePolicies_FeedKey_PathLower",
                table: "AssetCachePolicies",
                columns: new[] { "FeedKey", "PathLower" },
                unique: true);

            // Listing followed downloading before the switch existed; every directory keeps behaving as it did.
            migrationBuilder.Sql("UPDATE \"Feeds\" SET \"AnonymousList\" = \"AnonymousRead\" WHERE \"Kind\" = 'Assets';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetCachePolicies");

            migrationBuilder.DropColumn(
                name: "AnonymousList",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "FolderRoot",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "FolderWritable",
                table: "Feeds");
        }
    }
}
