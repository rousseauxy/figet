using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class JobLeaseResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FinishedUtc",
                table: "JobLeases",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastResult",
                table: "JobLeases",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinishedUtc",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "LastResult",
                table: "JobLeases");
        }
    }
}
