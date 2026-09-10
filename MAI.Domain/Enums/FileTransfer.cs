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

        /// <summary>
        /// Numele original al fișierului.
        ///
        /// LIMITARE conștientă, de menționat în raport: numele NU este criptat.
        /// Serverul îl vede, pentru că listele și căutarea server-side au nevoie
        /// de el. Într-o variantă strictă ar intra și el în plicul criptografic,
        /// iar listele ar afișa nume decriptate în browser — cu prețul pierderii
        /// căutării în baza de date.
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// Cheia obiectului în depozit, ex. "transfers/2026/09/{guid}.enc".
        ///
        /// Cheie, NU cale de sistem. Coloana veche EncryptedStoragePath conținea
        /// o cale absolută, care era pasată direct la File.ReadAllBytes: orice
        /// bug care ar fi permis scrierea coloanei devenea citire arbitrară de
        /// fișiere de pe server.
        /// </summary>
        public string StorageKey { get; set; } = string.Empty;

        /// <summary>Dimensiunea conținutului în clar, raportată de client (informativă).</summary>
        public long FileSize { get; set; }

        /// <summary>Dimensiunea reală a cifrotextului stocat. Cu ea se face contabilitatea.</summary>
        public long CiphertextSize { get; set; }

        /// <summary>
        /// SHA-256 (hex) al CIFROTEXTULUI, calculat de client înainte de upload
        /// și recalculat de server în timpul scrierii. Detectează coruperea la
        /// transport sau la stocare.
        ///
        /// Este o proprietate diferită de semnătură: aici verificăm că octeții
        /// stocați sunt cei primiți; semnătura dovedește cine i-a produs.
        /// </summary>
        public string ChecksumSHA256 { get; set; } = string.Empty;

        public TransferStatus Status { get; set; } = TransferStatus.Pending;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Momentul în care destinatarul a descărcat și decriptat fișierul.
        /// Este dovada de primire pe care o vede expeditorul.
        /// </summary>
        public DateTime? DownloadedAt { get; set; }

        /// <summary>
        /// Rezultatul verificării semnăturii, raportat de browserul destinatarului
        /// la confirmare.
        ///
        /// Null pentru transferurile necriptate sau neconfirmate încă. Contează
        /// pentru dovada de primire: „a fost descărcat” și „a fost descărcat, iar
        /// semnătura expeditorului s-a verificat” sunt afirmații diferite, iar
        /// expeditorul are dreptul să o vadă pe a doua.
        ///
        /// Server-side rămâne o afirmație a clientului, nu o verificare proprie —
        /// serverul nu poate verifica singur semnătura fără textul în clar, pe
        /// care prin construcție nu îl are.
        /// </summary>
        public bool? RecipientSignatureValid { get; set; }

        /// <summary>Momentul retragerii de către expeditor. Null dacă nu a fost retras.</summary>
        public DateTime? RevokedAt { get; set; }

        /// <summary>Motivul consemnat de expeditor la retragere, opțional.</summary>
        public string? RevokedReason { get; set; }

        /// <summary>
        /// După acest moment transferul nu mai poate fi descărcat, iar obiectul
        /// din depozit se șterge (lifecycle policy pe bucket). Reduce fereastra
        /// în care un document sensibil stă degeaba pe server.
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

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

        // ── Legacy ───────────────────────────────────────────────────────────

        /// <summary>
        /// Calea absolută folosită de implementarea veche, pe filesystem. Rămâne
        /// în model doar ca transferurile create înainte de migrare să nu devină
        /// invizibile. Cod nou NU scrie aici niciodată.
        /// </summary>
        [Obsolete("Folosește StorageKey. Coloana rămâne doar pentru transferurile dinaintea migrării.")]
        public string? EncryptedStoragePath { get; set; }
    }
}