using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Services
{
    /// <summary>
    /// Ciclul de viață al sesiunilor: creare la login, rotație la refresh,
    /// revocare la logout sau de la distanță.
    ///
    /// Metodele modifică entitățile și pun modificările în contextul curent, dar
    /// NU salvează. Apelantul decide când face SaveChanges, ca revocarea unei
    /// sesiuni și rândul de audit corespunzător să intre în aceeași tranzacție -
    /// altfel poți avea o sesiune revocată fără urmă în jurnal, sau invers.
    /// </summary>
    public interface ISessionService
    {
        /// <summary>Deschide o sesiune pentru dispozitivul curent.</summary>
        /// <param name="sessionId">
        /// Identificatorul ales de apelant ÎNAINTE de emiterea tokenurilor: el
        /// intră în JWT (claim-ul „sid”), deci trebuie cunoscut înainte ca
        /// rândul să existe.
        /// </param>
        UserSession Create(
            Guid sessionId, User user, string refreshTokenHash, DateTime expiresAt,
            string? userAgent, string? ipAddress);

        /// <summary>
        /// Găsește sesiunea activă căreia îi aparține un refresh token.
        /// Întoarce null dacă tokenul e necunoscut, revocat sau expirat.
        /// </summary>
        Task<UserSession?> FindActiveAsync(string refreshTokenHash, CancellationToken ct);

        /// <summary>
        /// Găsește sesiunea încă deschisă al cărei token ANTERIOR (înlocuit la
        /// ultima rotație) are acest hash. Un rezultat înseamnă că tokenul a
        /// fost copiat: clientul legitim nu retrimite un token deja rotit.
        /// </summary>
        Task<UserSession?> FindActiveByPreviousTokenAsync(string refreshTokenHash, CancellationToken ct);

        /// <summary>
        /// Rotește tokenul unei sesiuni existente. Rândul rămâne același, deci
        /// utilizatorul nu vede o sesiune nouă la fiecare reîmprospătare.
        /// Noua expirare nu trece de <see cref="UserSession.AbsoluteExpiresAt"/>.
        /// </summary>
        void Rotate(UserSession session, string newRefreshTokenHash, DateTime newExpiresAt);

        /// <summary>Închide o sesiune. Idempotentă: o sesiune deja revocată nu se rescrie.</summary>
        void Revoke(UserSession session, string reason);

        /// <summary>
        /// Închide toate sesiunile active ale unui utilizator, opțional cu o
        /// excepție (sesiunea curentă, la „deconectează celelalte dispozitive”).
        /// Întoarce câte au fost închise.
        /// </summary>
        Task<int> RevokeAllAsync(
            Guid userId, string reason, Guid? exceptSessionId, CancellationToken ct);

        /// <summary>Sesiunile unui utilizator, cele active întâi, apoi cele recent închise.</summary>
        Task<List<UserSession>> ListAsync(Guid userId, CancellationToken ct);
    }

    public sealed class SessionService : ISessionService
    {
        private readonly AppDbContext _context;

        /// <summary>
        /// Câte sesiuni închise se păstrează în listă. Istoricul complet rămâne
        /// în jurnalul de audit; pagina de sesiuni e un instrument de acțiune, nu
        /// o arhivă, iar o listă de două sute de rânduri moarte ascunde exact
        /// sesiunea activă pe care utilizatorul o caută.
        /// </summary>
        private const int ClosedSessionsShown = 10;

        /// <summary>
        /// User-Agent-urile pot fi arbitrar de lungi și vin de la client.
        /// Trunchierea se face aici, o dată, nu în fiecare apelant.
        /// </summary>
        private const int MaxUserAgentChars = 256;

        private readonly JwtOptions _jwt;

        public SessionService(AppDbContext context, JwtOptions jwt)
        {
            _context = context;
            _jwt     = jwt;
        }

        public UserSession Create(
            Guid sessionId, User user, string refreshTokenHash, DateTime expiresAt,
            string? userAgent, string? ipAddress)
        {
            var now      = DateTime.UtcNow;
            var absolute = now.AddHours(_jwt.SessionAbsoluteHours);

            var session = new UserSession
            {
                Id                = sessionId,
                UserId            = user.Id,
                RefreshTokenHash  = refreshTokenHash,
                CreatedAt         = now,
                LastSeenAt        = now,
                ExpiresAt         = SessionLifetime.Cap(expiresAt, absolute),
                AbsoluteExpiresAt = absolute,
                UserAgent        = Truncate(userAgent, MaxUserAgentChars),
                IpAddress        = Truncate(ipAddress, 64),
            };

            _context.UserSessions.Add(session);
            return session;
        }

        public async Task<UserSession?> FindActiveAsync(string refreshTokenHash, CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            // Filtrarea pe RevokedAt și ExpiresAt se face în baza de date, nu în
            // memorie prin proprietatea IsActive: aceasta e calculată în C# și
            // EF nu o poate traduce în SQL.
            return await _context.UserSessions
                .Include(s => s.User)
                .FirstOrDefaultAsync(
                    s => s.RefreshTokenHash == refreshTokenHash
                      && s.RevokedAt == null
                      && s.ExpiresAt > now,
                    ct);
        }

        public async Task<UserSession?> FindActiveByPreviousTokenAsync(string refreshTokenHash, CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            // Doar sesiuni încă deschise: una deja închisă sau expirată nu mai
            // are ce pierde, iar un token vechi al ei e simplu invalid.
            return await _context.UserSessions
                .Include(s => s.User)
                .FirstOrDefaultAsync(
                    s => s.PreviousRefreshTokenHash == refreshTokenHash
                      && s.RevokedAt == null
                      && s.ExpiresAt > now,
                    ct);
        }

        public void Rotate(UserSession session, string newRefreshTokenHash, DateTime newExpiresAt)
        {
            // Tokenul înlocuit se păstrează (ca hash) pentru detectarea
            // refolosirii; vezi UserSession.PreviousRefreshTokenHash.
            session.PreviousRefreshTokenHash = session.RefreshTokenHash;
            session.RefreshTokenHash         = newRefreshTokenHash;
            session.ExpiresAt                = SessionLifetime.Cap(newExpiresAt, session.AbsoluteExpiresAt);
            session.LastSeenAt               = DateTime.UtcNow;
        }

        public void Revoke(UserSession session, string reason)
        {
            if (session.RevokedAt is not null) return;

            session.RevokedAt     = DateTime.UtcNow;
            session.RevokedReason = Truncate(reason, 128);

            // Tokenul devine inutilizabil imediat, chiar dacă rândul rămâne:
            // FindActiveAsync filtrează pe RevokedAt, deci un token furat nu mai
            // poate produce nimic.
        }

        public async Task<int> RevokeAllAsync(
            Guid userId, string reason, Guid? exceptSessionId, CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            var sessions = await _context.UserSessions
                .Where(s => s.UserId == userId
                         && s.RevokedAt == null
                         && s.ExpiresAt > now
                         && (exceptSessionId == null || s.Id != exceptSessionId))
                .ToListAsync(ct);

            foreach (var session in sessions)
                Revoke(session, reason);

            return sessions.Count;
        }

        public async Task<List<UserSession>> ListAsync(Guid userId, CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            var active = await _context.UserSessions
                .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
                .OrderByDescending(s => s.LastSeenAt)
                .ToListAsync(ct);

            var closed = await _context.UserSessions
                .Where(s => s.UserId == userId && (s.RevokedAt != null || s.ExpiresAt <= now))
                .OrderByDescending(s => s.RevokedAt ?? s.ExpiresAt)
                .Take(ClosedSessionsShown)
                .ToListAsync(ct);

            return [.. active, .. closed];
        }

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : value.Length <= max ? value : value[..max];
    }
}

namespace MAI.Api.Services
{
    /// <summary>
    /// Regula duratei absolute, separată ca funcție pură: se testează fără bază
    /// de date și e aceeași la creare și la rotație.
    /// </summary>
    public static class SessionLifetime
    {
        /// <summary>
        /// Expirarea cerută, dar nu mai târziu de sfârșitul absolut al sesiunii.
        /// Un token emis cu 7 zile de valabilitate într-o sesiune care se încheie
        /// peste o oră primește o oră.
        /// </summary>
        public static DateTime Cap(DateTime requested, DateTime absolute) =>
            requested <= absolute ? requested : absolute;
    }
}
