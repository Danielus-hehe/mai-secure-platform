using MAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MAI.DataAccessLayer.Configurations
{
    public class InternalDocumentConfiguration : IEntityTypeConfiguration<InternalDocument>
    {
        public void Configure(EntityTypeBuilder<InternalDocument> builder)
        {
            builder.ToTable("InternalDocuments");
            builder.HasKey(d => d.Id);

            builder.Property(d => d.Title).IsRequired().HasMaxLength(300);
            builder.Property(d => d.Number).HasMaxLength(64);
            builder.Property(d => d.Summary).HasMaxLength(2000);
            builder.Property(d => d.FileName).IsRequired().HasMaxLength(260);
            builder.Property(d => d.ContentType).IsRequired().HasMaxLength(128);
            builder.Property(d => d.Sha256).IsRequired().HasMaxLength(64);
            builder.Property(d => d.StorageKey).IsRequired().HasMaxLength(512);
            builder.Property(d => d.RepealedReason).HasMaxLength(500);

            builder.HasOne(d => d.Author)
                .WithMany()
                .HasForeignKey(d => d.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(d => d.AuthorOrgUnit)
                .WithMany()
                .HasForeignKey(d => d.AuthorOrgUnitId)
                .OnDelete(DeleteBehavior.SetNull);

            // „Create de mine”, ordonate după dată.
            builder.HasIndex(d => new { d.AuthorId, d.CreatedAt })
                .HasDatabaseName("IX_InternalDocuments_Author_CreatedAt");

            builder.HasIndex(d => d.Status)
                .HasDatabaseName("IX_InternalDocuments_Status");
        }
    }

    public class InternalDocumentTargetConfiguration : IEntityTypeConfiguration<InternalDocumentTarget>
    {
        public void Configure(EntityTypeBuilder<InternalDocumentTarget> builder)
        {
            builder.ToTable("InternalDocumentTargets");
            builder.HasKey(t => new { t.DocumentId, t.Kind, t.TargetId });

            // Fără cheie străină pe TargetId: indică fie o subdiviziune, fie un
            // utilizator. E doar evidența alegerii autorului; lista care contează
            // juridic e InternalDocumentRecipients.
            builder.HasOne(t => t.Document)
                .WithMany(d => d.Targets)
                .HasForeignKey(t => t.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    public class InternalDocumentRecipientConfiguration : IEntityTypeConfiguration<InternalDocumentRecipient>
    {
        public void Configure(EntityTypeBuilder<InternalDocumentRecipient> builder)
        {
            builder.ToTable("InternalDocumentRecipients");
            builder.HasKey(r => new { r.DocumentId, r.UserId });

            builder.HasOne(r => r.Document)
                .WithMany(d => d.Recipients)
                .HasForeignKey(r => r.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(r => r.OrgUnit)
                .WithMany()
                .HasForeignKey(r => r.OrgUnitId)
                .OnDelete(DeleteBehavior.SetNull);

            // „Documentele mele de confirmat” și contorul din bara de sus.
            builder.HasIndex(r => new { r.UserId, r.AcknowledgedAt })
                .HasDatabaseName("IX_InternalDocumentRecipients_User_Ack");
        }
    }
}
