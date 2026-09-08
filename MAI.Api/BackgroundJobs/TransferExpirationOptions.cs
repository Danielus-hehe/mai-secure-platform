namespace MAI.Api.BackgroundJobs
{
    public class TransferExpirationOptions
    {
        public bool Enabled { get; set; } = true;
        
        public int IntervalMinutes { get; set; } = 15;
        public int InitialDelaySeconds { get; set; } = 30;
        public int BatchSize { get; set; } = 200;
        public int GraceMinutes { get; set; } = 5;

        public bool PurgeObjects { get; set; } = true;

        public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes > 0 ? IntervalMinutes : 15);
        public TimeSpan InitialDelay => TimeSpan.FromSeconds(InitialDelaySeconds >= 0 ? InitialDelaySeconds : 30);
        public TimeSpan Grace => TimeSpan.FromMinutes(GraceMinutes >= 0 ? GraceMinutes : 0);

        public void Validate()
        {
            if (IntervalMinutes <= 0)
                throw new InvalidOperationException("TransferExpiration:IntervalMinutes trebuie sa fie > 0.");

            if (BatchSize is <= 0 or > 5000)
                throw new InvalidOperationException("TransferExpiration:BatchSize trebuie sa fie intre 1 si 5000.");

            if (GraceMinutes < 0)
                throw new InvalidOperationException("TransferExpiration:GraceMinutes nu poate fi negativ.");
        }
    }
}