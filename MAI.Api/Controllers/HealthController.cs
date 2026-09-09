using System.Diagnostics;
using MAI.BusinessLogic.Interfaces;
using MAI.DataAccessLayer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Starea dependențelor externe: baza de date și stocarea de obiecte.
    ///
    /// Anonim intenționat: un health check care cere autentificare nu poate fi
    /// folosit de un load balancer sau de Docker <c>HEALTHCHECK</c>, adică exact
    /// de cine are nevoie de el. În schimb, răspunsul nu conține niciodată
    /// stringuri de conexiune, endpointuri sau mesaje de excepție — cine sondează
    /// endpointul află „merge / nu merge”, nu topologia sistemului.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/health")]
    public class HealthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IFileStorage _storage;
        private readonly ILogger<HealthController> _logger;

        /// <summary>
        /// Peste atât, dependența e considerată căzută. O sondă care așteaptă
        /// 30 de secunde după o bază de date moartă blochează chiar procesul de
        /// rotire pe care ar trebui să-l declanșeze.
        /// </summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        public HealthController(AppDbContext context, IFileStorage storage, ILogger<HealthController> logger)
        {
            _context = context;
            _storage = storage;
            _logger  = logger;
        }

        /// <summary>
        /// GET /api/health — sondă de disponibilitate (readiness).
        /// 200 dacă toate dependențele răspund, 503 dacă măcar una nu.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            var database = await ProbeAsync("postgresql",
                async token => await _context.Database.CanConnectAsync(token), cts.Token);

            var storage = await ProbeAsync("storage",
                async token => await _storage.HealthCheckAsync(token), cts.Token);

            var healthy = database.Healthy && storage.Healthy;

            var payload = new
            {
                status = healthy ? "healthy" : "unhealthy",
                timestamp = DateTime.UtcNow,
                provider = _storage.ProviderName,
                checks = new[] { database, storage },
            };

            // 503, nu 200 cu un câmp „status”: orchestratoarele citesc codul de
            // stare, nu corpul răspunsului.
            return healthy
                ? Ok(payload)
                : StatusCode(StatusCodes.Status503ServiceUnavailable, payload);
        }

        /// <summary>
        /// GET /api/health/live — sondă de viață (liveness).
        /// Nu atinge nicio dependență: răspunde cât timp procesul mai poate servi
        /// cereri. Dacă ar verifica baza de date, o indisponibilitate temporară a
        /// bazei ar face orchestratorul să repornească un proces perfect sănătos.
        /// </summary>
        [HttpGet("live")]
        public IActionResult Live() => Ok(new { status = "alive", timestamp = DateTime.UtcNow });

        // ─────────────────────────────────────────────────────────────────────

        private async Task<HealthCheckResult> ProbeAsync(
            string name, Func<CancellationToken, Task<bool>> probe, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var ok = await probe(ct);
                stopwatch.Stop();

                return new HealthCheckResult(name, ok, (int)stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                _logger.LogWarning("Health check {Name}: timeout dupa {Ms} ms", name, stopwatch.ElapsedMilliseconds);
                return new HealthCheckResult(name, false, (int)stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // Excepția se jurnalizează, nu se returnează: mesajul unei erori
                // Npgsql conține host, port și nume de bază de date.
                _logger.LogWarning(ex, "Health check {Name}: esuat", name);
                return new HealthCheckResult(name, false, (int)stopwatch.ElapsedMilliseconds);
            }
        }

        private sealed record HealthCheckResult(string Name, bool Healthy, int DurationMs);
    }
}
