using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Control de concurență și durată absolută a sesiunii.
    ///
    /// DocumentVersions primește indexul unic (DocumentId, VersionNumber). Înainte,
    /// două versiuni noi încărcate simultan primeau același număr, iar a doua
    /// suprascria obiectul primei în MinIO. Indexul vechi pe DocumentId se
    /// șterge: cel nou începe cu aceeași coloană și îl acoperă.
    ///
    /// UserSessions primește:
    ///   - AbsoluteExpiresAt: sfârșitul fix al sesiunii, pe care rotația nu îl
    ///     mai poate depăși;
    ///   - PreviousRefreshTokenHash: tokenul înlocuit la ultima rotație, ca
    ///     refolosirea lui să fie recunoscută și sesiunea închisă.
    ///
    /// Tokenurile de concurență (Users, UserSessions, Documents) folosesc coloana
    /// de sistem xmin, pe care PostgreSQL o are deja pe fiecare rând. De aceea
    /// nu apar mai jos: nu e nimic de creat.
    ///
    /// SQL scris de mână, idempotent (IF [NOT] EXISTS), ca restul migrărilor din
    /// proiect: poate fi rulat și pe o bază unde o parte a fost aplicată manual.
    /// </summary>
    public partial class ConcurrencyAndSessionLifetime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- Versiuni duplicate existente, produse de cursa pe care o repară
                -- migrarea. Nu se renumerotează automat: două rânduri cu același
                -- număr au, cel mai probabil, aceeași cheie în depozit, deci unul
                -- dintre ele indică un fișier suprascris. Care e cel bun decide un
                -- om, nu migrarea. Mesajul spune exact ce rânduri sunt.
                DO $$
                DECLARE
                    duplicates text;
                BEGIN
                    SELECT string_agg(format('document %s, v%s (%s randuri)', d."DocumentId", d."VersionNumber", d.n), '; ')
                      INTO duplicates
                      FROM (SELECT "DocumentId", "VersionNumber", count(*) AS n
                              FROM "DocumentVersions"
                             GROUP BY "DocumentId", "VersionNumber"
                            HAVING count(*) > 1) d;

                    IF duplicates IS NOT NULL THEN
                        RAISE EXCEPTION 'DocumentVersions contine versiuni duplicate: %. Verificati randurile (SELECT * FROM "DocumentVersions" WHERE "DocumentId" = ...), stergeti-l pe cel gresit si rulati din nou migrarea.', duplicates;
                    END IF;
                END $$;

                DROP INDEX IF EXISTS "IX_DocumentVersions_DocumentId";

                CREATE UNIQUE INDEX IF NOT EXISTS "UX_DocumentVersions_DocumentId_VersionNumber"
                    ON "DocumentVersions" ("DocumentId", "VersionNumber");

                ALTER TABLE "UserSessions"
                    ADD COLUMN IF NOT EXISTS "AbsoluteExpiresAt"        timestamp with time zone,
                    ADD COLUMN IF NOT EXISTS "PreviousRefreshTokenHash" character varying(64);

                -- Sesiunile deja deschise primesc limita implicită de 12 ore de la
                -- autentificare (Jwt:SessionAbsoluteHours). Cele mai vechi de atât
                -- se închid la următorul refresh: exact efectul dorit, fiindcă
                -- până acum nicio sesiune nu avea limită.
                UPDATE "UserSessions"
                   SET "AbsoluteExpiresAt" = LEAST("ExpiresAt", "CreatedAt" + interval '12 hours')
                 WHERE "AbsoluteExpiresAt" IS NULL;

                UPDATE "UserSessions"
                   SET "ExpiresAt" = "AbsoluteExpiresAt"
                 WHERE "ExpiresAt" > "AbsoluteExpiresAt";

                ALTER TABLE "UserSessions"
                    ALTER COLUMN "AbsoluteExpiresAt" SET NOT NULL;

                CREATE INDEX IF NOT EXISTS "IX_UserSessions_PreviousRefreshTokenHash"
                    ON "UserSessions" ("PreviousRefreshTokenHash")
                 WHERE "PreviousRefreshTokenHash" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // La revenire, sesiunile redevin fără limită absolută, iar versiunile
            // pot din nou primi același număr. Tokenurile xmin dispar odată cu
            // codul care le folosește; coloana de sistem rămâne, ca întotdeauna.
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_UserSessions_PreviousRefreshTokenHash";

                ALTER TABLE "UserSessions"
                    DROP COLUMN IF EXISTS "PreviousRefreshTokenHash",
                    DROP COLUMN IF EXISTS "AbsoluteExpiresAt";

                DROP INDEX IF EXISTS "UX_DocumentVersions_DocumentId_VersionNumber";

                CREATE INDEX IF NOT EXISTS "IX_DocumentVersions_DocumentId"
                    ON "DocumentVersions" ("DocumentId");
                """);
        }
    }
}
