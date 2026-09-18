using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MAI.Api.Security;
using MAI.Api.Services;
using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using LoginDto = MAI.DataAccessLayer.DTOs.LoginDto;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Autentificare: login, pasul doi, rotația sesiunii, delogare, schimbare de parolă.
    ///
    /// Controllerul orchestrează; nu implementează. Semnarea JWT-urilor, generarea
    /// și hashingul tokenurilor opace stau în <see cref="ITokenService"/>, iar
    /// politica de blocare a contului în <see cref="IAccountLockoutService"/>.
    /// Ce rămâne aici sunt deciziile care depind de contextul HTTP: ce cod de stare
    /// se întoarce, ce mesaj vede utilizatorul, ce se scrie în jurnal.
    /// </summary>
    [ApiController]
    [Route("api/Auth")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _hasher;
        private readonly PasswordPolicy _policy;
        private readonly Argon2Options _argon2;
        private readonly TwoFactorOptions _twoFactor;
        private readonly TotpService _totp;
        private readonly SecretProtector _protector;
        private readonly ITokenService _tokens;
        private readonly ISessionService _sessions;
        private readonly IAccountLockoutService _lockout;
        private readonly ILogger<AuthController> _logger;
        private readonly IInvitationService _invitation;

        public AuthController(
            AppDbContext context,
            IPasswordHasher hasher,
            PasswordPolicy policy,
            Argon2Options argon2,
            TwoFactorOptions twoFactor,
            TotpService totp,
            SecretProtector protector,
            ITokenService tokens,
            ISessionService sessions,
            IAccountLockoutService lockout,
            ILogger<AuthController> logger,
            IInvitationService invitation)
        {
            _context   = context;
            _hasher    = hasher;
            _policy    = policy;
            _argon2    = argon2;
            _twoFactor = twoFactor;
            _totp      = totp;
            _protector = protector;
            _tokens    = tokens;
            _sessions  = sessions;
            _lockout    = lockout;
            _logger     = logger;
            _invitation = invitation;
        }

        private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        /// <summary>
        /// Lungimile maxime acceptate la login. Conturile se creează cu cel mult 50
        /// de caractere în nume (UsersController), deci 64 lasă o marjă fără să
        /// identifice vreun cont real. Parola e plafonată ca un corp mare să nu
        /// ajungă în Argon2.
        /// </summary>
        private const int MaxLoginUsernameLength = 64;
        private const int MaxLoginPasswordLength = 1024;

        /// <summary>Lungimea coloanei AuditLogs.Username (vezi AuditLogConfiguration).</summary>
        private const int AuditUsernameMaxLength = 128;

        /// <summary>
        /// Numele tastat, trunchiat la lungimea coloanei de audit. Fără trunchiere,
        /// un nume de 129+ caractere făcea inserarea rândului de audit să eșueze:
        /// răspunsul era 500, iar încercarea nu mai apărea în jurnal deloc.
        /// </summary>
        private static string AuditName(string username) =>
            username.Length <= AuditUsernameMaxLength ? username : username[..AuditUsernameMaxLength];

        /// <summary>
        /// User-Agent-ul cererii, pentru eticheta sesiunii. Vine de la client,
        /// deci e o indicație, nu o dovadă — se afișează, nu se folosește la
        /// nicio decizie de autorizare.
        /// </summary>
        private string? UserAgent => Request.Headers.UserAgent.ToString() is { Length: > 0 } ua
            ? ua
            : null;

        /// <summary>Profilul Argon2 potrivit rolului: conturile privilegiate primesc cost mai mare.</summary>
        private string ProfileFor(UserRole role) =>
            role >= UserRole.SefDirectie ? _argon2.PrivilegedProfile : _argon2.DefaultProfile;

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Auth/login
        // ═════════════════════════════════════════════════════════════════════
        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.Login)]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto request, CancellationToken ct)
        {
            var username = request.Username?.Trim() ?? string.Empty;
            var password = request.Password ?? string.Empty;

            // Mesaj identic pentru orice eșec — nu divulgăm dacă userul există,
            // dacă e blocat sau dacă parola era aproape corectă.
            const string genericError = "Nume de utilizator sau parolă incorectă.";

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                await WriteAuditAsync(null, AuditName(username), AuditAction.Login,
                    "Credentiale lipsa", AuditResult.Failure);
                return BadRequest(new { message = genericError });
            }

            // Limitele se verifică înaintea bazei de date și a lui Argon2. Un nume
            // mai lung decât orice cont posibil nu identifică pe nimeni, deci
            // răspunsul imediat nu dezvăluie existența vreunui cont.
            if (username.Length > MaxLoginUsernameLength || password.Length > MaxLoginPasswordLength)
            {
                await WriteAuditAsync(null, AuditName(username), AuditAction.Login,
                    "Credentiale peste lungimea maxima acceptata", AuditResult.Failure);
                return BadRequest(new { message = genericError });
            }

            // Numele de utilizator sunt unice fără diferență între majuscule și
            // minuscule (indexul UX_Users_Username_Lower). Căutarea urmează aceeași
            // regulă: „Ion.Popescu” și „ion.popescu” sunt același cont, nu două.
            var usernameKey = username.ToLowerInvariant();
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Username.ToLower() == usernameKey, ct);

            try
            {
                if (user is null)
                {
                    // Consumăm același timp ca o verificare reală, ca să nu se poată
                    // enumera conturile măsurând latența răspunsului.
                    await _hasher.SimulateVerificationAsync(ct);
                    await WriteAuditAsync(null, AuditName(username), AuditAction.Login,
                        "Utilizator inexistent", AuditResult.Failure);
                    return BadRequest(new { message = genericError });
                }

                // Contul blocat: NU calculăm hash. Exact ăsta e scopul blocării —
                // un atacator nu trebuie să poată consuma 19 MiB pe încercare la nesfârșit.
                if (user.IsLockedOut)
                {
                    var remaining = _lockout.RemainingLockoutSeconds(user);

                    await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                        $"Cont blocat, {remaining}s ramase", AuditResult.Failure);

                    Response.Headers.RetryAfter = remaining.ToString();
                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        message = genericError,
                        retryAfter = remaining,
                    });
                }

                var verification = await _hasher.VerifyPasswordAsync(password, user.PasswordHash, ct);

                if (verification == PasswordVerificationResult.Failed)
                {
                    var outcome = _lockout.RegisterFailedAttempt(user);

                    if (outcome.LockedOut)
                    {
                        _logger.LogWarning("Cont blocat: {Username}, IP={Ip}, {Minutes} minute",
                            user.Username, Ip, outcome.LockoutMinutes);
                    }

                    AddAudit(user.Id, user.Username, AuditAction.Login,
                        outcome.AuditDetails, AuditResult.Failure);
                    await _context.SaveChangesAsync(ct);

                    return BadRequest(new { message = genericError });
                }

                if (!user.IsActive)
                {
                    await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                        "Cont dezactivat", AuditResult.Failure);
                    return BadRequest(new { message = "Contul este dezactivat. Contactați administratorul." });
                }

                // Contul creat de administrator cu email, dar neactivat încă.
                // Mesajul e specific — parola a fost oricum corectă la acest punct.
                if (!user.EmailConfirmed)
                {
                    await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                        "Cont neactivat prin email", AuditResult.Failure);
                    return BadRequest(new
                    {
                        message       = "Contul nu a fost activat. Verificați emailul pentru linkul de activare.",
                        emailNotConfirmed = true,
                    });
                }

                // Migrare transparentă plain text / parametri slabi → profilul potrivit rolului.
                if (verification == PasswordVerificationResult.SuccessRehashNeeded)
                {
                    user.PasswordHash = await _hasher.HashPasswordAsync(password, ProfileFor(user.Role), ct);
                    _logger.LogInformation("Hash parola migrat pentru {Username}.", user.Username);
                    AddAudit(user.Id, user.Username, AuditAction.UserUpdated, "Hash parola migrat la Argon2id");
                }

                // Parola e corectă: contorul de eșecuri se resetează aici, indiferent
                // dacă mai urmează sau nu pasul doi. Altfel, un utilizator cu 2FA
                // activ ar rămâne cu eșecuri vechi neșterse și s-ar bloca aparent
                // din senin la o greșeală ulterioară.
                _lockout.ResetCounters(user);

                // ── Pasul doi, DOAR dacă utilizatorul l-a activat singur ──────
                //
                // 2FA este opțional. Un cont fără 2FA se autentifică exact ca
                // înainte — comportamentul vechi rămâne calea implicită, iar
                // activarea se face din pagina de profil, de către utilizator.
                if (user.TwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret))
                {
                    var challenge = _tokens.IssueTwoFactorChallenge(user);

                    AddAudit(user.Id, user.Username, AuditAction.Login,
                        "Parola corecta, se asteapta codul 2FA");

                    await _context.SaveChangesAsync(ct);

                    // 200, nu 401: parola a fost corectă. Autentificarea nu a
                    // eșuat, doar nu s-a încheiat.
                    return Ok(new
                    {
                        twoFactorRequired = true,
                        challengeToken    = challenge.Token,
                        expiresAt         = challenge.ExpiresAt,
                        // Frontend-ul afișează dacă mai există coduri de recuperare,
                        // ca utilizatorul să știe dacă are pe ce conta.
                        recoveryAvailable = user.RemainingRecoveryCodes > 0,
                    });
                }

                user.LastLoginAt = DateTime.UtcNow;

                var issued = _tokens.IssueTokens(user);

                // Sesiune nouă pentru acest dispozitiv. Autentificarea pe telefon
                // nu mai deconectează laptopul: fiecare are rândul lui.
                _sessions.Create(
                    user, issued.RefreshTokenHash, issued.RefreshTokenExpiresAt, UserAgent, Ip);

                AddAudit(user.Id, user.Username, AuditAction.Login,
                    user.MustChangePassword
                        ? "Autentificare reusita cu parola temporara, schimbarea parolei este obligatorie"
                        : "Autentificare reusita");
                await _context.SaveChangesAsync(ct);

                return Ok(issued.Response);
            }
            catch (HashingCapacityExceededException ex)
            {
                return CapacityResponse(ex);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Auth/2fa/verify — pasul doi
        // ═════════════════════════════════════════════════════════════════════
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

            var hash = _tokens.HashOpaqueToken(dto.ChallengeToken);

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.TwoFactorChallengeHash == hash, ct);

            if (user is null)
            {
                await WriteAuditAsync(null, "necunoscut", AuditAction.Login,
                    "Provocare 2FA invalida sau deja folosita", AuditResult.Failure);
                return Unauthorized(new { message = genericError });
            }

            if (user.TwoFactorChallengeExpiresAt is null ||
                user.TwoFactorChallengeExpiresAt <= DateTime.UtcNow)
            {
                _tokens.ClearChallenge(user);
                await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                    "Provocare 2FA expirata", AuditResult.Failure);
                return Unauthorized(new { message = genericError });
            }

            if (!user.IsActive || user.IsLockedOut || !user.EmailConfirmed)
            {
                _tokens.ClearChallenge(user);
                await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                    "Verificare 2FA pe cont dezactivat, blocat sau neactivat", AuditResult.Failure);
                return Unauthorized(new { message = genericError });
            }

            if (user.TwoFactorChallengeAttempts >= _twoFactor.MaxChallengeAttempts)
            {
                _tokens.ClearChallenge(user);
                await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                    $"Provocare 2FA anulata dupa {_twoFactor.MaxChallengeAttempts} coduri gresite",
                    AuditResult.Failure);
                return Unauthorized(new { message = genericError });
            }

            var usedRecoveryCode = false;
            var accepted         = false;
            var replayed         = false;

            // Codul TOTP are exact Digits cifre. Orice altceva e tratat ca posibil
            // cod de recuperare — nu-l trimitem degeaba prin HMAC.
            var digitsOnly = new string(dto.Code.Where(char.IsDigit).ToArray());

            if (digitsOnly.Length == _twoFactor.Digits)
            {
                try
                {
                    var secret = _protector.Unprotect(user.TwoFactorSecret!);

                    // Un cod TOTP se acceptă o singură dată (RFC 6238, 5.2). Fără
                    // regula asta, un cod văzut pe ecranul altcuiva sau interceptat
                    // mergea refolosit cât timp era valid, adică ~90 de secunde.
                    var totp = _totp.Verify(secret, dto.Code, user.TwoFactorLastUsedStep);
                    accepted = totp.IsAccepted;
                    replayed = totp.Status == TotpVerificationStatus.Replayed;

                    if (accepted) user.TwoFactorLastUsedStep = totp.Step;
                }
                catch (CryptographicException ex)
                {
                    // Secretul nu se mai poate descifra: cheia de cifrare s-a
                    // schimbat sau coloana a fost alterată. Nu are rost să pretindem
                    // că e o greșeală de tastare a utilizatorului.
                    _logger.LogError(ex,
                        "Secretul 2FA al utilizatorului {Username} nu poate fi descifrat.", user.Username);

                    await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                        "Secret 2FA indescifrabil", AuditResult.Failure);

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

                // Codurile greșite intră și în contorul de blocare al contului. Altfel,
                // cine știa parola putea încerca coduri la nesfârșit: câte cinci pe
                // provocare, cu provocări noi cerute de pe alte adrese IP.
                var lockout = _lockout.RegisterFailedAttempt(user);

                if (lockout.LockedOut)
                {
                    _tokens.ClearChallenge(user);
                    _logger.LogWarning(
                        "Cont blocat dupa coduri 2FA gresite: {Username}, IP={Ip}, {Minutes} minute",
                        user.Username, Ip, lockout.LockoutMinutes);
                }

                AddAudit(user.Id, user.Username, AuditAction.Login,
                    (replayed
                        ? "Cod 2FA deja folosit, respins (posibila reluare a unui cod interceptat)"
                        : $"Cod 2FA incorect ({user.TwoFactorChallengeAttempts}/{_twoFactor.MaxChallengeAttempts})") +
                    (lockout.LockedOut ? $", cont blocat {lockout.LockoutMinutes:0} minute" : string.Empty),
                    // O reluare nu e o simplă greșeală de tastare: cineva a avut
                    // un cod valid. E rândul pe care un supervizor trebuie să-l vadă.
                    replayed ? AuditResult.Warning : AuditResult.Failure);

                await _context.SaveChangesAsync(ct);

                if (lockout.LockedOut)
                    return Unauthorized(new { message = genericError, attemptsRemaining = 0 });

                return Unauthorized(new
                {
                    message = replayed
                        ? "Codul a fost deja folosit. Așteptați următorul cod din aplicația de autentificare."
                        : remaining > 0
                            ? $"Cod incorect. Mai aveți {remaining} încercări."
                            : genericError,
                    attemptsRemaining = Math.Max(remaining, 0),
                });
            }

            // Pasul 2FA a reușit: contorul de eșecuri, crescut eventual de coduri
            // greșite în această provocare, se resetează ca la o parolă corectă.
            _lockout.ResetCounters(user);

            // Provocarea se consumă indiferent de ce urmează: un token de unică
            // folosință care rămâne valid după utilizare nu mai e de unică folosință.
            _tokens.ClearChallenge(user);

            user.LastLoginAt = DateTime.UtcNow;

            var issued   = _tokens.IssueTokens(user);
            var response = issued.Response;

            _sessions.Create(
                user, issued.RefreshTokenHash, issued.RefreshTokenExpiresAt, UserAgent, Ip);

            AddAudit(user.Id, user.Username, AuditAction.Login,
                usedRecoveryCode
                    ? $"Autentificare cu COD DE RECUPERARE 2FA ({user.RemainingRecoveryCodes} ramase)"
                    : "Autentificare reusita cu 2FA",
                // Un login cu cod de recuperare nu e o eroare — dar e exact rândul
                // pe care un supervizor vrea să-l găsească filtrând, nu citind.
                usedRecoveryCode ? AuditResult.Warning : AuditResult.Success);

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
                response.MustChangePassword,
                response.MfaEnrollmentRequired,
                usedRecoveryCode,
                remainingRecoveryCodes = user.RemainingRecoveryCodes,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Auth/refresh
        // ═════════════════════════════════════════════════════════════════════
        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.Refresh)]
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequestDto dto, CancellationToken ct)
        {
            const string genericError = "Sesiune invalidă sau expirată. Autentificați-vă din nou.";

            if (string.IsNullOrWhiteSpace(dto.RefreshToken))
                return Unauthorized(new { message = genericError });

            var hash = _tokens.HashOpaqueToken(dto.RefreshToken);

            // Un token revocat sau expirat nu este găsit deloc: filtrarea se face
            // în interogare, nu după. Diferența contează — un token furat dintr-o
            // sesiune încheiată nu trebuie nici măcar să identifice utilizatorul.
            var session = await _sessions.FindActiveAsync(hash, ct);

            if (session?.User is null)
            {
                await WriteAuditAsync(null, "necunoscut", AuditAction.Login,
                    "Refresh token invalid, revocat sau expirat", AuditResult.Failure);
                return Unauthorized(new { message = genericError });
            }

            var user = session.User;

            // Doar dezactivarea închide sesiunea aici, nu și blocarea. Blocarea
            // apără login-ul de ghicirea parolei, iar oricine știe un username o
            // poate declanșa cu cinci parole greșite. Dacă ar închide și
            // sesiunile, oricine ar putea deconecta pe oricine, oricând. Titularul
            // sesiunii și-a dovedit deja identitatea; o sesiune suspectă se închide
            // explicit, din lista de sesiuni sau prin dezactivarea contului.
            if (!user.IsActive || !user.EmailConfirmed)
            {
                _sessions.Revoke(session, "cont dezactivat sau neactivat");
                await WriteAuditAsync(user.Id, user.Username, AuditAction.Login,
                    !user.IsActive ? "Refresh pe cont dezactivat" : "Refresh pe cont neactivat",
                    AuditResult.Failure);
                return Unauthorized(new { message = genericError });
            }

            // Rotație în cadrul aceleiași sesiuni: tokenul vechi devine invalid,
            // dar rândul rămâne. Altfel utilizatorul ar vedea o „sesiune nouă” la
            // fiecare cincisprezece minute, iar lista ar deveni inutilizabilă.
            var issued = _tokens.IssueTokens(user);
            _sessions.Rotate(session, issued.RefreshTokenHash, issued.RefreshTokenExpiresAt);

            await _context.SaveChangesAsync(ct);

            // Reîmprospătarea NU se scrie în audit: se întâmplă la fiecare
            // cincisprezece minute, pentru fiecare sesiune activă, și ar îneca
            // jurnalul în zgomot. Activitatea rămâne vizibilă prin LastSeenAt.

            return Ok(issued.Response);
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Auth/logout
        // ═════════════════════════════════════════════════════════════════════
        [AllowAnonymous]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] RefreshTokenRequestDto? dto, CancellationToken ct)
        {
            // Delogarea închide DOAR sesiunea de pe acest dispozitiv. Închiderea
            // tuturor e o acțiune separată, explicită, din pagina de sesiuni —
            // un utilizator care apasă „ieși” pe telefon nu se așteaptă să fie
            // deconectat și de pe calculatorul de la birou.
            if (!string.IsNullOrWhiteSpace(dto?.RefreshToken))
            {
                var hash    = _tokens.HashOpaqueToken(dto.RefreshToken);
                var session = await _sessions.FindActiveAsync(hash, ct);

                if (session?.User is not null)
                {
                    _sessions.Revoke(session, "logout");

                    AddAudit(session.UserId, session.User.Username, AuditAction.Logout,
                        "Delogare, sesiune inchisa");

                    await _context.SaveChangesAsync(ct);
                }
            }

            // Răspuns identic indiferent dacă tokenul a fost recunoscut: un
            // endpoint de logout care spune „nu cunosc acest token” devine un
            // oracol pentru verificarea tokenurilor furate.
            return Ok(new { message = "Sesiune încheiată." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PATCH api/Auth/change-password
        // ═════════════════════════════════════════════════════════════════════
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
                        "Schimbare parola - parola curenta incorecta", AuditResult.Failure);
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

                // Parola nouă e aleasă de utilizator, deci nu mai e cunoscută de
                // administratorul care a creat sau resetat contul. Abia de acum
                // serverul acceptă înregistrarea cheilor E2EE (KeysController).
                var replacedTemporaryPassword = user.MustChangePassword;
                user.MustChangePassword = false;

                // Schimbarea parolei închide TOATE sesiunile, fără excepție —
                // inclusiv cea curentă. Motivul obișnuit pentru care cineva își
                // schimbă parola este suspiciunea că altcineva o știe; a lăsa
                // deschisă chiar și o sesiune ar rata exact scenariul.
                //
                // Atenție: secretul 2FA NU se atinge. Este independent de parolă —
                // exact ăsta e rostul celui de-al doilea factor. Dacă l-am reseta
                // aici, o schimbare de parolă ar dezactiva pe tăcute protecția.
                var closed = await _sessions.RevokeAllAsync(
                    user.Id, "schimbare parola", exceptSessionId: null, ct);

                AddAudit(user.Id, user.Username, AuditAction.UserUpdated,
                    (replacedTemporaryPassword
                        ? "Parola temporara inlocuita de utilizator (Argon2id)"
                        : "Parola schimbata (Argon2id)") +
                    $", {closed} sesiuni inchise");

                await _context.SaveChangesAsync(ct);
                return Ok(new { message = "Parola a fost actualizată cu succes." });
            }
            catch (HashingCapacityExceededException ex)
            {
                return CapacityResponse(ex);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Coduri de recuperare
        // ═════════════════════════════════════════════════════════════════════

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

            // Comparație în timp constant peste toată lista, fără ieșire devreme:
            // o buclă care se oprește la prima potrivire spune, prin durată, câte
            // coduri a parcurs până a găsit-o.
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

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

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

        private void AddAudit(
            Guid? userId, string username, AuditAction action, string details,
            AuditResult result = AuditResult.Success)
        {
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = username,
                Action    = action,
                Details   = details,
                Result    = result,
                IpAddress = Ip,
                Timestamp = DateTime.UtcNow,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Auth/check-invitation?token=...
        // Verifică tokenul și returnează username+email (fără a-l consuma).
        // Apelat de frontend la mount-ul paginii /confirm-account.
        // ═════════════════════════════════════════════════════════════════════
        [AllowAnonymous]
        [HttpGet("check-invitation")]
        public async Task<IActionResult> CheckInvitation(
            [FromQuery] string? token,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(new { error = "Token lipsă." });

            var info = await _invitation.GetTokenInfoAsync(token, ct);
            if (info is null)
                return NotFound(new { error = "Token invalid sau expirat." });

            return Ok(new { username = info.Username, email = info.Email });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Auth/confirm-invitation
        // Consumă tokenul, hashează parola cu Argon2id și activează contul.
        // ═════════════════════════════════════════════════════════════════════
        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        [HttpPost("confirm-invitation")]
        public async Task<IActionResult> ConfirmInvitation(
            [FromBody] ConfirmInvitationRequest req,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.Token))
                return BadRequest(new { error = "Token lipsă." });

            if (req.NewPassword != req.ConfirmPassword)
                return BadRequest(new { error = "Parolele nu coincid." });

            try
            {
                var result = await _invitation.ConfirmAsync(req.Token, req.NewPassword, ct);
                if (!result.Success)
                    return BadRequest(new { error = result.Error, errors = result.Errors });

                return Ok(new { message = "Contul a fost activat. Vă puteți autentifica acum." });
            }
            catch (HashingCapacityExceededException ex)
            {
                return CapacityResponse(ex);
            }
        }

        /// <summary>DTO pentru confirmarea invitației.</summary>
        public sealed record ConfirmInvitationRequest(
            string Token,
            string NewPassword,
            string ConfirmPassword);

        private async Task WriteAuditAsync(
            Guid? userId, string username, AuditAction action, string details,
            AuditResult result = AuditResult.Success)
        {
            AddAudit(userId, username, action, details, result);
            await _context.SaveChangesAsync();
        }
    }
}
