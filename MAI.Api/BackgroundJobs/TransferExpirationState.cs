namespace MAI.Api.BackgroundJobs
{
    /// <summary>
    /// Exista ca sa poti raspunde la intrebarea "de unde stiu ca jobul chiar
    /// ruleaza?" fara sa deschizi logurile serverului. StatsController o
    /// expune pe /admin, iar comisia vede o valoare reala, nu un "Online" pictat.
    ///
    /// Singleton, scris de un singur fir (jobul) si citit de multe (cererile
    /// HTTP). Scrierile trec prin Interlocked / camp volatile, deci nu e nevoie
    /// de lock la citire.
    /// </summary>
    public class TransferExpirationState
    {
        private long _lastRunTicks;
        private long _lastSuccessTicks;

        private volatile string _status = "Nu a rulat inca";
        private volatile string? _lastError;

        private int _expiredTotal;
        private int _purgedTotal;
        private int _failedPurgeTotal;
        private int _lastRunExpired;
        private int _lastRunPurged;
        private int _runCount;

        public bool Enabled { get; set; } = true;

        public DateTime? LastRunAt =>
            Interlocked.Read(ref _lastRunTicks) is var t && t > 0
                ? new DateTime(t, DateTimeKind.Utc)
                : null;

        public DateTime? LastSuccessAt =>
            Interlocked.Read(ref _lastSuccessTicks) is var t && t > 0
                ? new DateTime(t, DateTimeKind.Utc)
                : null;

        public string Status => _status;
        public string? LastError => _lastError;

        public int ExpiredTotal => Volatile.Read(ref _expiredTotal);
        public int PurgedTotal => Volatile.Read(ref _purgedTotal);
        public int FailedPurgeTotal => Volatile.Read(ref _failedPurgeTotal);
        public int LastRunExpired => Volatile.Read(ref _lastRunExpired);
        public int LastRunPurged => Volatile.Read(ref _lastRunPurged);
        public int RunCount => Volatile.Read(ref _runCount);

        public void MarkRunStarted()
        {
            Interlocked.Exchange(ref _lastRunTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _runCount);
            _status = "In curs";
        }

        public void MarkRunSucceeded(int expired, int purged, int failedPurge)
        {
            Interlocked.Exchange(ref _lastSuccessTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _lastRunExpired, expired);
            Interlocked.Exchange(ref _lastRunPurged, purged);
            Interlocked.Add(ref _expiredTotal, expired);
            Interlocked.Add(ref _purgedTotal, purged);
            Interlocked.Add(ref _failedPurgeTotal, failedPurge);
            _lastError = null;
            _status = "OK";
        }

        public void MarkRunFailed(string error)
        {
            _lastError = error;
            _status = "Eroare";
        }

        public void MarkDisabled()
        {
            Enabled = false;
            _status = "Dezactivat";
        }
    }
}