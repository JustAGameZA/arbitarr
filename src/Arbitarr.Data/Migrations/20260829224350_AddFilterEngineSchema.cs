using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFilterEngineSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiKeyProfiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApiKeyName = table.Column<string>(type: "TEXT", nullable: false),
                    FilterProfileId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FilterProfiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilterProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FilterRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FilterProfileId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsAllow = table.Column<bool>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    Precedence = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilterRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VerdictCacheEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReleaseKeyHash = table.Column<string>(type: "TEXT", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", nullable: false),
                    ModelDigest = table.Column<string>(type: "TEXT", nullable: false),
                    PromptVersion = table.Column<string>(type: "TEXT", nullable: false),
                    Verdict = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastAccessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerdictCacheEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyProfiles_ApiKeyName",
                table: "ApiKeyProfiles",
                column: "ApiKeyName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilterProfiles_Name",
                table: "FilterProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilterRules_FilterProfileId",
                table: "FilterRules",
                column: "FilterProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_VerdictCacheEntries_LastAccessedAt",
                table: "VerdictCacheEntries",
                column: "LastAccessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VerdictCacheEntries_ReleaseKeyHash",
                table: "VerdictCacheEntries",
                column: "ReleaseKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeyProfiles");

            migrationBuilder.DropTable(
                name: "FilterProfiles");

            migrationBuilder.DropTable(
                name: "FilterRules");

            migrationBuilder.DropTable(
                name: "VerdictCacheEntries");
        }
    }
}
