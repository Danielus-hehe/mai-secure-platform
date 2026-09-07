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
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            AppDbContext context,
            IConfiguration config,
            IPasswordHasher hasher,
            PasswordPolicy policy,
            Argon2Options argon2,
            LockoutOptions lockout,
            ILogger<AuthController> logger)
        {
            _context = context;
            _config  = config;
            _hasher  = hasher;
            _policy  = policy;
            _argon2  = argon2;
            _lockout = lockout;
            _logger  = logger;
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

                // Login reușit: resetăm contorul de eșecuri.
                user.FailedLoginAttempts = 0;
                user.LockoutEndsAt       = null;
                user.LastLoginAt         = DateTime.UtcNow;

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
            var hash = HashRefreshToken(dto.RefreshToken);

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
                var hash = HashRefreshToken(dto.RefreshToken);
                user = await _context.Users.FirstOrDefaultAsync(u => u.RefreshTokenHash == hash, ct);
            }

            if (user is not null)
            {
                user.RefreshTokenHash      = null;
                user.RefreshTokenExpiresAt = null;
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
        // Emitere tokenuri
        // ─────────────────────────────────────────────────────────────────────

        private TokenResponseDto IssueTokens(User user)
        {
            var now            = DateTime.UtcNow;
            var accessExpires  = now.AddMinutes(AccessTokenMinutes);
            var refreshExpires = now.AddDays(RefreshTokenDays);

            var accessToken  = GenerateJwtToken(user, accessExpires);
            var refreshToken = GenerateRefreshToken();

            user.RefreshTokenHash      = HashRefreshToken(refreshToken);
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

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name,           user.Username),
                new Claim(ClaimTypes.Role,           user.Role.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            };

            var token = new JwtSecurityToken(
                claims:             claims,
                expires:            expires,
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private static string GenerateRefreshToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(64);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static string HashRefreshToken(string token)
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
}