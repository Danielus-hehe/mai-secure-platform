namespace MAI.Api.Services
{
    /// <summary>
    /// Gestionează fluxul de invitație la crearea contului:
    /// generare token → trimitere email → confirmare cu setare parolă.
    ///
    /// Tokenul este stocat în DB ca SHA-256 (hex) - niciodată în clar.
    /// Linkul din email conține tokenul brut; serverul îl hashează la validare.
    /// </summary>
    public interface IInvitationService
    {
        /// <summary>
        /// Generează un token de invitație valid 72 de ore, îl salvează pe user
        /// (hash SHA-256) și trimite emailul cu linkul de activare.
        /// Userul trebuie să aibă adresă de email configurată.
        ///
        /// Folosește <c>AppDbContext</c>-ul scope-ului curent. Apelantul care vrea
        /// să nu aștepte trimiterea (crearea contului) NU are voie să pornească
        /// metoda fire-and-forget pe scope-ul cererii HTTP: scope-ul se închide la
        /// răspuns și contextul devine disposed. Se folosește
        /// <see cref="IInvitationDispatcher"/>, care își creează propriul scope.
        /// </summary>
        /// <returns>
        /// True dacă emailul a plecat. False dacă serverul SMTP nu e configurat
        /// sau a refuzat trimiterea - tokenul rămâne salvat, deci invitația se
        /// poate retrimite din /users fără alte efecte.
        /// </returns>
        Task<bool> SendInvitationAsync(Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Validează tokenul, hashează parola cu Argon2id și activează contul.
        /// Marchează EmailConfirmed = true, MustChangePassword = false,
        /// șterge InvitationToken + InvitationTokenExpiry din DB.
        /// </summary>
        Task<InvitationResult> ConfirmAsync(
            string token,
            string newPassword,
            CancellationToken ct = default);

        /// <summary>
        /// Returnează username + email dacă tokenul este valid și neexpirat.
        /// Null dacă tokenul nu există, expirat sau deja folosit.
        /// Folosit de frontend ca să afișeze datele contului înainte de setarea parolei.
        /// </summary>
        Task<InvitationTokenInfo?> GetTokenInfoAsync(
            string token,
            CancellationToken ct = default);
    }

    /// <summary>Rezultatul operației de confirmare invitație.</summary>
    public sealed record InvitationResult(
        bool Success,
        string? Error = null,
        IReadOnlyList<string>? Errors = null);

    /// <summary>
    /// Date minime despre cont returnate frontend-ului la verificarea tokenului,
    /// fără a expune date sensibile (fără parolă hash, fără roluri).
    /// </summary>
    public sealed record InvitationTokenInfo(string Username, string Email);
}
