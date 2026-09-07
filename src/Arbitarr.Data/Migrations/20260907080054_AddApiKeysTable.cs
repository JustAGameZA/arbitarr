using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitarr.Data.Migrations
{
    /// <inheritdoc />
    // #58: purely additive (one new table, zero changes to any existing table or column), so
    // applying this to an existing populated database is lossless by construction. Nothing is
    // migrated INTO it either: the pre-#58 shared admin key stays exactly where it is, in the
    // Settings table, and keeps working — see DbAdminKeyResolver. An upgrade that moved it here
    // would invalidate the credential every caller on a running deployment is using, at a moment
    // the operator is not watching, which is the one failure this feature must not have.
    //
    // No credential can land in this table: ApiKeyEntry carries no secret-shaped column, only a
    // SHA-256 digest (see that type's doc comment), so a leaked database or a #56 backup archive
    // yields hashes rather than working keys.
    public partial class AddApiKeysTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Label",
                table: "ApiKeys",
                column: "Label",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys");
        }
    }
}
