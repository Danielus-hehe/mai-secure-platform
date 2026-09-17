using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddExpiryAndCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── ExpiresAt ─────────────────────────────────────────────────
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "FileTransfers",
                type: "timestamp with time zone",
                nullable: true);

            // ── Category ──────────────────────────────────────────────────
            // 0=Critical  1=Important  2=General(default)  3=Normal
            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "FileTransfers",
                type: "integer",
                nullable: false,
                defaultValue: 2); // TransferCategory.General

            // Index parțial — interogările de expirare nu scanează rândurile fără ExpiresAt
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_FileTransfers_ExpiresAt"
                    ON "FileTransfers" ("ExpiresAt")
                    WHERE "ExpiresAt" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"DROP INDEX IF EXISTS ""IX_FileTransfers_ExpiresAt"";");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "FileTransfers");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "FileTransfers");
        }
    }
}