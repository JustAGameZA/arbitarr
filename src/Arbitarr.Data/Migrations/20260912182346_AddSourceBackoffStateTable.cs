using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceBackoffStateTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceBackoffStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisabledUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DisabledLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    LastOutcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsPermanentlyDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceBackoffStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceBackoffStates_SourceName",
                table: "SourceBackoffStates",
                column: "SourceName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceBackoffStates");
        }
    }
}
