using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class GroupsAndFeedPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Key = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameLower = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "FeedPermissions",
                columns: table => new
                {
                    Key = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FeedKey = table.Column<int>(type: "int", nullable: false),
                    UserKey = table.Column<int>(type: "int", nullable: true),
                    GroupKey = table.Column<int>(type: "int", nullable: true),
                    Level = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedPermissions", x => x.Key);
                    table.ForeignKey(
                        name: "FK_FeedPermissions_Feeds_FeedKey",
                        column: x => x.FeedKey,
                        principalTable: "Feeds",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FeedPermissions_Groups_GroupKey",
                        column: x => x.GroupKey,
                        principalTable: "Groups",
                        principalColumn: "Key");
                    table.ForeignKey(
                        name: "FK_FeedPermissions_Users_UserKey",
                        column: x => x.UserKey,
                        principalTable: "Users",
                        principalColumn: "Key");
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    GroupKey = table.Column<int>(type: "int", nullable: false),
                    UserKey = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => new { x.GroupKey, x.UserKey });
                    table.ForeignKey(
                        name: "FK_GroupMembers_Groups_GroupKey",
                        column: x => x.GroupKey,
                        principalTable: "Groups",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Users_UserKey",
                        column: x => x.UserKey,
                        principalTable: "Users",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedPermissions_FeedKey_GroupKey",
                table: "FeedPermissions",
                columns: new[] { "FeedKey", "GroupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedPermissions_FeedKey_UserKey",
                table: "FeedPermissions",
                columns: new[] { "FeedKey", "UserKey" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedPermissions_GroupKey",
                table: "FeedPermissions",
                column: "GroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_FeedPermissions_UserKey",
                table: "FeedPermissions",
                column: "UserKey");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_UserKey",
                table: "GroupMembers",
                column: "UserKey");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_NameLower",
                table: "Groups",
                column: "NameLower",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedPermissions");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "Groups");
        }
    }
}
