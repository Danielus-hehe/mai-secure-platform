using MAI.Api.Security;
using MAI.Api.Services;
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
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Administrarea conturilor.
    ///
    /// TOATE operațiile de scriere din acest controller sunt exclusiv pentru
    /// Administrator. Versiunea anterioară avea atributul comentat pe POST și
    /// lipsă complet pe /role, /activate și /deactivate: orice utilizator
    /// autentificat, inclusiv rolul Utilizator, putea să-și dea singur rol de
    /// Administrator cu un singur PATCH sau să dezactiveze toți administratorii.
    /// Restul stivei de securitate — Argon2id, 2FA, E2EE, audit — nu apăra de
    /// nimic cât timp promovarea de rol era deschisă tuturor.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _hasher;
        private readonly PasswordPolicy _policy;
        private readonly Argon2Options _argon2;
        private readonly ISessionService _sessions;
        private readonly ILogger<UsersController> _logger;

        public UsersController(
            AppDbContext context,
            IPasswordHasher hasher,
            PasswordPolicy policy,
            Argon2Options argon2,
            ISessionService sessions,
            ILogger<UsersController> logger)
        {
            _context  = context;
            _hasher   = hasher;
            _policy   = policy;
            _argon2   = argon2;
            _sessions = sessions;
            _logger   = logger;
        }

        private string CallerUsername => HttpContext.User.Identity?.Name ?? "sistem";
        private string CallerIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        /// <summary>
        /// Id-ul apelantului. Necesar ca să putem refuza operațiile prin care
        /// un administrator s-ar închide singur în afara sistemului.
        /// </summary>
        private Guid CallerId =>
            Guid.TryParse(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
                ? id
                : Guid.Empty;

        private static readonly Regex EmailRegex =
            new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        /// <summary>Conturile privilegiate primesc profilul Argon2 cu cost mai mare.</summary>
        private string ProfileFor(UserRole role) =>
            role >= UserRole.SefDirectie ? _argon2.PrivilegedProfile : _argon2.DefaultProfile;

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Users — listă completă, doar pentru roluri privilegiate
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Returnează email, rol, stare de blocare și ultima autentificare pentru
        /// tot personalul. Într-o instituție de forță, lista asta este ea însăși
        /// informație sensibilă: arată cine are drepturi de administrator și cine
        /// e blocat chiar acum, adică exact hărțile de care are nevoie cineva care
        /// pregătește un atac de tip phishing intern. Un Utilizator obișnuit are
        /// nevoie doar de /api/Keys/recipients ca să trimită un fișier.
        /// </summary>
        [Authorize(Roles = "Administrator,SefDirectie")]
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
                    Id            = u.Id,
                    Username      = u.Username,
                    Email         = u.Email,
                    FullName      = u.FullName   ?? string.Empty,
                    Department    = u.Department ?? string.Empty,
                    Role          = u.Role,
                    IsActive      = u.IsActive,
                    CreatedAt     = u.CreatedAt,
                    IsLockedOut   = u.LockoutEndsAt.HasValue && u.LockoutEndsAt > DateTime.UtcNow,
                    LockoutEndsAt = u.LockoutEndsAt,
                    LastLoginAt   = u.LastLoginAt,
                })
                .ToListAsync(ct);

            return Ok(PagedResult<UserDto>.Create(users, total, pagination));
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Users/all — directorul intern, pentru dropdown-uri
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Rămâne deschis oricărui utilizator autentificat, dar returnează strict
        /// câmpurile necesare alegerii unui destinatar: fără email, fără rol, fără
        /// stare de blocare, fără ultima autentificare.
        /// </summary>
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

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Users — creare cont, exclusiv Administrator
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Atributul de mai jos era comentat, cu nota „pentru producție
        /// decomentează”. Consecința: orice cont autentificat, inclusiv unul de
        /// rol Utilizator, putea crea un al doilea cont cu Role=Administrator și
        /// să se autentifice imediat cu el. Nu mai este opțional.
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
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

            // Un rol nedefinit (Role=99) ar trece prin binding, ar fi comparat cu
            // >= SefDirectie și ar produce un cont cu un rol pe care niciun
            // [Authorize(Roles=...)] nu îl recunoaște — imposibil de administrat
            // ulterior din interfață.
            if (!Enum.IsDefined(typeof(UserRole), dto.Role))
                return BadRequest(new { message = "Rolul specificat nu există." });

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
                    PasswordHash = await _hasher.HashPasswordAsync(dto.Password, ProfileFor(dto.Role), ct),
                    FullName     = dto.FullName?.Trim()   ?? string.Empty,
                    Department   = dto.Department?.Trim() ?? string.Empty,
                    Role         = dto.Role,
                    IsActive     = true,
                    CreatedAt    = DateTime.UtcNow,
                };

                _context.Users.Add(user);

                AddAudit(user.Id, AuditAction.UserCreated,
                    $"Cont creat @{user.Username} ({user.Role}) - Argon2id/{ProfileFor(dto.Role)}",
                    // Crearea unui cont privilegiat e exact rândul pe care un
                    // ofițer de securitate trebuie să-l găsească filtrând.
                    dto.Role >= UserRole.SefDirectie ? AuditResult.Warning : AuditResult.Success);

                await _context.SaveChangesAsync(ct);

                if (dto.Role >= UserRole.SefDirectie)
                {
                    _logger.LogWarning(
                        "Cont privilegiat creat: {New} ({Role}) de catre {Admin}, IP={Ip}",
                        user.Username, user.Role, CallerUsername, CallerIp);
                }

                return Ok(new { message = $"Contul @{user.Username} a fost creat cu succes.", id = user.Id });
            }
            catch (HashingCapacityExceededException ex)
            {
                Response.Headers.RetryAfter = "5";
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { message = ex.Message, retryAfter = 5 });
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Users/{id}/reset-password
        // ═════════════════════════════════════════════════════════════════════
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

            user.FailedLoginAttempts = 0;
            user.LockoutEndsAt       = null;

            // Revocarea REALĂ a sesiunilor. Versiunea anterioară punea pe null
            // User.RefreshTokenHash — o coloană rămasă din implementarea de
            // dinaintea tabelei UserSessions, pe care nimic nu o mai citește.
            // Efectul practic: o resetare de parolă cerută tocmai fiindcă se
            // bănuia că altcineva are acces lăsa sesiunea aceluia activă încă
            // șapte zile, adică rata exact scenariul pentru care există.
            var closed = await _sessions.RevokeAllAsync(
                user.Id, "resetare administrativa a parolei", exceptSessionId: null, ct);

            // Cheile private E2EE sunt încuiate cu o cheie derivată din parola
            // VECHE, pe care nu o mai știe nimeni. Lăsate pe loc, contul intra
            // într-un impas: descuierea eșua la fiecare autentificare, iar
            // POST /api/Keys refuza chei noi fiindcă „există deja” — utilizatorul
            // nu mai putea nici trimite, nici primi fișiere.
            //
            // Fără key escrow, ștergerea materialului de chei e singura ieșire.
            // Costul se spune explicit: fișierele primite anterior nu mai pot fi
            // deschise de acest cont. Expeditorii le pot redeschide din „Trimise”
            // (au propria copie a cheii de fișier) și le pot retrimite.
            var hadKeys = user.HasKeys;
            if (hadKeys)
            {
                user.PublicKeyEncryption     = null;
                user.PublicKeySigning        = null;
                user.EncryptedPrivateBundle  = null;
                user.KeyDerivationSalt       = null;
                user.KeyDerivationIterations = null;
                user.KeyWrapIv               = null;
                user.CryptoSuite             = null;
                user.KeysCreatedAt           = null;
            }

            AddAudit(user.Id, AuditAction.UserUpdated,
                $"Parola resetata administrativ pentru @{user.Username}, {closed} sesiuni inchise" +
                (hadKeys ? ", chei E2EE invalidate (fisierele primite anterior devin inaccesibile)" : string.Empty),
                AuditResult.Warning);

            await _context.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Parola resetata administrativ: {Admin} → {Target}, {Closed} sesiuni inchise, chei invalidate={KeysInvalidated}",
                CallerUsername, user.Username, closed, hadKeys);

            return Ok(new
            {
                message = hadKeys
                    ? $"Parola contului @{user.Username} a fost resetata. Cheile de criptare au fost " +
                      "invalidate: la urmatoarea autentificare utilizatorul va genera chei noi, iar " +
                      "fisierele primite anterior trebuie retrimise de expeditori."
                    : $"Parola contului @{user.Username} a fost resetata.",
                sessionsClosed  = closed,
                keysInvalidated = hadKeys,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Users/{id}/unlock
        // ═════════════════════════════════════════════════════════════════════
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPost("{id:guid}/unlock")]
        public async Task<IActionResult> Unlock(Guid id, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });

            user.FailedLoginAttempts = 0;
            user.LockoutEndsAt       = null;

            AddAudit(user.Id, AuditAction.UserUpdated, $"Cont deblocat @{user.Username}");
            await _context.SaveChangesAsync(ct);

            return Ok(new { message = $"Contul @{user.Username} a fost deblocat." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PATCH api/Users/{id}/role — exclusiv Administrator
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Endpointul nu avea NICIO restricție de rol. Un utilizator obișnuit își
        /// citea propriul id din token și trimitea PATCH /api/Users/{id-ul lui}/role
        /// cu Role=3. Din acel moment avea acces la jurnalul de audit, la
        /// resetarea parolelor tuturor și la ștergerea oricărui transfer.
        ///
        /// Pe lângă restricția de rol, două protecții structurale: nimeni nu-și
        /// schimbă propriul rol, și ultimul administrator activ nu poate fi
        /// retrogradat — altfel sistemul rămâne fără nicio cale de administrare.
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPatch("{id:guid}/role")]
        public async Task<IActionResult> ChangeRole(
            Guid id, [FromBody] ChangeRoleDto dto, CancellationToken ct)
        {
            if (!Enum.IsDefined(typeof(UserRole), dto.Role))
                return BadRequest(new { message = "Rolul specificat nu există." });

            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });

            // Separarea atribuțiilor: schimbarea propriului rol e singura operație
            // pe care un administrator nu are de ce s-o facă legitim, dar care ar
            // ascunde perfect o escaladare dacă tokenul lui e furat.
            if (user.Id == CallerId)
            {
                AddAudit(user.Id, AuditAction.UserUpdated,
                    "Incercare de schimbare a propriului rol, refuzata", AuditResult.Failure);
                await _context.SaveChangesAsync(ct);

                return BadRequest(new
                {
                    message = "Nu vă puteți modifica propriul rol. " +
                              "Solicitați operația unui alt administrator.",
                });
            }

            if (await WouldRemoveLastAdministratorAsync(user, dto.Role, deactivating: false, ct))
            {
                return BadRequest(new
                {
                    message = "Aceasta este ultima persoană cu rol de Administrator activ. " +
                              "Promovați mai întâi pe altcineva.",
                });
            }

            var old = user.Role;
            if (old == dto.Role)
                return Ok(new { message = $"Rolul @{user.Username} era deja {dto.Role}." });

            user.Role = dto.Role;

            // Promovarea nu re-hash-uiește parola (nu avem parola în clar). Hash-ul
            // urcă la profilul mai scump la următoarea schimbare sau resetare.
            //
            // Retrogradarea închide sesiunile: tokenurile de acces deja emise
            // poartă rolul VECHI în claim-uri și rămân valide până la expirare.
            // Fără revocare, cineva retrogradat păstrează drepturile de
            // administrator încă un sfert de oră.
            var closed = 0;
            if (dto.Role < old)
            {
                closed = await _sessions.RevokeAllAsync(
                    user.Id, "retrogradare de rol", exceptSessionId: null, ct);
            }

            AddAudit(user.Id, AuditAction.UserUpdated,
                $"Rol schimbat @{user.Username}: {old} -> {dto.Role}" +
                (closed > 0 ? $", {closed} sesiuni inchise" : string.Empty),
                AuditResult.Warning);

            await _context.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Rol schimbat de {Admin}: {Target} {Old} -> {New}, IP={Ip}",
                CallerUsername, user.Username, old, dto.Role, CallerIp);

            return Ok(new { message = $"Rolul @{user.Username} a fost actualizat." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PATCH api/Users/{id}/deactivate — exclusiv Administrator
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Fără restricție de rol, orice utilizator putea dezactiva toți
        /// administratorii cu câteva cereri și lăsa instituția fără acces
        /// administrativ până la o intervenție directă în baza de date.
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPatch("{id:guid}/deactivate")]
        public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });

            if (user.Id == CallerId)
                return BadRequest(new { message = "Nu vă puteți dezactiva propriul cont." });

            if (await WouldRemoveLastAdministratorAsync(user, user.Role, deactivating: true, ct))
            {
                return BadRequest(new
                {
                    message = "Aceasta este ultima persoană cu rol de Administrator activ. " +
                              "Promovați mai întâi pe altcineva.",
                });
            }

            if (!user.IsActive)
                return Ok(new { message = $"Contul @{user.Username} era deja dezactivat." });

            user.IsActive = false;

            // Dezactivarea trebuie să omoare sesiunile, nu doar să blocheze
            // autentificările viitoare. Refresh-ul verifică IsActive, dar tokenul
            // de acces deja emis rămâne valid până la expirare — revocarea explicită
            // închide fereastra și lasă urmă în lista de sesiuni.
            var closed = await _sessions.RevokeAllAsync(
                user.Id, "cont dezactivat", exceptSessionId: null, ct);

            AddAudit(user.Id, AuditAction.UserUpdated,
                $"Cont dezactivat @{user.Username}, {closed} sesiuni inchise", AuditResult.Warning);

            await _context.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Cont dezactivat de {Admin}: {Target}, {Closed} sesiuni inchise",
                CallerUsername, user.Username, closed);

            return Ok(new
            {
                message = $"Contul @{user.Username} a fost dezactivat.",
                sessionsClosed = closed,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PATCH api/Users/{id}/activate — exclusiv Administrator
        // ═════════════════════════════════════════════════════════════════════
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPatch("{id:guid}/activate")]
        public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });

            if (user.IsActive)
                return Ok(new { message = $"Contul @{user.Username} era deja activ." });

            user.IsActive = true;

            AddAudit(user.Id, AuditAction.UserUpdated,
                $"Cont activat @{user.Username}", AuditResult.Warning);

            await _context.SaveChangesAsync(ct);

            return Ok(new { message = $"Contul @{user.Username} a fost activat." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// True dacă operația ar lăsa sistemul fără niciun Administrator activ.
        ///
        /// Nu e o măsură de securitate împotriva unui atacator — cine e deja
        /// administrator poate face oricum daune. E o măsură împotriva accidentului:
        /// un sistem fără administrator nu se mai poate repara din interfață, iar
        /// recuperarea cere acces direct la baza de date.
        /// </summary>
        private async Task<bool> WouldRemoveLastAdministratorAsync(
            User target, UserRole newRole, bool deactivating, CancellationToken ct)
        {
            if (target.Role != UserRole.Administrator) return false;
            if (!deactivating && newRole == UserRole.Administrator) return false;
            if (!target.IsActive) return false;

            var otherActiveAdmins = await _context.Users.CountAsync(
                u => u.Role == UserRole.Administrator && u.IsActive && u.Id != target.Id, ct);

            return otherActiveAdmins == 0;
        }

        private void AddAudit(
            Guid? targetUserId, AuditAction action, string details,
            AuditResult result = AuditResult.Success)
        {
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = targetUserId,
                Username  = CallerUsername,
                Action    = action,
                Details   = details,
                Result    = result,
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
        }
    }
}