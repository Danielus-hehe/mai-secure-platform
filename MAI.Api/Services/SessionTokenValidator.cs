using System.Security.Claims;
using MAI.DataAccessLayer;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Services
{
    /// <summary>
    /// Leagă tokenul de acces de sesiunea din care a fost emis.
    ///
    /// Până acum JWT-ul era complet fără stare: delogarea, închiderea unei
    /// sesiuni din profil, dezactivarea contului sau schimbarea rolului revocau
    /// doar refresh token-ul, iar tokenul de acces deja emis mergea în continuare
    /// până la expirare - până la 15 minute, cu rolul vechi în el. Un
    /// administrator retrogradat rămânea administrator un sfert de oră.
    ///
    /// Acum fiecare cerere autentificată verifică, după semnătură și expirare,
    /// că sesiunea din claim-ul „sid” există, nu a fost închisă, aparține
    /// aceluiași utilizator, contul e activ și rolul din token e rolul curent.
    ///
    /// Costul: o interogare după cheia primară pe UserSessions, cu JOIN pe
    /// Users, la fiecare cerere. Intenționat fără cache: un cache ar reintroduce
    /// exact fereastra pe care o închidem, iar la volumul unui intranet o
    /// căutare după PK e neglijabilă. Cu mai multe instanțe ale API-ului,
    /// regula rămâne corectă, pentru că sursa de adevăr e baza, nu memoria.
    /// </summary>
    public interface ISessionTokenValidator
    {
        /// <summary>Motivul respingerii (pentru log), sau null dacă tokenul e încă valid.</summary>
        Task<string?> ValidateAsync(ClaimsPrincipal principal, CancellationToken ct);
    }

    public sealed class SessionTokenValidator : ISessionTokenValidator
    {
        /// <summary>Claim-ul emis de TokenService (RFC 7519 / OpenID Connect „sid”).</summary>
        public const string SessionClaimType = "sid";

        private readonly AppDbContext _context;

        public SessionTokenValidator(AppDbContext context) => _context = context;

        public async Task<string?> ValidateAsync(ClaimsPrincipal principal, CancellationToken ct)
        {
            // Handler-ul JWT poate traduce „sid” în tipul lung ClaimTypes.Sid
            // (MapInboundClaims). Se acceptă ambele, ca verificarea să nu
            // depindă de o setare a handler-ului.
            var sidValue = principal.FindFirst(SessionClaimType)?.Value
                           ?? principal.FindFirst(ClaimTypes.Sid)?.Value;

            // Tokenurile emise înainte de această versiune nu au „sid”. Se
            // resping: clientul face refresh și primește unul nou, cu sesiune.
            if (!Guid.TryParse(sidValue, out var sessionId))
                return "token fara sesiune (sid)";

            if (!Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                return "token fara utilizator";

            var roleInToken = principal.FindFirst(ClaimTypes.Role)?.Value;
            var now         = DateTime.UtcNow;

            var session = await _context.UserSessions
                .AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Select(s => new
                {
                    s.UserId,
                    s.RevokedAt,
                    s.ExpiresAt,
                    IsActive = s.User != null && s.User.IsActive,
                    Role     = s.User != null ? s.User.Role : default,
                })
                .FirstOrDefaultAsync(ct);

            if (session is null)                       return "sesiune inexistenta";
            if (session.UserId != userId)              return "sesiunea apartine altui utilizator";
            if (session.RevokedAt is not null)         return "sesiune inchisa";
            if (session.ExpiresAt <= now)              return "sesiune expirata";
            if (!session.IsActive)                     return "cont dezactivat";

            // Schimbarea rolului închide deja sesiunile. Verificarea rămâne ca a
            // doua barieră: rolul din token trebuie să fie rolul din baza de date.
            if (!string.Equals(roleInToken, session.Role.ToString(), StringComparison.Ordinal))
                return "rol schimbat";

            return null;
        }
    }
}
