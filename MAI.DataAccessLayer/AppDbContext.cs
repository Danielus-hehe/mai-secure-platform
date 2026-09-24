using Microsoft.EntityFrameworkCore;
using MAI.Domain.Entities;
using MAI.DataAccessLayer.Configurations;

namespace MAI.DataAccessLayer
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users => Set<User>();
        public DbSet<FileTransfer> FileTransfers => Set<FileTransfer>();
        public DbSet<TransferRecipient> TransferRecipients => Set<TransferRecipient>();
        public DbSet<Document> Documents => Set<Document>();
        public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<UserSession> UserSessions => Set<UserSession>();
        public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();
        public DbSet<OrgLevel> OrgLevels => Set<OrgLevel>();
        public DbSet<InternalDocument> InternalDocuments => Set<InternalDocument>();
        public DbSet<InternalDocumentTarget> InternalDocumentTargets => Set<InternalDocumentTarget>();
        public DbSet<InternalDocumentRecipient> InternalDocumentRecipients => Set<InternalDocumentRecipient>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfiguration(new FileTransferConfiguration());
            modelBuilder.ApplyConfiguration(new TransferRecipientConfiguration());
            modelBuilder.ApplyConfiguration(new AuditLogConfiguration());
            modelBuilder.ApplyConfiguration(new UserSessionConfiguration());
            modelBuilder.ApplyConfiguration(new UserConfiguration());
            modelBuilder.ApplyConfiguration(new OrgUnitConfiguration());
            modelBuilder.ApplyConfiguration(new OrgLevelConfiguration());
            modelBuilder.ApplyConfiguration(new InternalDocumentConfiguration());
            modelBuilder.ApplyConfiguration(new InternalDocumentTargetConfiguration());
            modelBuilder.ApplyConfiguration(new InternalDocumentRecipientConfiguration());
        }

        // Toate variantele publice de SaveChanges ajung în aceste două metode,
        // deci limitarea jurnalului de audit se aplică oricum ar fi apelată
        // salvarea. Vezi AuditLogLimits.
        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            AuditLogLimits.Apply(ChangeTracker);
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            AuditLogLimits.Apply(ChangeTracker);
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }
}
