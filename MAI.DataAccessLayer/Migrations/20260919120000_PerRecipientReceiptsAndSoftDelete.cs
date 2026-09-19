using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Dovada de primire per destinatar, politica de forward și ștergerea logică.
    ///
    /// 1. Destinatarul original se mută de pe FileTransfers (RecipientId,
    ///    EncryptedKeyForRecipient, DownloadedAt, RecipientSignatureValid) în
    ///    TransferRecipients, alături de destinatarii de forward. Fiecare rând
    ///    primește DownloadedAt și SignatureValid proprii. Coloanele vechi se
    ///    elimină: două locuri pentru același fapt ajung să se contrazică.
    ///
    /// 2. Se repară stările suprascrise de bug-ul jobului de expirare, care punea
    ///    Expired peste transferuri retrase sau descărcate.
    ///
    /// 3. FileTransfers primește AllowForward, DeletedAt și DeletedById.
    ///
    /// LIMITARE pentru datele existente: până acum, confirmarea oricărui
    /// destinatar (original sau de forward) scria DownloadedAt pe rândul
    /// transferului. Nu se mai poate afla cine a confirmat, așa că migrarea
    /// atribuie confirmarea destinatarului original. Pentru transferurile noi,
    /// fiecare destinatar are propria confirmare.
    /// </summary>
    public partial class PerRecipientReceiptsAndSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Dovada de primire pe TransferRecipients ───────────────────
            migrationBuilder.Sql(
                """
                ALTER TABLE "TransferRecipients"
                    ADD COLUMN IF NOT EXISTS "DownloadedAt"   timestamp with time zone NULL,
                    ADD COLUMN IF NOT EXISTS "SignatureValid" boolean NULL;
                """);

            // ── 2. Destinatarul original devine rând în TransferRecipients ───
            // ForwardedById NULL = destinatar direct. ON CONFLICT: forward-ul
            // refuza deja destinatarii existenți, dar o bază modificată manual
            // nu trebuie să oprească migrarea.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name   = 'FileTransfers'
                          AND column_name  = 'RecipientId'
                    ) THEN
                        INSERT INTO "TransferRecipients" (
                            "TransferId", "UserId", "EncryptedKeyForUser", "ForwardedById",
                            "SentAt", "DownloadedAt", "SignatureValid")
                        SELECT t."Id",
                               t."RecipientId",
                               COALESCE(t."EncryptedKeyForRecipient", ''),
                               NULL,
                               t."CreatedAt",
                               t."DownloadedAt",
                               t."RecipientSignatureValid"
                        FROM "FileTransfers" t
                        WHERE t."RecipientId" IS NOT NULL
                        ON CONFLICT ("TransferId", "UserId") DO NOTHING;
                    END IF;
                END
                $$;
                """);

            // ── 3. Reparăm stările suprascrise de jobul de expirare ──────────
            // Jobul selecta „ExpiresAt trecut și Status <> Expired”, deci punea
            // Expired și peste Revoked și peste Downloaded. RevokedAt și
            // DownloadedAt au rămas scrise, așa că starea reală se poate reface.
            //
            // Întâi golim StorageKey pe toate rândurile Expired: jobul vechi ștergea
            // obiectul, dar lăsa cheia în coloană. De acum invariantul este
            // „StorageKey nevid ⇔ obiectul există în depozit”, pe care se bazează
            // și jobul, și contabilitatea spațiului de pe /admin.
            migrationBuilder.Sql(
                """
                UPDATE "FileTransfers"
                   SET "StorageKey" = ''
                 WHERE "Status" = 2;

                -- Retrase (RevokedAt setat) → Revoked
                UPDATE "FileTransfers"
                   SET "Status" = 3
                 WHERE "Status" = 2
                   AND "RevokedAt" IS NOT NULL;

                -- Descărcate de toți destinatarii → Downloaded
                UPDATE "FileTransfers" t
                   SET "Status" = 1
                 WHERE t."Status" = 2
                   AND t."RevokedAt" IS NULL
                   AND EXISTS     (SELECT 1 FROM "TransferRecipients" r
                                    WHERE r."TransferId" = t."Id")
                   AND NOT EXISTS (SELECT 1 FROM "TransferRecipients" r
                                    WHERE r."TransferId" = t."Id"
                                      AND r."DownloadedAt" IS NULL);

                -- Noua semantică: Downloaded = TOȚI au descărcat. Un transfer marcat
                -- Downloaded de prima confirmare, dar cu destinatari de forward care
                -- nu l-au deschis, revine în Pending.
                UPDATE "FileTransfers" t
                   SET "Status" = 0
                 WHERE t."Status" = 1
                   AND EXISTS (SELECT 1 FROM "TransferRecipients" r
                                WHERE r."TransferId" = t."Id"
                                  AND r."DownloadedAt" IS NULL);
                """);

            // ── 4. Coloanele vechi dispar ────────────────────────────────────
            // PostgreSQL elimină automat, odată cu coloana, cheia străină
            // FK_FileTransfers_Users_RecipientId și indexul
            // IX_FileTransfers_Recipient_CreatedAt.
            migrationBuilder.Sql(
                """
                ALTER TABLE "FileTransfers"
                    DROP COLUMN IF EXISTS "RecipientId",
                    DROP COLUMN IF EXISTS "EncryptedKeyForRecipient",
                    DROP COLUMN IF EXISTS "DownloadedAt",
                    DROP COLUMN IF EXISTS "RecipientSignatureValid";
                """);

            // ── 5. Politica de forward și ștergerea logică ───────────────────
            // AllowForward: transferurile EXISTENTE primesc TRUE, pentru că așa
            // se comportau până acum (oricine putea redirecționa). Default-ul
            // coloanei devine apoi FALSE pentru orice rând nou.
            migrationBuilder.Sql(
                """
                ALTER TABLE "FileTransfers"
                    ADD COLUMN IF NOT EXISTS "AllowForward" boolean NOT NULL DEFAULT TRUE,
                    ADD COLUMN IF NOT EXISTS "DeletedAt"    timestamp with time zone NULL,
                    ADD COLUMN IF NOT EXISTS "DeletedById"  uuid NULL;

                ALTER TABLE "FileTransfers"
                    ALTER COLUMN "AllowForward" SET DEFAULT FALSE;
                """);

            // ── 6. Indexuri ──────────────────────────────────────────────────
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_TransferRecipients_UserId";

                CREATE INDEX IF NOT EXISTS "IX_TransferRecipients_UserId_DownloadedAt"
                    ON "TransferRecipients" ("UserId", "DownloadedAt");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Modelul vechi avea UN singur destinatar pe rândul transferului.
            // Se readuce pe coloanele vechi primul destinatar direct (cel mai
            // vechi); ceilalți rămân în TransferRecipients, ca destinatari de
            // forward. Dovezile lor de primire individuale se pierd la Down.
            migrationBuilder.Sql(
                """
                ALTER TABLE "FileTransfers"
                    ADD COLUMN IF NOT EXISTS "RecipientId"              uuid NULL,
                    ADD COLUMN IF NOT EXISTS "EncryptedKeyForRecipient" character varying(600) NULL,
                    ADD COLUMN IF NOT EXISTS "DownloadedAt"             timestamp with time zone NULL,
                    ADD COLUMN IF NOT EXISTS "RecipientSignatureValid"  boolean NULL;

                WITH primul AS (
                    SELECT DISTINCT ON (r."TransferId")
                           r."TransferId", r."UserId", r."EncryptedKeyForUser",
                           r."DownloadedAt", r."SignatureValid"
                      FROM "TransferRecipients" r
                     WHERE r."ForwardedById" IS NULL
                     ORDER BY r."TransferId", r."SentAt", r."UserId"
                )
                UPDATE "FileTransfers" t
                   SET "RecipientId"              = p."UserId",
                       "EncryptedKeyForRecipient" = NULLIF(p."EncryptedKeyForUser", ''),
                       "DownloadedAt"             = p."DownloadedAt",
                       "RecipientSignatureValid"  = p."SignatureValid"
                  FROM primul p
                 WHERE p."TransferId" = t."Id";

                DELETE FROM "TransferRecipients" r
                 USING "FileTransfers" t
                 WHERE r."TransferId" = t."Id"
                   AND r."UserId"     = t."RecipientId";

                -- Un transfer fără niciun destinatar direct nu poate exista în
                -- modelul vechi (RecipientId NOT NULL).
                DELETE FROM "FileTransfers" WHERE "RecipientId" IS NULL;

                ALTER TABLE "FileTransfers"
                    ALTER COLUMN "RecipientId" SET NOT NULL;

                ALTER TABLE "FileTransfers"
                    ADD CONSTRAINT "FK_FileTransfers_Users_RecipientId"
                    FOREIGN KEY ("RecipientId") REFERENCES "Users" ("Id") ON DELETE RESTRICT;

                CREATE INDEX IF NOT EXISTS "IX_FileTransfers_Recipient_CreatedAt"
                    ON "FileTransfers" ("RecipientId", "CreatedAt");

                ALTER TABLE "FileTransfers"
                    DROP COLUMN IF EXISTS "AllowForward",
                    DROP COLUMN IF EXISTS "DeletedAt",
                    DROP COLUMN IF EXISTS "DeletedById";

                DROP INDEX IF EXISTS "IX_TransferRecipients_UserId_DownloadedAt";

                CREATE INDEX IF NOT EXISTS "IX_TransferRecipients_UserId"
                    ON "TransferRecipients" ("UserId");

                ALTER TABLE "TransferRecipients"
                    DROP COLUMN IF EXISTS "DownloadedAt",
                    DROP COLUMN IF EXISTS "SignatureValid";
                """);
        }
    }
}
