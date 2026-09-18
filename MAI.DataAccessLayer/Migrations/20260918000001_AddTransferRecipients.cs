using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Tabelă nouă: destinatari suplimentari (forward) ────────────────
            // Destinatarul original rămâne pe rândul FileTransfer (RecipientId +
            // EncryptedKeyForRecipient). Rândurile din această tabelă sunt adăugate
            // exclusiv prin POST /api/Transfers/{id}/forward.
            migrationBuilder.CreateTable(
                name: "TransferRecipients",
                columns: table => new
                {
                    TransferId = table.Column<Guid>(
                        type: "uuid",
                        nullable: false),

                    UserId = table.Column<Guid>(
                        type: "uuid",
                        nullable: false),

                    // RSA-3072 → 384 octeți → ~512 caractere base64. Marja de 600
                    // corespunde cu FileTransfers.EncryptedKeyForRecipient.
                    EncryptedKeyForUser = table.Column<string>(
                        type: "character varying(600)",
                        maxLength: 600,
                        nullable: false),

                    // Cine a inițiat forward-ul. Null = nu e populat deocamdată.
                    ForwardedById = table.Column<Guid>(
                        type: "uuid",
                        nullable: true),

                    SentAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    // Cheie primară compusă: același utilizator nu poate apărea
                    // de două ori ca destinatar al aceluiași transfer.
                    table.PrimaryKey("PK_TransferRecipients", x => new { x.TransferId, x.UserId });

                    // Dacă transferul e șters fizic, destinatarii dispar și ei.
                    table.ForeignKey(
                        name: "FK_TransferRecipients_FileTransfers_TransferId",
                        column: x => x.TransferId,
                        principalTable: "FileTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);

                    // Dacă utilizatorul-destinatar e șters, rândul rămâne (Restrict)
                    // — ștergerea unui cont nu trebuie să ascundă faptul că a primit
                    // documente clasificate.
                    table.ForeignKey(
                        name: "FK_TransferRecipients_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);

                    // Dacă utilizatorul care a făcut forward e șters, câmpul devine
                    // null — nu pierdem rândul destinatarului.
                    table.ForeignKey(
                        name: "FK_TransferRecipients_Users_ForwardedById",
                        column: x => x.ForwardedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            // ── Indecși ────────────────────────────────────────────────────────

            // GetAll: „există vreun rând cu UserId == currentUser?" pe fiecare pagină.
            migrationBuilder.CreateIndex(
                name: "IX_TransferRecipients_UserId",
                table: "TransferRecipients",
                column: "UserId");

            // Rapid la ștergerea în cascadă și la listarea destinatarilor unui transfer.
            migrationBuilder.CreateIndex(
                name: "IX_TransferRecipients_TransferId",
                table: "TransferRecipients",
                column: "TransferId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TransferRecipients");
        }
    }
}
