using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Security.Cryptography;
using MAI.Api.Security;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Gestionarea autentificării în doi pași, din pagina de profil.
    ///
    /// 2FA este OPȚIONAL și se activează exclusiv de către utilizator, pentru
    /// contul lui. Nu există endpoint prin care un administrator să activeze 2FA
    /// pe contul altcuiva — ar fi inutil: administratorul nu are cum să scaneze
    /// codul QR pe telefonul acelei persoane. Poate doar să dezactiveze, pentru
    /// deblocarea unui coleg care și-a pierdut telefonul și codurile.
    ///
    /// Fluxul de activare are trei pași, în ordinea asta din motive concrete:
    ///
    ///   1. /setup   — cere parola, generează un secret ÎN AȘTEPTARE și îl arată
    ///                 ca URI otpauth. Nimic nu e activat încă.
    ///   2. /enable  — cere un cod generat din acel secret. Abia dovada că
    ///                 aplicația de pe telefon funcționează activează 2FA.
    ///                 Returnează codurile de recuperare, o singură dată.
    ///   3. /disable — cere parola ȘI un cod valid.
    ///
    /// Pasul 2 nu e birocrație: dacă am activa direct la /setup, un utilizator
    /// care scanează greșit codul QR se închide singur în afara contului, iar
    /// singura ieșire ar fi intervenția administratorului.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class TwoFactorController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _hasher;
        private readonly TotpService _totp;
        private readonly SecretProtector _protector;
        private readonly TwoFactorOptions _options;
        private readonly ILogger<TwoFactorController> _logger;

        public TwoFactorController(
            AppDbContext context,
            IPasswordHasher hasher,
            TotpService totp,
            SecretProtector protector,
            TwoFactorOptions options,
            ILogger<TwoFactorController> logger)
        {
            _context   = context;
            _hasher    = hasher;
            _totp      = totp;
            _protector = protector;
            _options   = options;
            _logger    = logger;
        }

        private Guid CurrentUserId =>
            Guid.Parse(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // ═════════════════════════════════════════════════════════════════════
        // GET api/TwoFactor/status
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet("status")]
        public async Task<IActionResult> GetStatus(CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { CurrentUserId }, ct);
            if (user is null) return NotFound();

            return Ok(new
            {
                enabled                = user.TwoFactorEnabled,
                enrolledAt             = user.TwoFactorEnrolledAt,
                remainingRecoveryCodes = user.RemainingRecoveryCodes,
                setupInProgress        = !user.TwoFactorEnabled && user.TwoFactorPendingSecret != null,
                issuer                 = _options.Issuer,
                digits                 = _options.Digits,
                periodSeconds          = _options.PeriodSeconds,
                // Informativ pentru interfață: rolurile privilegiate pot fi obligate
                // prin configurare, dar implicit 2FA rămâne o alegere personală.
                recommended            = user.Role >= UserRole.SefDirectie,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/TwoFactor/setup
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Generează un secret în așteptare și returnează datele pentru codul QR.
        ///
        /// Cere parola. Fără verificare, oricine ajunge la un calculator lăsat
        /// deblocat poate reînrola 2FA pe telefonul lui și prelua contul definitiv —
        /// atacul devine mai grav decât absența 2FA.
        /// </summary>
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPost("setup")]
        public async Task<IActionResult> Setup([FromBody] TwoFactorPasswordDto dto, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { CurrentUserId }, ct);
            if (user is null) return NotFound();

            if (user.TwoFactorEnabled)
                return Conflict(new
                {
                    message = "Autentificarea în doi pași este deja activă. " +
                              "Dezactivați-o înainte de a înrola un alt dispozitiv.",
                });

            if (!await VerifyPasswordAsync(user, dto.Password, ct))
            {
                await AuditAsync(user, "Initiere 2FA cu parola incorecta", ct, AuditResult.Failure);
                return BadRequest(new { message = "Parola este incorectă." });
            }

            var secret = _totp.GenerateSecret();

            user.TwoFactorPendingSecret = _protector.Protect(secret);

            AddAudit(user, "Secret 2FA generat, in asteptarea confirmarii");
            await _context.SaveChangesAsync(ct);

            return Ok(new
            {
                // Secretul în clar iese din server O SINGURĂ DATĂ, aici, către
                // utilizatorul care tocmai și-a dovedit parola. În baza de date
                // stă cifrat, iar /status nu îl returnează niciodată.
                secret          = secret,
                secretFormatted = TotpService.FormatSecretForDisplay(secret),
                otpauthUri      = _totp.BuildOtpAuthUri(user.Username, secret),
                digits          = _options.Digits,
                periodSeconds   = _options.PeriodSeconds,
                issuer          = _options.Issuer,
                account         = user.Username,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/TwoFactor/enable
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Confirmă înrolarea cu un cod generat din secretul în așteptare și
        /// activează 2FA. Returnează codurile de recuperare — singura dată când
        /// sunt vizibile în clar.
        /// </summary>
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPost("enable")]
        public async Task<IActionResult> Enable([FromBody] TwoFactorCodeDto dto, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { CurrentUserId }, ct);
            if (user is null) return NotFound();

            if (user.TwoFactorEnabled)
                return Conflict(new { message = "Autentificarea în doi pași este deja activă." });

            if (string.IsNullOrEmpty(user.TwoFactorPendingSecret))
                return BadRequest(new
                {
                    message = "Nu există o înrolare în curs. Reluați configurarea de la început.",
                });

            string pendingSecret;
            try
            {
                pendingSecret = _protector.Unprotect(user.TwoFactorPendingSecret);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "Secretul 2FA in asteptare al lui {User} nu poate fi descifrat.", user.Username);
                user.TwoFactorPendingSecret = null;
                await _context.SaveChangesAsync(ct);
                return StatusCode(500, new { message = "Înrolarea a eșuat. Reluați configurarea." });
            }

            if (!_totp.VerifyCode(pendingSecret, dto.Code))
            {
                await AuditAsync(user, "Cod incorect la activarea 2FA", ct, AuditResult.Failure);
                return BadRequest(new
                {
                    message = "Codul nu este valid. Verificați că ora telefonului este sincronizată automat.",
                });
            }

            var recoveryCodes = _totp.GenerateRecoveryCodes();

            user.TwoFactorSecret             = user.TwoFactorPendingSecret;
            user.TwoFactorPendingSecret      = null;
            user.TwoFactorEnabled            = true;
            user.TwoFactorEnrolledAt         = DateTime.UtcNow;
            user.TwoFactorRecoveryCodeHashes = string.Join(';',
                recoveryCodes.Select(TotpService.HashRecoveryCode));

            // Activarea 2FA invalidează sesiunile deschise. Dacă cineva era deja
            // logat pe contul ăsta de pe alt dispozitiv, tocmai a devenit motivul
            // pentru care utilizatorul a activat 2FA.
            user.RefreshTokenHash      = null;
            user.RefreshTokenExpiresAt = null;

            AddAudit(user, $"2FA activat, {recoveryCodes.Count} coduri de recuperare emise, sesiuni revocate");
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("2FA activat pentru {Username}.", user.Username);

            return Ok(new
            {
                message = "Autentificarea în doi pași este activă.",
                recoveryCodes,
                warning = "Salvați aceste coduri acum. Nu vor mai fi afișate niciodată.",
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/TwoFactor/cancel-setup
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>Renunță la o înrolare începută și neconfirmată.</summary>
        [HttpPost("cancel-setup")]
        public async Task<IActionResult> CancelSetup(CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { CurrentUserId }, ct);
            if (user is null) return NotFound();

            user.TwoFactorPendingSecret = null;
            await _context.SaveChangesAsync(ct);

            return Ok(new { message = "Configurarea a fost anulată." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/TwoFactor/disable
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Dezactivează 2FA. Cere parola ȘI un cod valid (sau un cod de recuperare).
        ///
        /// Ambele, nu unul singur: dacă ar fi suficientă parola, un atacator care
        /// a obținut-o ar dezactiva pur și simplu al doilea factor, iar 2FA nu ar
        /// apăra de nimic.
        /// </summary>
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPost("disable")]
        public async Task<IActionResult> Disable([FromBody] TwoFactorDisableDto dto, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { CurrentUserId }, ct);
            if (user is null) return NotFound();

            if (!user.TwoFactorEnabled)
                return BadRequest(new { message = "Autentificarea în doi pași nu este activă." });

            if (!await VerifyPasswordAsync(user, dto.Password, ct))
            {
                await AuditAsync(user, "Dezactivare 2FA cu parola incorecta", ct, AuditResult.Failure);
                return BadRequest(new { message = "Parola este incorectă." });
            }

            if (!await VerifySecondFactorAsync(user, dto.Code))
            {
                await AuditAsync(user, "Dezactivare 2FA cu cod incorect", ct, AuditResult.Failure);
                return BadRequest(new { message = "Codul nu este valid." });
            }

            user.TwoFactorEnabled            = false;
            user.TwoFactorSecret             = null;
            user.TwoFactorPendingSecret      = null;
            user.TwoFactorRecoveryCodeHashes = null;
            user.TwoFactorEnrolledAt         = null;
            user.TwoFactorChallengeHash      = null;
            user.TwoFactorChallengeExpiresAt = null;
            user.TwoFactorChallengeAttempts  = 0;

            AddAudit(user, "2FA dezactivat de utilizator", AuditResult.Warning);
            await _context.SaveChangesAsync(ct);

            _logger.LogWarning("2FA dezactivat pentru {Username}, IP={Ip}.", user.Username, Ip);

            return Ok(new { message = "Autentificarea în doi pași a fost dezactivată." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/TwoFactor/recovery-codes
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Regenerează codurile de recuperare. Cele vechi devin inutilizabile
        /// imediat — altfel un set de coduri scurs ar rămâne valabil la nesfârșit.
        /// </summary>
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPost("recovery-codes")]
        public async Task<IActionResult> RegenerateRecoveryCodes(
            [FromBody] TwoFactorDisableDto dto, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { CurrentUserId }, ct);
            if (user is null) return NotFound();

            if (!user.TwoFactorEnabled)
                return BadRequest(new { message = "Autentificarea în doi pași nu este activă." });

            if (!await VerifyPasswordAsync(user, dto.Password, ct))
                return BadRequest(new { message = "Parola este incorectă." });

            if (!await VerifySecondFactorAsync(user, dto.Code))
                return BadRequest(new { message = "Codul nu este valid." });

            var recoveryCodes = _totp.GenerateRecoveryCodes();

            user.TwoFactorRecoveryCodeHashes = string.Join(';',
                recoveryCodes.Select(TotpService.HashRecoveryCode));

            AddAudit(user, $"Coduri de recuperare 2FA regenerate ({recoveryCodes.Count})");
            await _context.SaveChangesAsync(ct);

            return Ok(new
            {
                message = "Codurile vechi nu mai sunt valabile.",
                recoveryCodes,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/TwoFactor/admin/reset/{userId}
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Dezactivează 2FA pe contul altui utilizator. Exclusiv Administrator.
        ///
        /// Există pentru un singur scenariu real: cineva și-a pierdut telefonul ȘI
        /// codurile de recuperare. Fără această ieșire, contul devine inaccesibil
        /// permanent.
        ///
        /// Este, prin construcție, un ocol al celui de-al doilea factor — de aceea
        /// se jurnalizează cu ATENTIE și numele administratorului care l-a folosit.
        /// Verificarea identității persoanei se face în afara sistemului, iar
        /// procedura trebuie descrisă în documentația de exploatare.
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPost("admin/reset/{userId:guid}")]
        public async Task<IActionResult> AdminReset(Guid userId, CancellationToken ct)
        {
            var target = await _context.Users.FindAsync(new object?[] { userId }, ct);
            if (target is null) return NotFound(new { message = "Utilizatorul nu a fost găsit." });

            if (!target.TwoFactorEnabled && target.TwoFactorPendingSecret is null)
                return BadRequest(new { message = "Utilizatorul nu are 2FA configurat." });

            var adminName = HttpContext.User.Identity?.Name ?? "administrator";

            target.TwoFactorEnabled            = false;
            target.TwoFactorSecret             = null;
            target.TwoFactorPendingSecret      = null;
            target.TwoFactorRecoveryCodeHashes = null;
            target.TwoFactorEnrolledAt         = null;
            target.TwoFactorChallengeHash      = null;
            target.TwoFactorChallengeExpiresAt = null;
            target.TwoFactorChallengeAttempts  = 0;

            // Sesiunile țintei se revocă: dacă resetul a fost cerut fiindcă
            // telefonul e pierdut, o sesiune activă pe acel telefon e o problemă.
            target.RefreshTokenHash      = null;
            target.RefreshTokenExpiresAt = null;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = CurrentUserId,
                Username  = adminName,
                Action    = AuditAction.UserUpdated,
                Details   = $"2FA resetat administrativ pentru @{target.Username}, sesiuni revocate",
                Result    = AuditResult.Warning,
                IpAddress = Ip,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);

            _logger.LogWarning("2FA resetat administrativ: {Admin} → {Target}", adminName, target.Username);

            return Ok(new
            {
                message = $"2FA a fost dezactivat pentru @{target.Username}. " +
                          "Utilizatorul îl poate reconfigura din profil.",
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        private async Task<bool> VerifyPasswordAsync(User user, string? password, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(password)) return false;

            var result = await _hasher.VerifyPasswordAsync(password, user.PasswordHash, ct);
            return result != PasswordVerificationResult.Failed;
        }

        /// <summary>Acceptă un cod TOTP sau, ca alternativă, un cod de recuperare (care se consumă).</summary>
        private Task<bool> VerifySecondFactorAsync(User user, string? code)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrEmpty(user.TwoFactorSecret))
                return Task.FromResult(false);

            try
            {
                var secret = _protector.Unprotect(user.TwoFactorSecret);
                if (_totp.VerifyCode(secret, code)) return Task.FromResult(true);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "Secretul 2FA al lui {User} nu poate fi descifrat.", user.Username);
            }

            // Cod de recuperare: se consumă chiar dacă operația care urmează e
            // dezactivarea. Consumul e corect — codul a fost folosit.
            if (string.IsNullOrEmpty(user.TwoFactorRecoveryCodeHashes))
                return Task.FromResult(false);

            var candidate = TotpService.HashRecoveryCode(code);

            var hashes = user.TwoFactorRecoveryCodeHashes
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            var index = hashes.FindIndex(h => CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(h),
                System.Text.Encoding.ASCII.GetBytes(candidate)));

            if (index < 0) return Task.FromResult(false);

            hashes.RemoveAt(index);
            user.TwoFactorRecoveryCodeHashes = hashes.Count > 0 ? string.Join(';', hashes) : null;

            return Task.FromResult(true);
        }

        /// <summary>
        /// Adaugă o intrare de audit în contextul curent, fără să salveze.
        /// Rezultatul e parametru explicit, nu prefix de text: filtrarea din
        /// AuditLogsController se face pe coloană, nu pe primele caractere.
        /// </summary>
        private void AddAudit(User user, string details, AuditResult result = AuditResult.Success)
        {
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = user.Username,
                Action    = AuditAction.UserUpdated,
                Details   = details,
                Result    = result,
                IpAddress = Ip,
                Timestamp = DateTime.UtcNow,
            });
        }

        private async Task AuditAsync(
            User user, string details, CancellationToken ct, AuditResult result = AuditResult.Success)
        {
            AddAudit(user, details, result);
            await _context.SaveChangesAsync(ct);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // DTO-uri
    // ═════════════════════════════════════════════════════════════════════════

    public class TwoFactorPasswordDto
    {
        public string Password { get; set; } = string.Empty;
    }

    public class TwoFactorCodeDto
    {
        public string Code { get; set; } = string.Empty;
    }

    public class TwoFactorDisableDto
    {
        public string Password { get; set; } = string.Empty;

        /// <summary>Cod TOTP din aplicație sau cod de recuperare.</summary>
        public string Code { get; set; } = string.Empty;
    }
}