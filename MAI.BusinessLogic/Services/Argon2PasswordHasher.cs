using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Konscious.Security.Cryptography;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;

namespace MAI.BusinessLogic.Services
{
    /// <summary>
    /// Hashing de parole cu Argon2id (RFC 9106), în format PHC standard:
    ///   $argon2id$v=19$m=19456,t=2,p=1$&lt;salt-b64&gt;$&lt;hash-b64&gt;
    ///
    /// Trei mecanisme de protecție operațională:
    ///
    /// 1. PROFILE DE COST (asymmetric tuning) - login-ul folosește un profil ieftin,
    ///    crearea/resetarea de parolă și conturile privilegiate unul scump. Parametrii
    ///    fac parte din string-ul stocat, deci verificarea îi citește de acolo.
    ///
    /// 2. CONCURENȚĂ MĂRGINITĂ (offloading) - SemaphoreSlim limitează numărul de operații
    ///    simultane. Consumul de memorie devine determinist și nu mai depinde de trafic.
    ///    La saturare, cererile stau în coadă; la depășirea timeout-ului primesc 503.
    ///
    /// 3. THREAD POOL - calculul rulează pe Task.Run, nu pe firul de request. Un thread
    ///    care face 40 ms de muncă CPU-bound sincronă blochează Kestrel; aici nu.
    /// </summary>
    public class Argon2PasswordHasher : IPasswordHasher, IDisposable
    {
        private const string AlgorithmId = "argon2id";
        private const int Argon2Version = 19; // 0x13

        private readonly Argon2Options _options;
        private readonly byte[]? _pepper;
        private readonly SemaphoreSlim _gate;
        private readonly TimeSpan _queueTimeout;
        private bool _disposed;

        public Argon2PasswordHasher(Argon2Options options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();

            _pepper = string.IsNullOrEmpty(_options.Pepper)
                ? null
                : Encoding.UTF8.GetBytes(_options.Pepper);

            _gate         = new SemaphoreSlim(_options.MaxConcurrentHashes, _options.MaxConcurrentHashes);
            _queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        }

        public int AvailableCapacity => _gate.CurrentCount;
        public int PeakMemoryMib     => _options.EstimatedPeakMemoryMib;

        // ── Hashing ──────────────────────────────────────────────────────────

        public async Task<string> HashPasswordAsync(
            string password, string? profileName = null, CancellationToken ct = default)
        {
            if (password is null) throw new ArgumentNullException(nameof(password));

            var profile = _options.GetProfile(profileName);
            var salt    = RandomNumberGenerator.GetBytes(profile.SaltSizeBytes);

            var hash = await ComputeHashAsync(
                password, salt,
                profile.MemorySizeKib, profile.Iterations, profile.DegreeOfParallelism,
                profile.HashSizeBytes, ct);

            return Encode(salt, hash,
                profile.MemorySizeKib, profile.Iterations, profile.DegreeOfParallelism);
        }

        // ── Verificare ───────────────────────────────────────────────────────

        public async Task<PasswordVerificationResult> VerifyPasswordAsync(
            string password, string? storedHash, CancellationToken ct = default)
        {
            // Parola goală: Konscious.Argon2 refuză un input gol cu ArgumentException,
            // care ajungea ca 500 la login cu câmpul de parolă gol. O parolă goală
            // nu poate corespunde niciunui hash (politica o respinge la creare),
            // deci răspunsul corect e Failed - cu timp simulat, ca la hash invalid.
            if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(storedHash))
            {
                await SimulateVerificationAsync(ct);
                return PasswordVerificationResult.Failed;
            }

            // Cazul legacy: baza de date conține încă parole în clar.
            if (!storedHash.StartsWith("$argon2", StringComparison.Ordinal))
            {
                if (!_options.AllowLegacyPlaintext)
                {
                    await SimulateVerificationAsync(ct);
                    return PasswordVerificationResult.Failed;
                }

                var equal = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(password),
                    Encoding.UTF8.GetBytes(storedHash));

                await SimulateVerificationAsync(ct); // timp constant față de calea Argon2

                return equal
                    ? PasswordVerificationResult.SuccessRehashNeeded
                    : PasswordVerificationResult.Failed;
            }

            if (!TryDecode(storedHash, out var salt, out var expected, out var m, out var t, out var p))
            {
                await SimulateVerificationAsync(ct);
                return PasswordVerificationResult.Failed;
            }

            // Verificarea folosește parametrii din hash-ul stocat, nu pe cei curenți -
            // altfel hash-urile create cu alt profil nu s-ar mai valida niciodată.
            var actual = await ComputeHashAsync(password, salt, m, t, p, expected.Length, ct);

            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                return PasswordVerificationResult.Failed;

            // Re-hash doar dacă hash-ul e mai slab decât profilul implicit curent.
            // Un hash creat cu profilul "Sensitive" nu trebuie coborât la "Interactive".
            var current = _options.GetProfile(_options.DefaultProfile);
            var weaker  = m < current.MemorySizeKib
                       || t < current.Iterations
                       || expected.Length < current.HashSizeBytes
                       || salt.Length     < current.SaltSizeBytes;

            return weaker
                ? PasswordVerificationResult.SuccessRehashNeeded
                : PasswordVerificationResult.Success;
        }

        public async Task SimulateVerificationAsync(CancellationToken ct = default)
        {
            var profile   = _options.GetProfile(_options.DefaultProfile);
            var dummySalt = new byte[profile.SaltSizeBytes];

            await ComputeHashAsync(
                "MAI-dummy-password-for-constant-time",
                dummySalt,
                profile.MemorySizeKib, profile.Iterations, profile.DegreeOfParallelism,
                profile.HashSizeBytes, ct);
        }

        // ── Nucleul Argon2id, cu poartă de concurență ────────────────────────

        private async Task<byte[]> ComputeHashAsync(
            string password, byte[] salt,
            int memoryKib, int iterations, int parallelism, int outputLength,
            CancellationToken ct)
        {
            // Backpressure: dacă toate sloturile sunt ocupate, așteptăm; dacă așteptarea
            // depășește timeout-ul, refuzăm cererea în loc să alocăm memorie peste limită.
            if (!await _gate.WaitAsync(_queueTimeout, ct))
            {
                throw new HashingCapacityExceededException(
                    "Serviciul de autentificare este la capacitate maximă. Reîncercați în câteva secunde.");
            }

            try
            {
                // CPU-bound: scoatem calculul de pe firul de request.
                return await Task.Run(() =>
                {
                    using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
                    {
                        Salt                = salt,
                        MemorySize          = memoryKib,   // KiB
                        Iterations          = iterations,
                        DegreeOfParallelism = parallelism,
                    };

                    if (_pepper is not null)
                        argon2.KnownSecret = _pepper;

                    return argon2.GetBytes(outputLength);
                }, ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        // ── Encodare / decodare format PHC ───────────────────────────────────

        private static string Encode(byte[] salt, byte[] hash, int m, int t, int p) =>
            string.Format(
                CultureInfo.InvariantCulture,
                "${0}$v={1}$m={2},t={3},p={4}${5}${6}",
                AlgorithmId, Argon2Version, m, t, p,
                ToB64(salt), ToB64(hash));

        private static bool TryDecode(
            string encoded,
            out byte[] salt, out byte[] hash,
            out int memoryKib, out int iterations, out int parallelism)
        {
            salt = Array.Empty<byte>();
            hash = Array.Empty<byte>();
            memoryKib = iterations = parallelism = 0;

            // $argon2id$v=19$m=19456,t=2,p=1$<salt>$<hash>
            var parts = encoded.Split('$');
            if (parts.Length != 6) return false;
            if (!string.Equals(parts[1], AlgorithmId, StringComparison.Ordinal)) return false;
            if (!parts[2].StartsWith("v=", StringComparison.Ordinal)) return false;

            foreach (var param in parts[3].Split(','))
            {
                var kv = param.Split('=');
                if (kv.Length != 2) return false;
                if (!int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    return false;

                switch (kv[0])
                {
                    case "m": memoryKib   = value; break;
                    case "t": iterations  = value; break;
                    case "p": parallelism = value; break;
                    default:  return false;
                }
            }

            if (memoryKib <= 0 || iterations <= 0 || parallelism <= 0) return false;

            // Plafon de siguranță: un hash corupt cu m=8000000 ar aloca 8 GB la verificare.
            if (memoryKib > 1_048_576 || iterations > 20 || parallelism > 16) return false;

            try
            {
                salt = FromB64(parts[4]);
                hash = FromB64(parts[5]);
            }
            catch (FormatException)
            {
                return false;
            }

            return salt.Length > 0 && hash.Length > 0;
        }

        /// <summary>Base64 fără padding, conform specificației PHC.</summary>
        private static string ToB64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

        private static byte[] FromB64(string value)
        {
            var padded = (value.Length % 4) switch
            {
                2 => value + "==",
                3 => value + "=",
                0 => value,
                _ => throw new FormatException("Base64 invalid în hash-ul stocat."),
            };
            return Convert.FromBase64String(padded);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _gate.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}