using MAI.BusinessLogic.Dtos;
using MAI.Domain.Entities;

namespace MAI.Api.Services
{
    /// <summary>
    /// Emiterea tokenurilor. Fără acces la baza de date: serviciul produce
    /// valori, apelantul decide ce persistă.
    ///
    /// Separarea contează de când sesiunile au tabelă proprie. Dacă
    /// <see cref="ISessionService"/> ar fi fost topit aici, un serviciu care nu
    /// face decât HMAC și numere aleatorii ar fi devenit dependent de EF Core și
    /// n-ar mai fi putut fi testat fără o bază de date.
    /// </summary>
    public interface ITokenService
    {
        /// <summary>
        /// Produce o pereche access + refresh. NU scrie nimic: rezultatul conține
        /// hash-ul refresh token-ului, pe care apelantul îl pune într-o sesiune.
        /// </summary>
        /// <param name="user">Titularul tokenurilor.</param>
        /// <param name="sessionId">
        /// Sesiunea (rândul din UserSessions) căreia îi aparține tokenul de acces.
        /// Intră în JWT ca claim-ul „sid” și se verifică la fiecare cerere
        /// (vezi SessionTokenValidator): o sesiune închisă își pierde imediat și
        /// tokenul de acces, nu abia la expirarea lui.
        /// </param>
        TokenIssueResult IssueTokens(User user, Guid sessionId);

        /// <summary>
        /// Emite provocarea de pas doi. Tokenul în clar se întoarce o singură
        /// dată, pentru client; în baza de date ajunge doar hash-ul, pus direct
        /// pe utilizator (provocarea e efemeră și precede orice sesiune).
        /// </summary>
        (string Token, DateTime ExpiresAt) IssueTwoFactorChallenge(User user);

        /// <summary>Consumă provocarea 2FA. Idempotentă.</summary>
        void ClearChallenge(User user);

        /// <summary>
        /// Hash-ul cu care se caută în baza de date un token opac primit de la client.
        ///
        /// SHA-256, nu Argon2: tokenul are 512 biți de entropie generată
        /// criptografic, deci nu există atac prin dicționar de apărat. Argon2 aici
        /// ar transforma /refresh într-un vector de DoS mai bun decât /login.
        /// </summary>
        string HashOpaqueToken(string token);
    }

    /// <summary>
    /// Ce rezultă dintr-o emitere: răspunsul pentru client, plus datele de care
    /// are nevoie stratul de sesiuni ca să persiste rotația.
    /// </summary>
    /// <param name="Response">Payloadul trimis clientului, cu ambele tokenuri.</param>
    /// <param name="RefreshTokenHash">SHA-256 al refresh token-ului, pentru sesiune.</param>
    /// <param name="RefreshTokenExpiresAt">Expirarea refresh token-ului (UTC).</param>
    public readonly record struct TokenIssueResult(
        TokenResponseDto Response,
        string RefreshTokenHash,
        DateTime RefreshTokenExpiresAt);
}
