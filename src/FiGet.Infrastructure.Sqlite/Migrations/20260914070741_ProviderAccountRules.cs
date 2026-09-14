using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ProviderAccountRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedEmailDomains",
                table: "OidcProviders",
                type: "TEXT",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            // True for the providers that exist: they made accounts until now, and a sign-in that stops working after an
            // upgrade is not how a new setting should arrive. A provider added from here on starts with it off.
            migrationBuilder.AddColumn<bool>(
                name: "CreateAccounts",
                table: "OidcProviders",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedEmailDomains",
                table: "OidcProviders");

            migrationBuilder.DropColumn(
                name: "CreateAccounts",
                table: "OidcProviders");
        }
    }
}
