using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class JobLeaseTakenUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TakenUtc",
                table: "JobLeases",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TakenUtc",
                table: "JobLeases");
        }
    }
}
