using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using MAI.BusinessLogic.Interfaces;
using Microsoft.Extensions.Logging;

namespace MAI.BusinessLogic.Storage
{
    /// <summary>
    /// Stocare pe un serviciu compatibil S3.
    ///
    /// Aceeași clasă funcționează cu MinIO auto-găzduit, Amazon S3, Cloudflare R2
    /// și Supabase Storage - diferă doar endpointul și credențialele din
    /// configurare. Ăsta e rostul alegerii protocolului S3: decizia de provider
    /// rămâne reversibilă, fără să rescrii cod.
    /// </summary>
    public sealed class S3FileStorage : IFileStorage, IDisposable
    {
        private readonly StorageOptions _options;
        private readonly ILogger<S3FileStorage> _logger;
        private readonly IAmazonS3 _client;

        // Verificarea existenței bucketului se face o singură dată, la prima
        // operație. Nu la fiecare upload - ar fi un round-trip inutil de fiecare dată.
        private readonly SemaphoreSlim _bucketGate = new(1, 1);
        private bool _bucketChecked;

        public S3FileStorage(StorageOptions options, ILogger<S3FileStorage> logger)
        {
            _options = options;
            _logger  = logger;

            var config = new AmazonS3Config
            {
                ServiceURL            = _options.Endpoint,
                // MinIO nu are DNS wildcard pentru bucket.host, deci path-style
                // este obligatoriu: http://localhost:9000/mai-secure/cheie
                ForcePathStyle        = _options.ForcePathStyle,
                AuthenticationRegion  = _options.Region,
                UseHttp               = !_options.UseSsl,
            };

            _client = new AmazonS3Client(
                new BasicAWSCredentials(_options.AccessKey, _options.SecretKey),
                config);
        }

        public string ProviderName => $"S3 ({_options.Endpoint}, bucket={_options.Bucket})";

        public bool SupportsPresignedUrls => true;

        // ─────────────────────────────────────────────────────────────────────

        public async Task PutAsync(
            string key,
            Stream content,
            long contentLength,
            string contentType,
            CancellationToken ct = default)
        {
            await EnsureBucketAsync(ct);

            var request = new PutObjectRequest
            {
                BucketName  = _options.Bucket,
                Key         = key,
                InputStream = content,
                ContentType = string.IsNullOrWhiteSpace(contentType)
                    ? "application/octet-stream"
                    : contentType,

                // Fără chunk encoding, SDK-ul trimite corpul într-o singură bucată
                // semnată. Evită incompatibilități cu MinIO peste HTTP simplu.
                UseChunkEncoding = false,
            };

            // Content-Length explicit: altfel SDK-ul ar trebui să bufferizeze tot
            // conținutul ca să afle dimensiunea - exact ce vrem să evităm.
            request.Headers.ContentLength = contentLength;

            await _client.PutObjectAsync(request, ct);

            _logger.LogInformation(
                "Obiect scris în depozit: {Key} ({Bytes} octeți)", key, contentLength);
        }

        public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
        {
            await EnsureBucketAsync(ct);

            try
            {
                var response = await _client.GetObjectAsync(
                    new GetObjectRequest { BucketName = _options.Bucket, Key = key }, ct);

                return response.ResponseStream;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException(
                    $"Obiectul '{key}' nu există în depozit.", key, ex);
            }
        }

        public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        {
            await EnsureBucketAsync(ct);

            try
            {
                await _client.GetObjectMetadataAsync(
                    new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = key }, ct);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        public async Task DeleteAsync(string key, CancellationToken ct = default)
        {
            await EnsureBucketAsync(ct);

            try
            {
                await _client.DeleteObjectAsync(
                    new DeleteObjectRequest { BucketName = _options.Bucket, Key = key }, ct);

                _logger.LogInformation("Obiect șters din depozit: {Key}", key);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Ștergerea trebuie să fie idempotentă: dacă obiectul lipsește deja,
                // rezultatul dorit e oricum atins.
            }
        }

        public Task<string?> TryCreatePresignedDownloadUrlAsync(
            string key,
            TimeSpan lifetime,
            CancellationToken ct = default)
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _options.Bucket,
                Key        = key,
                Verb       = HttpVerb.GET,
                Expires    = DateTime.UtcNow.Add(lifetime),
                Protocol   = _options.UseSsl ? Protocol.HTTPS : Protocol.HTTP,
            };

            // Semnarea e pur locală (HMAC peste parametrii cererii) - nu există
            // apel de rețea, deci nu are ce eșua asincron.
            return Task.FromResult<string?>(_client.GetPreSignedURL(request));
        }

        /// <summary>
        /// Sondă de disponibilitate: cere metadatele bucketului.
        ///
        /// Deliberat NU apelează EnsureBucketAsync - acela creează bucketul dacă
        /// lipsește, iar o sondă de sănătate care modifică infrastructura pe care
        /// o măsoară nu mai măsoară nimic.
        /// </summary>
        public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
        {
            try
            {
                await _client.GetBucketLocationAsync(_options.Bucket, ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check depozit: {Endpoint} nu raspunde.", _options.Endpoint);
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────

        private async Task EnsureBucketAsync(CancellationToken ct)
        {
            if (_bucketChecked) return;

            await _bucketGate.WaitAsync(ct);
            try
            {
                if (_bucketChecked) return;

                // GetBucketLocation, nu ListBuckets: API-ul rulează cu un cont de
                // serviciu limitat la bucketul propriu (creat de minio-init), care
                // intenționat nu poate enumera bucketurile altora. ListBuckets ar fi
                // cerut s3:ListAllMyBuckets, adică exact dreptul pe care nu i-l dăm.
                bool exists;
                try
                {
                    await _client.GetBucketLocationAsync(_options.Bucket, ct);
                    exists = true;
                }
                catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
                {
                    exists = false;
                }

                // Crearea reușește doar cu un cont care are dreptul (MinIO local,
                // în dezvoltare, cu root). Contul de serviciu din Docker primește
                // AccessDenied - corect: acolo bucketul îl creează minio-init, cu
                // versionare și reguli de retenție, nu API-ul fără ele.
                if (!exists)
                {
                    _logger.LogWarning(
                        "Bucketul '{Bucket}' nu există. Se creează.", _options.Bucket);

                    await _client.PutBucketAsync(
                        new PutBucketRequest
                        {
                            BucketName = _options.Bucket,
                            UseClientRegion = false,
                        }, ct);
                }

                _bucketChecked = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Depozitul de fișiere nu răspunde la {Endpoint}. " +
                    "Rulează 'docker compose up -d'?", _options.Endpoint);
                throw;
            }
            finally
            {
                _bucketGate.Release();
            }
        }

        public void Dispose()
        {
            _client.Dispose();
            _bucketGate.Dispose();
        }
    }
}