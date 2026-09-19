using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Niveluri configurabile ale structurii organizatorice.
    ///
    /// Direcție / Secție / Serviciu devin rânduri în OrgLevels (redenumibile),
    /// iar administratorul poate adăuga niveluri proprii. Rangurile trec de la
    /// 1/2/3 la 100/200/300, ca un nivel nou să poată fi inserat între două
    /// existente (ex. „Departament” = 150) fără renumerotarea subdiviziunilor.
    /// </summary>
    public partial class OrgLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "OrgLevels" (
                    "Rank"      integer                  NOT NULL,
                    "Name"      character varying(60)    NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                    CONSTRAINT "PK_OrgLevels" PRIMARY KEY ("Rank")
                );

                INSERT INTO "OrgLevels" ("Rank", "Name", "CreatedAt") VALUES
                    (100, 'Direcție', NOW()),
                    (200, 'Secție',   NOW()),
                    (300, 'Serviciu', NOW())
                ON CONFLICT ("Rank") DO NOTHING;

                UPDATE "OrgUnits" SET "Type" = "Type" * 100 WHERE "Type" IN (1, 2, 3);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nivelurile proprii nu au corespondent în modelul vechi: fiecare
            // subdiviziune ajunge pe cel mai apropiat nivel predefinit.
            migrationBuilder.Sql(
                """
                UPDATE "OrgUnits"
                   SET "Type" = CASE
                                    WHEN "Type" < 150 THEN 1
                                    WHEN "Type" < 250 THEN 2
                                    ELSE 3
                                END
                 WHERE "Type" >= 4;

                DROP TABLE IF EXISTS "OrgLevels";
                """);
        }
    }
}
