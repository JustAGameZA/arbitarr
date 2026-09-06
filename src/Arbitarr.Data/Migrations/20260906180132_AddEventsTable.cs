using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    // #55 step 1 (foundation shared with #54): purely additive (one new table, zero changes to any
    // existing table or column), so applying this to an existing populated database is lossless by
    // construction — there is nothing here that could touch or drop an existing row. No credential
    // column exists on this table by design (see Entities.EventEntry's doc comment).
    public partial class AddEventsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    SourceDisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Kind_OccurredAt",
                table: "Events",
                columns: new[] { "Kind", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
