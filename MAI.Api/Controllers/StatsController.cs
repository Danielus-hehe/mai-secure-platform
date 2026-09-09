using System.Diagnostics;
using MAI.Api.BackgroundJobs;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Storage;
using MAI.DataAccessLayer;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Controllers
{

    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class StatsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IFileStorage _storage;
        private readonly StorageOptions _storageOptions;
        private readonly TransferExpirationState _expirationState;
        private readonly TransferExpirationOptions _expirationOptions;
        private readonly ILogger<StatsController> _logger;

        public StatsController(
            AppDbContext context,
            IFileStorage storage,
            StorageOptions storageOptions,
            TransferExpirationState expirationState,
            TransferExpirationOptions expirationOptions,
            ILogger<StatsController> logger)
        {
            _context           = context;
            _storage           = storage;
            _storageOptions    = storageOptions;
            _expirationState   = expirationState;
            _expirationOptions = expirationOptions;
            _logger            = logger;
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Stats — pagina principala (orice utilizator autentificat)
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> GetStats(CancellationToken ct)
        {
            var yesterday = DateTime.UtcNow.AddHours(-24);

            var activeUsers      = await _context.Users.CountAsync(u => u.IsActive, ct);
            var totalTransfers   = await _context.FileTransfers.CountAsync(ct);
            var pendingTransfers = await _context.FileTransfers
                .CountAsync(t => t.Status == TransferStatus.Pending, ct);
            var totalDocuments   = await _context.Documents.CountAsync(ct);

            // Cifra ramane in raspuns pentru compatibilitate cu DashboardPage,
            // dar se completeaza doar pentru rolurile care au dreptul sa o vada.
            var canSeeSecurityMetrics =
                User.IsInRole(nameof(UserRole.Administrator)) ||
                User.IsInRole(nameof(UserRole.SefDirectie));

            var failedLoginsLast24h = canSeeSecurityMetrics
                ? await _context.AuditLogs.CountAsync(a =>
                    a.Action == AuditAction.Login &&
                    a.Result == AuditResult.Failure &&
                    a.Timestamp >= yesterday, ct)
                : 0;

            var recentTransfers = await _context.FileTransfers
                .AsNoTracking()
                .Include(t => t.Sender)
                .Include(t => t.Recipient)
                .OrderByDescending(t => t.CreatedAt)
                .Take(5)
                .Select(t => new
                {
                    id            = t.Id,
                    fileName      = t.FileName,
                    fileSize      = t.FileSize,
                    senderName    = t.Sender != null ? (t.Sender.FullName ?? t.Sender.Username) : "—",
                    recipientName = t.Recipient != null ? (t.Recipient.FullName ?? t.Recipient.Username) : "—",
                    status        = t.Status.ToString(),
                    createdAt     = t.CreatedAt,
                })
                .ToListAsync(ct);

            return Ok(new
            {
                activeUsers,
                totalTransfers,
                pendingTransfers,
                totalDocuments,
                failedLoginsLast24h,
                recentTransfers,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Stats/admin — panoul de administrare
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Toate cifrele de pe /admin, intr-un singur apel. Un singur round-trip
        /// in loc de sase: pagina fie se incarca intreaga, fie afiseaza o eroare —
        /// nu ajunge in starea hibrida in care doua carduri au date si patru arata
        /// zero fara explicatie.
        /// </summary>
        [HttpGet("admin")]
        [Authorize(Roles = nameof(UserRole.Administrator))]
        public async Task<IActionResult> GetAdminStats(
            [FromQuery] int days = 14,
            CancellationToken ct = default)
        {
            if (days is < 1 or > 90) days = 14;

            var now      = DateTime.UtcNow;
            var since    = now.Date.AddDays(-(days - 1));
            var last24h  = now.AddHours(-24);
            var next24h  = now.AddHours(24);

            // ── Utilizatori ──────────────────────────────────────────────────
            var totalUsers   = await _context.Users.CountAsync(ct);
            var activeUsers  = await _context.Users.CountAsync(u => u.IsActive, ct);
            var lockedUsers  = await _context.Users
                .CountAsync(u => u.LockoutEndsAt != null && u.LockoutEndsAt > now, ct);

            // Utilizatorii fara chei nu pot primi fisiere criptate. E cifra pe care
            // un administrator trebuie sa o urmareasca: fiecare cont din lista asta
            // e un destinatar catre care uploadul va fi refuzat cu 400.
            var usersWithoutKeys = await _context.Users
                .CountAsync(u => u.IsActive && u.PublicKeyEncryption == null, ct);

            var usersByRole = await _context.Users
                .GroupBy(u => u.Role)
                .Select(g => new { role = g.Key, count = g.Count() })
                .ToListAsync(ct);

            // ── Transferuri ──────────────────────────────────────────────────
            var totalTransfers      = await _context.FileTransfers.CountAsync(ct);
            var pendingTransfers    = await _context.FileTransfers.CountAsync(t => t.Status == TransferStatus.Pending, ct);
            var downloadedTransfers = await _context.FileTransfers.CountAsync(t => t.Status == TransferStatus.Downloaded, ct);
            var expiredTransfers    = await _context.FileTransfers.CountAsync(t => t.Status == TransferStatus.Expired, ct);
            var encryptedTransfers  = await _context.FileTransfers.CountAsync(t => t.IsEncrypted, ct);

            // Coada jobului de expirare: randuri trecute de ExpiresAt dar inca
            // nemarcate. Daca numarul creste de la o reincarcare la alta, jobul nu
            // tine pasul sau a murit.
            var awaitingPurge = await _context.FileTransfers
                .CountAsync(t => t.ExpiresAt != null
                              && t.ExpiresAt < now
                              && t.Status != TransferStatus.Expired, ct);

            var expiringNext24h = await _context.FileTransfers
                .CountAsync(t => t.ExpiresAt != null
                              && t.ExpiresAt >= now
                              && t.ExpiresAt < next24h
                              && t.Status != TransferStatus.Expired, ct);

            // Contabilitatea se face pe cifrotext, nu pe dimensiunea in clar:
            // octetii care ocupa efectiv spatiu in bucket sunt cei criptati.
            // Transferurile deja expirate nu mai au obiect in depozit, deci nu intra.
            var storedCiphertextBytes = await _context.FileTransfers
                .Where(t => t.Status != TransferStatus.Expired)
                .SumAsync(t => (long?)t.CiphertextSize, ct) ?? 0;

            var totalCiphertextBytes = await _context.FileTransfers
                .SumAsync(t => (long?)t.CiphertextSize, ct) ?? 0;

            // ── Documente ────────────────────────────────────────────────────
            var totalDocuments       = await _context.Documents.CountAsync(ct);
            var totalDocumentVersions = await _context.DocumentVersions.CountAsync(ct);

            // ── Securitate ───────────────────────────────────────────────────
            var failedLoginsLast24h = await _context.AuditLogs.CountAsync(a =>
                a.Action == AuditAction.Login &&
                a.Result == AuditResult.Failure &&
                a.Timestamp >= last24h, ct);

            var successfulLoginsLast24h = await _context.AuditLogs.CountAsync(a =>
                a.Action == AuditAction.Login &&
                a.Result == AuditResult.Success &&
                a.Timestamp >= last24h, ct);

            // Semnaturi invalide raportate de clienti. Zero e valoarea asteptata;
            // orice altceva inseamna fie o cheie schimbata fara reimpachetare, fie
            // ceva ce merita investigat manual.
            var invalidSignatures = await _context.AuditLogs.CountAsync(a =>
                a.Action == AuditAction.FileDownload &&
                a.Result == AuditResult.Warning, ct);

            // ── Serii temporale ──────────────────────────────────────────────
            // Gruparea se face in baza de date (date_trunc), nu in memorie: pe un
            // jurnal de audit de sute de mii de randuri, aducerea tuturor in proces
            // ca sa numeri pe zile e exact genul de query care omoara serverul.
            var auditByDay = await _context.AuditLogs
                .Where(a => a.Timestamp >= since)
                .GroupBy(a => new { Day = a.Timestamp.Date, a.Action })
                .Select(g => new { g.Key.Day, g.Key.Action, Count = g.Count() })
                .ToListAsync(ct);

            var transfersByDay = await _context.FileTransfers
                .Where(t => t.CreatedAt >= since)
                .GroupBy(t => t.CreatedAt.Date)
                .Select(g => new { Day = g.Key, Count = g.Count(), Bytes = g.Sum(x => x.CiphertextSize) })
                .ToListAsync(ct);

            var failedLoginsByDay = await _context.AuditLogs
                .Where(a => a.Timestamp >= since
                         && a.Action == AuditAction.Login
                         && a.Result == AuditResult.Failure)
                .GroupBy(a => a.Timestamp.Date)
                .Select(g => new { Day = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            // Zilele fara activitate lipsesc din rezultatul SQL. Daca le-am trimite
            // asa, graficul ar comprima axa si ar arata o crestere care nu exista.
            // Completam seria pe client-side-ul serverului, nu in React.
            var series = new List<object>(days);
            for (var i = 0; i < days; i++)
            {
                var day = since.AddDays(i);

                var uploads = auditByDay
                    .Where(x => x.Day == day && x.Action == AuditAction.FileUpload)
                    .Sum(x => x.Count);

                var downloads = auditByDay
                    .Where(x => x.Day == day && x.Action == AuditAction.FileDownload)
                    .Sum(x => x.Count);

                var expirations = auditByDay
                    .Where(x => x.Day == day && x.Action == AuditAction.TransferExpired)
                    .Sum(x => x.Count);

                var created = transfersByDay.FirstOrDefault(x => x.Day == day);
                var failed  = failedLoginsByDay.FirstOrDefault(x => x.Day == day);

                series.Add(new
                {
                    date         = day.ToString("yyyy-MM-dd"),
                    label        = day.ToString("dd.MM"),
                    uploads,
                    downloads,
                    expirations,
                    transfers    = created?.Count ?? 0,
                    bytes        = created?.Bytes ?? 0,
                    failedLogins = failed?.Count ?? 0,
                });
            }

            // ── Top expeditori ───────────────────────────────────────────────
            var topSenders = await _context.FileTransfers
                .Where(t => t.CreatedAt >= since)
                .Include(t => t.Sender)
                .GroupBy(t => new { t.SenderId, Name = t.Sender!.FullName ?? t.Sender.Username })
                .Select(g => new
                {
                    userId = g.Key.SenderId,
                    name   = g.Key.Name,
                    count  = g.Count(),
                    bytes  = g.Sum(x => x.CiphertextSize),
                })
                .OrderByDescending(x => x.count)
                .Take(5)
                .ToListAsync(ct);

            // ── Starea modulelor ─────────────────────────────────────────────
            var (dbOnline, dbLatencyMs)           = await ProbeDatabaseAsync(ct);
            var (storageOnline, storageLatencyMs) = await ProbeStorageAsync(ct);

            return Ok(new
            {
                generatedAt = now,
                windowDays  = days,

                users = new
                {
                    total          = totalUsers,
                    active         = activeUsers,
                    inactive       = totalUsers - activeUsers,
                    locked         = lockedUsers,
                    withoutKeys    = usersWithoutKeys,
                    byRole = usersByRole.Select(x => new
                    {
                        role  = x.role.ToString(),
                        label = RoleLabel(x.role),
                        count = x.count,
                    }),
                },

                transfers = new
                {
                    total           = totalTransfers,
                    pending         = pendingTransfers,
                    downloaded      = downloadedTransfers,
                    expired         = expiredTransfers,
                    encrypted       = encryptedTransfers,
                    // Procentul de transferuri care trec efectiv prin plicul E2E.
                    // Inlocuieste "Integritate sistem: 100%", care era o constanta.
                    encryptedPercent = totalTransfers == 0
                        ? 100
                        : (int)Math.Round(encryptedTransfers * 100.0 / totalTransfers),
                    awaitingPurge   = awaitingPurge,
                    expiringNext24h = expiringNext24h,
                },

                storage = new
                {
                    provider              = _storage.ProviderName,
                    bucket                = _storageOptions.Bucket,
                    presignedDownload     = _storageOptions.UsePresignedDownload && _storage.SupportsPresignedUrls,
                    maxFileSizeMb         = _storageOptions.MaxFileSizeMb,
                    storedCiphertextBytes,
                    totalCiphertextBytes,
                },

                documents = new
                {
                    total    = totalDocuments,
                    versions = totalDocumentVersions,
                },

                security = new
                {
                    failedLoginsLast24h,
                    successfulLoginsLast24h,
                    invalidSignatures,
                },

                series,
                topSenders,

                expirationJob = new
                {
                    enabled         = _expirationOptions.Enabled,
                    intervalMinutes = _expirationOptions.IntervalMinutes,
                    purgeObjects    = _expirationOptions.PurgeObjects,
                    status          = _expirationState.Status,
                    lastRunAt       = _expirationState.LastRunAt,
                    lastSuccessAt   = _expirationState.LastSuccessAt,
                    lastError       = _expirationState.LastError,
                    runCount        = _expirationState.RunCount,
                    lastRunExpired  = _expirationState.LastRunExpired,
                    lastRunPurged   = _expirationState.LastRunPurged,
                    expiredTotal    = _expirationState.ExpiredTotal,
                    purgedTotal     = _expirationState.PurgedTotal,
                    failedPurgeTotal = _expirationState.FailedPurgeTotal,
                },

                modules = new[]
                {
                    new { name = "API (.NET 8)",                    online = true,          latencyMs = 0 },
                    new { name = "Baza de date (PostgreSQL)",       online = dbOnline,      latencyMs = dbLatencyMs },
                    new { name = $"Depozit fisiere ({_storage.ProviderName})", online = storageOnline, latencyMs = storageLatencyMs },
                    new { name = "Job expirare transferuri",        online = _expirationOptions.Enabled && _expirationState.Status != "Eroare", latencyMs = 0 },
                },
            });
        }
        
        [HttpPost("expiration/run")]
        [Authorize(Roles = nameof(UserRole.Administrator))]
        public async Task<IActionResult> RunExpiration(
            [FromServices] TransferExpirationService job,
            CancellationToken ct)
        {
            if (!_expirationOptions.Enabled)
                return StatusCode(503, new { message = "Jobul de expirare este dezactivat din configurare." });

            try
            {
                var (expired, purged, failed) = await job.RunOnceAsync(ct);

                return Ok(new
                {
                    message = failed == 0
                        ? $"{expired} transferuri expirate, {purged} obiecte sterse din depozit."
                        : $"{expired} transferuri expirate, {purged} obiecte sterse, {failed} esecuri la stergere.",
                    expired,
                    purged,
                    failed,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rularea manuala a jobului de expirare a esuat.");
                return StatusCode(500, new { message = "Rularea jobului a esuat. Verificati jurnalul serverului." });
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        private static string RoleLabel(UserRole role) => role switch
        {
            UserRole.Utilizator    => "Utilizator",
            UserRole.SefDirectie   => "Sef de Directie",
            UserRole.Administrator => "Administrator",
            _                      => role.ToString(),
        };

        /// <summary>
        /// Latenta reala a bazei de date, masurata pe o interogare triviala.
        /// Inlocuieste "24ms" scris in JSX.
        /// </summary>
        private async Task<(bool Online, int LatencyMs)> ProbeDatabaseAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var canConnect = await _context.Database.CanConnectAsync(ct);
                sw.Stop();
                return (canConnect, (int)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogWarning(ex, "Sonda catre baza de date a esuat.");
                return (false, (int)sw.ElapsedMilliseconds);
            }
        }
        
        private async Task<(bool Online, int LatencyMs)> ProbeStorageAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await _storage.ExistsAsync(
                    $"{_storageOptions.TransfersPrefix}/.healthcheck-{Guid.NewGuid():N}", ct);
                sw.Stop();
                return (true, (int)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogWarning(ex, "Sonda catre depozitul de fisiere a esuat.");
                return (false, (int)sw.ElapsedMilliseconds);
            }
        }
    }
}