using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MAI.BusinessLogic.Interfaces;
using Microsoft.Extensions.Logging;

namespace MAI.BusinessLogic.Storage
{
    /// <summary>
    /// Stocare pe filesystem-ul local. Există ca să poți rula aplicația fără
    /// Docker (dezvoltare, teste, o demonstrație rapidă pe un calculator străin).
    ///
    /// Nu se folosește în producție: nu se replică, nu suportă mai multe instanțe
    /// de API și se pierde la redeploy.
    /// </summary>
    public sealed class LocalFileStorage : IFileStorage
    {
        private readonly string _root;
        private readonly ILogger<LocalFileStorage> _logger;

        public LocalFileStorage(StorageOptions options, ILogger<LocalFileStorage> logger)
        {
            _logger = logger;

            _root = Path.IsPathRooted(options.LocalRootPath)
                ? options.LocalRootPath
                : Path.Combine(Directory.GetCurrentDirectory(), options.LocalRootPath);

            Directory.CreateDirectory(_root);
        }

        public string ProviderName => $"Local ({_root})";

        public bool SupportsPresignedUrls => false;

        public async Task PutAsync(
            string key,
            Stream content,
            long contentLength,
            string contentType,
            CancellationToken ct = default)
        {
            var path = ResolvePath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using var target = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81_920, useAsync: true);

            await content.CopyToAsync(target, ct);

            _logger.LogInformation("Obiect scris local: {Key} ({Bytes} octeți)", key, contentLength);
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
        {
            var path = ResolvePath(key);

            if (!File.Exists(path))
                throw new FileNotFoundException($"Obiectul '{key}' nu există.", key);

            Stream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81_920, useAsync: true);

            return Task.FromResult(stream);
        }

        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(File.Exists(ResolvePath(key)));

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            var path = ResolvePath(key);
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }

        public Task<string?> TryCreatePresignedDownloadUrlAsync(
            string key, TimeSpan lifetime, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        /// <summary>
        /// Sondă de disponibilitate: rădăcina există și se poate scrie în ea.
        /// Scrierea se testează efectiv, nu se deduce din atributele directorului —
        /// un mount read-only arată identic cu unul scriibil până încerci.
        /// </summary>
        public Task<bool> HealthCheckAsync(CancellationToken ct = default)
        {
            try
            {
                Directory.CreateDirectory(_root);

                var probe = Path.Combine(_root, $".health-{Guid.NewGuid():N}");
                File.WriteAllBytes(probe, []);
                File.Delete(probe);

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check depozit local: {Root} nu este scriibil.", _root);
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Transformă cheia de obiect în cale pe disc și verifică explicit că
        /// rezultatul rămâne sub rădăcină.
        ///
        /// Cheile sunt generate de server, nu de client, deci scenariul de atac
        /// nu există azi. Verificarea rămâne pentru că un refactor viitor care
        /// ar lăsa clientul să influențeze cheia ar transforma metoda asta în
        /// scriere arbitrară pe disc, iar bug-ul ar fi invizibil la review.
        /// </summary>
        private string ResolvePath(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cheia de obiect nu poate fi goală.", nameof(key));

            var relative = key.Replace('/', Path.DirectorySeparatorChar);
            var full     = Path.GetFullPath(Path.Combine(_root, relative));
            var rootFull = Path.GetFullPath(_root);

            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !string.Equals(full, rootFull, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"Cheia '{key}' iese din rădăcina depozitului.");
            }

            return full;
        }
    }
}