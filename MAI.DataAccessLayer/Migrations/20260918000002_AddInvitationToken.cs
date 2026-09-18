using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Câmpuri noi pe Users ───────────────────────────────────────────
            migrationBuilder.AddColumn<string>(
                name: "InvitationToken",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvitationTokenExpiry",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailConfirmed",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Userii existenți sunt considerați automat confirmați —
            // nu mai trebuie să treacă prin fluxul de invitație.
            migrationBuilder.Sql("UPDATE \"Users\" SET \"EmailConfirmed\" = TRUE;");

            // ── Index unic parțial pe token ─────────────────────────────────
            // Parțial (WHERE ... IS NOT NULL) ca să nu blocheze mai multe
            // rânduri cu NULL simultan — același principiu ca la email și username.
            migrationBuilder.CreateIndex(
                name: "IX_Users_InvitationToken",
                table: "Users",
                column: "InvitationToken",
                unique: true,
                filter: "\"InvitationToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_InvitationToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "InvitationToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "InvitationTokenExpiry",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EmailConfirmed",
                table: "Users");
        }
    }
}
