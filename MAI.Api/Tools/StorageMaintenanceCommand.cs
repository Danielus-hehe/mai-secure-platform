using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Storage.Encryption;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Tools
{
    /// <summary>
    /// Comenzi de întreținere a depozitului, rulate cu același executabil ca
    /// API-ul (aceeași configurare din .env, aceleași chei), dar fără să pornească
    /// serverul web:
    ///
    /// <code>
    /// dotnet run --project MAI.Api -- storage:generate-key [id]
    /// dotnet run --project MAI.Api -- storage:recrypt [--dry-run]
    /// docker compose run --rm api storage:recrypt [--dry-run]
    /// </code>
    ///
    /// storage:recrypt parcurge obiectele referite din baza de date (versiunile
    /// documentelor normative și documentele interne) și le aduce pe cheia
    /// principală activă. Obiectele nereferite (orfane) nu sunt atinse.
    /// </summary>
    public static class StorageMaintenanceCommand
    {
        public const string GenerateKeyVerb = "storage:generate-key";
        public const string RecryptVerb     = "storage:recrypt";
        public const string DryRunFlag      = "--dry-run";

        public static bool IsGenerateKey(string[] args) =>
            args.Length > 0 && string.Equals(args[0], GenerateKeyVerb, StringComparison.OrdinalIgnoreCase);

        public static bool IsRecrypt(string[] args) =>
            args.Length > 0 && string.Equals(args[0], RecryptVerb, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Scrie pe consolă o cheie nouă, gata de pus în .env. Nu are nevoie de
        /// configurare, deci rulează înainte de construirea host-ului.
        /// </summary>
        public static int GenerateKey(string[] args)
        {
            var id = args.Length > 1 ? args[1] : $"k{DateTime.UtcNow:yyyyMMdd}";

            if (!MasterKeyRing.IsValidKeyId(id))
            {
                Console.Error.WriteLine("Identificator invalid: 1-32 caractere din A-Z, a-z, 0-9, '.', '_', '-'.");
                return 2;
            }

            var entry = MasterKeyRing.GenerateEntry(id);

            Console.WriteLine();
            Console.WriteLine("Cheie principala noua pentru criptarea depozitului.");
            Console.WriteLine("Prima cheie - in .env:");
            Console.WriteLine($"  MAI_STORAGE_MASTER_KEYS={entry}");
            Console.WriteLine($"  STORAGE_ENCRYPTION_ACTIVE_KEY={id}");
            Console.WriteLine();
            Console.WriteLine("Rotire - cheia noua se ADAUGA la cele existente, separata prin ';',");
            Console.WriteLine("iar STORAGE_ENCRYPTION_ACTIVE_KEY trece pe ea. Apoi: storage:recrypt.");
            Console.WriteLine("Cheia veche se scoate doar dupa ce recriptarea raporteaza 0 obiecte pe ea.");
            Console.WriteLine();
            Console.WriteLine("Pastrati o copie a cheii in afara serverului: fara ea, documentele criptate");
            Console.WriteLine("cu ea nu mai pot fi citite de nimeni.");
            return 0;
        }

        /// <summary>Rulează recriptarea. Codul de ieșire: 0 = fără probleme, 1 = probleme, 2 = configurare.</summary>
        public static async Task<int> RunRecryptAsync(IServiceProvider services, string[] args, CancellationToken ct = default)
        {
            var dryRun = args.Skip(1).Any(a => string.Equals(a, DryRunFlag, StringComparison.OrdinalIgnoreCase));

            if (services.GetRequiredService<IFileStorage>() is not EncryptingFileStorage storage)
            {
                Console.Error.WriteLine("Depozitul nu este configurat cu criptare (EncryptingFileStorage lipseste).");
                return 2;
            }

            if (storage.KeyRing is null)
            {
                Console.Error.WriteLine("MAI_STORAGE_MASTER_KEYS nu este setat: nu exista cu ce cripta.");
                return 2;
            }

            if (!storage.Options.Enabled)
            {
                Console.Error.WriteLine("STORAGE_ENCRYPTION_ENABLED=false: activati criptarea inainte de recriptare.");
                return 2;
            }

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("StorageRecrypt");

            var items = await CollectItemsAsync(db, storage.Options, ct);

            Console.WriteLine();
            Console.WriteLine($"Recriptare depozit {(dryRun ? "(SIMULARE - nu se scrie nimic)" : string.Empty)}");
            Console.WriteLine($"  depozit:       {storage.Inner.ProviderName}");
            Console.WriteLine($"  cheia activa:  {storage.KeyRing.ActiveKeyId} (configurate: {string.Join(", ", storage.KeyRing.KeyIds)})");
            Console.WriteLine($"  prefixe:       {string.Join(", ", storage.Options.PrefixList)}");
            Console.WriteLine($"  obiecte:       {items.Objects.Count}");
            if (items.LegacyLocalPaths > 0)
                Console.WriteLine($"  ignorate:      {items.LegacyLocalPaths} versiuni vechi cu cale pe discul API-ului (nu sunt in depozit)");
            if (items.OutsidePrefixes > 0)
                Console.WriteLine($"  ignorate:      {items.OutsidePrefixes} obiecte in afara prefixelor criptate");
            Console.WriteLine();

            var recryptor = new StorageRecryptor(storage.Inner, storage.KeyRing, storage.Options, logger);
            var results = new List<RecryptResult>(items.Objects.Count);

            foreach (var item in items.Objects)
            {
                var result = await recryptor.ProcessAsync(item, dryRun, ct);
                results.Add(result);

                // Obiectele deja în regulă nu se listează: la sute de documente,
                // problemele s-ar pierde printre ele.
                if (result.Outcome != RecryptOutcome.AlreadyCurrent)
                    Console.WriteLine(
                        $"  [{Label(result.Outcome),-18}] {item.Label} ({item.Key})" +
                        (result.Detail is null ? string.Empty : $" - {result.Detail}"));
            }

            var summary = results
                .GroupBy(r => r.Outcome)
                .OrderBy(g => g.Key)
                .Select(g => $"{Label(g.Key)}: {g.Count()}")
                .ToList();

            var problems = results.Count(r => r.IsProblem);

            Console.WriteLine();
            Console.WriteLine("Rezultat: " + (summary.Count == 0 ? "niciun obiect" : string.Join(", ", summary)));
            Console.WriteLine(problems == 0
                ? "Fara probleme."
                : $"{problems} obiecte cu probleme - verificati lista de mai sus. Obiectele cu probleme au ramas neatinse.");

            if (!dryRun)
            {
                db.AuditLogs.Add(new AuditLog
                {
                    UserId    = null,
                    Username  = "sistem",
                    Action    = AuditAction.StorageRecrypted,
                    Details   = $"Recriptare depozit pe cheia '{storage.KeyRing.ActiveKeyId}': {string.Join(", ", summary)}",
                    Result    = problems == 0 ? AuditResult.Success : AuditResult.Warning,
                    IpAddress = "local",
                    Timestamp = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            return problems == 0 ? 0 : 1;
        }

        private sealed record Collected(List<RecryptItem> Objects, int LegacyLocalPaths, int OutsidePrefixes);

        private static async Task<Collected> CollectItemsAsync(
            AppDbContext db, StorageEncryptionOptions options, CancellationToken ct)
        {
            var objects = new List<RecryptItem>();
            var legacy = 0;
            var outside = 0;

            var versions = await db.DocumentVersions
                .AsNoTracking()
                .OrderBy(v => v.CreatedAt)
                .Select(v => new
                {
                    v.EncryptedStoragePath,
                    v.ChecksumSHA256,
                    v.VersionNumber,
                    Title = v.Document != null ? v.Document.Title : "?",
                })
                .ToListAsync(ct);

            foreach (var v in versions)
            {
                if (string.IsNullOrWhiteSpace(v.EncryptedStoragePath)) continue;

                if (Path.IsPathRooted(v.EncryptedStoragePath)) { legacy++; continue; }
                if (!options.AppliesTo(v.EncryptedStoragePath)) { outside++; continue; }

                objects.Add(new RecryptItem(v.EncryptedStoragePath, v.ChecksumSHA256, $"document '{v.Title}' v{v.VersionNumber}"));
            }

            var internalDocs = await db.InternalDocuments
                .AsNoTracking()
                .OrderBy(d => d.CreatedAt)
                .Select(d => new { d.StorageKey, d.Sha256, d.Title })
                .ToListAsync(ct);

            foreach (var d in internalDocs)
            {
                if (string.IsNullOrWhiteSpace(d.StorageKey)) continue;
                if (!options.AppliesTo(d.StorageKey)) { outside++; continue; }

                objects.Add(new RecryptItem(d.StorageKey, d.Sha256, $"document intern '{d.Title}'"));
            }

            // Aceeași cheie referită de două rânduri se procesează o singură dată.
            var distinct = objects
                .GroupBy(o => o.Key, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            return new Collected(distinct, legacy, outside);
        }

        private static string Label(RecryptOutcome outcome) => outcome switch
        {
            RecryptOutcome.Encrypted        => "CRIPTAT",
            RecryptOutcome.Rewrapped        => "RE-IMPACHETAT",
            RecryptOutcome.AlreadyCurrent   => "DEJA PE CHEIA ACTIVA",
            RecryptOutcome.WouldEncrypt     => "DE CRIPTAT",
            RecryptOutcome.WouldRewrap      => "DE RE-IMPACHETAT",
            RecryptOutcome.Missing          => "LIPSA",
            RecryptOutcome.ChecksumMismatch => "AMPRENTA DIFERITA",
            RecryptOutcome.Failed           => "EROARE",
            _                               => outcome.ToString(),
        };
    }
}
