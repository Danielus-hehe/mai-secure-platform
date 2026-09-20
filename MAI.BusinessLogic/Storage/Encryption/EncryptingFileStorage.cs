using System.Security.Cryptography;
using MAI.BusinessLogic.Interfaces;
using Microsoft.Extensions.Logging;

namespace MAI.BusinessLogic.Storage.Encryption
{
    /// <summary>
    /// Decorator peste depozitul real (MinIO/S3 sau disc local) care criptează
    /// la scriere și decriptează la citire obiectele de sub prefixele configurate
    /// (implicit documents/ și internal/). Restul obiectelor - transferurile,
    /// deja criptate end-to-end din browser - trec neatinse.
    ///
    /// Controllerele nu se schimbă: continuă să lucreze cu IFileStorage și cu
    /// octeți în clar, iar amprenta SHA-256 din baza de date rămâne cea a
    /// documentului original, deci verificarea din browser funcționează la fel.
    ///
    /// Ce apără: un dump al volumului MinIO, un backup pierdut, credențialele
    /// MinIO scurse, un administrator de stocare curios - toți văd doar
    /// cifrotext. Ce NU apără: un atacator care controlează procesul API, care
    /// are și cheile principale în memorie (pentru asta există E2EE, folosit la
    /// transferuri).
    /// </summary>
    public sealed class EncryptingFileStorage : IFileStorage
    {
        private readonly IFileStorage _inner;
        private readonly StorageEncryptionOptions _options;
        private readonly MasterKeyRing? _ring;
        private readonly ILogger<EncryptingFileStorage> _logger;

        public EncryptingFileStorage(
            IFileStorage inner,
            StorageEncryptionOptions options,
            MasterKeyRing? ring,
            ILogger<EncryptingFileStorage> logger)
        {
            _inner   = inner;
            _options = options;
            _ring    = ring;
            _logger  = logger;

            if (_options.Enabled && _ring is null)
                throw new InvalidOperationException(
                    "Criptarea depozitului este activă, dar nu s-a putut construi setul de chei principale.");
        }

        /// <summary>Depozitul real, fără criptare. Folosit doar de scriptul de recriptare.</summary>
        public IFileStorage Inner => _inner;

        public StorageEncryptionOptions Options => _options;

        public MasterKeyRing? KeyRing => _ring;

        /// <summary>True dacă obiectele noi de sub prefixele configurate se scriu criptat.</summary>
        public bool WritesEncrypted => _options.Enabled && _ring is not null;

        public string ProviderName => WritesEncrypted
            ? $"{_inner.ProviderName} + AES-256-GCM"
            : _inner.ProviderName;

        public bool SupportsPresignedUrls => _inner.SupportsPresignedUrls;

        // ─────────────────────────────────────────────────────────────────────

        public async Task PutAsync(
            string key,
            Stream content,
            long contentLength,
            string contentType,
            CancellationToken ct = default)
        {
            if (!WritesEncrypted || !_options.AppliesTo(key))
            {
                await _inner.PutAsync(key, content, contentLength, contentType, ct);
                return;
            }

            // Cifrotextul se scrie întâi într-un fișier temporar: clientul S3 are
            // nevoie de un stream repozitionabil cu lungime cunoscută (semnătura
            // SigV4 a corpului). Fișierul conține doar cifrotext și dispare la
            // închidere (DeleteOnClose), inclusiv dacă procesul cade.
            await using var temp = CreateTempFile();

            var plainLength = await StorageEnvelope.EncryptAsync(
                content, temp, _ring!, _options.ChunkSizeBytes, plaintextHash: null, ct);

            if (plainLength != contentLength)
                _logger.LogWarning(
                    "Obiectul {Key}: lungimea anuntata {Declared} difera de cea citita {Actual}.",
                    key, contentLength, plainLength);

            var expected = StorageEnvelope.GetEncryptedLength(plainLength, _ring!.ActiveKeyId.Length, _options.ChunkSizeBytes);
            if (temp.Length != expected)
                throw new InvalidOperationException(
                    $"Lungimea cifrotextului ({temp.Length}) nu corespunde formatului ({expected}).");

            temp.Position = 0;

            // Tipul real al documentului nu se mai expune în depozit: pentru cine
            // se uită în MinIO, orice obiect criptat e application/octet-stream.
            await _inner.PutAsync(key, temp, temp.Length, "application/octet-stream", ct);

            _logger.LogInformation(
                "Obiect criptat in depozit: {Key} ({Plain} octeti in clar, cheia principala {KeyId})",
                key, plainLength, _ring.ActiveKeyId);
        }

        /// <summary>
        /// Deschide obiectul, decriptat. Pentru obiectele criptate, cu
        /// <see cref="StorageEncryptionOptions.VerifyBeforeStreaming"/>, tot
        /// fișierul se verifică înainte ca metoda să întoarcă - o alterare
        /// oriunde în fișier ajunge la apelant ca <see cref="CryptographicException"/>,
        /// înainte de primul octet trimis browserului.
        /// </summary>
        public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
        {
            if (!_options.AppliesTo(key))
                return await _inner.OpenReadAsync(key, ct);

            var allowPlaintext = _options.AllowPlaintextRead || !_options.Enabled;

            var opened = await StorageEnvelope.OpenAsync(
                await _inner.OpenReadAsync(key, ct), _ring, allowPlaintext, ct);

            if (!opened.WasEncrypted)
            {
                _logger.LogWarning(
                    "Obiect necriptat sub prefix criptat: {Key}. Rulati storage:recrypt pentru a-l cripta.", key);
                return opened.Stream;
            }

            if (!_options.VerifyBeforeStreaming)
                return opened.Stream;

            // Prima trecere: doar verificare, rezultatul se aruncă.
            try
            {
                await using (opened.Stream)
                    await opened.Stream.CopyToAsync(Stream.Null, ct);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "Obiectul {Key} nu a trecut verificarea de integritate.", key);
                throw;
            }

            // A doua trecere: fluxul livrat. Dacă obiectul s-a schimbat între cele
            // două citiri, etichetele GCM tot îl opresc - fără date alterate livrate.
            var second = await StorageEnvelope.OpenAsync(
                await _inner.OpenReadAsync(key, ct), _ring, allowPlaintext: false, ct);

            return second.Stream;
        }

        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
            _inner.ExistsAsync(key, ct);

        public Task DeleteAsync(string key, CancellationToken ct = default) =>
            _inner.DeleteAsync(key, ct);

        /// <summary>
        /// Pentru obiectele criptate aici nu se emite URL presemnat: browserul
        /// ar primi cifrotextul, pe care nu îl poate decripta (cheia e pe server).
        /// Apelantul trece automat pe descărcarea prin API.
        /// </summary>
        public Task<string?> TryCreatePresignedDownloadUrlAsync(
            string key, TimeSpan lifetime, CancellationToken ct = default) =>
            _options.AppliesTo(key)
                ? Task.FromResult<string?>(null)
                : _inner.TryCreatePresignedDownloadUrlAsync(key, lifetime, ct);

        public Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
            _inner.HealthCheckAsync(ct);

        // ─────────────────────────────────────────────────────────────────────

        internal static FileStream CreateTempFile() =>
            new(
                Path.Combine(Path.GetTempPath(), $"sgdm-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }
}
