using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    // #53 stage 53a: purely additive (one new table, zero changes to any existing table or column),
    // so applying this to an existing populated database is lossless by construction — there is
    // nothing here that could touch or drop an existing row. Source API keys are NOT a column on
    // this table (see Entities.Source's doc comment); they land as ordinary write-only rows in the
    // pre-existing Settings table via SourceRepository, so this migration owns no secret storage.
    public partial class AddSourcesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sources_DisplayName",
                table: "Sources",
                column: "DisplayName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sources");
        }
    }
}
