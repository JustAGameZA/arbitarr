using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBaseBackoffSecondsToSourceHealthRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BaseBackoffSeconds",
                table: "SourceHealthRecords",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseBackoffSeconds",
                table: "SourceHealthRecords");
        }
    }
}
