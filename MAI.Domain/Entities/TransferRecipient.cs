using System;

namespace MAI.Domain.Entities
{
    /// <summary>
    /// Un destinatar al unui transfer, împreună cu dovada lui de primire.
    ///
    /// Toți destinatarii stau aici - și cei aleși la trimitere, și cei adăugați
    /// ulterior prin forward. Înainte de această schimbare, destinatarul original
    /// stătea pe rândul FileTransfer (RecipientId, EncryptedKeyForRecipient,
    /// DownloadedAt), iar cei de forward aici, fără dovadă de primire proprie:
    /// confirmarea oricăruia dintre ei marca întregul transfer ca descărcat, deci
    /// expeditorul nu avea cum să afle CINE anume a primit fișierul.
    ///
    /// Diferența dintre cele două feluri de destinatari e dată de ForwardedById:
    ///   • null  → destinatar direct, ales de expeditor la trimitere;
    ///   • setat → adăugat prin forward, de utilizatorul indicat.
    ///
    /// Cheia primară compusă (TransferId, UserId): același utilizator nu poate
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
        /// RSA-3072 produce blocuri de 384 de octeți → 512 caractere base64.
        ///
        /// Se golește la retragere și la ștergere: fără cifrotext cheia nu mai
        /// deschide nimic, dar nu are niciun motiv să rămână în bază.
        /// </summary>
        public string EncryptedKeyForUser { get; set; } = string.Empty;

        // ── Trasabilitate ────────────────────────────────────────────────────

        /// <summary>
        /// Utilizatorul care a adăugat acest destinatar prin forward.
        /// Null = destinatar direct, ales de expeditor la trimitere.
        /// </summary>
        public Guid? ForwardedById { get; set; }
        public User? ForwardedBy { get; set; }

        /// <summary>Momentul în care destinatarul a fost adăugat (UTC).</summary>
        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        // ── Dovada de primire ────────────────────────────────────────────────

        /// <summary>
        /// Momentul în care ACEST destinatar a descărcat și decriptat fișierul.
        /// Null = încă nu l-a deschis. Se scrie o singură dată: o a doua
        /// descărcare nu mută momentul primei confirmări.
        /// </summary>
        public DateTime? DownloadedAt { get; set; }

        /// <summary>
        /// Rezultatul verificării semnăturii expeditorului, raportat de browserul
        /// acestui destinatar la confirmare.
        ///
        /// „A descărcat” și „a descărcat, iar semnătura s-a verificat” sunt
        /// afirmații diferite, iar expeditorul are dreptul să o vadă pe a doua.
        /// Server-side rămâne o afirmație a clientului: serverul nu poate verifica
        /// singur semnătura fără textul în clar, pe care prin construcție nu îl are.
        /// </summary>
        public bool? SignatureValid { get; set; }
    }
}
