using MAI.BusinessLogic.Dtos;
using MAI.Domain.Entities;

namespace MAI.Api.Services
{
    /// <summary>
    /// Tot ce ține de emiterea, rotația și invalidarea tokenurilor.
    ///
    /// Extras din AuthController, care ajunsese să facă simultan: verificarea
    /// parolei, pasul doi, blocarea contului, semnarea JWT-urilor, generarea
    /// tokenurilor opace și hashingul lor. Un controller trebuie să orchestreze,
    /// nu să implementeze primitive criptografice — altfel testarea emiterii de
    /// tokenuri cere un HttpContext.
    ///
    /// Metodele care modifică <see cref="User"/> setează doar proprietățile;
    /// salvarea rămâne a apelantului, ca să poată grupa mai multe modificări
    /// într-un singur SaveChanges.
    /// </summary>
    public interface ITokenService
    {
        /// <summary>
        /// Emite o pereche access + refresh și rotește refresh token-ul existent.
        /// Tokenul anterior devine invalid în momentul apelului: în baza de date
        /// se păstrează un singur hash per utilizator.
        /// </summary>
        TokenResponseDto IssueTokens(User user);

        /// <summary>
        /// Emite provocarea de pas doi. Returnează tokenul în clar (o singură dată,
        /// pentru client); în baza de date ajunge doar hash-ul.
        /// </summary>
        (string Token, DateTime ExpiresAt) IssueTwoFactorChallenge(User user);

        /// <summary>Consumă provocarea 2FA. Idempotentă.</summary>
        void ClearChallenge(User user);

        /// <summary>Revocă refresh token-ul curent și orice provocare 2FA în aer.</summary>
        void RevokeSession(User user);

        /// <summary>
        /// Hash-ul cu care se caută în baza de date un token opac primit de la client.
        /// SHA-256, nu Argon2: tokenul are 512 biți de entropie generată criptografic,
        /// deci nu există atac prin dicționar de apărat. Argon2 aici ar transforma
        /// /refresh într-un vector de DoS mai bun decât /login.
        /// </summary>
        string HashOpaqueToken(string token);
    }
}
