using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAI.DataAccessLayer.Migrations
{
    /// <summary>
    /// Coloana Documents.Keywords, care lipsea din lanțul de migrări.
    ///
    /// Proprietatea există în model (și în snapshot) începând cu AddRefreshToken,
    /// dar nicio migrare nu a creat coloana: pe baza de dezvoltare ea venea din
    /// scripturile SQL vechi (000_baseline...), mutate apoi din Supabase în
    /// Docker. Pe o bază NOUĂ, creată doar din migrări (instalare curată, testele
    /// de integrare), publicarea oricărui document normativ se termina cu 500:
    /// INSERT-ul trimitea o coloană care nu exista. A ieșit la iveală la primul
    /// test de integrare care publică un document (ConcurrencyIntegrationTests).
    ///
    /// Migrare separată, nu adăugată în ConcurrencyAndSessionLifetime: aceea poate
    /// fi deja aplicată, iar EF nu rulează din nou o migrare modificată.
    ///
    /// Idempotentă: pe o bază unde coloana există deja (cazul bazei curente),
    /// ADD COLUMN IF NOT EXISTS nu face nimic, iar datele rămân neatinse.
    /// Valoarea implicită '' există doar cât se completează rândurile existente;
    /// modelul nu are implicit, EF trimite mereu valoarea.
    /// </summary>
    public partial class AddMissingDocumentKeywords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Documents"
                    ADD COLUMN IF NOT EXISTS "Keywords" text NOT NULL DEFAULT '';

                ALTER TABLE "Documents"
                    ALTER COLUMN "Keywords" DROP DEFAULT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intenționat gol. Pe bazele unde coloana exista dinainte (venită din
            // scripturile vechi), o ștergere la revenire ar pierde cuvintele-cheie
            // ale documentelor, deși această migrare nu le-a creat.
        }
    }
}
