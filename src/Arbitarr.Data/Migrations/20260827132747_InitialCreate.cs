using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CapsCacheEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceName = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsStale = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapsCacheEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetadataCacheEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesKey = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceSnapshotVersion = table.Column<string>(type: "TEXT", nullable: false),
                    IsNegative = table.Column<bool>(type: "INTEGER", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RefreshAfter = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataCacheEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuerySnapshotCacheEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SnapshotToken = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuerySnapshotCacheEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchResultCacheEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QueryKey = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FreshUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ServeUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastAccessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchResultCacheEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    Floor = table.Column<string>(type: "TEXT", nullable: true),
                    Ceiling = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "SourceHealthRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceName = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentBackoffSeconds = table.Column<double>(type: "REAL", nullable: false),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    NextProbeAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceHealthRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SuppressionAuditLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReleaseIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    QueryKey = table.Column<string>(type: "TEXT", nullable: false),
                    RuleName = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ShadowMode = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuppressionAuditLogEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CapsCacheEntries_SourceName",
                table: "CapsCacheEntries",
                column: "SourceName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetadataCacheEntries_SeriesKey_Source",
                table: "MetadataCacheEntries",
                columns: new[] { "SeriesKey", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuerySnapshotCacheEntries_ExpiresAt",
                table: "QuerySnapshotCacheEntries",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_QuerySnapshotCacheEntries_SnapshotToken",
                table: "QuerySnapshotCacheEntries",
                column: "SnapshotToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchResultCacheEntries_LastAccessedAt",
                table: "SearchResultCacheEntries",
                column: "LastAccessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SearchResultCacheEntries_QueryKey",
                table: "SearchResultCacheEntries",
                column: "QueryKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchResultCacheEntries_ServeUntil",
                table: "SearchResultCacheEntries",
                column: "ServeUntil");

            migrationBuilder.CreateIndex(
                name: "IX_SourceHealthRecords_SourceName",
                table: "SourceHealthRecords",
                column: "SourceName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionAuditLogEntries_OccurredAt",
                table: "SuppressionAuditLogEntries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionAuditLogEntries_QueryKey",
                table: "SuppressionAuditLogEntries",
                column: "QueryKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapsCacheEntries");

            migrationBuilder.DropTable(
                name: "MetadataCacheEntries");

            migrationBuilder.DropTable(
                name: "QuerySnapshotCacheEntries");

            migrationBuilder.DropTable(
                name: "SearchResultCacheEntries");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SourceHealthRecords");

            migrationBuilder.DropTable(
                name: "SuppressionAuditLogEntries");
        }
    }
}
