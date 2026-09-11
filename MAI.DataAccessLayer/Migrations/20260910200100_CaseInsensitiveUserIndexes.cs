using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Unicitate fără diferență între majuscule și minuscule pentru Username și Email.
    ///
    /// Înainte:
    ///   • UX_Users_Username era unic pe valoarea exactă: „Ion” și „ion” puteau fi
    ///     două conturi diferite, ușor de confundat în lista de destinatari.
    ///   • UX_Users_Email era unic pe valoarea exactă, inclusiv pentru "". Emailul
    ///     e opțional, deci al doilea cont creat fără email pica la salvare cu 500.
    ///
    /// Indexurile noi sunt pe expresii (lower(...)), pe care EF Core nu le poate
    /// descrie în model; de aceea sunt create prin SQL și nu apar în snapshot.
    /// UX_Users_Username rămâne: e redundant, dar inofensiv.
    ///
    /// ÎNAINTE DE APLICARE, verificați că nu există duplicate care diferă doar prin
    /// majuscule (interogările sunt în Migrations/README.md). Dacă există, crearea
    /// indexului eșuează și migrarea nu se aplică deloc.
    /// </summary>
    public partial class CaseInsensitiveUserIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Users_Email",
                table: "Users");

            migrationBuilder.Sql(
                @"CREATE UNIQUE INDEX ""UX_Users_Username_Lower"" ON ""Users"" (lower(""Username""));");

            migrationBuilder.Sql(
                @"CREATE UNIQUE INDEX ""UX_Users_Email_Lower"" ON ""Users"" (lower(""Email"")) WHERE ""Email"" <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""UX_Users_Email_Lower"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""UX_Users_Username_Lower"";");

            migrationBuilder.CreateIndex(
                name: "UX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }
    }
}
