using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourcePerIndexerColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiPath",
                table: "Sources",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "/api");

            migrationBuilder.AddColumn<int>(
                name: "GrabLimit",
                table: "Sources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LimitsUnit",
                table: "Sources",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Day");

            migrationBuilder.AddColumn<string>(
                name: "NzbAccessMode",
                table: "Sources",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Proxy");

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Sources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QueryLimit",
                table: "Sources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutSeconds",
                table: "Sources",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiPath",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "GrabLimit",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "LimitsUnit",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "NzbAccessMode",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "QueryLimit",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "TimeoutSeconds",
                table: "Sources");
        }
    }
}
