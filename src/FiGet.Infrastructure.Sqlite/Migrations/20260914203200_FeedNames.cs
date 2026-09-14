using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class FeedNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Names",
                columns: table => new
                {
                    NameLower = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FeedKey = table.Column<int>(type: "INTEGER", nullable: false),
                    IsAlias = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Names", x => x.NameLower);
                    table.ForeignKey(
                        name: "FK_Names_Feeds_FeedKey",
                        column: x => x.FeedKey,
                        principalTable: "Feeds",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Names_FeedKey",
                table: "Names",
                column: "FeedKey");

            // Every name already in use claims its row, so the primary key guards existing feeds from the first start on.
            migrationBuilder.Sql("INSERT INTO \"Names\" (\"NameLower\", \"FeedKey\", \"IsAlias\") SELECT \"NameLower\", \"Key\", 0 FROM \"Feeds\";");
            migrationBuilder.Sql("INSERT INTO \"Names\" (\"NameLower\", \"FeedKey\", \"IsAlias\") SELECT \"NameLower\", \"FeedKey\", 1 FROM \"FeedAliases\" WHERE \"NameLower\" NOT IN (SELECT \"NameLower\" FROM \"Names\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Names");
        }
    }
}
