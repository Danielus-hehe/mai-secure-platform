using MAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MAI.DataAccessLayer.Configurations
{
    public class TransferRecipientConfiguration : IEntityTypeConfiguration<TransferRecipient>
    {
        public void Configure(EntityTypeBuilder<TransferRecipient> builder)
        {
            // ── Cheia primară compusă ────────────────────────────────────────
            // Același utilizator nu poate apărea de două ori ca destinatar al
            // aceluiași transfer: combinația (TransferId, UserId) este unică.
            builder.HasKey(r => new { r.TransferId, r.UserId });

            // ── Relații ──────────────────────────────────────────────────────

            // Cascada rămâne doar pentru consistență referențială la un DELETE
            // manual în baza de date. Aplicația nu mai șterge fizic transferuri:
            // DELETE /api/Transfers/{id} face ștergere logică, tocmai ca rândurile
            // de aici — cu dovezile de primire — să nu dispară.
            builder.HasOne(r => r.Transfer)
                .WithMany(t => t.Recipients)
                .HasForeignKey(r => r.TransferId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // ForwardedById e opțional. Dacă utilizatorul este șters, câmpul
            // devine null (SetNull) — nu vrem să pierdem rândul destinatarului
            // doar pentru că autorul forward-ului nu mai există.
            builder.HasOne(r => r.ForwardedBy)
                .WithMany()
                .HasForeignKey(r => r.ForwardedById)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            // ── Lungimi ──────────────────────────────────────────────────────
            // RSA-3072 → 384 octeți → ~512 caractere base64. Marja de 600
            // corespunde cu FileTransferConfiguration.
            builder.Property(r => r.EncryptedKeyForUser)
                .IsRequired()
                .HasMaxLength(600);

            // ── Indecși ──────────────────────────────────────────────────────
            // Lista de transferuri și contorul „te așteaptă” de pe pagina
            // principală caută „rândurile lui X, nedescărcate încă”. Indexul
            // compus acoperă ambele interogări; cel vechi, doar pe UserId, e
            // prefixul lui și ar fi fost redundant.
            builder.HasIndex(r => new { r.UserId, r.DownloadedAt })
                .HasDatabaseName("IX_TransferRecipients_UserId_DownloadedAt");

            // Index pe TransferId: rapid la listarea destinatarilor unui transfer
            // (dovada de primire din listă, dialogul de forward).
            builder.HasIndex(r => r.TransferId)
                .HasDatabaseName("IX_TransferRecipients_TransferId");
        }
    }
}
