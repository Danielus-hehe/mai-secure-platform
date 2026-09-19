using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Mută rezultatul auditului din prefixul textual al lui <c>Details</c>
    /// ("ESEC:", "SUCCES:", "ATENTIE:") într-o coloană proprie și adaugă
    /// indexurile pe care se sprijină pagina /audit și dashboardul.
    ///
    /// Migrarea nu doar adaugă coloana: convertește și rândurile deja scrise.
    /// Fără pasul acesta, tot istoricul de dinaintea schimbării ar apărea drept
    /// „succes”, iar numărul de autentificări eșuate din ultimele 24h ar porni
    /// de la zero exact în momentul în care are cel mai puțin sens.
    /// </summary>
    public partial class AddAuditResultAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Result",
                table: "AuditLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // ── Conversia istoricului ────────────────────────────────────────
            // Prefixul se citește o singură dată, aici, și dispare din date.
            // 0 = Success, 1 = Failure, 2 = Warning (MAI.Domain.Enums.AuditResult).
            migrationBuilder.Sql(@"
                UPDATE ""AuditLogs""
                SET ""Result"" = CASE
                        WHEN ""Details"" LIKE 'ESEC:%'    THEN 1
                        WHEN ""Details"" LIKE 'ATENTIE:%' THEN 2
                        ELSE 0
                    END,
                    ""Details"" = CASE
                        WHEN ""Details"" LIKE 'ESEC:%'    THEN btrim(substring(""Details"" from 6))
                        WHEN ""Details"" LIKE 'ATENTIE:%' THEN btrim(substring(""Details"" from 9))
                        WHEN ""Details"" LIKE 'SUCCES:%'  THEN btrim(substring(""Details"" from 8))
                        ELSE ""Details""
                    END;
            ");

            // Coloanele erau 'text' nemărginit, deci pot conține valori mai lungi
            // decât noile limite - un nume de fișier lung în Details, sau un
            // username inventat, trimis la /login de un client oarecare și
            // consemnat ca atare. ALTER COLUMN ar eșua pe ele, așa că se scurtează
            // înainte; nimic din ce se pierde nu are valoare probatorie.
            migrationBuilder.Sql(@"
                UPDATE ""AuditLogs"" SET ""Details""   = left(""Details"", 1024) WHERE length(""Details"")   > 1024;
                UPDATE ""AuditLogs"" SET ""Username""  = left(""Username"", 128) WHERE length(""Username"")  > 128;
                UPDATE ""AuditLogs"" SET ""IpAddress"" = left(""IpAddress"", 64)  WHERE length(""IpAddress"") > 64;
            ");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "AuditLogs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "IpAddress",
                table: "AuditLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Details",
                table: "AuditLogs",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action_Result_Timestamp",
                table: "AuditLogs",
                columns: new[] { "Action", "Result", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Timestamp",
                table: "AuditLogs",
                column: "Timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Username_Timestamp",
                table: "AuditLogs",
                columns: new[] { "Username", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Action_Result_Timestamp",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Timestamp",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Username_Timestamp",
                table: "AuditLogs");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "AuditLogs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "IpAddress",
                table: "AuditLogs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "Details",
                table: "AuditLogs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024);

            // Prefixul se pune la loc înainte de a pierde coloana: altfel
            // revenirea la versiunea veche ar citi tot istoricul drept succes.
            migrationBuilder.Sql(@"
                UPDATE ""AuditLogs""
                SET ""Details"" = CASE ""Result""
                        WHEN 1 THEN 'ESEC: '    || ""Details""
                        WHEN 2 THEN 'ATENTIE: ' || ""Details""
                        ELSE        'SUCCES: '  || ""Details""
                    END;
            ");

            migrationBuilder.DropColumn(
                name: "Result",
                table: "AuditLogs");
        }
    }
}
