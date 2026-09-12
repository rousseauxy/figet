using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiGet.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Feeds",
                columns: table => new
                {
                    Key = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameLower = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AnonymousRead = table.Column<bool>(type: "bit", nullable: false),
                    AllowOverwrite = table.Column<bool>(type: "bit", nullable: false),
                    DeletionBehavior = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feeds", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AccessTokens",
                columns: table => new
                {
                    Key = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FeedKey = table.Column<int>(type: "int", nullable: true),
                    Scopes = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessTokens", x => x.Key);
                    table.ForeignKey(
                        name: "FK_AccessTokens_Feeds_FeedKey",
                        column: x => x.FeedKey,
                        principalTable: "Feeds",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    Key = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FeedKey = table.Column<int>(type: "int", nullable: false),
                    Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IdLower = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.Key);
                    table.ForeignKey(
                        name: "FK_Packages_Feeds_FeedKey",
                        column: x => x.FeedKey,
                        principalTable: "Feeds",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageVersions",
                columns: table => new
                {
                    Key = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PackageKey = table.Column<long>(type: "bigint", nullable: false),
                    OriginalVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NormalizedVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NormalizedVersionLower = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsPrerelease = table.Column<bool>(type: "bit", nullable: false),
                    IsSemVer2 = table.Column<bool>(type: "bit", nullable: false),
                    Listed = table.Column<bool>(type: "bit", nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Authors = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Tags = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IconUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    LicenseUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    LicenseExpression = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ProjectUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    RepositoryUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    RepositoryType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReleaseNotes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Copyright = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MinClientVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequireLicenseAcceptance = table.Column<bool>(type: "bit", nullable: false),
                    PackageTypes = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    HashAlgorithm = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Downloads = table.Column<long>(type: "bigint", nullable: false),
                    SearchTextLower = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TagsLower = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    PackageTypesLower = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageVersions", x => x.Key);
                    table.ForeignKey(
                        name: "FK_PackageVersions_Packages_PackageKey",
                        column: x => x.PackageKey,
                        principalTable: "Packages",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageDependencies",
                columns: table => new
                {
                    Key = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PackageVersionKey = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    TargetFramework = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    VersionRange = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageDependencies", x => x.Key);
                    table.ForeignKey(
                        name: "FK_PackageDependencies_PackageVersions_PackageVersionKey",
                        column: x => x.PackageVersionKey,
                        principalTable: "PackageVersions",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SymbolFiles",
                columns: table => new
                {
                    Key = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PackageVersionKey = table.Column<long>(type: "bigint", nullable: false),
                    FeedKey = table.Column<int>(type: "int", nullable: false),
                    FileNameLower = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SymbolKeyLower = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymbolFiles", x => x.Key);
                    table.ForeignKey(
                        name: "FK_SymbolFiles_PackageVersions_PackageVersionKey",
                        column: x => x.PackageVersionKey,
                        principalTable: "PackageVersions",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessTokens_FeedKey",
                table: "AccessTokens",
                column: "FeedKey");

            migrationBuilder.CreateIndex(
                name: "IX_AccessTokens_Hash",
                table: "AccessTokens",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Feeds_NameLower",
                table: "Feeds",
                column: "NameLower",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageDependencies_PackageVersionKey",
                table: "PackageDependencies",
                column: "PackageVersionKey");

            migrationBuilder.CreateIndex(
                name: "IX_Packages_FeedKey_IdLower",
                table: "Packages",
                columns: new[] { "FeedKey", "IdLower" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_PackageKey_NormalizedVersionLower",
                table: "PackageVersions",
                columns: new[] { "PackageKey", "NormalizedVersionLower" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SymbolFiles_FeedKey_FileNameLower_SymbolKeyLower",
                table: "SymbolFiles",
                columns: new[] { "FeedKey", "FileNameLower", "SymbolKeyLower" });

            migrationBuilder.CreateIndex(
                name: "IX_SymbolFiles_PackageVersionKey",
                table: "SymbolFiles",
                column: "PackageVersionKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessTokens");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "PackageDependencies");

            migrationBuilder.DropTable(
                name: "SymbolFiles");

            migrationBuilder.DropTable(
                name: "PackageVersions");

            migrationBuilder.DropTable(
                name: "Packages");

            migrationBuilder.DropTable(
                name: "Feeds");
        }
    }
}
