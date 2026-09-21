using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Autentificare prin Active Directory, ca provider paralel cu conturile
    /// locale.
    ///
    /// Users primește providerul și identificatorii din domeniu, plus
    /// KeysWrappedAt - momentul ultimei împachetări a cheilor private E2EE,
    /// comparat cu pwdLastSet din AD ca să știm când parola s-a schimbat acolo
    /// și cheile trebuie reîmpachetate.
    ///
    /// OrgUnits primește DN-ul OU-ului corespunzător din AD, ca importul
    /// structurii să fie repetabil: la a doua rulare, un OU deja importat se
    /// recunoaște după DN și se actualizează, nu se dublează.
    ///
    /// AuthProvider pornește cu 0 (Local) pentru toate rândurile existente.
    /// Orice altă valoare implicită ar fi transformat conturile de acum în
    /// conturi de domeniu, deci nimeni nu s-ar mai fi putut autentifica.
    /// </summary>
    public partial class LdapDirectoryAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Users"
                    ADD COLUMN IF NOT EXISTS "AuthProvider"           integer NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS "DirectoryObjectId"      character varying(64),
                    ADD COLUMN IF NOT EXISTS "DirectoryDn"            character varying(512),
                    ADD COLUMN IF NOT EXISTS "DirectoryPasswordSetAt" timestamp with time zone,
                    ADD COLUMN IF NOT EXISTS "DirectorySyncedAt"      timestamp with time zone,
                    ADD COLUMN IF NOT EXISTS "KeysWrappedAt"          timestamp with time zone;

                -- Conturile care au deja chei au fost împachetate la generare.
                -- Fără reperul acesta, prima schimbare a parolei în AD nu s-ar
                -- putea deosebi de „cheile nu au existat niciodată”.
                UPDATE "Users"
                   SET "KeysWrappedAt" = "KeysCreatedAt"
                 WHERE "KeysWrappedAt" IS NULL
                   AND "KeysCreatedAt" IS NOT NULL;

                ALTER TABLE "OrgUnits"
                    ADD COLUMN IF NOT EXISTS "DirectoryDn" character varying(512);

                CREATE UNIQUE INDEX IF NOT EXISTS "UX_Users_DirectoryObjectId"
                    ON "Users" ("DirectoryObjectId")
                 WHERE "DirectoryObjectId" IS NOT NULL;

                CREATE UNIQUE INDEX IF NOT EXISTS "UX_OrgUnits_DirectoryDn"
                    ON "OrgUnits" ("DirectoryDn")
                 WHERE "DirectoryDn" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Atenție la revenire: conturile de domeniu rămân fără hash de
            // parolă, deci nu se mai pot autentifica deloc până când
            // administratorul nu le stabilește o parolă locală.
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "UX_OrgUnits_DirectoryDn";
                DROP INDEX IF EXISTS "UX_Users_DirectoryObjectId";

                ALTER TABLE "OrgUnits" DROP COLUMN IF EXISTS "DirectoryDn";

                ALTER TABLE "Users"
                    DROP COLUMN IF EXISTS "KeysWrappedAt",
                    DROP COLUMN IF EXISTS "DirectorySyncedAt",
                    DROP COLUMN IF EXISTS "DirectoryPasswordSetAt",
                    DROP COLUMN IF EXISTS "DirectoryDn",
                    DROP COLUMN IF EXISTS "DirectoryObjectId",
                    DROP COLUMN IF EXISTS "AuthProvider";
                """);
        }
    }
}
