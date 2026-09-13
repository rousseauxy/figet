using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AuditEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Key = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WhenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(192)", maxLength: 192, nullable: false),
                    ActorLower = table.Column<string>(type: "nvarchar(192)", maxLength: 192, nullable: false),
                    Feed = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FeedLower = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Caller = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Action_Key",
                table: "AuditEntries",
                columns: new[] { "Action", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_ActorLower_Key",
                table: "AuditEntries",
                columns: new[] { "ActorLower", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_FeedLower_Key",
                table: "AuditEntries",
                columns: new[] { "FeedLower", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_WhenUtc",
                table: "AuditEntries",
                column: "WhenUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");
        }
    }
}
