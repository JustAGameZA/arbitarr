using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceHealthLastOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastOutcome",
                table: "SourceHealthRecords",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpstreamStatusCode",
                table: "SourceHealthRecords",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastOutcome",
                table: "SourceHealthRecords");

            migrationBuilder.DropColumn(
                name: "LastUpstreamStatusCode",
                table: "SourceHealthRecords");
        }
    }
}