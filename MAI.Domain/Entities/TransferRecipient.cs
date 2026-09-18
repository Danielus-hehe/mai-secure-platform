using System;

namespace MAI.Domain.Entities
{
    /// <summary>
    /// Destinatar suplimentar al unui transfer — adăugat prin operația de forward.
    ///
    /// Diferența față de FileTransfer.RecipientId (destinatarul original):
    ///   • Destinatarul original → coloana RecipientId + EncryptedKeyForRecipient
    ///     pe rândul FileTransfer. Există de la primul upload, are dovadă de
    ///     primire (DownloadedAt, RecipientSignatureValid) și apare în audit log
    ///     principal.
    ///   • Destinatarii suplimentari (forward) → câte un rând TransferRecipient.
    ///     Fiecare are propria cheie de fișier împachetată cu cheia lui publică,
    ///     exact ca destinatarul original — serverul rămâne agnostic față de DEK.
    ///
    /// Cheia primară este compusă (TransferId, UserId): același utilizator nu poate
    /// apărea de două ori ca destinatar al aceluiași transfer.
    /// </summary>
    public class TransferRecipient
    {
        // ── Cheia primară compusă ────────────────────────────────────────────

        public Guid TransferId { get; set; }
        public FileTransfer? Transfer { get; set; }

        public Guid UserId { get; set; }
        public User? User { get; set; }

        // ── Plicul criptografic ──────────────────────────────────────────────
        // Serverul stochează valoarea dar nu o poate folosi: DEK-ul (cheia de fișier)
        // este împachetat cu cheia publică RSA-OAEP a lui UserId. Numai utilizatorul
        // cu cheia privată corespunzătoare poate să-l deschidă.

        /// <summary>
        /// DEK-ul transferului împachetat cu cheia publică RSA-OAEP a acestui destinatar.
        /// Dimensiune: RSA-3072 produce blocuri de 384 de octeți → ~512 caractere base64.
        /// </summary>
        public string EncryptedKeyForUser { get; set; } = string.Empty;

        // ── Trasabilitate ────────────────────────────────────────────────────

        /// <summary>
        /// Utilizatorul care a inițiat forward-ul. Null nu se folosește în prezent —
        /// câmpul este pregătit pentru auditarea granulară a lanțurilor de redistribuire.
        /// </summary>
        public Guid? ForwardedById { get; set; }
        public User? ForwardedBy { get; set; }

        /// <summary>Momentul în care destinatarul a fost adăugat (UTC).</summary>
        public DateTime SentAt { get; set; } = DateTime.UtcNow;
    }
}
