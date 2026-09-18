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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfiguration(new FileTransferConfiguration());
            modelBuilder.ApplyConfiguration(new TransferRecipientConfiguration());
            modelBuilder.ApplyConfiguration(new AuditLogConfiguration());
            modelBuilder.ApplyConfiguration(new UserSessionConfiguration());
            modelBuilder.ApplyConfiguration(new UserConfiguration());
        }
    }
}
