namespace MAI.BusinessLogic.Storage.Encryption
{
    /// <summary>
    /// Criptarea la nivel de aplicație a obiectelor din depozit (secțiunea
    /// "StorageEncryption"; din .env: STORAGE_ENCRYPTION_* și MAI_STORAGE_MASTER_KEYS).
    ///
    /// Se aplică documentelor normative (documents/) și interne (internal/),
    /// care NU sunt criptate end-to-end: serverul trebuie să le poată citi ca
    /// să rezolve distribuția și accesul. Fără criptarea de aici, oricine are
    /// credențialele MinIO, un backup al volumului sau acces la discul serverului
    /// de stocare citește actele în clar. Transferurile rămân neatinse: ele ajung
    /// deja criptate din browser, iar o a doua criptare ar costa fără să apere
    /// nimic în plus.
    ///
    /// Modelul este „envelope encryption”: fiecare fișier are propria cheie
    /// aleatorie (DEK), împachetată cu o cheie principală (KEK) păstrată în afara
    /// depozitului. Cheile principale au un identificator scris în antetul
    /// fiecărui fișier, deci se pot roti: cheia nouă devine activă pentru
    /// scrieri, cea veche rămâne în listă cât timp mai există fișiere împachetate
    /// cu ea, iar scriptul de recriptare le mută pe cheia activă.
    /// </summary>
    public sealed class StorageEncryptionOptions
    {
        /// <summary>
        /// True: obiectele noi de sub <see cref="EncryptedPrefixes"/> se scriu
        /// criptat. Citirea obiectelor deja criptate funcționează și cu false,
        /// dacă cheile sunt configurate - dezactivarea nu face ilizibil nimic.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Identificatorul cheii principale folosite la scrieri noi.</summary>
        public string ActiveKeyId { get; set; } = string.Empty;

        /// <summary>
        /// Cheile principale: „id:base64;id2:base64”. Fiecare cheie are exact 32
        /// de octeți (openssl rand -base64 32). Vine din MAI_STORAGE_MASTER_KEYS,
        /// niciodată din appsettings.json.
        /// </summary>
        public string MasterKeys { get; set; } = string.Empty;

        /// <summary>
        /// Prefixele criptate, separate prin virgulă. Șir, nu listă: binderul de
        /// configurare ADAUGĂ elementele din fișier la cele implicite ale unei
        /// liste, deci o listă implicită nu s-ar mai putea restrânge din config.
        /// </summary>
        public string EncryptedPrefixes { get; set; } = "documents/,internal/";

        /// <summary>
        /// True: obiectele scrise înainte de activarea criptării (în clar) se
        /// citesc în continuare. După rularea scriptului de recriptare se pune
        /// false: de atunci, un obiect în clar strecurat direct în MinIO în
        /// locul unuia criptat este refuzat, nu servit.
        /// </summary>
        public bool AllowPlaintextRead { get; set; } = true;

        /// <summary>
        /// True: înainte de a livra un fișier, serverul îl decriptează o dată
        /// integral doar ca să verifice etichetele GCM, apoi îl redeschide și îl
        /// streamează. Costă o a doua citire din depozit, dar garantează că nu
        /// pleacă spre browser niciun octet dintr-un fișier alterat - altfel o
        /// modificare în a doua jumătate a fișierului ar fi descoperită abia după
        /// ce prima jumătate a fost deja trimisă.
        /// </summary>
        public bool VerifyBeforeStreaming { get; set; } = true;

        /// <summary>Dimensiunea unui segment criptat, în KiB (4 - 4096).</summary>
        public int ChunkSizeKb { get; set; } = 64;

        public int ChunkSizeBytes => ChunkSizeKb * 1024;

        /// <summary>Prefixele normalizate: fără spații, cu „/” la final, fără duplicate.</summary>
        public IReadOnlyList<string> PrefixList =>
            EncryptedPrefixes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.TrimStart('/'))
                .Where(p => p.Length > 0)
                .Select(p => p.EndsWith('/') ? p : p + "/")
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>True dacă obiectul cu cheia dată intră sub criptarea aplicației.</summary>
        public bool AppliesTo(string objectKey) =>
            !string.IsNullOrEmpty(objectKey) &&
            PrefixList.Any(p => objectKey.StartsWith(p, StringComparison.Ordinal));

        /// <summary>True dacă sunt configurate chei principale (indiferent de Enabled).</summary>
        public bool HasKeys => !string.IsNullOrWhiteSpace(MasterKeys);

        /// <summary>
        /// Validare la pornire. Nu verifică cheile propriu-zise - asta o face
        /// <see cref="MasterKeyRing.Parse"/>, cu mesaje care nu includ valorile.
        /// </summary>
        public void Validate()
        {
            if (ChunkSizeKb < 4 || ChunkSizeKb > 4096)
                throw new InvalidOperationException(
                    "StorageEncryption:ChunkSizeKb trebuie să fie între 4 și 4096.");

            if (PrefixList.Count == 0)
                throw new InvalidOperationException(
                    "StorageEncryption:EncryptedPrefixes nu conține niciun prefix (ex. \"documents/,internal/\").");

            if (Enabled && !HasKeys)
                throw new InvalidOperationException(
                    "Criptarea depozitului este activă (STORAGE_ENCRYPTION_ENABLED=true), dar " +
                    "MAI_STORAGE_MASTER_KEYS lipsește. Generați o cheie cu " +
                    "'dotnet run --project MAI.Api -- storage:generate-key' și puneți în .env " +
                    "MAI_STORAGE_MASTER_KEYS=<id>:<cheie> și STORAGE_ENCRYPTION_ACTIVE_KEY=<id>. " +
                    "Dacă vreți explicit fără criptare: STORAGE_ENCRYPTION_ENABLED=false.");

            if (Enabled && string.IsNullOrWhiteSpace(ActiveKeyId))
                throw new InvalidOperationException(
                    "STORAGE_ENCRYPTION_ACTIVE_KEY lipsește: nu se știe cu ce cheie se criptează fișierele noi.");

            // Fără chei, singurul mod în care se poate citi ceva e în clar.
            if (!Enabled && !HasKeys && !AllowPlaintextRead)
                throw new InvalidOperationException(
                    "StorageEncryption: criptarea e dezactivată și nu există chei, dar citirea în clar " +
                    "e interzisă (STORAGE_ENCRYPTION_ALLOW_PLAINTEXT=false) - niciun document nu s-ar mai putea citi.");
        }
    }
}
