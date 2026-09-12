using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class UpstreamCatalogueIdRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows predate the column and hold NULL. SQLite implements the alter below as a
            // table rebuild that copies the data across, so a NULL here fails the migration on startup -
            // and this application migrates on startup, which would turn a broken page into a container
            // that never comes up.
            migrationBuilder.Sql("UPDATE CachedUpstreamIndexes SET Id = '' WHERE Id IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "Id",
                table: "CachedUpstreamIndexes",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Id",
                table: "CachedUpstreamIndexes",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldDefaultValue: "");
        }
    }
}
