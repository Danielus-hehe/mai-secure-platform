using System.Security.Cryptography;
using MAI.BusinessLogic.Interfaces;
using Microsoft.Extensions.Logging;

namespace MAI.BusinessLogic.Storage.Encryption
{
    /// <summary>Un obiect de verificat/recriptat: cheia din depozit și amprenta așteptată.</summary>
    /// <param name="ExpectedSha256">SHA-256 al conținutului în clar, din baza de date; null dacă nu e cunoscut.</param>
    /// <param name="Label">Descriere pentru raport (ex. „document X v2”).</param>
    public sealed record RecryptItem(string Key, string? ExpectedSha256, string Label);

    public enum RecryptOutcome
    {
        /// <summary>Era în clar; acum e criptat cu cheia activă.</summary>
        Encrypted,
        /// <summary>Era criptat cu o cheie veche; antetul a fost re-împachetat cu cheia activă.</summary>
        Rewrapped,
        /// <summary>Deja criptat cu cheia activă. Nimic de făcut.</summary>
        AlreadyCurrent,
        /// <summary>Simulare: ar fi fost criptat.</summary>
        WouldEncrypt,
        /// <summary>Simulare: ar fi fost re-împachetat.</summary>
        WouldRewrap,
        /// <summary>Obiectul nu există în depozit.</summary>
        Missing,
        /// <summary>Conținutul în clar nu corespunde amprentei din baza de date. Lăsat neatins.</summary>
        ChecksumMismatch,
        /// <summary>Eroare (cheie lipsă, obiect alterat, scriere eșuată).</summary>
        Failed,
    }

    public sealed record RecryptResult(RecryptItem Item, RecryptOutcome Outcome, string? Detail = null)
    {
        public bool IsProblem => Outcome is RecryptOutcome.Missing
                                        or RecryptOutcome.ChecksumMismatch
                                        or RecryptOutcome.Failed;
    }

    /// <summary>
    /// Aduce obiectele existente la starea „criptat cu cheia activă”:
    /// <list type="bullet">
    ///   <item>obiect în clar (scris înainte de activarea criptării) → criptat
    ///   integral, dar NUMAI dacă amprenta SHA-256 a conținutului corespunde celei
    ///   din baza de date; un fișier modificat între timp în depozit nu primește
    ///   „ștampila” criptării, ci e raportat;</item>
    ///   <item>obiect criptat cu o cheie principală veche → doar antetul se
    ///   re-împachetează cu cheia activă, corpul se copiază neatins;</item>
    ///   <item>obiect deja pe cheia activă → nimic.</item>
    /// </list>
    ///
    /// După fiecare scriere, obiectul se citește înapoi și se decriptează complet
    /// (verificare), iar amprenta se compară din nou cu baza de date. Dacă
    /// verificarea pică, versiunea anterioară a obiectului rămâne recuperabilă
    /// din versionarea bucketului (24 de ore, vezi docker-compose.yml).
    ///
    /// Idempotent: rulat de două ori, a doua oară raportează doar AlreadyCurrent.
    /// </summary>
    public sealed class StorageRecryptor
    {
        private readonly IFileStorage _raw;
        private readonly MasterKeyRing _ring;
        private readonly StorageEncryptionOptions _options;
        private readonly ILogger _logger;

        /// <param name="raw">Depozitul REAL, fără decorator: aici se văd octeții exact cum sunt stocați.</param>
        public StorageRecryptor(IFileStorage raw, MasterKeyRing ring, StorageEncryptionOptions options, ILogger logger)
        {
            _raw     = raw;
            _ring    = ring;
            _options = options;
            _logger  = logger;
        }

        public async Task<RecryptResult> ProcessAsync(RecryptItem item, bool dryRun, CancellationToken ct = default)
        {
            try
            {
                if (!await _raw.ExistsAsync(item.Key, ct))
                    return new RecryptResult(item, RecryptOutcome.Missing, "obiectul nu exista in depozit");

                EnvelopeProbe probe;
                Prepared? prepared = null;

                // Sursa se închide înainte de scriere: pe discul local (provider
                // Local) fișierul nu poate fi rescris cât timp e încă deschis.
                await using (var source = await _raw.OpenReadAsync(item.Key, ct))
                {
                    probe = await StorageEnvelope.ProbeAsync(source, ct);

                    if (!probe.IsEncrypted)
                        prepared = await PrepareEncryptionAsync(item, source, probe.ConsumedBytes, dryRun, ct);
                }

                if (prepared is not null)
                    return await CompleteEncryptionAsync(item, prepared, ct);

                var header = probe.Header!;

                if (header.KeyId == _ring.ActiveKeyId)
                    return new RecryptResult(item, RecryptOutcome.AlreadyCurrent);

                if (!_ring.Contains(header.KeyId))
                    return new RecryptResult(item, RecryptOutcome.Failed,
                        $"criptat cu cheia '{header.KeyId}', care nu e in MAI_STORAGE_MASTER_KEYS");

                if (dryRun)
                    return new RecryptResult(item, RecryptOutcome.WouldRewrap, $"cheia {header.KeyId} -> {_ring.ActiveKeyId}");

                return await RewrapAsync(item, header, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recriptarea obiectului {Key} a esuat.", item.Key);
                return new RecryptResult(item, RecryptOutcome.Failed, ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Criptarea pregătită: fie un rezultat final (simulare, amprentă greșită), fie cifrotextul în fișier temporar.</summary>
        private sealed record Prepared(RecryptResult? Result, FileStream? Ciphertext, string? Sha256);

        private async Task<Prepared> PrepareEncryptionAsync(
            RecryptItem item, Stream rest, byte[] consumed, bool dryRun, CancellationToken ct)
        {
            // Prefixul consumat la sondare + restul sursei = conținutul în clar complet.
            // Sursa nu se închide aici; o închide blocul care a deschis-o.
            var plaintext = new PrefixedReadStream(consumed, new NonDisposingStream(rest));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            if (dryRun)
            {
                var buffer = new byte[81_920];
                int read;
                while ((read = await plaintext.ReadAsync(buffer, ct)) > 0)
                    hash.AppendData(buffer, 0, read);

                var actual = Hex(hash);
                return new Prepared(
                    Matches(item, actual)
                        ? new RecryptResult(item, RecryptOutcome.WouldEncrypt)
                        : new RecryptResult(item, RecryptOutcome.ChecksumMismatch, MismatchDetail(item, actual)),
                    null, null);
            }

            var temp = EncryptingFileStorage.CreateTempFile();
            try
            {
                await StorageEnvelope.EncryptAsync(plaintext, temp, _ring, _options.ChunkSizeBytes, hash, ct);
            }
            catch
            {
                await temp.DisposeAsync();
                throw;
            }

            var sha = Hex(hash);
            if (!Matches(item, sha))
            {
                await temp.DisposeAsync();
                return new Prepared(new RecryptResult(item, RecryptOutcome.ChecksumMismatch, MismatchDetail(item, sha)), null, null);
            }

            return new Prepared(null, temp, sha);
        }

        private async Task<RecryptResult> CompleteEncryptionAsync(RecryptItem item, Prepared prepared, CancellationToken ct)
        {
            if (prepared.Ciphertext is null)
                return prepared.Result!;

            await using (var temp = prepared.Ciphertext)
            {
                temp.Position = 0;
                await _raw.PutAsync(item.Key, temp, temp.Length, "application/octet-stream", ct);
            }

            return await VerifyAsync(item, RecryptOutcome.Encrypted, prepared.Sha256, ct);
        }

        private async Task<RecryptResult> RewrapAsync(RecryptItem item, EnvelopeHeader oldHeader, CancellationToken ct)
        {
            var newHeader = StorageEnvelope.RewrapHeader(oldHeader, _ring);

            await using var temp = EncryptingFileStorage.CreateTempFile();
            await temp.WriteAsync(newHeader, ct);

            await using (var source = await _raw.OpenReadAsync(item.Key, ct))
            {
                // Se sare peste antetul vechi și se copiază corpul exact cum e.
                var skip = new byte[oldHeader.Length];
                if (await StorageEnvelope.FillAsync(source, skip, ct) != skip.Length)
                    throw StorageEnvelope.Truncated();

                await source.CopyToAsync(temp, ct);
            }

            temp.Position = 0;
            await _raw.PutAsync(item.Key, temp, temp.Length, "application/octet-stream", ct);

            return await VerifyAsync(item, RecryptOutcome.Rewrapped, knownSha: null, ct,
                $"cheia {oldHeader.KeyId} -> {_ring.ActiveKeyId}");
        }

        /// <summary>Citește obiectul înapoi, îl decriptează complet și compară amprenta.</summary>
        private async Task<RecryptResult> VerifyAsync(
            RecryptItem item, RecryptOutcome success, string? knownSha, CancellationToken ct, string? detail = null)
        {
            var opened = await StorageEnvelope.OpenAsync(
                await _raw.OpenReadAsync(item.Key, ct), _ring, allowPlaintext: false, ct);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (opened.Stream)
            {
                var buffer = new byte[81_920];
                int read;
                while ((read = await opened.Stream.ReadAsync(buffer, ct)) > 0)
                    hash.AppendData(buffer, 0, read);
            }

            var sha = Hex(hash);

            if (opened.Header?.KeyId != _ring.ActiveKeyId)
                return new RecryptResult(item, RecryptOutcome.Failed, "verificare: obiectul recitit nu este pe cheia activa");

            if (knownSha is not null && !string.Equals(sha, knownSha, StringComparison.Ordinal))
                return new RecryptResult(item, RecryptOutcome.Failed, "verificare: continutul recitit difera de cel scris");

            if (!Matches(item, sha))
                return new RecryptResult(item, RecryptOutcome.Failed, "verificare: " + MismatchDetail(item, sha));

            return new(item, success, detail);
        }

        private static bool Matches(RecryptItem item, string actualSha) =>
            string.IsNullOrWhiteSpace(item.ExpectedSha256) ||
            string.Equals(item.ExpectedSha256.Trim(), actualSha, StringComparison.OrdinalIgnoreCase);

        private static string MismatchDetail(RecryptItem item, string actual) =>
            $"SHA-256 in baza {Short(item.ExpectedSha256)}, in depozit {Short(actual)}";

        private static string Short(string? sha) =>
            string.IsNullOrEmpty(sha) ? "-" : sha.Length > 16 ? sha[..16] + "..." : sha;

        private static string Hex(IncrementalHash hash) =>
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        /// <summary>Lasă sursa deschisă: proprietarul ei o închide (blocul await using de mai sus).</summary>
        private sealed class NonDisposingStream(Stream inner) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override int Read(Span<byte> buffer) => inner.Read(buffer);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                inner.ReadAsync(buffer, offset, count, ct);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                inner.ReadAsync(buffer, ct);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
