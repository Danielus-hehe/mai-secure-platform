using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ExpiresAt există deja — adăugăm doar Category.
            // 0=Critical  1=Important  2=General(default)  3=Normal
            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "FileTransfers",
                type: "integer",
                nullable: false,
                defaultValue: 2); // TransferCategory.General
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "FileTransfers");
        }
    }
}