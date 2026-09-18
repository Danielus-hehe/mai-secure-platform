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

            // Dacă transferul se șterge fizic (DELETE pe FileTransfers), toate
            // rândurile de destinatari se șterg în cascadă. Logica de business
            // șterge rar transferuri — de obicei revocă — dar consitența
            // referențială trebuie garantată și la DELETE direct în baza de date.
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
            // doar pentru că expeditorul forward-ului nu mai există.
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
            // GetAll caută „există vreun rând cu UserId == currentUser?" pentru
            // fiecare pagină de transferuri. Fără index pe UserId, interogarea
            // face sequential scan pe toată tabela.
            builder.HasIndex(r => r.UserId)
                .HasDatabaseName("IX_TransferRecipients_UserId");

            // Index pe TransferId: rapid la ștergerea în cascadă și la listarea
            // destinatarilor unui transfer (de ex. în dialogul de forward).
            builder.HasIndex(r => r.TransferId)
                .HasDatabaseName("IX_TransferRecipients_TransferId");
        }
    }
}
