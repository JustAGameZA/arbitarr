using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameLastAccessedAtToLastRequestedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastAccessedAt",
                table: "SearchResultCacheEntries",
                newName: "LastRequestedAt");

            migrationBuilder.RenameIndex(
                name: "IX_SearchResultCacheEntries_LastAccessedAt",
                table: "SearchResultCacheEntries",
                newName: "IX_SearchResultCacheEntries_LastRequestedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastRequestedAt",
                table: "SearchResultCacheEntries",
                newName: "LastAccessedAt");

            migrationBuilder.RenameIndex(
                name: "IX_SearchResultCacheEntries_LastRequestedAt",
                table: "SearchResultCacheEntries",
                newName: "IX_SearchResultCacheEntries_LastAccessedAt");
        }
    }
}
