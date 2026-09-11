using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Adaugă Users.TwoFactorLastUsedStep: intervalul TOTP al ultimului cod
    /// acceptat. Cu el, același cod nu mai poate fi folosit de două ori
    /// (RFC 6238, 5.2). Null pentru toate conturile existente: primul cod
    /// acceptat după migrare îl completează.
    /// </summary>
    public partial class AddTotpReplayProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TwoFactorLastUsedStep",
                table: "Users",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TwoFactorLastUsedStep",
                table: "Users");
        }
    }
}
