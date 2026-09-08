using MAI.BusinessLogic.Interfaces;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.BackgroundJobs
{
    public class TransferExpirationService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TransferExpirationOptions _options;
        private readonly TransferExpirationState _state;
        private readonly ILogger<TransferExpirationService> _logger;
        
        private readonly SemaphoreSlim _gate = new(1, 1);

        public TransferExpirationService(
            IServiceScopeFactory scopeFactory,
            TransferExpirationOptions options,
            TransferExpirationState state,
            ILogger<TransferExpirationService> logger)
        {
            _scopeFactory = scopeFactory;
            _options      = options;
            _state        = state;
            _logger       = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _state.MarkDisabled();
                _logger.LogWarning(
                    "Jobul de expirare a transferurilor este DEZACTIVAT (TransferExpiration:Enabled=false). " +
                    "Transferurile expirate raman in depozit pana la lifecycle policy.");
                return;
            }

            _logger.LogInformation(
                "Job expirare transferuri: pornit. Interval {Interval} min, lot {Batch}, marja {Grace} min, purjare {Purge}.",
                _options.IntervalMinutes, _options.BatchSize, _options.GraceMinutes,
                _options.PurgeObjects ? "activata" : "DEZACTIVATA (doar marcare)");

            try
            {
                await Task.Delay(_options.InitialDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using var timer = new PeriodicTimer(_options.Interval);

            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Orice exceptie neprinsa aici ar opri definitiv BackgroundService-ul,
                    // fara ca aplicatia sa cada — adica jobul ar muri in tacere.
                    _state.MarkRunFailed(ex.Message);
                    _logger.LogError(ex, "Trecerea jobului de expirare a esuat. Se reincearca la urmatorul interval.");
                }
            }
            while (await SafeWaitAsync(timer, stoppingToken));

            _logger.LogInformation("Job expirare transferuri: oprit.");
        }

        private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
        {
            try { return await timer.WaitForNextTickAsync(ct); }
            catch (OperationCanceledException) { return false; }
        }

        /// <summary>
        /// O trecere completa. Publica pentru ca poate fi apelata si manual din
        /// StatsController (butonul "Ruleaza acum" de pe /admin).
        /// </summary>
        public async Task<(int Expired, int Purged, int FailedPurge)> RunOnceAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                return await RunOnceCoreAsync(ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<(int Expired, int Purged, int FailedPurge)> RunOnceCoreAsync(CancellationToken ct)
        {
            _state.MarkRunStarted();

            var cutoff = DateTime.UtcNow - _options.Grace;

            var totalExpired = 0;
            var totalPurged  = 0;
            var totalFailed  = 0;

            while (!ct.IsCancellationRequested)
            {
                // Scope nou per lot: DbContext-ul este Scoped, iar un context tinut
                // deschis peste mii de randuri acumuleaza entitati urmarite si
                // creste monoton in memorie.
                using var scope = _scopeFactory.CreateScope();

                var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

                var batch = await db.FileTransfers
                    .Where(t => t.ExpiresAt != null
                             && t.ExpiresAt < cutoff
                             && t.Status != TransferStatus.Expired)
                    .OrderBy(t => t.ExpiresAt)
                    .Take(_options.BatchSize)
                    .ToListAsync(ct);

                if (batch.Count == 0)
                    break;

                var purgedInBatch = 0;
                var failedInBatch = 0;

                foreach (var transfer in batch)
                {
                    var purged = false;

                    if (_options.PurgeObjects && !string.IsNullOrEmpty(transfer.StorageKey))
                    {
                        try
                        {
                            await storage.DeleteAsync(transfer.StorageKey, ct);
                            purged = true;
                            purgedInBatch++;
                        }
                        catch (Exception ex)
                        {
                            failedInBatch++;

                            // Nu marcam Expired. Randul ramane in coada si se
                            // reincearca la urmatoarea trecere. Utilizatorul nu e
                            // afectat: accesul e deja refuzat de controller pe baza
                            // lui ExpiresAt, indiferent de valoarea lui Status.
                            _logger.LogError(ex,
                                "Stergerea obiectului {Key} (transfer {Id}) a esuat. Se reincearca la urmatoarea trecere.",
                                transfer.StorageKey, transfer.Id);

                            continue;
                        }
                    }
                    else
                    {
                        // Fara obiect de sters (transfer vechi, fara StorageKey) sau
                        // purjare dezactivata: doar marcam.
                        purged = !_options.PurgeObjects || string.IsNullOrEmpty(transfer.StorageKey);
                    }

                    transfer.Status = TransferStatus.Expired;

                    db.AuditLogs.Add(new AuditLog
                    {
                        UserId    = null,
                        Username  = "sistem",
                        Action    = AuditAction.TransferExpired,
                        Details   = purged && _options.PurgeObjects && !string.IsNullOrEmpty(transfer.StorageKey)
                            ? $"SUCCES: Transfer expirat '{transfer.FileName}' (id {transfer.Id}); " +
                              $"cifrotext sters din depozit ({transfer.CiphertextSize} octeti)"
                            : $"SUCCES: Transfer expirat '{transfer.FileName}' (id {transfer.Id}); " +
                              "fara obiect de sters in depozit",
                        IpAddress = "sistem",
                        Timestamp = DateTime.UtcNow,
                    });
                }

                await db.SaveChangesAsync(ct);

                var markedInBatch = batch.Count - failedInBatch;

                totalExpired += markedInBatch;
                totalPurged  += purgedInBatch;
                totalFailed  += failedInBatch;

                _logger.LogInformation(
                    "Job expirare: lot procesat — {Marked} marcate, {Purged} obiecte sterse, {Failed} esecuri.",
                    markedInBatch, purgedInBatch, failedInBatch);

                // Lotul a fost mai mic decat maximul: nu mai are ce urma.
                if (batch.Count < _options.BatchSize)
                    break;

                // Toate randurile din lot au esuat la stergere: fara pauza am intra
                // intr-o bucla stransa pe acelasi lot pana la urmatorul interval.
                if (markedInBatch == 0)
                    break;
            }

            _state.MarkRunSucceeded(totalExpired, totalPurged, totalFailed);

            if (totalExpired > 0 || totalFailed > 0)
            {
                _logger.LogInformation(
                    "Job expirare: trecere incheiata — {Expired} transferuri expirate, {Purged} obiecte purjate, {Failed} esecuri.",
                    totalExpired, totalPurged, totalFailed);
            }

            return (totalExpired, totalPurged, totalFailed);
        }

        public override void Dispose()
        {
            _gate.Dispose();
            base.Dispose();
        }
    }
}