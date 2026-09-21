using MAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MAI.DataAccessLayer.Configurations
{
    public class OrgUnitConfiguration : IEntityTypeConfiguration<OrgUnit>
    {
        public void Configure(EntityTypeBuilder<OrgUnit> builder)
        {
            builder.ToTable("OrgUnits");
            builder.HasKey(u => u.Id);

            builder.Property(u => u.Name).IsRequired().HasMaxLength(200);
            builder.Property(u => u.Code).HasMaxLength(20);

            // DN-ul OU-ului din AD, dacă subdiviziunea a fost importată. Unic:
            // același OU nu poate ajunge de două ori în structură, oricâte
            // importuri se rulează.
            builder.Property(u => u.DirectoryDn).HasMaxLength(512);

            builder.HasIndex(u => u.DirectoryDn)
                .IsUnique()
                .HasFilter("\"DirectoryDn\" IS NOT NULL")
                .HasDatabaseName("UX_OrgUnits_DirectoryDn");

            // Restrict: o subdiviziune cu subunități nu se poate șterge. Ștergerea
            // în cascadă a unui arbore întreg dintr-un singur click ar lăsa
            // utilizatorii neîncadrați și documentele fără raport corect.
            builder.HasOne(u => u.Parent)
                .WithMany(u => u.Children)
                .HasForeignKey(u => u.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(u => u.ParentId)
                .HasDatabaseName("IX_OrgUnits_ParentId");

            // Șeful se pierde odată cu contul (SetNull), subdiviziunea rămâne.
            builder.HasOne(u => u.HeadUser)
                .WithMany()
                .HasForeignKey(u => u.HeadUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Un utilizator conduce cel mult o subdiviziune. „Subdiviziunea mea”
            // trebuie să aibă un singur înțeles pentru distribuție.
            builder.HasIndex(u => u.HeadUserId)
                .IsUnique()
                .HasFilter("\"HeadUserId\" IS NOT NULL")
                .HasDatabaseName("UX_OrgUnits_HeadUserId");
        }
    }
}
