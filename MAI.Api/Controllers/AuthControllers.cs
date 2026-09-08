using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MAI.Api.Security;
using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using LoginDto = MAI.DataAccessLayer.DTOs.LoginDto;

namespace MAI.Api.Controllers
{
    [ApiController]
    [Route("api/Auth")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _config;
        private readonly IPasswordHasher _hasher;
        private readonly PasswordPolicy _policy;
        private readonly Argon2Options _argon2;
        private readonly LockoutOptions _lockout;
        private readonly TwoFactorOptions _twoFactor;
        private readonly TotpService _totp;
        private readonly SecretProtector _protector;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            AppDbContext context,
            IConfiguration config,
            IPasswordHasher hasher,
            PasswordPolicy policy,
            Argon2Options argon2,
            LockoutOptions lockout,
            TwoFactorOptions twoFactor,
            TotpService totp,
            SecretProtector protector,
            ILogger<AuthController> logger)
        {
            _context   = context;
            _config    = config;
            _hasher    = hasher;
            _policy    = policy;
            _argon2    = argon2;
            _lockout   = lockout;
            _twoFactor = twoFactor;
            _totp      = totp;
            _protector = protector;
            _logger    = logger;
        }

        private int AccessTokenMinutes =>
            int.TryParse(_config["Jwt:AccessTokenMinutes"], out var m) && m > 0 ? m : 15;

        private int RefreshTokenDays =>
            int.TryParse(_config["Jwt:RefreshTokenDays"], out var d) && d > 0 ? d : 7;

        private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        /// <summary>Profilul Argon2 potrivit rolului: conturile privilegiate primesc cost mai mare.</summary>
        private string ProfileFor(UserRole role) =>
            role >= UserRole.SefDirectie ? _argon2.PrivilegedProfile : _argon2.DefaultProfile;

        // ─────────────────────────────────────────────────────────────────────
        // POST api/Auth/login
        // ─────────────────────────────────────────────────────────────────────
        [EnableRateLimiting(RateLimitPolicies.Login)]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto request, CancellationToken ct)
        {
            var username = request.Username?.Trim() ?? string.Empty;

            // Mesaj identic pentru orice eșec — nu divulgăm dacă userul există sau e blocat.
            const string genericError = "Nume de utilizator sau parolă incorectă.";

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(request.Password))
            {
                await WriteAuditAsync(null, username, AuditAction.Login, "ESEC: Credentiale lipsa");
                return BadRequest(new { message = genericError });
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

            try
            {
                if (user is null)
                {
                    // Consumăm același timp ca o verificare reală, ca să nu se poată enumera
                    // conturile măsurând latența răspunsului.
                    await _hasher.SimulateVerificationAsync(ct);
                    await WriteAuditAsync(null, username, AuditAction.Login, "ESEC: Utilizator inexistent");
                    return BadRequest(new { message = genericError });
                }

                // Contul blocat: NU calculăm hash. Exact ăsta e scopul blocării —
                // un atacator nu trebuie să poată consuma 19 MiB pe încercare la nesfârșit.
                if (user.IsLockedOut)
                {
                    var remaining = (int)Math.Ceiling((user.LockoutEndsAt!.Value - DateTime.UtcNow).TotalSeconds);
                    await WriteAuditAsync(user.Id, username, AuditAction.Login,
                        $"ESEC: Cont blocat, {remaining}s ramase");

                    Response.Headers.RetryAfter = remaining.ToString();
                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        message = genericError,
                        retryAfter = remaining,
                    });
                }

                var verification = await _hasher.VerifyPasswordAsync(request.Password, user.PasswordHash, ct);

                if (verification == PasswordVerificationResult.Failed)
                {
                    await RegisterFailedAttemptAsync(user, ct);
                    return BadRequest(new { message = genericError });
                }

                if (!user.IsActive)
                {
                    await WriteAuditAsync(user.Id, username, AuditAction.Login, "ESEC: Cont dezactivat");
                    return BadRequest(new { message = "Contul este dezactivat. Contactați administratorul." });
                }

                // Migrare transparentă plain text / parametri slabi → profilul potrivit rolului.
                if (verification == PasswordVerificationResult.SuccessRehashNeeded)
                {
                    user.PasswordHash = await _hasher.HashPasswordAsync(request.Password, ProfileFor(user.Role), ct);
                    _logger.LogInformation("Hash parola migrat pentru {Username}.", username);
                    AddAudit(user.Id, username, AuditAction.UserUpdated, "SUCCES: Hash parola migrat la Argon2id");
                }

                // Parola e corectă: contorul de eșecuri se resetează aici, indiferent
                // dacă mai urmează sau nu pasul doi. Altfel, un utilizator cu 2FA
                // activ ar rămâne cu eșecuri vechi neșterse și s-ar bloca aparent
                // din senin la o greșeală ulterioară.
                user.FailedLoginAttempts = 0;
                user.LockoutEndsAt       = null;

                // ── Pasul doi, DOAR dacă utilizatorul l-a activat singur ──────
                //
                // 2FA este opțional. Un cont fără 2FA se autentifică exact ca
                // înainte — comportamentul vechi rămâne calea implicită, iar
                // activarea se face din pagina de profil, de către utilizator.
                if (user.TwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret))
                {
                    var challenge = IssueTwoFactorChallenge(user);

                    AddAudit(user.Id, username, AuditAction.Login,
                        "SUCCES: Parola corecta, se asteapta codul 2FA");

                    await _context.SaveChangesAsync(ct);

                    // 200, nu 401: parola a fost corectă. Autentificarea nu a
                    // eșuat, doar nu s-a încheiat.
                    return Ok(new
                    {
                        twoFactorRequired = true,
                        challengeToken    = challenge.Token,
                        expiresAt         = challenge.ExpiresAt,
                        // Frontend-ul afișează câte coduri de recuperare mai există,
                        // ca utilizatorul să știe dacă are pe ce conta.
                        recoveryAvailable = user.RemainingRecoveryCodes > 0,
                    });
                }

                user.LastLoginAt = DateTime.UtcNow;

                var response = IssueTokens(user);

                AddAudit(user.Id, username, AuditAction.Login, "SUCCES: Autentificare reusita");
                await _context.SaveChangesAsync(ct);

                return Ok(response);
            }
            catch (HashingCapacityExceededException ex)
            {
                return CapacityResponse(ex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST api/Auth/2fa/verify — pasul doi
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Schimbă provocarea emisă de /login pe tokenurile reale, dacă vine
        /// însoțită de un cod TOTP valid sau de un cod de recuperare.
        ///
        /// Provocarea este un token opac cu stare pe server, nu un JWT. Un JWT
        /// „pe jumătate autentificat” riscă mereu să fie acceptat de middleware-ul
        /// de autentificare pe alte rute; un șir aleatoriu verificat manual aici
        /// nu are cum să fie.
        /// </summary>
        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.Login)]
        [HttpPost("2fa/verify")]
        public async Task<IActionResult> VerifyTwoFactor(
            [FromBody] TwoFactorVerifyDto dto, CancellationToken ct)
        {
            const string genericError = "Cod invalid sau sesiune expirată. Autentificați-vă din nou.";

            if (string.IsNullOrWhiteSpace(dto.ChallengeToken) || string.IsNullOrWhiteSpace(dto.Code))
                return Unauthorized(new { message = genericError });

            var hash = HashOpaqueToken(dto.ChallengeToken);

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.TwoFactorChallengeHash == hash, ct);

            if (user is null)
            {
                await WriteAuditAsync(null, "necunoscut", AuditAction.Login,
                    "ESEC: Provocare 2FA invalida sau deja folosita");
                return Unauthorized(new { message = genericError });
            }

            if (user.TwoFactorChallengeExpiresAt is null ||
                user.TwoFactorChallengeExpiresAt <= DateTime.UtcNow)
            {
                ClearChallenge(user);
                await WriteAuditAsync(user.Id, user.Username, AuditAction.Login, "ESEC: Provocare 2FA expirata");
                return Unauthorized(new { message = genericError });
            }

            if (!user.IsActive || user.IsLockedOut)
            {
                ClearChallenge(user);
                await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                    "ESEC: Verificare 2FA pe cont dezactivat sau blocat");
                return Unauthorized(new { message = genericError });
            }

            if (user.TwoFactorChallengeAttempts >= _twoFactor.MaxChallengeAttempts)
            {
                ClearChallenge(user);
                await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                    $"ESEC: Provocare 2FA anulata dupa {_twoFactor.MaxChallengeAttempts} coduri gresite");
                return Unauthorized(new { message = genericError });
            }

            var usedRecoveryCode = false;
            var accepted         = false;

            // Codul TOTP are exact Digits cifre. Orice altceva e tratat ca posibil
            // cod de recuperare — nu-l trimitem degeaba prin HMAC.
            var digitsOnly = new string(dto.Code.Where(char.IsDigit).ToArray());

            if (digitsOnly.Length == _twoFactor.Digits)
            {
                try
                {
                    var secret = _protector.Unprotect(user.TwoFactorSecret!);
                    accepted = _totp.VerifyCode(secret, dto.Code);
                }
                catch (CryptographicException ex)
                {
                    // Secretul nu se mai poate descifra: cheia de cifrare s-a
                    // schimbat sau coloana a fost alterată. Nu are rost să pretindem
                    // că e o greșeală de tastare a utilizatorului.
                    _logger.LogError(ex,
                        "Secretul 2FA al utilizatorului {Username} nu poate fi descifrat.", user.Username);

                    await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                        "ESEC: Secret 2FA indescifrabil");

                    return StatusCode(500, new
                    {
                        message = "Configurarea 2FA a acestui cont nu mai poate fi citită. " +
                                  "Contactați administratorul de sistem.",
                    });
                }
            }

            if (!accepted)
            {
                accepted = TryConsumeRecoveryCode(user, dto.Code);
                usedRecoveryCode = accepted;
            }

            if (!accepted)
            {
                user.TwoFactorChallengeAttempts++;

                var remaining = _twoFactor.MaxChallengeAttempts - user.TwoFactorChallengeAttempts;

                AddAudit(user.Id, user.Username, AuditAction.Login,
                    $"ESEC: Cod 2FA incorect ({user.TwoFactorChallengeAttempts}/{_twoFactor.MaxChallengeAttempts})");

                await _context.SaveChangesAsync(ct);

                return Unauthorized(new
                {
                    message = remaining > 0
                        ? $"Cod incorect. Mai aveți {remaining} încercări."
                        : genericError,
                    attemptsRemaining = Math.Max(remaining, 0),
                });
            }

            // Provocarea se consumă indiferent de ce urmează: un token de unică
            // folosință care rămâne valid după utilizare nu mai e de unică folosință.
            ClearChallenge(user);

            user.LastLoginAt = DateTime.UtcNow;

            var response = IssueTokens(user);

            AddAudit(user.Id, user.Username, AuditAction.Login,
                usedRecoveryCode
                    ? $"SUCCES: Autentificare cu COD DE RECUPERARE 2FA ({user.RemainingRecoveryCodes} ramase)"
                    : "SUCCES: Autentificare reusita cu 2FA");

            await _context.SaveChangesAsync(ct);

            if (usedRecoveryCode)
            {
                _logger.LogWarning(
                    "Utilizatorul {Username} s-a autentificat cu un cod de recuperare. Ramase: {Left}",
                    user.Username, user.RemainingRecoveryCodes);
            }

            return Ok(new
            {
                response.Id,
                response.Username,
                response.FullName,
                response.Department,
                response.Role,
                response.AccessToken,
                response.Token,
                response.RefreshToken,
                response.ExpiresIn,
                response.AccessTokenExpiresAt,
                response.RefreshTokenExpiresAt,
                usedRecoveryCode,
                remainingRecoveryCodes = user.RemainingRecoveryCodes,
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST api/Auth/refresh
        // ─────────────────────────────────────────────────────────────────────
        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.Refresh)]
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequestDto dto, CancellationToken ct)
        {
            const string genericError = "Sesiune invalidă sau expirată. Autentificați-vă din nou.";

            if (string.IsNullOrWhiteSpace(dto.RefreshToken))
                return Unauthorized(new { message = genericError });

            // SHA-256, nu Argon2: tokenul are 512 biți de entropie generată criptografic,
            // deci nu există atac prin dicționar, iar refresh-ul trebuie să fie ieftin.
            // Dacă am folosi Argon2 aici, /refresh ar deveni un vector de DoS mai bun decât /login.
            var hash = HashOpaqueToken(dto.RefreshToken);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.RefreshTokenHash == hash, ct);

            if (user is null)
            {
                await WriteAuditAsync(null, "necunoscut", AuditAction.Login,
                    "ESEC: Refresh token invalid sau deja folosit");
                return Unauthorized(new { message = genericError });
            }

            if (user.RefreshTokenExpiresAt is null || user.RefreshTokenExpiresAt <= DateTime.UtcNow)
            {
                user.RefreshTokenHash      = null;
                user.RefreshTokenExpiresAt = null;
                await WriteAuditAsync(user.Id, user.Username, AuditAction.Login, "ESEC: Refresh token expirat");
                return Unauthorized(new { message = genericError });
            }

            if (!user.IsActive || user.IsLockedOut)
            {
                user.RefreshTokenHash      = null;
                user.RefreshTokenExpiresAt = null;
                await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                    "ESEC: Refresh pe cont dezactivat sau blocat");
                return Unauthorized(new { message = genericError });
            }

            // Rotație: tokenul vechi devine invalid în momentul emiterii celui nou.
            var response = IssueTokens(user);

            AddAudit(user.Id, user.Username, AuditAction.Login, "SUCCES: Token reimprospatat");
            await _context.SaveChangesAsync(ct);

            return Ok(response);
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST api/Auth/logout
        // ─────────────────────────────────────────────────────────────────────
        [AllowAnonymous]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] RefreshTokenRequestDto? dto, CancellationToken ct)
        {
            User? user = null;

            var userIdStr = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdStr is not null && Guid.TryParse(userIdStr, out var userId))
                user = await _context.Users.FindAsync(new object?[] { userId }, ct);

            if (user is null && !string.IsNullOrWhiteSpace(dto?.RefreshToken))
            {
                var hash = HashOpaqueToken(dto.RefreshToken);
                user = await _context.Users.FirstOrDefaultAsync(u => u.RefreshTokenHash == hash, ct);
            }

            if (user is not null)
            {
                user.RefreshTokenHash      = null;
                user.RefreshTokenExpiresAt = null;

                // O provocare 2FA rămasă în aer nu are ce căuta după delogare.
                ClearChallenge(user);

                AddAudit(user.Id, user.Username, AuditAction.Logout, "SUCCES: Delogare, refresh token revocat");
                await _context.SaveChangesAsync(ct);
            }

            return Ok(new { message = "Sesiune încheiată." });
        }

        // ─────────────────────────────────────────────────────────────────────
        // PATCH api/Auth/change-password
        // ─────────────────────────────────────────────────────────────────────
        [Authorize]
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPatch("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto, CancellationToken ct)
        {
            var userIdStr = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdStr is null || !Guid.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _context.Users.FindAsync(new object?[] { userId }, ct);
            if (user is null) return NotFound();

            try
            {
                var verification = await _hasher.VerifyPasswordAsync(dto.CurrentPassword, user.PasswordHash, ct);
                if (verification == PasswordVerificationResult.Failed)
                {
                    await WriteAuditAsync(user.Id, user.Username, AuditAction.UserUpdated,
                        "ESEC: Schimbare parola - parola curenta incorecta");
                    return BadRequest(new { message = "Parola curentă este incorectă." });
                }

                var validation = _policy.Validate(dto.NewPassword, user.Username);
                if (!validation.IsValid)
                    return BadRequest(new { message = validation.Message, errors = validation.Errors });

                var sameAsOld = await _hasher.VerifyPasswordAsync(dto.NewPassword, user.PasswordHash, ct);
                if (sameAsOld != PasswordVerificationResult.Failed)
                    return BadRequest(new { message = "Parola nouă trebuie să fie diferită de cea curentă." });

                // Profilul scump: schimbarea de parolă e o operație rară, își permite costul.
                user.PasswordHash = await _hasher.HashPasswordAsync(dto.NewPassword, _argon2.PrivilegedProfile, ct);

                // Schimbarea parolei invalidează sesiunile de pe alte dispozitive.
                user.RefreshTokenHash      = null;
                user.RefreshTokenExpiresAt = null;

                // Atenție: secretul 2FA NU se atinge. Este independent de parolă —
                // exact ăsta e rostul celui de-al doilea factor. Dacă l-am reseta
                // aici, o schimbare de parolă ar dezactiva pe tăcute protecția.
                ClearChallenge(user);

                AddAudit(user.Id, user.Username, AuditAction.UserUpdated,
                    "SUCCES: Parola schimbata (Argon2id), sesiuni revocate");

                await _context.SaveChangesAsync(ct);
                return Ok(new { message = "Parola a fost actualizată cu succes." });
            }
            catch (HashingCapacityExceededException ex)
            {
                return CapacityResponse(ex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Blocare progresivă pe cont
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Incrementează contorul de eșecuri și blochează contul după MaxFailedAttempts.
        /// Durata crește exponențial la blocări repetate, plafonată la MaxLockoutMinutes.
        /// </summary>
        private async Task RegisterFailedAttemptAsync(User user, CancellationToken ct)
        {
            // Fereastră glisantă: dacă ultima greșeală e veche, pornim contorul de la zero.
            if (user.LastFailedLoginAt is { } last &&
                (DateTime.UtcNow - last).TotalMinutes > _lockout.AttemptWindowMinutes)
            {
                user.FailedLoginAttempts = 0;
            }

            user.FailedLoginAttempts++;
            user.LastFailedLoginAt = DateTime.UtcNow;

            var details = $"ESEC: Parola incorecta ({user.FailedLoginAttempts}/{_lockout.MaxFailedAttempts})";

            if (user.FailedLoginAttempts >= _lockout.MaxFailedAttempts)
            {
                var over     = user.FailedLoginAttempts - _lockout.MaxFailedAttempts;
                var minutes  = _lockout.BaseLockoutMinutes * Math.Pow(2, Math.Min(over, 10));
                var capped   = Math.Min(minutes, _lockout.MaxLockoutMinutes);

                user.LockoutEndsAt = DateTime.UtcNow.AddMinutes(capped);
                details = $"ESEC: Cont blocat {capped:0} minute dupa {user.FailedLoginAttempts} incercari";

                _logger.LogWarning("Cont blocat: {Username}, IP={Ip}, {Minutes} minute",
                    user.Username, Ip, capped);
            }

            AddAudit(user.Id, user.Username, AuditAction.Login, details);
            await _context.SaveChangesAsync(ct);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Provocarea 2FA
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Emite o provocare de unică folosință. În baza de date ajunge doar
        /// hash-ul, ca și în cazul refresh token-ului: un dump al bazei nu trebuie
        /// să conțină nimic care să poată fi rejucat.
        /// </summary>
        private (string Token, DateTime ExpiresAt) IssueTwoFactorChallenge(User user)
        {
            var token     = GenerateOpaqueToken(32);
            var expiresAt = DateTime.UtcNow.AddSeconds(_twoFactor.ChallengeLifetimeSeconds);

            user.TwoFactorChallengeHash        = HashOpaqueToken(token);
            user.TwoFactorChallengeExpiresAt   = expiresAt;
            user.TwoFactorChallengeAttempts    = 0;

            return (token, expiresAt);
        }

        private static void ClearChallenge(User user)
        {
            user.TwoFactorChallengeHash      = null;
            user.TwoFactorChallengeExpiresAt = null;
            user.TwoFactorChallengeAttempts  = 0;
        }

        /// <summary>
        /// Verifică un cod de recuperare și, dacă e valid, îl șterge din listă.
        ///
        /// Consumarea e obligatorie: un cod de recuperare refolosibil e o a doua
        /// parolă permanentă, scrisă pe hârtie.
        /// </summary>
        private static bool TryConsumeRecoveryCode(User user, string code)
        {
            if (string.IsNullOrEmpty(user.TwoFactorRecoveryCodeHashes)) return false;

            var candidate = TotpService.HashRecoveryCode(code);

            var hashes = user.TwoFactorRecoveryCodeHashes
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            // Comparație în timp constant peste toată lista, fără ieșire devreme.
            var matchIndex = -1;
            for (var i = 0; i < hashes.Count; i++)
            {
                var equal = CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hashes[i]),
                    Encoding.ASCII.GetBytes(candidate));

                if (equal) matchIndex = i;
            }

            if (matchIndex < 0) return false;

            hashes.RemoveAt(matchIndex);
            user.TwoFactorRecoveryCodeHashes = hashes.Count > 0 ? string.Join(';', hashes) : null;

            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Emitere tokenuri
        // ─────────────────────────────────────────────────────────────────────

        private TokenResponseDto IssueTokens(User user)
        {
            var now            = DateTime.UtcNow;
            var accessExpires  = now.AddMinutes(AccessTokenMinutes);
            var refreshExpires = now.AddDays(RefreshTokenDays);

            var accessToken  = GenerateJwtToken(user, accessExpires);
            var refreshToken = GenerateOpaqueToken(64);

            user.RefreshTokenHash      = HashOpaqueToken(refreshToken);
            user.RefreshTokenIssuedAt  = now;
            user.RefreshTokenExpiresAt = refreshExpires;

            return new TokenResponseDto
            {
                Id                    = user.Id,
                Username              = user.Username,
                FullName              = user.FullName ?? user.Username,
                Department            = user.Department ?? string.Empty,
                Role                  = user.Role,
                AccessToken           = accessToken,
                Token                 = accessToken,   // compatibilitate cu frontend-ul existent
                RefreshToken          = refreshToken,
                ExpiresIn             = AccessTokenMinutes * 60,
                AccessTokenExpiresAt  = accessExpires,
                RefreshTokenExpiresAt = refreshExpires,
            };
        }

        private string GenerateJwtToken(User user, DateTime expires)
        {
            var jwtKey = _config["Jwt:Key"]
                ?? throw new InvalidOperationException("Jwt:Key lipsește din appsettings!");

            var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name,           user.Username),
                new(ClaimTypes.Role,           user.Role.ToString()),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),

                // "amr" (authentication methods references), RFC 8176. Consemnează
                // cu ce a fost obținut tokenul. Nu schimbă nimic azi, dar face
                // posibil mai târziu ca operațiile sensibile să ceară "mfa".
                new("amr", user.TwoFactorEnabled ? "mfa" : "pwd"),
            };

            var token = new JwtSecurityToken(
                claims:             claims,
                expires:            expires,
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>Token opac base64url, fără padding. Folosit pentru refresh și pentru provocarea 2FA.</summary>
        private static string GenerateOpaqueToken(int byteLength)
        {
            var bytes = RandomNumberGenerator.GetBytes(byteLength);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static string HashOpaqueToken(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private IActionResult CapacityResponse(HashingCapacityExceededException ex)
        {
            _logger.LogWarning("Capacitate hashing depasita: IP={Ip}", Ip);
            Response.Headers.RetryAfter = "5";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = ex.Message,
                retryAfter = 5,
            });
        }

        private void AddAudit(Guid? userId, string username, AuditAction action, string details)
        {
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = username,
                Action    = action,
                Details   = details,
                IpAddress = Ip,
                Timestamp = DateTime.UtcNow,
            });
        }

        private async Task WriteAuditAsync(Guid? userId, string username, AuditAction action, string details)
        {
            AddAudit(userId, username, action, details);
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>Corpul cererii POST /api/Auth/2fa/verify.</summary>
    public class TwoFactorVerifyDto
    {
        /// <summary>Provocarea primită de la /login.</summary>
        public string ChallengeToken { get; set; } = string.Empty;

        /// <summary>Codul din aplicația de autentificare SAU un cod de recuperare.</summary>
        public string Code { get; set; } = string.Empty;
    }
}