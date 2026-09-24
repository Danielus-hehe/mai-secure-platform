using System.Security.Cryptography;
using System.Text;
using MAI.BusinessLogic.Interfaces;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Tools
{
    /// <summary>
    /// Creează fișierele pentru documentele demonstrative din 900_seed_demo.sql:
    ///
    /// <code>
    /// dotnet run --project MAI.Api -- demo:seed-files
    /// docker compose run --rm api demo:seed-files
    /// </code>
    ///
    /// Înlocuiește scripts/seed-demo-objects.sh, care avea trei defecte ce se
    /// adunau: comanda documentată nu îl executa deloc (entrypoint-ul serviciului
    /// minio-init înghițea argumentele), scria fișierele în clar (refuzate cu
    /// STORAGE_ENCRYPTION_ALLOW_PLAINTEXT=false), iar SHA-256 din seed-ul SQL era
    /// calculat din alt text decât conținutul fișierelor. Rezultatul: storage:recrypt
    /// raporta toate obiectele LIPSA sau cu amprentă diferită, iar browserul marca
    /// documentele demo drept alterate la descărcare.
    ///
    /// Aici fișierele trec prin același IFileStorage ca un upload real, deci
    /// ajung în MinIO deja criptate (documents/, internal/), iar amprenta din
    /// baza de date se recalculează din conținutul efectiv. Nu mai e nevoie nici
    /// de citire în clar, nici de recriptare.
    ///
    /// Se ating DOAR rândurile demo, recunoscute după același marcaj pe care îl
    /// folosește seed-ul SQL la curățenie: autorul are username-ul „*.demo”.
    /// Documentele reale nu sunt citite, scrise sau modificate. Rulată de mai
    /// multe ori, comanda rescrie aceleași obiecte și aceleași amprente.
    /// </summary>
    public static class DemoSeedFilesCommand
    {
        public const string Verb = "demo:seed-files";

        /// <summary>Sufixul conturilor create de 900_seed_demo.sql.</summary>
        private const string DemoUserSuffix = ".demo";

        public static bool IsSeedFiles(string[] args) =>
            args.Length > 0 && string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase);

        /// <summary>Codul de ieșire: 0 = fără probleme, 1 = unele fișiere au eșuat, 2 = nimic de făcut.</summary>
        public static async Task<int> RunAsync(IServiceProvider services, CancellationToken ct = default)
        {
            using var scope = services.CreateScope();
            var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

            var versions = await db.DocumentVersions
                .Include(v => v.Document)
                .Where(v => v.Document != null
                         && v.Document.CreatedBy != null
                         && v.Document.CreatedBy.Username.EndsWith(DemoUserSuffix))
                .OrderBy(v => v.Document!.CreatedAt).ThenBy(v => v.VersionNumber)
                .ToListAsync(ct);

            var internalDocs = await db.InternalDocuments
                .Where(d => d.Author != null && d.Author.Username.EndsWith(DemoUserSuffix))
                .OrderBy(d => d.CreatedAt)
                .ToListAsync(ct);

            Console.WriteLine();
            Console.WriteLine("Fisiere pentru datele demonstrative");
            Console.WriteLine($"  depozit:            {storage.ProviderName}");
            Console.WriteLine($"  versiuni documente: {versions.Count}");
            Console.WriteLine($"  documente interne:  {internalDocs.Count}");
            Console.WriteLine();

            if (versions.Count == 0 && internalDocs.Count == 0)
            {
                Console.WriteLine("Nu exista date demo in baza. Rulati intai MAI.DataAccessLayer/Migrations/900_seed_demo.sql.");
                return 2;
            }

            var failures = 0;

            foreach (var version in versions)
            {
                // Cheia din seed-ul vechi se termina în „.enc”, iar numele propus
                // la descărcare preia extensia din cheie: documentele demo ajungeau
                // pe disc ca „Regulament intern de ordine.enc”. Conținutul e text,
                // deci cheia devine „.txt”.
                var key = version.EncryptedStoragePath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                    ? version.EncryptedStoragePath[..^4] + ".txt"
                    : version.EncryptedStoragePath;

                var text = BuildText(
                    version.Document!.Title,
                    $"Nr. {version.Document.DocumentNumber}, versiunea {version.VersionNumber}",
                    version.CreatedAt,
                    string.IsNullOrWhiteSpace(version.ChangeNotes) ? "Versiune initiala" : version.ChangeNotes);

                var sha = await TryWriteAsync(storage, key, text, ct);
                if (sha is null) { failures++; continue; }

                version.EncryptedStoragePath = key;
                version.ChecksumSHA256       = sha;
                Console.WriteLine($"  + {key}");
            }

            foreach (var doc in internalDocs)
            {
                var text = BuildText(
                    doc.Title,
                    string.IsNullOrWhiteSpace(doc.Number) ? "Document intern" : $"Nr. {doc.Number}",
                    doc.CreatedAt,
                    doc.Summary ?? string.Empty);

                var sha = await TryWriteAsync(storage, doc.StorageKey, text, ct);
                if (sha is null) { failures++; continue; }

                doc.Sha256      = sha;
                doc.FileSize    = Encoding.UTF8.GetByteCount(text);
                doc.ContentType = "text/plain";
                Console.WriteLine($"  + {doc.StorageKey}");
            }

            db.AuditLogs.Add(new AuditLog
            {
                UserId    = null,
                Username  = "sistem",
                Action    = AuditAction.StorageRecrypted,
                Details   = $"Fisiere demonstrative scrise prin depozitul criptat: {versions.Count} versiuni de " +
                            $"documente, {internalDocs.Count} documente interne, {failures} esecuri",
                Result    = failures == 0 ? AuditResult.Success : AuditResult.Warning,
                IpAddress = "local",
                Timestamp = DateTime.UtcNow,
            });

            await db.SaveChangesAsync(ct);

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "Gata. Fisierele sunt criptate in depozit, iar amprentele din baza corespund continutului."
                : $"{failures} fisiere nu au putut fi scrise - vedeti erorile de mai sus. Randurile lor au ramas neschimbate.");

            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Scrie obiectul și întoarce SHA-256 al conținutului în clar (forma din
        /// registru, verificată de browser la descărcare), sau null la eșec.
        /// </summary>
        private static async Task<string?> TryWriteAsync(IFileStorage storage, string key, string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            try
            {
                using var content = new MemoryStream(bytes, writable: false);
                await storage.PutAsync(key, content, bytes.Length, "text/plain", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  ! {key}: {ex.Message}");
                return null;
            }

            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        /// <summary>
        /// Conținutul unui document demo. Determinist: data vine din rândul din
        /// baza de date, nu din ceasul mașinii, deci două rulări dau aceiași
        /// octeți și aceeași amprentă.
        /// </summary>
        private static string BuildText(string title, string reference, DateTime createdAt, string body)
        {
            var rule = new string('=', 80);
            var thin = new string('-', 80);

            return string.Join("\n",
                rule,
                "  MINISTERUL AFACERILOR INTERNE AL REPUBLICII MOLDOVA",
                "  Sistem de Gestiune a Documentelor si Transferurilor Securizate (SGDM)",
                rule,
                string.Empty,
                $"  {title}",
                $"  {reference}",
                $"  Data: {createdAt:yyyy-MM-dd}",
                "  Clasificare: INTERN",
                string.Empty,
                thin,
                string.Empty,
                $"  {body}",
                string.Empty,
                thin,
                "  Document generat automat pentru demonstrarea platformei SGDM.",
                "  Nu reprezinta un document oficial al MAI.",
                rule,
                string.Empty);
        }
    }
}
