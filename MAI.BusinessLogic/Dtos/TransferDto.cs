using MAI.Domain.Enums;

namespace MAI.BusinessLogic.Dtos
{
    /// <summary>
    /// Un rând din lista de transferuri, din perspectiva utilizatorului curent.
    ///
    /// Înlocuiește vechiul FileTransferDto (nefolosit) și TransferDto-ul definit
    /// în TransfersController, care avea un singur destinatar și o singură dată
    /// de descărcare pentru tot transferul.
    /// </summary>
    public class TransferDto
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public long CiphertextSize { get; set; }
        public string Sha256 { get; set; } = string.Empty;

        public Guid SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string SenderDepartment { get; set; } = string.Empty;

        /// <summary>
        /// Starea agregată (Pending / Downloaded / Expired / Revoked), cu regula
        /// din TransferRules.EffectiveStatus.
        /// </summary>
        public string Status { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? RevokedReason { get; set; }
        public TransferCategory Category { get; set; }

        /// <summary>Politica aleasă de expeditor: pot destinatarii redirecționa?</summary>
        public bool AllowForward { get; set; }

        public bool IsMine { get; set; }
        public bool IsEncrypted { get; set; }
        public string? CryptoSuite { get; set; }

        // ── Destinatari și dovada de primire ─────────────────────────────────

        /// <summary>
        /// Destinatarii, în ordinea adăugării. Toată lumea implicată vede cine a
        /// primit transferul (ca la CC); DownloadedAt și SignatureValid apar doar
        /// unde utilizatorul curent are dreptul să le vadă — vezi
        /// TransferRecipientDto.ReceiptVisible.
        /// </summary>
        public List<TransferRecipientDto> Recipients { get; set; } = [];

        public int RecipientCount { get; set; }

        /// <summary>
        /// Câți destinatari au confirmat. Null când utilizatorul nu are dreptul
        /// să vadă confirmările tuturor (e doar unul dintre destinatari).
        /// </summary>
        public int? DownloadedCount { get; set; }

        /// <summary>Confirmarea proprie, dacă utilizatorul curent e destinatar.</summary>
        public DateTime? MyDownloadedAt { get; set; }

        /// <summary>Rezultatul propriei verificări de semnătură.</summary>
        public bool? MySignatureValid { get; set; }

        /// <summary>True dacă utilizatorul curent este destinatar (direct sau prin forward).</summary>
        public bool IsRecipient { get; set; }

        // ── Acțiuni permise, calculate pe server ─────────────────────────────
        // Interfața nu reface regulile: afișează butoanele pe care serverul le
        // declară permise, iar serverul le verifică oricum din nou la acțiune.

        public bool CanDownload { get; set; }
        public bool CanForward { get; set; }
        public bool CanRevoke { get; set; }
        public bool CanDelete { get; set; }
    }

    public class TransferRecipientDto
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }

        /// <summary>Null = destinatar direct, ales la trimitere.</summary>
        public Guid? ForwardedById { get; set; }
        public string? ForwardedByName { get; set; }

        /// <summary>
        /// True dacă utilizatorul curent poate vedea confirmarea acestui rând:
        /// e expeditorul, e cel care l-a adăugat prin forward, e chiar destinatarul
        /// respectiv sau e administrator. Un destinatar nu vede cine dintre
        /// colegi a deschis deja documentul.
        /// </summary>
        public bool ReceiptVisible { get; set; }

        public DateTime? DownloadedAt { get; set; }
        public bool? SignatureValid { get; set; }
    }

    /// <summary>Politica expusă frontend-ului (GET /api/Transfers/policy).</summary>
    public record TransferPolicyDto(int DefaultExpiryDays, int MaxExpiryDays, int MaxRecipients);
}
