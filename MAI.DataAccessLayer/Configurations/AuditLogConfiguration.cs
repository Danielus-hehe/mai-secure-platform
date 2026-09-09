using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MAI.Domain.Entities;

namespace MAI.DataAccessLayer.Configurations
{
    /// <summary>
    /// Configurarea jurnalului de audit. Tabela crește monoton și nu se șterge
    /// niciodată, deci indexurile de aici sunt cele care decid dacă pagina
    /// /audit rămâne utilizabilă peste un an.
    /// </summary>
    public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
    {
        public void Configure(EntityTypeBuilder<AuditLog> builder)
        {
            builder.HasKey(a => a.Id);

            builder.Property(a => a.Username)
                .IsRequired()
                .HasMaxLength(128);

            // Fără limită, Npgsql creează 'text' nemărginit. Un Details generat
            // dintr-un nume de fișier controlat de utilizator poate fi oricât.
            builder.Property(a => a.Details)
                .IsRequired()
                .HasMaxLength(1024);

            builder.Property(a => a.IpAddress)
                .HasMaxLength(64);        // IPv6 cu zonă încape confortabil

            // Persistat ca int, nu ca string: o coloană de tip enum stocată ca
            // text ar readuce exact problema pe care migrarea o rezolvă.
            builder.Property(a => a.Result)
                .HasConversion<int>()
                .HasDefaultValue(Domain.Enums.AuditResult.Success);

            // ── Indexuri ─────────────────────────────────────────────────────
            // Pagina de audit sortează întotdeauna descrescător pe Timestamp, iar
            // filtrele cele mai folosite sunt utilizator, acțiune și rezultat.

            builder.HasIndex(a => a.Timestamp)
                .IsDescending()
                .HasDatabaseName("IX_AuditLogs_Timestamp");

            builder.HasIndex(a => new { a.Username, a.Timestamp })
                .HasDatabaseName("IX_AuditLogs_Username_Timestamp");

            // StatsController numără eșecurile de login din ultimele 24h la
            // fiecare încărcare a dashboardului. Fără index compus, asta e un
            // scan complet pe cea mai mare tabelă din sistem.
            builder.HasIndex(a => new { a.Action, a.Result, a.Timestamp })
                .HasDatabaseName("IX_AuditLogs_Action_Result_Timestamp");
        }
    }
}
