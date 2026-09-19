using MAI.Api.Security;
using MAI.Api.Services;
using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Organization;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
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
    /// Restul stivei de securitate - Argon2id, 2FA, E2EE, audit - nu apăra de
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
        private readonly IInvitationService _invitation;
        private readonly IInvitationDispatcher _invitationDispatcher;
        private readonly IEmailService _email;
        private readonly IPasswordResetService _passwordReset;
        private readonly ILogger<UsersController> _logger;

        public UsersController(
            AppDbContext context,
            IPasswordHasher hasher,
            PasswordPolicy policy,
            Argon2Options argon2,
            ISessionService sessions,
            IInvitationService invitation,
            IInvitationDispatcher invitationDispatcher,
            IEmailService email,
            IPasswordResetService passwordReset,
            ILogger<UsersController> logger)
        {
            _context              = context;
            _hasher               = hasher;
            _policy               = policy;
            _argon2               = argon2;
            _sessions             = sessions;
            _invitation           = invitation;
            _invitationDispatcher = invitationDispatcher;
            _email                = email;
            _passwordReset        = passwordReset;
            _logger               = logger;
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

        /// <summary>
        /// Litere și cifre latine, punct, liniuță, underscore; 3–50 de caractere.
        ///
        /// Fără restricția de alfabet, două conturi puteau arăta identic în lista
        /// de destinatari folosind litere asemănătoare din alte alfabete (de ex.
        /// „а” chirilic în loc de „a” latin): cineva putea primi fișiere destinate
        /// altcuiva. Majusculele sunt permise, dar unicitatea se verifică fără
        /// diferență între ele (vezi migrarea CaseInsensitiveUserIndexes).
        /// </summary>
        private static readonly Regex UsernameRegex =
            new(@"^[A-Za-z0-9._-]{3,50}$", RegexOptions.Compiled);

        /// <summary>Codul PostgreSQL pentru încălcarea unui index unic.</summary>
        private const string UniqueViolation = "23505";

        /// <summary>Conturile privilegiate primesc profilul Argon2 cu cost mai mare.</summary>
        private string ProfileFor(UserRole role) =>
            role >= UserRole.SefDirectie ? _argon2.PrivilegedProfile : _argon2.DefaultProfile;

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Users - listă completă, doar pentru roluri privilegiate
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
            [FromQuery] Guid? orgUnitId,
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
                    (u.OrgUnit != null && EF.Functions.ILike(u.OrgUnit.Name, term)));
            }

            // Filtru pe subdiviziune, cu tot cu subunitățile ei: „cine lucrează
            // în Direcția X” include și secțiile direcției.
            if (orgUnitId.HasValue)
            {
                var tree  = await OrgStructure.LoadTreeAsync(_context, ct);
                var units = tree.Subtree(orgUnitId.Value).Select(u => u.Id).ToList();
                query = query.Where(u => u.OrgUnitId != null && units.Contains(u.OrgUnitId.Value));
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
                    Department    = u.OrgUnit != null ? u.OrgUnit.Name : string.Empty,
                    OrgUnitId     = u.OrgUnitId,
                    LedOrgUnitId   = _context.OrgUnits.Where(o => o.HeadUserId == u.Id).Select(o => (Guid?)o.Id).FirstOrDefault(),
                    LedOrgUnitName = _context.OrgUnits.Where(o => o.HeadUserId == u.Id).Select(o => o.Name).FirstOrDefault(),
                    HasEmail      = u.Email != "",
                    Role          = u.Role,
                    IsActive       = u.IsActive,
                    EmailConfirmed = u.EmailConfirmed,
                    CreatedAt      = u.CreatedAt,
                    IsLockedOut   = u.LockoutEndsAt.HasValue && u.LockoutEndsAt > DateTime.UtcNow,
                    LockoutEndsAt = u.LockoutEndsAt,
                    LastLoginAt   = u.LastLoginAt,
                })
                .ToListAsync(ct);

            return Ok(PagedResult<UserDto>.Create(users, total, pagination));
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Users/all - directorul intern, pentru dropdown-uri
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
                    department = u.OrgUnit != null ? u.OrgUnit.Name : string.Empty,
                    orgUnitId  = u.OrgUnitId,
                })
                .ToListAsync(ct);

            return Ok(users);
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Users/search?q= - autocomplete pentru forward / share
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Caută utilizatori activi care și-au generat cheile (pot primi fișiere
        /// criptate). Folosit de dialogul de forward pentru autocomplete.
        ///
        /// Returnează maxim 10 rezultate, exclude apelantul și include cheia
        /// publică de criptare - browserul o folosește direct ca să împacheteze
        /// DEK-ul fără un apel suplimentar.
        ///
        /// Cheia publică este publică prin definiție: scopul ei este să fie
        /// distribuită. Nu există date private în răspuns.
        /// </summary>
        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
        {
            var callerId = CallerId;

            // Minim 1 caracter: fără asta, un apel accidental fără query ar returna
            // o listă completă de utilizatori cu cheile lor publice.
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 1)
                return Ok(Array.Empty<object>());

            var term = $"%{q.Trim()}%";

            var results = await _context.Users
                .AsNoTracking()
                .Where(u => u.IsActive
                         && u.Id != callerId
                         && u.PublicKeyEncryption != null
                         && (EF.Functions.ILike(u.Username, term)
                          || (u.FullName   != null && EF.Functions.ILike(u.FullName,   term))
                          || (u.OrgUnit != null && EF.Functions.ILike(u.OrgUnit.Name, term))))
                .OrderBy(u => u.FullName ?? u.Username)
                .Take(10)
                .Select(u => new
                {
                    id                  = u.Id,
                    username            = u.Username,
                    fullName            = u.FullName   ?? u.Username,
                    department          = u.OrgUnit != null ? u.OrgUnit.Name : string.Empty,
                    // Cheia publică e necesară în browser ca să împacheteze DEK-ul
                    // fără un al doilea round-trip. Cheile publice sunt publice.
                    publicKeyEncryption = u.PublicKeyEncryption,
                })
                .ToListAsync(ct);

            return Ok(results);
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Users - creare cont, exclusiv Administrator
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Creează un cont cu o parolă inițială aleasă de administrator.
        ///
        /// Contul pornește cu MustChangePassword = true. Parola inițială e
        /// cunoscută de administrator și, adesea, trimisă pe un canal nesigur
        /// (chat, hârtie). Dacă utilizatorul și-ar genera cheile E2EE cu ea,
        /// administratorul le-ar putea descuia oricând din baza de date. Serverul
        /// refuză deci înregistrarea cheilor până la prima schimbare de parolă.
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

            if (!UsernameRegex.IsMatch(username))
                return BadRequest(new
                {
                    message = "Username-ul trebuie să aibă 3–50 de caractere: litere fără diacritice, " +
                              "cifre, punct, liniuță sau underscore.",
                });

            // Un rol nedefinit (Role=99) ar trece prin binding, ar fi comparat cu
            // >= SefDirectie și ar produce un cont cu un rol pe care niciun
            // [Authorize(Roles=...)] nu îl recunoaște - imposibil de administrat
            // ulterior din interfață.
            if (!Enum.IsDefined(typeof(UserRole), dto.Role))
                return BadRequest(new { message = "Rolul specificat nu există." });

            if (!string.IsNullOrEmpty(email) && !EmailRegex.IsMatch(email))
                return BadRequest(new { message = "Adresa de email nu este validă." });

            var validation = _policy.Validate(dto.Password, username);
            if (!validation.IsValid)
                return BadRequest(new { message = validation.Message, errors = validation.Errors });

            if (dto.OrgUnitId.HasValue &&
                !await _context.OrgUnits.AnyAsync(o => o.Id == dto.OrgUnitId && o.IsActive, ct))
                return BadRequest(new { message = "Subdiviziunea aleasă nu există sau este desființată." });

            // Aceeași regulă ca indexurile unice din baza de date: fără diferență
            // între majuscule și minuscule. Verificarea de aici dă un mesaj clar;
            // indexul rămâne garanția, pentru două cereri simultane.
            var usernameKey = username.ToLowerInvariant();
            if (await _context.Users.AnyAsync(u => u.Username.ToLower() == usernameKey, ct))
                return Conflict(new { message = $"Username-ul '{username}' există deja." });

            // Emailul e opțional. Mai multe conturi fără email sunt permise: indexul
            // unic pe email ignoră valorile goale.
            if (!string.IsNullOrEmpty(email))
            {
                var emailKey = email.ToLowerInvariant();
                if (await _context.Users.AnyAsync(u => u.Email.ToLower() == emailKey, ct))
                    return Conflict(new { message = $"Adresa de email '{email}' este deja folosită." });
            }

            // Invitația prin email se folosește doar dacă are pe unde să plece.
            // Fără SMTP configurat, un cont cu EmailConfirmed=false nu s-ar mai
            // putea autentifica niciodată: login-ul îl refuză, iar linkul de
            // activare nu ajunge la nimeni. În cazul ăsta contul pornește pe
            // fluxul clasic: confirmat, cu parolă temporară și schimbare forțată.
            var useInvitation = !string.IsNullOrEmpty(email) && _email.IsConfigured;

            try
            {
                var user = new User
                {
                    Id                 = Guid.NewGuid(),
                    Username           = username,
                    Email              = email,
                    PasswordHash       = await _hasher.HashPasswordAsync(dto.Password, ProfileFor(dto.Role), ct),
                    FullName           = dto.FullName?.Trim()   ?? string.Empty,
                    OrgUnitId          = dto.OrgUnitId,
                    Role               = dto.Role,
                    IsActive           = true,
                    // Cu invitație: contul pornește neconfirmat, userul îl activează
                    // din linkul primit și își setează singur parola.
                    // Fără invitație (fără email sau fără SMTP): fluxul clasic -
                    // cont confirmat, schimbare forțată a parolei temporare.
                    EmailConfirmed     = !useInvitation,
                    CreatedAt          = DateTime.UtcNow,
                    MustChangePassword = true,
                };

                _context.Users.Add(user);

                AddAudit(user.Id, AuditAction.UserCreated,
                    $"Cont creat @{user.Username} ({user.Role}) - Argon2id/{ProfileFor(dto.Role)}, " +
                    "parola initiala temporara",
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

                // Invitația pleacă în fundal, cu scope DI propriu (vezi
                // InvitationDispatcher). Contul e deja salvat; un eșec SMTP nu
                // anulează crearea, ci apare în audit și se poate retrimite.
                if (useInvitation)
                {
                    _invitationDispatcher.Enqueue(user.Id, user.Username);

                    return Ok(new
                    {
                        message = $"Contul @{user.Username} a fost creat. Emailul de activare " +
                                  "se trimite acum la adresa furnizată. Dacă nu ajunge, îl puteți " +
                                  "retrimite din lista de utilizatori.",
                        id                 = user.Id,
                        mustChangePassword = true,
                        // „programată”, nu „trimisă”: trimiterea reală se confirmă
                        // în jurnalul de audit, nu în acest răspuns.
                        invitationSent     = true,
                    });
                }

                return Ok(new
                {
                    message = !string.IsNullOrEmpty(email) && !_email.IsConfigured
                        ? $"Contul @{user.Username} a fost creat. SMTP nu este configurat, așa că nu s-a " +
                          "trimis email de activare: comunicați parola temporară utilizatorului. La prima " +
                          "autentificare va fi obligat să-și aleagă o parolă proprie."
                        : $"Contul @{user.Username} a fost creat. La prima autentificare, " +
                          "utilizatorul va fi obligat să-și aleagă o parolă proprie.",
                    id = user.Id,
                    mustChangePassword = true,
                    invitationSent     = false,
                });
            }
            catch (DbUpdateException ex) when (ex.InnerException is DbException { SqlState: UniqueViolation })
            {
                // Două cereri simultane cu același nume trec amândouă de verificarea
                // de mai sus; indexul unic o oprește pe a doua. Răspunsul corect e
                // 409, nu 500.
                return Conflict(new { message = "Username-ul sau adresa de email există deja." });
            }
            catch (HashingCapacityExceededException ex)
            {
                Response.Headers.RetryAfter = "5";
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { message = ex.Message, retryAfter = 5 });
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Users/{id}/resend-invitation
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Retrimite emailul de invitație (regenerează tokenul).
        /// Util când utilizatorul a pierdut emailul inițial sau linkul a expirat.
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPost("{id:guid}/resend-invitation")]
        public async Task<IActionResult> ResendInvitation(Guid id, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync([id], ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost găsit." });

            if (user.EmailConfirmed)
                return BadRequest(new { message = "Contul este deja activat." });

            if (string.IsNullOrWhiteSpace(user.Email))
                return BadRequest(new { message = "Utilizatorul nu are adresă de email. Adăugați mai întâi o adresă." });

            if (!user.IsActive)
                return BadRequest(new { message = "Contul este dezactivat. Activați-l înainte de a retrimite invitația." });

            if (!_email.IsConfigured)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "Serverul SMTP nu este configurat, deci invitația nu poate fi trimisă. " +
                              "Configurați secțiunea Smtp sau resetați parola contului din această pagină.",
                });

            // Aici trimiterea e AȘTEPTATĂ, pe scope-ul cererii: administratorul a
            // cerut explicit retrimiterea și trebuie să afle dacă a reușit.
            var sent = await _invitation.SendInvitationAsync(id, ct);

            AddAudit(id, AuditAction.UserUpdated,
                sent
                    ? $"Invitatie retrimisa pentru @{user.Username}"
                    : $"Retrimiterea invitatiei pentru @{user.Username} a esuat (SMTP indisponibil)",
                sent ? AuditResult.Success : AuditResult.Failure);
            await _context.SaveChangesAsync(ct);

            if (!sent)
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Serverul SMTP nu a acceptat emailul. Invitația NU a fost trimisă; încercați din nou mai târziu.",
                });

            return Ok(new { message = $"Invitația a fost retrimisă la {user.Email}." });
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

            // Parola nouă e cunoscută de administrator. Până când utilizatorul nu
            // o înlocuiește cu una proprie, serverul nu acceptă chei E2EE noi:
            // altfel cheile generate după resetare ar fi încuiate cu o parolă pe
            // care administratorul o știe, iar el le-ar putea descuia din bază
            // fără ca amprenta sau jurnalul să arate ceva.
            user.MustChangePassword = true;

            // Resetarea se cere de obicei tocmai fiindcă altcineva ar putea avea
            // acces: toate sesiunile se închid, în tabela UserSessions.
            var closed = await _sessions.RevokeAllAsync(
                user.Id, "resetare administrativa a parolei", exceptSessionId: null, ct);

            // Cheile private E2EE sunt încuiate cu o cheie derivată din parola
            // VECHE, pe care nu o mai știe nimeni. Lăsate pe loc, contul intra
            // într-un impas: descuierea eșua la fiecare autentificare, iar
            // POST /api/Keys refuza chei noi fiindcă „există deja” - utilizatorul
            // nu mai putea nici trimite, nici primi fișiere.
            //
            // Fără key escrow, ștergerea materialului de chei e singura ieșire.
            // Costul se spune explicit: fișierele primite anterior nu mai pot fi
            // deschise de acest cont. Expeditorii le pot redeschide din „Trimise”
            // (au propria copie a cheii de fișier) și le pot retrimite.
            var hadKeys = user.ClearEncryptionKeys();

            // O resetare cu parolă temporară anulează orice link de resetare
            // trimis anterior: altfel linkul vechi ar mai putea schimba parola.
            user.PasswordResetToken       = null;
            user.PasswordResetTokenExpiry = null;

            AddAudit(user.Id, AuditAction.UserUpdated,
                $"Parola resetata administrativ pentru @{user.Username}, {closed} sesiuni inchise" +
                (hadKeys ? ", chei E2EE invalidate (fisierele primite anterior devin inaccesibile)" : string.Empty) +
                ", schimbarea parolei este obligatorie la urmatoarea autentificare",
                AuditResult.Warning);

            await _context.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Parola resetata administrativ: {Admin} → {Target}, {Closed} sesiuni inchise, chei invalidate={KeysInvalidated}",
                CallerUsername, user.Username, closed, hadKeys);

            return Ok(new
            {
                message = hadKeys
                    ? $"Parola contului @{user.Username} a fost resetată. La următoarea autentificare, " +
                      "utilizatorul își alege o parolă proprie și abia apoi generează chei noi. " +
                      "Fișierele primite anterior trebuie retrimise de expeditori."
                    : $"Parola contului @{user.Username} a fost resetată. La următoarea autentificare, " +
                      "utilizatorul va fi obligat să-și aleagă o parolă proprie.",
                sessionsClosed     = closed,
                keysInvalidated    = hadKeys,
                mustChangePassword = true,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Users/{id}/send-password-reset
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Trimite titularului un link de resetare a parolei. Varianta preferată
        /// față de parola temporară: administratorul nu află parola nouă, iar
        /// titularul nu mai trece prin schimbarea forțată la primul login.
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPost("{id:guid}/send-password-reset")]
        public async Task<IActionResult> SendPasswordReset(Guid id, CancellationToken ct)
        {
            var result = await _passwordReset.RequestAsync(id, ct);

            switch (result.Status)
            {
                case PasswordResetRequestStatus.UserNotFound:
                    return NotFound(new { message = "Utilizatorul nu a fost găsit." });
                case PasswordResetRequestStatus.NoEmail:
                    return BadRequest(new
                    {
                        message = $"Contul @{result.Username} nu are adresă de email. " +
                                  "Folosiți resetarea cu parolă temporară.",
                    });
                case PasswordResetRequestStatus.Inactive:
                    return BadRequest(new { message = "Contul este dezactivat. Activați-l înainte de resetare." });
                case PasswordResetRequestStatus.SmtpNotConfigured:
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                    {
                        message = "Serverul SMTP nu este configurat, deci linkul nu poate fi trimis. " +
                                  "Folosiți resetarea cu parolă temporară.",
                    });
            }

            var sent = result.Status == PasswordResetRequestStatus.Sent;

            AddAudit(id, AuditAction.PasswordResetRequested,
                sent
                    ? $"Link de resetare a parolei trimis pentru @{result.Username} la {result.Email}, " +
                      $"valabil pana la {result.ExpiresAt:yyyy-MM-dd HH:mm} UTC"
                    : $"Trimiterea linkului de resetare pentru @{result.Username} a esuat (SMTP)",
                sent ? AuditResult.Success : AuditResult.Failure);
            await _context.SaveChangesAsync(ct);

            if (!sent)
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Serverul SMTP nu a acceptat emailul. Linkul NU a fost trimis; încercați din nou.",
                });

            return Ok(new
            {
                message   = $"Linkul de resetare a fost trimis la {result.Email}. Parola actuală rămâne " +
                            "valabilă până când utilizatorul o stabilește pe cea nouă.",
                expiresAt = result.ExpiresAt,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PATCH api/Users/{id}/org-unit
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Încadrează utilizatorul într-o subdiviziune (sau îl scoate din structură).
        ///
        /// Dacă persoana conduce o subdiviziune și e mutată în alta, funcția de
        /// șef se eliberează: un șef încadrat în afara unității conduse ar face ca
        /// „subdiviziunea mea” să însemne altceva pentru el decât pentru colegi.
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPatch("{id:guid}/org-unit")]
        public async Task<IActionResult> ChangeOrgUnit(Guid id, [FromBody] ChangeOrgUnitDto? dto, CancellationToken ct)
        {
            var user = await _context.Users.Include(u => u.OrgUnit).FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost găsit." });

            var newUnitId = dto?.OrgUnitId;
            string? newName = null;
            if (newUnitId.HasValue)
            {
                var unit = await _context.OrgUnits.FirstOrDefaultAsync(o => o.Id == newUnitId && o.IsActive, ct);
                if (unit is null)
                    return BadRequest(new { message = "Subdiviziunea aleasă nu există sau este desființată." });
                newName = unit.Name;
            }

            if (user.OrgUnitId == newUnitId)
                return Ok(new { message = "Încadrarea era deja aceasta.", headReleased = false });

            var oldName = user.OrgUnit?.Name ?? "neincadrat";

            var led = await _context.OrgUnits.FirstOrDefaultAsync(o => o.HeadUserId == id, ct);
            var headReleased = led is not null && led.Id != newUnitId;
            if (headReleased) led!.HeadUserId = null;

            user.OrgUnitId = newUnitId;

            AddAudit(id, AuditAction.OrgStructureChanged,
                $"Incadrare @{user.Username}: {oldName} -> {newName ?? "neincadrat"}" +
                (headReleased ? $"; functia de sef al subdiviziunii '{led!.Name}' a fost eliberata" : string.Empty));
            await _context.SaveChangesAsync(ct);

            return Ok(new
            {
                message = headReleased
                    ? $"@{user.Username} a fost mutat. Funcția de șef al subdiviziunii „{led!.Name}” a rămas vacantă."
                    : $"@{user.Username} a fost încadrat în {newName ?? "nicio subdiviziune"}.",
                headReleased,
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
        // PATCH api/Users/{id}/role - exclusiv Administrator
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Endpointul nu avea NICIO restricție de rol. Un utilizator obișnuit își
        /// citea propriul id din token și trimitea PATCH /api/Users/{id-ul lui}/role
        /// cu Role=3. Din acel moment avea acces la jurnalul de audit, la
        /// resetarea parolelor tuturor și la ștergerea oricărui transfer.
        ///
        /// Pe lângă restricția de rol, două protecții structurale: nimeni nu-și
        /// schimbă propriul rol, și ultimul administrator activ nu poate fi
        /// retrogradat - altfel sistemul rămâne fără nicio cale de administrare.
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
        // PATCH api/Users/{id}/deactivate - exclusiv Administrator
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
            // de acces deja emis rămâne valid până la expirare - revocarea explicită
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
        // PATCH api/Users/{id}/activate - exclusiv Administrator
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
        /// Nu e o măsură de securitate împotriva unui atacator - cine e deja
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