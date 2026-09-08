using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MAI.Domain.Entities;

namespace MAI.DataAccessLayer.Configurations
{
    public class FileTransferConfiguration : IEntityTypeConfiguration<FileTransfer>
    {
        public void Configure(EntityTypeBuilder<FileTransfer> builder)
        {
            builder.HasKey(f => f.Id);

            builder.HasOne(f => f.Sender)
                .WithMany()
                .HasForeignKey(f => f.SenderId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(f => f.Recipient)
                .WithMany()
                .HasForeignKey(f => f.RecipientId)
                .OnDelete(DeleteBehavior.Restrict);

            // ── Limite de lungime ────────────────────────────────────────────
            // Fără ele, Npgsql creează 'text' nemărginit: un client poate trimite
            // un nume de fișier de 10 MB și umple baza de date.

            builder.Property(f => f.FileName)
                .IsRequired()
                .HasMaxLength(260);      // limita practică de nume de fișier pe Windows

            builder.Property(f => f.StorageKey)
                .IsRequired()
                .HasMaxLength(512);

            builder.Property(f => f.ChecksumSHA256)
                .HasMaxLength(64);       // SHA-256 în hex = exact 64 de caractere

            builder.Property(f => f.EncryptionIv)
                .HasMaxLength(32);       // 12 octeți în base64 = 16 caractere

            // RSA-3072 produce blocuri de 384 de octeți → 512 caractere base64.
            builder.Property(f => f.EncryptedKeyForRecipient).HasMaxLength(600);
            builder.Property(f => f.EncryptedKeyForSender).HasMaxLength(600);
            builder.Property(f => f.SenderSignature).HasMaxLength(600);
            builder.Property(f => f.CryptoSuite).HasMaxLength(128);

            // ── Indexuri ─────────────────────────────────────────────────────
            // Lista de transferuri filtrează mereu pe destinatar sau expeditor și
            // sortează pe dată. Fără indexuri compuse, PostgreSQL face sequential
            // scan pe toată tabela la fiecare pagină.

            builder.HasIndex(f => new { f.RecipientId, f.CreatedAt })
                .HasDatabaseName("IX_FileTransfers_Recipient_CreatedAt");

            builder.HasIndex(f => new { f.SenderId, f.CreatedAt })
                .HasDatabaseName("IX_FileTransfers_Sender_CreatedAt");

            builder.HasIndex(f => f.Status)
                .HasDatabaseName("IX_FileTransfers_Status");

            // Folosit de jobul de expirare.
            builder.HasIndex(f => f.ExpiresAt)
                .HasDatabaseName("IX_FileTransfers_ExpiresAt");

            // StorageKey trebuie să fie unic: două rânduri care indică același
            // obiect ar însemna că ștergerea unuia rupe celălalt transfer.
            // Filtrul e obligatoriu: transferurile dinaintea migrării au StorageKey
            // gol, iar un index unic nefiltrat le-ar considera duplicate între ele
            // și ar face imposibilă crearea indexului pe o bază existentă.
            builder.HasIndex(f => f.StorageKey)
                .IsUnique()
                .HasFilter("\"StorageKey\" <> ''")
                .HasDatabaseName("UX_FileTransfers_StorageKey");

            // Coloana veche rămâne mapată doar ca transferurile dinaintea migrării
            // să rămână vizibile în liste.
#pragma warning disable CS0618
            builder.Property(f => f.EncryptedStoragePath).HasMaxLength(1024);
#pragma warning restore CS0618
        }
    }
}