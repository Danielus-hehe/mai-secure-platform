using System.Security.Claims;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAI.Api.Middleware
{
    /// <summary>
    /// Transformă o salvare respinsă din cauza concurenței în 409, cu mesaj și
    /// cu urmă în jurnalul de audit.
    ///
    /// Două surse:
    ///   - <see cref="DbUpdateConcurrencyException"/>: rândul s-a schimbat între
    ///     citire și scriere (tokenul xmin de pe Users, UserSessions, Documents);
    ///   - <see cref="DbUpdateException"/> cu SQLSTATE 23505: un index unic a
    ///     refuzat rândul (de exemplu două versiuni cu același număr).
    ///
    /// De ce central și nu în fiecare controller: tokenurile sunt pe entități
    /// salvate din zeci de locuri. Fără acest strat, fiecare conflict ar ieși ca
    /// 500 fără mesaj, iar un 500 spune „eroare de server”, deși serverul a făcut
    /// exact ce trebuia: a refuzat să piardă o scriere.
    ///
    /// Răspunsul e intenționat identic pentru orice conflict. La /2fa/verify, o
    /// cerere cu cod corect și una cu cod greșit care pierd cursa primesc
    /// același 409: rafala de cereri paralele nu află nimic despre coduri.
    ///
    /// Alte excepții trec mai departe neatinse: tratarea lor (pagina de erori
    /// din Development, 500 în rest) rămâne cea de dinainte.
    /// </summary>
    public sealed class ConcurrencyConflictMiddleware
    {
        public const string ConflictCode = "CONCURRENCY_CONFLICT";

        private const string UniqueViolation = "23505";

        private readonly RequestDelegate _next;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<ConcurrencyConflictMiddleware> _logger;

        public ConcurrencyConflictMiddleware(
            RequestDelegate next,
            IServiceScopeFactory scopes,
            ILogger<ConcurrencyConflictMiddleware> logger)
        {
            _next   = next;
            _scopes = scopes;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex) when (Describe(ex) is { } what && !context.Response.HasStarted)
            {
                _logger.LogWarning(
                    "Conflict de concurenta pe {Method} {Path}: {What}, IP={Ip}",
                    context.Request.Method, context.Request.Path.Value, what,
                    context.Connection.RemoteIpAddress?.ToString());

                await WriteAuditAsync(context, what);

                // Fără Response.Clear(): ar șterge și antetele puse deja de
                // straturile exterioare (CORS, antetele de securitate). În modul
                // de dezvoltare, fără antetul CORS, browserul de pe :5173 nici
                // n-ar putea citi mesajul de mai jos. Răspunsul nu a pornit
                // (verificat în filtru), deci corpul e încă gol.
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Datele au fost modificate între timp de o altă operație. " +
                              "Reîncărcați pagina și încercați din nou.",
                    code    = ConflictCode,
                });
            }
        }

        /// <summary>
        /// Descrierea conflictului, pentru jurnal, sau null dacă excepția nu e un
        /// conflict. Publică și statică: se testează fără bază de date.
        /// </summary>
        public static string? Describe(Exception ex)
        {
            if (ex is DbUpdateConcurrencyException concurrency)
            {
                var entities = concurrency.Entries
                    .Select(e => e.Metadata.ClrType.Name)
                    .Distinct()
                    .ToList();

                return entities.Count > 0
                    ? $"rand modificat intre citire si scriere ({string.Join(", ", entities)})"
                    : "rand modificat intre citire si scriere";
            }

            if (ex is DbUpdateException { InnerException: PostgresException { SqlState: UniqueViolation } pg })
                return $"index unic incalcat ({pg.ConstraintName ?? pg.TableName ?? "necunoscut"})";

            return null;
        }

        /// <summary>
        /// Rândul de audit se scrie într-un context NOU. Cel al cererii conține
        /// exact modificările respinse; o nouă salvare pe el ar eșua din nou.
        /// Un eșec aici nu schimbă răspunsul: conflictul e deja în log.
        /// </summary>
        private async Task WriteAuditAsync(HttpContext context, string what)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                Guid? userId = Guid.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
                    ? id
                    : null;

                db.AuditLogs.Add(new AuditLog
                {
                    UserId    = userId,
                    Username  = context.User.Identity?.Name ?? "anonim",
                    Action    = AuditAction.ConcurrencyConflict,
                    Details   = $"{context.Request.Method} {context.Request.Path.Value}: {what}",
                    Result    = AuditResult.Failure,
                    IpAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    Timestamp = DateTime.UtcNow,
                });

                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Conflictul de concurenta nu a putut fi scris in jurnalul de audit.");
            }
        }
    }

    public static class ConcurrencyConflictMiddlewareExtensions
    {
        public static IApplicationBuilder UseConcurrencyConflicts(this IApplicationBuilder app) =>
            app.UseMiddleware<ConcurrencyConflictMiddleware>();
    }
}
