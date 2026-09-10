using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactorAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FileTransfers_RecipientId",
                table: "FileTransfers");

            migrationBuilder.DropIndex(
                name: "IX_FileTransfers_SenderId",
                table: "FileTransfers");

            migrationBuilder.AddColumn<int>(
                name: "TwoFactorChallengeAttempts",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "TwoFactorChallengeExpiresAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TwoFactorChallengeHash",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TwoFactorEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "TwoFactorEnrolledAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TwoFactorPendingSecret",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TwoFactorRecoveryCodeHashes",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TwoFactorSecret",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SenderSignature",
                table: "FileTransfers",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FileName",
                table: "FileTransfers",
                type: "character varying(260)",
                maxLength: 260,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "EncryptionIv",
                table: "FileTransfers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedStoragePath",
                table: "FileTransfers",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedKeyForSender",
                table: "FileTransfers",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedKeyForRecipient",
                table: "FileTransfers",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CryptoSuite",
                table: "FileTransfers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ChecksumSHA256",
                table: "FileTransfers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<long>(
                name: "CiphertextSize",
                table: "FileTransfers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "FileTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageKey",
                table: "FileTransfers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_ExpiresAt",
                table: "FileTransfers",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_Recipient_CreatedAt",
                table: "FileTransfers",
                columns: new[] { "RecipientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_Sender_CreatedAt",
                table: "FileTransfers",
                columns: new[] { "SenderId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_Status",
                table: "FileTransfers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UX_FileTransfers_StorageKey",
                table: "FileTransfers",
                column: "StorageKey",
                unique: true,
                filter: "\"StorageKey\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FileTransfers_ExpiresAt",
                table: "FileTransfers");

            migrationBuilder.DropIndex(
                name: "IX_FileTransfers_Recipient_CreatedAt",
                table: "FileTransfers");

            migrationBuilder.DropIndex(
                name: "IX_FileTransfers_Sender_CreatedAt",
                table: "FileTransfers");

            migrationBuilder.DropIndex(
                name: "IX_FileTransfers_Status",
                table: "FileTransfers");

            migrationBuilder.DropIndex(
                name: "UX_FileTransfers_StorageKey",
                table: "FileTransfers");

            migrationBuilder.DropColumn(
                name: "TwoFactorChallengeAttempts",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorChallengeExpiresAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorChallengeHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorEnrolledAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorPendingSecret",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorRecoveryCodeHashes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorSecret",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CiphertextSize",
                table: "FileTransfers");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "FileTransfers");

            migrationBuilder.DropColumn(
                name: "StorageKey",
                table: "FileTransfers");

            migrationBuilder.AlterColumn<string>(
                name: "SenderSignature",
                table: "FileTransfers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(600)",
                oldMaxLength: 600,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FileName",
                table: "FileTransfers",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(260)",
                oldMaxLength: 260);

            migrationBuilder.AlterColumn<string>(
                name: "EncryptionIv",
                table: "FileTransfers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedStoragePath",
                table: "FileTransfers",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedKeyForSender",
                table: "FileTransfers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(600)",
                oldMaxLength: 600,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedKeyForRecipient",
                table: "FileTransfers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(600)",
                oldMaxLength: 600,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CryptoSuite",
                table: "FileTransfers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ChecksumSHA256",
                table: "FileTransfers",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_RecipientId",
                table: "FileTransfers",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_SenderId",
                table: "FileTransfers",
                column: "SenderId");
        }
    }
}
