using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Structura organizatorică, documentele interne și resetarea parolei prin email.
    ///
    /// 1. OrgUnits (arbore Direcție / Secție / Serviciu, cu șef).
    /// 2. Users.OrgUnitId în locul câmpului text liber Department. Valorile
    ///    existente devin subdiviziuni de nivel Direcție (una pentru fiecare
    ///    denumire distinctă, fără diferență de majuscule), iar conturile cu rolul
    ///    SefDirectie din fiecare devin șefii ei. Structura mai fină (secții,
    ///    servicii) se construiește apoi din pagina „Structura organizatorică”.
    /// 3. Users.PasswordResetToken / PasswordResetTokenExpiry.
    /// 4. InternalDocuments, InternalDocumentTargets, InternalDocumentRecipients.
    /// </summary>
    public partial class OrgStructureInternalDocsPasswordReset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. OrgUnits ──────────────────────────────────────────────────
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "OrgUnits" (
                    "Id"         uuid                     NOT NULL,
                    "Name"       character varying(200)   NOT NULL,
                    "Code"       character varying(20)    NULL,
                    "Type"       integer                  NOT NULL,
                    "ParentId"   uuid                     NULL,
                    "HeadUserId" uuid                     NULL,
                    "IsActive"   boolean                  NOT NULL DEFAULT TRUE,
                    "CreatedAt"  timestamp with time zone NOT NULL DEFAULT NOW(),

                    CONSTRAINT "PK_OrgUnits" PRIMARY KEY ("Id"),

                    CONSTRAINT "FK_OrgUnits_OrgUnits_ParentId"
                        FOREIGN KEY ("ParentId") REFERENCES "OrgUnits" ("Id") ON DELETE RESTRICT,

                    CONSTRAINT "FK_OrgUnits_Users_HeadUserId"
                        FOREIGN KEY ("HeadUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL
                );

                CREATE INDEX IF NOT EXISTS "IX_OrgUnits_ParentId" ON "OrgUnits" ("ParentId");

                CREATE UNIQUE INDEX IF NOT EXISTS "UX_OrgUnits_HeadUserId"
                    ON "OrgUnits" ("HeadUserId") WHERE "HeadUserId" IS NOT NULL;
                """);

            // ── 2. Users: încadrare + resetare parolă ────────────────────────
            migrationBuilder.Sql(
                """
                ALTER TABLE "Users"
                    ADD COLUMN IF NOT EXISTS "OrgUnitId"                uuid NULL,
                    ADD COLUMN IF NOT EXISTS "PasswordResetToken"       text NULL,
                    ADD COLUMN IF NOT EXISTS "PasswordResetTokenExpiry" timestamp with time zone NULL;

                ALTER TABLE "Users"
                    ADD CONSTRAINT "FK_Users_OrgUnits_OrgUnitId"
                    FOREIGN KEY ("OrgUnitId") REFERENCES "OrgUnits" ("Id") ON DELETE RESTRICT;

                CREATE INDEX IF NOT EXISTS "IX_Users_OrgUnitId" ON "Users" ("OrgUnitId");

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_PasswordResetToken"
                    ON "Users" ("PasswordResetToken") WHERE "PasswordResetToken" IS NOT NULL;
                """);

            // ── 3. Department (text) → OrgUnits ──────────────────────────────
            // O subdiviziune pentru fiecare denumire distinctă. Grafia păstrată e
            // cea mai frecventă; „directia IT” și „Directia IT” devin una singură.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name   = 'Users'
                          AND column_name  = 'Department'
                    ) THEN
                        INSERT INTO "OrgUnits" ("Id", "Name", "Type", "IsActive", "CreatedAt")
                        SELECT gen_random_uuid(), x.name, 1, TRUE, NOW()
                          FROM (
                              SELECT DISTINCT ON (lower(btrim("Department")))
                                     left(btrim("Department"), 200) AS name
                                FROM "Users"
                               WHERE btrim(coalesce("Department", '')) <> ''
                               GROUP BY lower(btrim("Department")), btrim("Department")
                               ORDER BY lower(btrim("Department")), count(*) DESC, btrim("Department")
                          ) x
                         WHERE NOT EXISTS (
                             SELECT 1 FROM "OrgUnits" o WHERE lower(o."Name") = lower(x.name));

                        UPDATE "Users" u
                           SET "OrgUnitId" = o."Id"
                          FROM "OrgUnits" o
                         WHERE u."OrgUnitId" IS NULL
                           AND lower(left(btrim(u."Department"), 200)) = lower(o."Name");

                        -- Șeful: cel mai vechi cont activ cu rolul SefDirectie (2)
                        -- din subdiviziune. Un cont conduce cel mult o subdiviziune,
                        -- iar un cont aparține uneia singure, deci unicitatea ține.
                        UPDATE "OrgUnits" o
                           SET "HeadUserId" = h."Id"
                          FROM (
                              SELECT DISTINCT ON ("OrgUnitId") "OrgUnitId", "Id"
                                FROM "Users"
                               WHERE "Role" = 2 AND "IsActive" AND "OrgUnitId" IS NOT NULL
                               ORDER BY "OrgUnitId", "CreatedAt"
                          ) h
                         WHERE h."OrgUnitId" = o."Id"
                           AND o."HeadUserId" IS NULL;

                        ALTER TABLE "Users" DROP COLUMN "Department";
                    END IF;
                END
                $$;
                """);

            // ── 4. Documente interne ─────────────────────────────────────────
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "InternalDocuments" (
                    "Id"                      uuid                     NOT NULL,
                    "Title"                   character varying(300)   NOT NULL,
                    "Number"                  character varying(64)    NULL,
                    "Summary"                 character varying(2000)  NULL,
                    "AuthorId"                uuid                     NOT NULL,
                    "AuthorOrgUnitId"         uuid                     NULL,
                    "Status"                  integer                  NOT NULL,
                    "DistributionMode"        integer                  NOT NULL,
                    "IncludeSubunits"         boolean                  NOT NULL,
                    "RequiresAcknowledgement" boolean                  NOT NULL,
                    "FileName"                character varying(260)   NOT NULL,
                    "ContentType"             character varying(128)   NOT NULL,
                    "FileSize"                bigint                   NOT NULL,
                    "Sha256"                  character varying(64)    NOT NULL,
                    "StorageKey"              character varying(512)   NOT NULL,
                    "CreatedAt"               timestamp with time zone NOT NULL,
                    "UpdatedAt"               timestamp with time zone NULL,
                    "PublishedAt"             timestamp with time zone NULL,
                    "RepealedAt"              timestamp with time zone NULL,
                    "RepealedById"            uuid                     NULL,
                    "RepealedReason"          character varying(500)   NULL,

                    CONSTRAINT "PK_InternalDocuments" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_InternalDocuments_Users_AuthorId"
                        FOREIGN KEY ("AuthorId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_InternalDocuments_OrgUnits_AuthorOrgUnitId"
                        FOREIGN KEY ("AuthorOrgUnitId") REFERENCES "OrgUnits" ("Id") ON DELETE SET NULL
                );

                CREATE INDEX IF NOT EXISTS "IX_InternalDocuments_Author_CreatedAt"
                    ON "InternalDocuments" ("AuthorId", "CreatedAt");
                CREATE INDEX IF NOT EXISTS "IX_InternalDocuments_AuthorOrgUnitId"
                    ON "InternalDocuments" ("AuthorOrgUnitId");
                CREATE INDEX IF NOT EXISTS "IX_InternalDocuments_Status"
                    ON "InternalDocuments" ("Status");

                CREATE TABLE IF NOT EXISTS "InternalDocumentTargets" (
                    "DocumentId" uuid    NOT NULL,
                    "Kind"       integer NOT NULL,
                    "TargetId"   uuid    NOT NULL,

                    CONSTRAINT "PK_InternalDocumentTargets" PRIMARY KEY ("DocumentId", "Kind", "TargetId"),
                    CONSTRAINT "FK_InternalDocumentTargets_InternalDocuments_DocumentId"
                        FOREIGN KEY ("DocumentId") REFERENCES "InternalDocuments" ("Id") ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS "InternalDocumentRecipients" (
                    "DocumentId"     uuid                     NOT NULL,
                    "UserId"         uuid                     NOT NULL,
                    "OrgUnitId"      uuid                     NULL,
                    "AddedAt"        timestamp with time zone NOT NULL,
                    "FirstOpenedAt"  timestamp with time zone NULL,
                    "AcknowledgedAt" timestamp with time zone NULL,

                    CONSTRAINT "PK_InternalDocumentRecipients" PRIMARY KEY ("DocumentId", "UserId"),
                    CONSTRAINT "FK_InternalDocumentRecipients_InternalDocuments_DocumentId"
                        FOREIGN KEY ("DocumentId") REFERENCES "InternalDocuments" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_InternalDocumentRecipients_Users_UserId"
                        FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_InternalDocumentRecipients_OrgUnits_OrgUnitId"
                        FOREIGN KEY ("OrgUnitId") REFERENCES "OrgUnits" ("Id") ON DELETE SET NULL
                );

                CREATE INDEX IF NOT EXISTS "IX_InternalDocumentRecipients_User_Ack"
                    ON "InternalDocumentRecipients" ("UserId", "AcknowledgedAt");
                CREATE INDEX IF NOT EXISTS "IX_InternalDocumentRecipients_OrgUnitId"
                    ON "InternalDocumentRecipients" ("OrgUnitId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Department se reface din denumirea subdiviziunii. Ierarhia și șefii
            // se pierd - modelul vechi nu avea unde să-i țină.
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "InternalDocumentRecipients";
                DROP TABLE IF EXISTS "InternalDocumentTargets";
                DROP TABLE IF EXISTS "InternalDocuments";

                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "Department" text NULL;

                UPDATE "Users" u
                   SET "Department" = o."Name"
                  FROM "OrgUnits" o
                 WHERE u."OrgUnitId" = o."Id";

                DROP INDEX IF EXISTS "IX_Users_PasswordResetToken";
                DROP INDEX IF EXISTS "IX_Users_OrgUnitId";
                ALTER TABLE "Users" DROP CONSTRAINT IF EXISTS "FK_Users_OrgUnits_OrgUnitId";

                ALTER TABLE "Users"
                    DROP COLUMN IF EXISTS "OrgUnitId",
                    DROP COLUMN IF EXISTS "PasswordResetToken",
                    DROP COLUMN IF EXISTS "PasswordResetTokenExpiry";

                DROP TABLE IF EXISTS "OrgUnits";
                """);
        }
    }
}
