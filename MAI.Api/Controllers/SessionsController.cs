using MAI.Api.Services;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Sesiunile proprii: unde ești conectat și cum închizi o sesiune de la distanță.
    ///
    /// Toate rutele operează exclusiv pe sesiunile utilizatorului autentificat.
    /// Nu există aici o variantă administrativă „vezi sesiunile altcuiva”:
    /// deconectarea forțată a unui angajat se face prin dezactivarea contului din
    /// UsersController, care este o acțiune vizibilă și consemnată ca atare.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/Sessions")]
    public class SessionsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ISessionService _sessions;
        private readonly ITokenService _tokens;

        public SessionsController(
            AppDbContext context, ISessionService sessions, ITokenService tokens)
        {
            _context  = context;
            _sessions = sessions;
            _tokens   = tokens;
        }

        private Guid CurrentUserId =>
            Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
                ? id
                : Guid.Empty;

        private string CurrentUsername => User.Identity?.Name ?? "necunoscut";

        private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Sessions
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Sesiunile proprii: cele active, apoi ultimele închise.
        ///
        /// Clientul poate trimite refresh token-ul curent ca parametru, pentru ca
        /// interfața să marcheze „acest dispozitiv”. Se trimite tokenul, nu
        /// id-ul sesiunii, pentru că frontend-ul nu îl cunoaște pe al doilea - și
        /// nici nu are de ce: ar fi un identificator în plus de ținut minte.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMySessions(
            [FromQuery] string? currentRefreshToken, CancellationToken ct)
        {
            var userId = CurrentUserId;
            if (userId == Guid.Empty) return Unauthorized();

            var sessions = await _sessions.ListAsync(userId, ct);

            Guid? currentId = null;
            if (!string.IsNullOrWhiteSpace(currentRefreshToken))
            {
                var hash = _tokens.HashOpaqueToken(currentRefreshToken);
                currentId = sessions.FirstOrDefault(s => s.RefreshTokenHash == hash)?.Id;
            }

            var now = DateTime.UtcNow;

            var items = sessions.Select(s => new
            {
                s.Id,
                s.CreatedAt,
                s.LastSeenAt,
                s.ExpiresAt,
                s.RevokedAt,
                s.RevokedReason,
                s.IpAddress,

                // Hash-ul NU pleacă spre client. E singura valoare din rând cu
                // care se poate face ceva, iar interfața nu are nevoie de ea.
                Device    = DescribeDevice(s.UserAgent),
                UserAgent = s.UserAgent,

                IsActive  = s.RevokedAt is null && s.ExpiresAt > now,
                IsCurrent = currentId.HasValue && s.Id == currentId.Value,
            });

            return Ok(items);
        }

        // ═════════════════════════════════════════════════════════════════════
        // DELETE api/Sessions/{id}
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>Închide o sesiune anume. Efectul e imediat la următorul /refresh.</summary>
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> RevokeSession(Guid id, CancellationToken ct)
        {
            var userId = CurrentUserId;
            if (userId == Guid.Empty) return Unauthorized();

            var session = await _context.UserSessions.FindAsync([id], ct);

            // NotFound și pentru sesiunea altcuiva, nu Forbid: un 403 ar confirma
            // că id-ul există și aparține cuiva.
            if (session is null || session.UserId != userId)
                return NotFound(new { message = "Sesiunea nu a fost găsită." });

            if (session.RevokedAt is not null)
                return Ok(new { message = "Sesiunea era deja închisă." });

            _sessions.Revoke(session, "revocare de la distanta");

            AddAudit(userId, AuditAction.SessionRevoked,
                $"Sesiune inchisa de la distanta ({DescribeDevice(session.UserAgent)}, {session.IpAddress})",
                // Nu e o eroare, dar e o acțiune de securitate pe care un
                // supervizor trebuie s-o poată găsi filtrând.
                AuditResult.Warning);

            await _context.SaveChangesAsync(ct);

            // Tokenul de acces al sesiunii închise rămâne valid până expiră -
            // cel mult cincisprezece minute. Invalidarea lui imediată ar cere o
            // listă de revocare consultată la fiecare cerere, adică o interogare
            // în plus pe tot API-ul ca să acoperi un sfert de oră.
            return Ok(new
            {
                message = "Sesiunea a fost închisă. Dispozitivul va fi deconectat în cel mult 15 minute.",
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // DELETE api/Sessions/others
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Închide toate sesiunile în afară de cea curentă.
        ///
        /// Necesită refresh token-ul curent: fără el nu se poate ști care sesiune
        /// să fie păstrată, iar utilizatorul s-ar deconecta singur exact când
        /// încearcă să-și securizeze contul.
        /// </summary>
        [HttpDelete("others")]
        public async Task<IActionResult> RevokeOthers(
            [FromBody] RevokeOthersRequest request, CancellationToken ct)
        {
            var userId = CurrentUserId;
            if (userId == Guid.Empty) return Unauthorized();

            if (string.IsNullOrWhiteSpace(request.CurrentRefreshToken))
                return BadRequest(new { message = "Sesiunea curentă nu a putut fi identificată." });

            var hash    = _tokens.HashOpaqueToken(request.CurrentRefreshToken);
            var current = await _sessions.FindActiveAsync(hash, ct);

            if (current is null || current.UserId != userId)
                return BadRequest(new { message = "Sesiunea curentă nu a putut fi identificată." });

            var closed = await _sessions.RevokeAllAsync(
                userId, "revocare de la distanta", exceptSessionId: current.Id, ct);

            if (closed > 0)
            {
                AddAudit(userId, AuditAction.SessionRevoked,
                    $"{closed} sesiuni inchise de la distanta, sesiunea curenta pastrata",
                    AuditResult.Warning);
            }

            await _context.SaveChangesAsync(ct);

            return Ok(new { closed, message = $"{closed} sesiuni au fost închise." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Etichetă lizibilă din User-Agent: „Chrome pe Windows”.
        ///
        /// Intenționat grosier. User-Agent-ul e controlat de client, deci o
        /// analiză fină ar da o falsă impresie de precizie pe un șir în care nu
        /// se poate avea încredere. Scopul e doar ca utilizatorul să-și
        /// recunoască dispozitivele; șirul complet e oricum disponibil alături.
        /// </summary>
        private static string DescribeDevice(string? userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return "Dispozitiv necunoscut";

            var ua = userAgent;

            // Ordinea contează: Edge conține "Chrome", Chrome conține "Safari".
            var browser =
                ua.Contains("Edg/",     StringComparison.OrdinalIgnoreCase) ? "Edge"    :
                ua.Contains("OPR/",     StringComparison.OrdinalIgnoreCase) ? "Opera"   :
                ua.Contains("Firefox",  StringComparison.OrdinalIgnoreCase) ? "Firefox" :
                ua.Contains("Chrome",   StringComparison.OrdinalIgnoreCase) ? "Chrome"  :
                ua.Contains("Safari",   StringComparison.OrdinalIgnoreCase) ? "Safari"  :
                                                                             "Browser";

            var os =
                ua.Contains("Windows",  StringComparison.OrdinalIgnoreCase) ? "Windows" :
                ua.Contains("Android",  StringComparison.OrdinalIgnoreCase) ? "Android" :
                ua.Contains("iPhone",   StringComparison.OrdinalIgnoreCase) ||
                ua.Contains("iPad",     StringComparison.OrdinalIgnoreCase) ? "iOS"     :
                ua.Contains("Mac OS",   StringComparison.OrdinalIgnoreCase) ? "macOS"   :
                ua.Contains("Linux",    StringComparison.OrdinalIgnoreCase) ? "Linux"   :
                                                                             "sistem necunoscut";

            return $"{browser} pe {os}";
        }

        private void AddAudit(
            Guid userId, AuditAction action, string details,
            AuditResult result = AuditResult.Success)
        {
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = action,
                Details   = details,
                Result    = result,
                IpAddress = Ip,
                Timestamp = DateTime.UtcNow,
            });
        }
    }

    /// <summary>Corpul cererii DELETE /api/Sessions/others.</summary>
    public class RevokeOthersRequest
    {
        /// <summary>Refresh token-ul sesiunii care trebuie păstrată.</summary>
        public string CurrentRefreshToken { get; set; } = string.Empty;
    }
}
