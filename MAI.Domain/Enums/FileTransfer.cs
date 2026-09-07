using System;
using MAI.Domain.Enums;

namespace MAI.Domain.Entities
{
    public class FileTransfer
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid SenderId { get; set; }
        public User? Sender { get; set; }
        public Guid RecipientId { get; set; }
        public User? Recipient { get; set; }

        public string FileName { get; set; } = string.Empty;
        public string EncryptedStoragePath { get; set; } = string.Empty;

        /// <summary>Dimensiunea conținutului în clar, raportată de client (informativă).</summary>
        public long FileSize { get; set; }

        /// <summary>SHA-256 al CIFROTEXTULUI. Detectează coruperea la stocare sau transport.</summary>
        public string ChecksumSHA256 { get; set; } = string.Empty;

        public TransferStatus Status { get; set; } = TransferStatus.Pending;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DownloadedAt { get; set; }

        // ── Plicul criptografic ──────────────────────────────────────────────
        // Serverul stochează aceste valori dar nu le poate folosi: cheile de fișier
        // sunt împachetate cu chei publice RSA ale căror perechi private nu ajung
        // niciodată aici în clar.

        /// <summary>IV-ul AES-GCM folosit la criptarea conținutului (base64, 12 octeți).</summary>
        public string? EncryptionIv { get; set; }

        /// <summary>Cheia de fișier (DEK) împachetată cu cheia publică a destinatarului.</summary>
        public string? EncryptedKeyForRecipient { get; set; }

        /// <summary>
        /// Aceeași DEK, împachetată și cu cheia publică a expeditorului — altfel
        /// expeditorul nu și-ar mai putea deschide propriile fișiere trimise.
        /// </summary>
        public string? EncryptedKeyForSender { get; set; }

        /// <summary>
        /// Semnătura RSA-PSS a expeditorului peste SHA-256 al conținutului în clar.
        /// Asigură autenticitatea și non-repudierea: dovedește cine a trimis și că
        /// nimic nu s-a modificat pe drum.
        /// </summary>
        public string? SenderSignature { get; set; }

        /// <summary>Suita criptografică, ex. "AES-256-GCM+RSA-OAEP-3072+RSA-PSS-3072".</summary>
        public string? CryptoSuite { get; set; }

        /// <summary>False pentru transferurile vechi, necriptate, dinainte de migrare.</summary>
        public bool IsEncrypted { get; set; }
    }
}