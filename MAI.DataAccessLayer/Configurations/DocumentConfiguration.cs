using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MAI.Domain.Entities;

namespace MAI.DataAccessLayer.Configurations
{
    /// <summary>
    /// Documentele normative. Restul coloanelor rămân pe convenții, exact ca
    /// înainte de această clasă: aici intră doar ce convențiile nu pot ști.
    /// </summary>
    public class DocumentConfiguration : IEntityTypeConfiguration<Document>
    {
        public void Configure(EntityTypeBuilder<Document> builder)
        {
            // Token de concurență pe xmin. Adăugarea unei versiuni citește
            // CurrentVersion, îl crește și îl scrie înapoi; două încărcări
            // simultane citeau aceeași valoare. Cu tokenul, a doua salvare
            // eșuează în loc să producă două versiuni cu același număr.
            builder.Property(d => d.Version)
                .IsRowVersion()
                .HasColumnName("xmin")
                .HasColumnType("xid");
        }
    }

    /// <summary>
    /// Versiunile documentelor normative.
    /// </summary>
    public class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
    {
        public void Configure(EntityTypeBuilder<DocumentVersion> builder)
        {
            // A doua barieră, independentă de token: chiar dacă cineva scrie
            // o versiune pe altă cale (import, SQL, alt endpoint), baza refuză
            // două rânduri cu același număr pentru același document. Indexul
            // începe cu DocumentId, deci acoperă și căutarea după document;
            // vechiul IX_DocumentVersions_DocumentId devine redundant și se
            // șterge în migrare.
            builder.HasIndex(v => new { v.DocumentId, v.VersionNumber })
                .IsUnique()
                .HasDatabaseName("UX_DocumentVersions_DocumentId_VersionNumber");
        }
    }
}
