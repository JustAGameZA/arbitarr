using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProxyGuidReleaseRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProxyGuidReleaseEntries",
                columns: table => new
                {
                    ProxyGuid = table.Column<string>(type: "TEXT", nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CandidateJson = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxyGuidReleaseEntries", x => x.ProxyGuid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProxyGuidReleaseEntries_ExpiresAt",
                table: "ProxyGuidReleaseEntries",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProxyGuidReleaseEntries");
        }
    }
}
