using System;

namespace MAI.Domain.Entities
{
    /// <summary>
    /// O sesiune activă: un dispozitiv pe care utilizatorul e autentificat.
    ///
    /// Înainte, sesiunea era o singură coloană pe <see cref="User"/>
    /// (<c>RefreshTokenHash</c>). Consecința practică: autentificarea pe telefon
    /// deconecta tăcut laptopul, iar utilizatorul nu avea nicio cale să vadă unde
    /// e conectat. Într-o instituție unde „mi-am lăsat contul deschis pe un
    /// calculator din altă direcție” e un scenariu real, singura soluție era
    /// schimbarea parolei.
    ///
    /// Fiecare rând ține hash-ul SHA-256 al refresh token-ului, niciodată tokenul.
    /// Rândurile revocate NU se șterg: cine a fost conectat, de unde și când s-a
    /// încheiat sesiunea sunt exact datele pe care le caută o anchetă.
    /// </summary>
    public class UserSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid UserId { get; set; }
        public User? User { get; set; }

        /// <summary>
        /// SHA-256 (hex) al refresh token-ului curent al acestei sesiuni.
        /// Se rotește la fiecare /refresh; rândul rămâne același.
        /// </summary>
        public string RefreshTokenHash { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Ultima rotație de token. Aproximează „ultima activitate”.</summary>
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

        public DateTime ExpiresAt { get; set; }

        /// <summary>Când a fost revocată. Null = sesiune activă.</summary>
        public DateTime? RevokedAt { get; set; }

        /// <summary>
        /// De ce s-a încheiat: "logout", "revocare de la distanta",
        /// "schimbare parola", "cont dezactivat". Text scurt, afișat direct.
        /// </summary>
        public string? RevokedReason { get; set; }

        /// <summary>
        /// User-Agent-ul de la crearea sesiunii, trunchiat.
        ///
        /// Se păstrează cel de la creare, nu cel de la ultima cerere: dacă un
        /// token e furat și folosit de altcineva, un User-Agent care se schimbă
        /// peste rândul existent ar șterge tocmai indiciul.
        /// </summary>
        public string? UserAgent { get; set; }

        /// <summary>IP-ul de la care s-a deschis sesiunea.</summary>
        public string? IpAddress { get; set; }

        /// <summary>Adevărat cât timp sesiunea mai poate produce tokenuri noi.</summary>
        public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
    }
}
