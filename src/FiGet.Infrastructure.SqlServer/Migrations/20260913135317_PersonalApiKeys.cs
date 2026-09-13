using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class PersonalApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserKey",
                table: "AccessTokens",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessTokens_UserKey",
                table: "AccessTokens",
                column: "UserKey");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessTokens_Users_UserKey",
                table: "AccessTokens",
                column: "UserKey",
                principalTable: "Users",
                principalColumn: "Key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessTokens_Users_UserKey",
                table: "AccessTokens");

            migrationBuilder.DropIndex(
                name: "IX_AccessTokens_UserKey",
                table: "AccessTokens");

            migrationBuilder.DropColumn(
                name: "UserKey",
                table: "AccessTokens");
        }
    }
}
