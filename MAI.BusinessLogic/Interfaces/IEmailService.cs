namespace MAI.BusinessLogic.Interfaces
{
    /// <summary>
    /// Contract pentru trimiterea notificărilor prin email.
    ///
    /// Implementarea concretă (SMTP/MailKit) e înregistrată în DI; codul care
    /// apelează serviciul nu știe și nu trebuie să știe ce provider folosește.
    ///
    /// Eșecul trimiterii NU trebuie să se propage ca excepție la apelant:
    /// implementările iau erorile, le loghează și returnează false. Un email
    /// neconfigurat sau un server SMTP căzut nu trebuie să blocheze un transfer.
    /// </summary>
    public interface IEmailService
    {
        /// <summary>
        /// Notifică destinatarul că a primit un fișier criptat.
        /// </summary>
        /// <param name="toEmail">Adresa de email a destinatarului.</param>
        /// <param name="toName">Numele complet al destinatarului (pentru salut).</param>
        /// <param name="senderName">Numele expeditorului, afișat în corp.</param>
        /// <param name="fileName">Numele fișierului transferat.</param>
        /// <param name="expiresAt">Data de expirare a transferului (UTC).</param>
        /// <param name="ct">Token de anulare.</param>
        /// <returns>True dacă emailul a fost trimis cu succes, false altfel.</returns>
        Task<bool> SendTransferNotificationAsync(
            string toEmail,
            string toName,
            string senderName,
            string fileName,
            DateTime? expiresAt,
            CancellationToken ct = default);

        /// <summary>
        /// Trimite emailul de activare cont cu linkul de invitație.
        /// </summary>
        /// <param name="toEmail">Adresa destinatarului.</param>
        /// <param name="toName">Numele complet (pentru salut).</param>
        /// <param name="invitationLink">URL-ul complet cu tokenul de activare.</param>
        /// <param name="ct">Token de anulare.</param>
        Task<bool> SendInvitationEmailAsync(
            string toEmail,
            string toName,
            string invitationLink,
            CancellationToken ct = default);

        /// <summary>
        /// Verifică dacă serviciul este configurat și gata de utilizare.
        /// Returnează false dacă lipsesc credențialele SMTP — utile pentru
        /// health check și pentru a sări notificările în locuri neeceritabile.
        /// </summary>
        bool IsConfigured { get; }
    }
}