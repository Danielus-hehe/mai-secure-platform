using MAI.Api.Security;
using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace MAI.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _hasher;
        private readonly PasswordPolicy _policy;
        private readonly Argon2Options _argon2;

        public UsersController(
            AppDbContext context, IPasswordHasher hasher, PasswordPolicy policy, Argon2Options argon2)
        {
            _context = context;
            _hasher  = hasher;
            _policy  = policy;
            _argon2  = argon2;
        }

        private string CallerUsername => HttpContext.User.Identity?.Name ?? "sistem";
        private string CallerIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        private static readonly Regex EmailRegex =
            new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        /// <summary>Conturile privilegiate primesc profilul Argon2 cu cost mai mare.</summary>
        private string ProfileFor(UserRole role) =>
            role >= UserRole.SefDirectie ? _argon2.PrivilegedProfile : _argon2.DefaultProfile;

        // GET api/Users?search=&role=&isActive=&page=&pageSize=
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? search,
            [FromQuery] UserRole? role,
            [FromQuery] bool? isActive,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            CancellationToken ct = default)
        {
            var pagination = new PaginationQuery { Page = page, PageSize = pageSize };

            var query = _context.Users.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = $"%{search.Trim()}%";
                query = query.Where(u =>
                    EF.Functions.ILike(u.Username, term) ||
                    EF.Functions.ILike(u.Email, term) ||
                    (u.FullName != null && EF.Functions.ILike(u.FullName, term)) ||
                    (u.Department != null && EF.Functions.ILike(u.Department, term)));
            }

            if (role.HasValue)
                query = query.Where(u => u.Role == role.Value);

            if (isActive.HasValue)
                query = query.Where(u => u.IsActive == isActive.Value);

            var total = await query.CountAsync(ct);

            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip(pagination.Skip)
                .Take(pagination.PageSize)
                .Select(u => new UserDto
                {
                    Id         = u.Id,
                    Username   = u.Username,
                    Email      = u.Email,
                    FullName   = u.FullName   ?? string.Empty,
                    Department = u.Department ?? string.Empty,
                    Role       = u.Role,
                    IsActive   = u.IsActive,
                    CreatedAt  = u.CreatedAt,
                    IsLockedOut   = u.LockoutEndsAt.HasValue && u.LockoutEndsAt > DateTime.UtcNow,
                    LockoutEndsAt = u.LockoutEndsAt,
                    LastLoginAt   = u.LastLoginAt,
                })
                .ToListAsync(ct);

            return Ok(PagedResult<UserDto>.Create(users, total, pagination));
        }

        // GET api/Users/all — listă completă fără paginare, pentru dropdown-uri
        // (ex: selectarea destinatarului la un transfer). Doar câmpurile minime.
        [HttpGet("all")]
        public async Task<IActionResult> GetAllForDropdown(CancellationToken ct)
        {
            var users = await _context.Users
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.FullName ?? u.Username)
                .Select(u => new
                {
                    id         = u.Id,
                    username   = u.Username,
                    fullName   = u.FullName ?? u.Username,
                    department = u.Department ?? string.Empty,
                })
                .ToListAsync(ct);
            return Ok(users);
        }

        // POST api/Users
        // NOTĂ: pentru producție decomentează linia de mai jos ca doar adminii să creeze conturi.
        // [Authorize(Roles = nameof(UserRole.Administrator))]
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPost]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserDto dto, CancellationToken ct)
        {
            var username = dto.Username?.Trim() ?? string.Empty;
            var email    = dto.Email?.Trim()    ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username))
                return BadRequest(new { message = "Username-ul este obligatoriu." });

            if (username.Length < 3 || username.Length > 50)
                return BadRequest(new { message = "Username-ul trebuie să aibă între 3 și 50 de caractere." });

            if (!string.IsNullOrEmpty(email) && !EmailRegex.IsMatch(email))
                return BadRequest(new { message = "Adresa de email nu este validă." });

            var validation = _policy.Validate(dto.Password, username);
            if (!validation.IsValid)
                return BadRequest(new { message = validation.Message, errors = validation.Errors });

            if (await _context.Users.AnyAsync(u => u.Username == username, ct))
                return Conflict(new { message = $"Username-ul '{username}' exista deja." });

            if (!string.IsNullOrEmpty(email) && await _context.Users.AnyAsync(u => u.Email == email, ct))
                return Conflict(new { message = $"Adresa de email '{email}' este deja folosita." });

            try
            {
                var user = new User
                {
                    Id           = Guid.NewGuid(),
                    Username     = username,
                    Email        = email,
                    // Crearea de cont e rară → profilul scump, indiferent de rol.
                    PasswordHash = await _hasher.HashPasswordAsync(dto.Password, ProfileFor(dto.Role), ct),
                    FullName     = dto.FullName?.Trim()   ?? string.Empty,
                    Department   = dto.Department?.Trim() ?? string.Empty,
                    Role         = dto.Role,
                    IsActive     = true,
                    CreatedAt    = DateTime.UtcNow,
                };

                _context.Users.Add(user);
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId    = user.Id,
                    Username  = CallerUsername,
                    Action    = AuditAction.UserCreated,
                    Details   = $"SUCCES: Cont creat @{user.Username} ({user.Role}) - Argon2id/{ProfileFor(dto.Role)}",
                    IpAddress = CallerIp,
                    Timestamp = DateTime.UtcNow,
                });
                await _context.SaveChangesAsync(ct);
                return Ok(new { message = $"Contul @{user.Username} a fost creat cu succes.", id = user.Id });
            }
            catch (HashingCapacityExceededException ex)
            {
                Response.Headers.RetryAfter = "5";
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { message = ex.Message, retryAfter = 5 });
            }
        }

        // POST api/Users/{id}/reset-password  — doar administrator
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPost("{id:guid}/reset-password")]
        public async Task<IActionResult> ResetPassword(
            Guid id, [FromBody] ResetPasswordDto dto, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });

            var validation = _policy.Validate(dto.NewPassword, user.Username);
            if (!validation.IsValid)
                return BadRequest(new { message = validation.Message, errors = validation.Errors });

            try
            {
                user.PasswordHash = await _hasher.HashPasswordAsync(dto.NewPassword, ProfileFor(user.Role), ct);
            }
            catch (HashingCapacityExceededException ex)
            {
                Response.Headers.RetryAfter = "5";
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { message = ex.Message, retryAfter = 5 });
            }

            // Resetarea administrativă deblochează contul și revocă sesiunile existente.
            user.FailedLoginAttempts   = 0;
            user.LockoutEndsAt         = null;
            user.RefreshTokenHash      = null;
            user.RefreshTokenExpiresAt = null;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"SUCCES: Parola resetata administrativ pentru @{user.Username}, sesiuni revocate",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(ct);

            return Ok(new { message = $"Parola contului @{user.Username} a fost resetata." });
        }

        // POST api/Users/{id}/unlock — deblochează un cont blocat de prea multe încercări
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPost("{id:guid}/unlock")]
        public async Task<IActionResult> Unlock(Guid id, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });

            user.FailedLoginAttempts = 0;
            user.LockoutEndsAt       = null;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"SUCCES: Cont deblocat @{user.Username}",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(ct);

            return Ok(new { message = $"Contul @{user.Username} a fost deblocat." });
        }

        // POST api/Users/migrate-passwords — doar administrator, se rulează o singură dată
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPost("migrate-passwords")]
        public async Task<IActionResult> MigratePasswords(CancellationToken ct)
        {
            var legacyUsers = await _context.Users
                .Where(u => u.PasswordHash != null && !u.PasswordHash.StartsWith("$argon2"))
                .ToListAsync(ct);

            // Secvențial, nu în paralel: fiecare hash consumă memoria profilului, iar
            // semaforul din hasher ar serializa oricum. Paralelizarea ar doborî serverul
            // la o migrare cu multe conturi.
            foreach (var u in legacyUsers)
            {
                // Valoarea stocată ESTE parola în clar — o hash-uim direct.
                u.PasswordHash = await _hasher.HashPasswordAsync(u.PasswordHash, ProfileFor(u.Role), ct);
            }

            if (legacyUsers.Count > 0)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    Username  = CallerUsername,
                    Action    = AuditAction.UserUpdated,
                    Details   = $"SUCCES: Migrare parole plain text -> Argon2id ({legacyUsers.Count} conturi)",
                    IpAddress = CallerIp,
                    Timestamp = DateTime.UtcNow,
                });
                await _context.SaveChangesAsync(ct);
            }

            return Ok(new
            {
                message   = $"{legacyUsers.Count} parole au fost migrate la Argon2id.",
                migrated  = legacyUsers.Count,
                usernames = legacyUsers.Select(u => u.Username).ToList(),
            });
        }

        // PATCH api/Users/{id}/role
        [HttpPatch("{id:guid}/role")]
        public async Task<IActionResult> ChangeRole(Guid id, [FromBody] ChangeRoleDto dto, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });
            var old = user.Role;
            user.Role = dto.Role;

            // Promovarea la un rol privilegiat nu re-hash-uiește parola aici (nu avem parola
            // în clar). Hash-ul se ridică la profilul mai scump la următoarea schimbare de
            // parolă sau resetare administrativă.

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"SUCCES: Rol schimbat @{user.Username}: {old} -> {dto.Role}",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(ct);
            return Ok(new { message = $"Rolul @{user.Username} a fost actualizat." });
        }

        // PATCH api/Users/{id}/deactivate
        [HttpPatch("{id:guid}/deactivate")]
        public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });
            user.IsActive = false;

            // Dezactivarea trebuie să omoare imediat sesiunile, nu să aștepte expirarea JWT.
            user.RefreshTokenHash      = null;
            user.RefreshTokenExpiresAt = null;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"SUCCES: Cont dezactivat @{user.Username}, sesiuni revocate",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(ct);
            return Ok(new { message = $"Contul @{user.Username} a fost dezactivat." });
        }

        // PATCH api/Users/{id}/activate
        [HttpPatch("{id:guid}/activate")]
        public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });
            user.IsActive = true;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"SUCCES: Cont activat @{user.Username}",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(ct);
            return Ok(new { message = $"Contul @{user.Username} a fost activat." });
        }
    }
}