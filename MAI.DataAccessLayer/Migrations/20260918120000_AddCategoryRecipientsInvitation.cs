using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Categoria transferului, destinatarii suplimentari (forward) și invitația
    /// de activare a contului - într-o singură migrare.
    ///
    /// Înlocuiește patru migrări fără .Designer.cs (deci fără atributele
    /// [Migration]/[DbContext]), pe care EF Core nu le descoperea și nu le aplica
    /// niciodată: AddExpiryAndCategory, AddTransferCategory, AddTransferRecipients,
    /// AddInvitationToken. Două dintre ele aveau chiar același timestamp și
    /// adăugau amândouă coloana Category; prima adăuga și ExpiresAt, care există
    /// deja din AddTwoFactorAuthentication.
    ///
    /// SQL-ul e intenționat idempotent (IF NOT EXISTS). O parte din aceste
    /// schimbări au fost aplicate manual în Supabase prin scripturile 008–010,
    /// deci migrarea trebuie să ruleze corect și pe o bază unde coloanele sau
    /// tabela există deja, și pe una unde lipsesc.
    /// </summary>
    public partial class AddCategoryRecipientsInvitation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── FileTransfers.Category ────────────────────────────────────────
            // 0=Critical  1=Important  2=General (implicit)  3=Normal
            migrationBuilder.Sql(
                """
                ALTER TABLE "FileTransfers"
                    ADD COLUMN IF NOT EXISTS "Category" integer NOT NULL DEFAULT 2;
                """);

            // ── TransferRecipients ────────────────────────────────────────────
            // Destinatarul original rămâne pe rândul FileTransfer; aici intră doar
            // destinatarii adăugați prin POST /api/Transfers/{id}/forward.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "TransferRecipients" (
                    "TransferId"          uuid                   NOT NULL,
                    "UserId"              uuid                   NOT NULL,
                    "EncryptedKeyForUser" character varying(600) NOT NULL,
                    "ForwardedById"       uuid                   NULL,
                    "SentAt"              timestamp with time zone NOT NULL DEFAULT NOW(),

                    CONSTRAINT "PK_TransferRecipients"
                        PRIMARY KEY ("TransferId", "UserId"),

                    CONSTRAINT "FK_TransferRecipients_FileTransfers_TransferId"
                        FOREIGN KEY ("TransferId") REFERENCES "FileTransfers" ("Id")
                        ON DELETE CASCADE,

                    CONSTRAINT "FK_TransferRecipients_Users_UserId"
                        FOREIGN KEY ("UserId") REFERENCES "Users" ("Id")
                        ON DELETE RESTRICT,

                    CONSTRAINT "FK_TransferRecipients_Users_ForwardedById"
                        FOREIGN KEY ("ForwardedById") REFERENCES "Users" ("Id")
                        ON DELETE SET NULL
                );

                CREATE INDEX IF NOT EXISTS "IX_TransferRecipients_UserId"
                    ON "TransferRecipients" ("UserId");

                CREATE INDEX IF NOT EXISTS "IX_TransferRecipients_TransferId"
                    ON "TransferRecipients" ("TransferId");

                -- Indexul pe cheia străină ForwardedById e creat de EF prin convenție.
                -- Scriptul manual 010 nu îl avea; fără el, modelul și baza diferă.
                CREATE INDEX IF NOT EXISTS "IX_TransferRecipients_ForwardedById"
                    ON "TransferRecipients" ("ForwardedById");
                """);

            // ── Users: invitație de activare ──────────────────────────────────
            // EmailConfirmed se adaugă într-un bloc DO, nu cu IF NOT EXISTS simplu:
            // conturile EXISTENTE trebuie marcate confirmate doar în momentul în
            // care coloana apare. Dacă coloana exista deja (adăugată manual),
            // un UPDATE necondiționat ar activa pe tăcute conturile care așteaptă
            // încă invitația.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name   = 'Users'
                          AND column_name  = 'EmailConfirmed'
                    ) THEN
                        ALTER TABLE "Users"
                            ADD COLUMN "EmailConfirmed" boolean NOT NULL DEFAULT FALSE;

                        UPDATE "Users" SET "EmailConfirmed" = TRUE;
                    END IF;
                END
                $$;

                ALTER TABLE "Users"
                    ADD COLUMN IF NOT EXISTS "InvitationToken"       text NULL,
                    ADD COLUMN IF NOT EXISTS "InvitationTokenExpiry" timestamp with time zone NULL;

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_InvitationToken"
                    ON "Users" ("InvitationToken")
                    WHERE "InvitationToken" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Users_InvitationToken";

                ALTER TABLE "Users"
                    DROP COLUMN IF EXISTS "InvitationTokenExpiry",
                    DROP COLUMN IF EXISTS "InvitationToken",
                    DROP COLUMN IF EXISTS "EmailConfirmed";

                DROP TABLE IF EXISTS "TransferRecipients";

                ALTER TABLE "FileTransfers"
                    DROP COLUMN IF EXISTS "Category";
                """);
        }
    }
}
