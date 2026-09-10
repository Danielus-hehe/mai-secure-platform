using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddE2eeKeyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CryptoSuite",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedPrivateBundle",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KeyDerivationIterations",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyDerivationSalt",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyWrapIv",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "KeysCreatedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicKeyEncryption",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicKeySigning",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CryptoSuite",
                table: "FileTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedKeyForRecipient",
                table: "FileTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedKeyForSender",
                table: "FileTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptionIv",
                table: "FileTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEncrypted",
                table: "FileTransfers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SenderSignature",
                table: "FileTransfers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CryptoSuite",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EncryptedPrivateBundle",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "KeyDerivationIterations",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "KeyDerivationSalt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "KeyWrapIv",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "KeysCreatedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PublicKeyEncryption",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PublicKeySigning",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CryptoSuite",
                table: "FileTransfers");

            migrationBuilder.DropColumn(
                name: "EncryptedKeyForRecipient",
                table: "FileTransfers");

            migrationBuilder.DropColumn(
                name: "EncryptedKeyForSender",
                table: "FileTransfers");

            migrationBuilder.DropColumn(
                name: "EncryptionIv",
                table: "FileTransfers");

            migrationBuilder.DropColumn(
                name: "IsEncrypted",
                table: "FileTransfers");

            migrationBuilder.DropColumn(
                name: "SenderSignature",
                table: "FileTransfers");
        }
    }
}
