using MAI.Api.Security;
using MAI.BusinessLogic.Interfaces;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Gestionarea cheilor criptografice ale utilizatorilor.
    ///
    /// Serverul este aici doar un director de chei publice și un depozit pentru
    /// blobul de chei private criptat. Nu poate descuia nimic: blobul e criptat
    /// AES-256-GCM cu o cheie derivată din parolă, în browser, iar parola nu se
    /// stochează nicăieri (doar hash-ul ei Argon2id).
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class KeysController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _passwordHasher;

        public KeysController(AppDbContext context, IPasswordHasher passwordHasher)
        {
            _context        = context;
            _passwordHasher = passwordHasher;
        }

        private Guid CurrentUserId =>
            Guid.Parse(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        private string CurrentUsername => HttpContext.User.Identity?.Name ?? "sistem";
        private string CallerIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // ─────────────────────────────────────────────────────────────────────
        // GET api/Keys/me — pachetul propriu, pentru descuiere în browser
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet("me")]
        public async Task<IActionResult> GetMyBundle(CancellationToken ct)
        {
            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == CurrentUserId, ct);

            if (user is null) return NotFound();

            if (string.IsNullOrEmpty(user.PublicKeyEncryption))
                return Ok(new { hasKeys = false });

            return Ok(new
            {
                hasKeys = true,
                publicKeyEncryption     = user.PublicKeyEncryption,
                publicKeySigning        = user.PublicKeySigning,
                encryptedPrivateBundle  = user.EncryptedPrivateBundle,
                keyDerivationSalt       = user.KeyDerivationSalt,
                keyDerivationIterations = user.KeyDerivationIterations,
                wrapIv                  = user.KeyWrapIv,
                suite                   = user.CryptoSuite,
                keysCreatedAt           = user.KeysCreatedAt,
                fingerprint             = Fingerprint(user.PublicKeyEncryption),
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST api/Keys/verify-password — confirmă parola înainte de generare
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Cheile private se încuie cu o cheie derivată din parola contului. Dacă
        /// utilizatorul tastează greșit parola pe ecranul de generare, cheile se
        /// încuie cu o parolă care nu există nicăieri, iar la următoarea
        /// autentificare nu se mai pot descuia — pierdere permanentă și tăcută.
        ///
        /// Endpointul elimină exact acest scenariu. Nu returnează nimic în afara
        /// unui bool și consumă din cota de rate limiting pentru operații cu
        /// parole, ca să nu poată fi folosit ca oracol de forță brută de către un
        /// token furat.
        /// </summary>
        [HttpPost("verify-password")]
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        public async Task<IActionResult> VerifyPassword(
            [FromBody] VerifyPasswordDto dto, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(dto.Password))
                return BadRequest(new { message = "Parola este obligatorie." });

            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == CurrentUserId, ct);

            if (user is null)
            {
                // Consumă același timp de calcul ca o verificare reală.
                await _passwordHasher.SimulateVerificationAsync(ct);
                return Ok(new { valid = false });
            }

            var result = await _passwordHasher.VerifyPasswordAsync(dto.Password, user.PasswordHash, ct);
            return Ok(new { valid = result != PasswordVerificationResult.Failed });
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST api/Keys — înregistrează pachetul de chei (o singură dată)
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> PublishBundle([FromBody] PublishKeysDto dto, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { CurrentUserId }, ct);
            if (user is null) return NotFound();

            // Republicarea ar invalida toate fișierele primite anterior: cheile lor
            // sunt împachetate cu vechea cheie publică și nu s-ar mai putea deschide.
            if (!string.IsNullOrEmpty(user.PublicKeyEncryption))
            {
                return Conflict(new
                {
                    message = "Cheile sunt deja înregistrate. Regenerarea lor ar face " +
                              "imposibilă deschiderea fișierelor primite până acum.",
                });
            }

            if (!TryValidate(dto, out var error))
                return BadRequest(new { message = error });

            user.PublicKeyEncryption     = dto.PublicKeyEncryption;
            user.PublicKeySigning        = dto.PublicKeySigning;
            user.EncryptedPrivateBundle  = dto.EncryptedPrivateBundle;
            user.KeyDerivationSalt       = dto.KeyDerivationSalt;
            user.KeyDerivationIterations = dto.KeyDerivationIterations;
            user.KeyWrapIv               = dto.WrapIv;
            user.CryptoSuite             = dto.Suite;
            user.KeysCreatedAt           = DateTime.UtcNow;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CurrentUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"Chei E2E inregistrate ({dto.Suite}), amprenta {Fingerprint(dto.PublicKeyEncryption)}",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);
            return Ok(new { message = "Cheile au fost înregistrate.", fingerprint = Fingerprint(dto.PublicKeyEncryption) });
        }

        // ─────────────────────────────────────────────────────────────────────
        // PATCH api/Keys/rewrap — după schimbarea parolei
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Cheile private sunt încuiate cu o cheie derivată din parolă. Când parola
        /// se schimbă, browserul le descuie cu cea veche, le reîncuie cu cea nouă și
        /// trimite noul blob aici. Cheile publice rămân aceleași, deci fișierele
        /// primite anterior rămân accesibile.
        /// </summary>
        [HttpPatch("rewrap")]
        public async Task<IActionResult> Rewrap([FromBody] RewrapKeysDto dto, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { CurrentUserId }, ct);
            if (user is null) return NotFound();

            if (string.IsNullOrEmpty(user.PublicKeyEncryption))
                return BadRequest(new { message = "Nu există chei înregistrate pentru acest cont." });

            if (string.IsNullOrWhiteSpace(dto.EncryptedPrivateBundle) ||
                string.IsNullOrWhiteSpace(dto.KeyDerivationSalt) ||
                string.IsNullOrWhiteSpace(dto.WrapIv))
            {
                return BadRequest(new { message = "Pachet de chei incomplet." });
            }

            if (dto.KeyDerivationIterations < 100_000)
                return BadRequest(new { message = "Numărul de iterații PBKDF2 este prea mic (minim 100.000)." });

            user.EncryptedPrivateBundle  = dto.EncryptedPrivateBundle;
            user.KeyDerivationSalt       = dto.KeyDerivationSalt;
            user.KeyDerivationIterations = dto.KeyDerivationIterations;
            user.KeyWrapIv               = dto.WrapIv;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CurrentUsername,
                Action    = AuditAction.UserUpdated,
                Details   = "Chei private reimpachetate cu parola noua",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);
            return Ok(new { message = "Cheile au fost reîmpachetate." });
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET api/Keys/{userId}/public — cheile publice ale unui destinatar
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet("{userId:guid}/public")]
        public async Task<IActionResult> GetPublicKeys(Guid userId, CancellationToken ct)
        {
            var user = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.FullName,
                    u.PublicKeyEncryption,
                    u.PublicKeySigning,
                    u.KeysCreatedAt,
                })
                .FirstOrDefaultAsync(ct);

            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost găsit." });

            if (string.IsNullOrEmpty(user.PublicKeyEncryption))
            {
                return BadRequest(new
                {
                    message = $"Utilizatorul @{user.Username} nu și-a generat încă cheile. " +
                              "Nu i se pot trimite fișiere criptate până nu se autentifică cel puțin o dată.",
                    hasKeys = false,
                });
            }

            return Ok(new
            {
                userId              = user.Id,
                username            = user.Username,
                fullName            = user.FullName,
                publicKeyEncryption = user.PublicKeyEncryption,
                publicKeySigning    = user.PublicKeySigning,
                keysCreatedAt       = user.KeysCreatedAt,
                // Amprenta se compară în afara aplicației (telefon, față în față) ca să
                // se excludă substituirea cheilor de către un server compromis.
                fingerprint         = Fingerprint(user.PublicKeyEncryption),
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET api/Keys/recipients — utilizatori activi care POT primi fișiere
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet("recipients")]
        public async Task<IActionResult> GetRecipients(CancellationToken ct)
        {
            var me = CurrentUserId;

            var users = await _context.Users
                .AsNoTracking()
                .Where(u => u.IsActive && u.Id != me && u.PublicKeyEncryption != null)
                .OrderBy(u => u.FullName ?? u.Username)
                .Select(u => new
                {
                    id                  = u.Id,
                    username            = u.Username,
                    fullName            = u.FullName ?? u.Username,
                    department          = u.Department ?? string.Empty,
                    publicKeyEncryption = u.PublicKeyEncryption,
                    publicKeySigning    = u.PublicKeySigning,
                })
                .ToListAsync(ct);

            return Ok(users);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validare structurală. Serverul nu poate verifica criptografic pachetul
        /// (nu are cheile), dar poate refuza valori evident greșite.
        /// </summary>
        private static bool TryValidate(PublishKeysDto dto, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(dto.PublicKeyEncryption) ||
                string.IsNullOrWhiteSpace(dto.PublicKeySigning) ||
                string.IsNullOrWhiteSpace(dto.EncryptedPrivateBundle) ||
                string.IsNullOrWhiteSpace(dto.KeyDerivationSalt) ||
                string.IsNullOrWhiteSpace(dto.WrapIv))
            {
                error = "Pachet de chei incomplet.";
                return false;
            }

            // Sub pragul OWASP, blobul de chei private ar fi prea ieftin de spart offline.
            if (dto.KeyDerivationIterations < 100_000)
            {
                error = "Numărul de iterații PBKDF2 este prea mic (minim 100.000).";
                return false;
            }

            foreach (var (name, value) in new[]
            {
                ("publicKeyEncryption", dto.PublicKeyEncryption),
                ("publicKeySigning", dto.PublicKeySigning),
                ("encryptedPrivateBundle", dto.EncryptedPrivateBundle),
                ("keyDerivationSalt", dto.KeyDerivationSalt),
                ("wrapIv", dto.WrapIv),
            })
            {
                if (!IsBase64(value))
                {
                    error = $"Câmpul '{name}' nu este base64 valid.";
                    return false;
                }
            }

            return true;
        }

        private static bool IsBase64(string value)
        {
            Span<byte> buffer = new byte[value.Length];
            return Convert.TryFromBase64String(value, buffer, out _);
        }

        /// <summary>Primii 128 de biți din SHA-256 al cheii publice, în grupuri de 4.</summary>
        private static string Fingerprint(string? spkiBase64)
        {
            if (string.IsNullOrEmpty(spkiBase64)) return string.Empty;

            var digest = SHA256.HashData(Convert.FromBase64String(spkiBase64));
            var hex    = Convert.ToHexString(digest)[..32];

            var sb = new StringBuilder();
            for (var i = 0; i < hex.Length; i += 4)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(hex, i, 4);
            }
            return sb.ToString();
        }
    }

    public class PublishKeysDto
    {
        public string PublicKeyEncryption { get; set; } = string.Empty;
        public string PublicKeySigning { get; set; } = string.Empty;
        public string EncryptedPrivateBundle { get; set; } = string.Empty;
        public string KeyDerivationSalt { get; set; } = string.Empty;
        public int KeyDerivationIterations { get; set; }
        public string WrapIv { get; set; } = string.Empty;
        public string Suite { get; set; } = string.Empty;
    }

    public class RewrapKeysDto
    {
        public string EncryptedPrivateBundle { get; set; } = string.Empty;
        public string KeyDerivationSalt { get; set; } = string.Empty;
        public int KeyDerivationIterations { get; set; }
        public string WrapIv { get; set; } = string.Empty;
    }

    public class VerifyPasswordDto
    {
        public string Password { get; set; } = string.Empty;
    }
}