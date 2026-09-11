using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Adaugă Users.MustChangePassword.
    ///
    /// Marchează conturile a căror parolă a fost aleasă de un administrator (cont
    /// nou sau resetare). Cât timp e true, serverul refuză înregistrarea cheilor
    /// E2EE: cheile private s-ar încuia cu o parolă cunoscută de administrator.
    ///
    /// Conturile existente primesc false: parolele lor au fost deja folosite, iar
    /// o schimbare forțată pentru toată lumea ar bloca demonstrația fără motiv.
    /// </summary>
    public partial class AddMustChangePassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "Users");
        }
    }
}
