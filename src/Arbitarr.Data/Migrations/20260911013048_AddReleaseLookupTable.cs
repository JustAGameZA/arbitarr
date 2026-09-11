using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseLookupTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReleaseLookupEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProxyGuid = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseLookupEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseLookupEntries_ExpiresAt",
                table: "ReleaseLookupEntries",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseLookupEntries_ProxyGuid",
                table: "ReleaseLookupEntries",
                column: "ProxyGuid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReleaseLookupEntries");
        }
    }
}
