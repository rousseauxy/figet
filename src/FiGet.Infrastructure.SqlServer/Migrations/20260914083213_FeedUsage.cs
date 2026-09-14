using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class FeedUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChartColor",
                table: "Feeds",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FeedUsage",
                columns: table => new
                {
                    FeedKey = table.Column<int>(type: "int", nullable: false),
                    HourUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedUsage", x => new { x.FeedKey, x.HourUtc, x.Kind });
                    table.ForeignKey(
                        name: "FK_FeedUsage_Feeds_FeedKey",
                        column: x => x.FeedKey,
                        principalTable: "Feeds",
                        principalColumn: "Key");
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedUsage_HourUtc",
                table: "FeedUsage",
                column: "HourUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedUsage");

            migrationBuilder.DropColumn(
                name: "ChartColor",
                table: "Feeds");
        }
    }
}
