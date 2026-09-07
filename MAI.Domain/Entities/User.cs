using System;
using System.ComponentModel.DataAnnotations.Schema;
using MAI.Domain.Enums;

namespace MAI.Domain.Entities
{
    public class User
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        /// <summary>Hash Argon2id în format PHC. Niciodată parolă în clar.</summary>
        public string PasswordHash { get; set; } = string.Empty;

        public string? FullName { get; set; } = string.Empty;
        public string? Department { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.Utilizator;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // ── Refresh token ────────────────────────────────────────────────────
        // În DB se stochează DOAR hash-ul SHA-256 al refresh token-ului, nu tokenul.

        /// <summary>SHA-256 (hex) al refresh token-ului activ. Null = fără sesiune activă.</summary>
        public string? RefreshTokenHash { get; set; }

        /// <summary>Momentul expirării refresh token-ului (UTC).</summary>
        public DateTime? RefreshTokenExpiresAt { get; set; }

        /// <summary>Când a fost emis ultimul refresh token (UTC).</summary>
        public DateTime? RefreshTokenIssuedAt { get; set; }

        // ── Blocare cont după încercări eșuate ───────────────────────────────
        // Rate limiting-ul per IP nu e suficient: un botnet distribuie încercările
        // pe mii de IP-uri, fiecare rămânând sub prag. Contorul per cont prinde
        // exact acest scenariu, indiferent de câte IP-uri folosește atacatorul.

        /// <summary>Încercări eșuate consecutive. Se resetează la login reușit.</summary>
        public int FailedLoginAttempts { get; set; }

        /// <summary>Contul e blocat până la acest moment (UTC). Null = neblocat.</summary>
        public DateTime? LockoutEndsAt { get; set; }

        /// <summary>Ultima încercare eșuată (UTC) — pentru audit și rapoarte.</summary>
        public DateTime? LastFailedLoginAt { get; set; }

        /// <summary>Ultimul login reușit (UTC).</summary>
        public DateTime? LastLoginAt { get; set; }

        /// <summary>
        /// True dacă contul este blocat chiar acum. Calculată, nu stocată —
        /// [NotMapped] este obligatoriu, altfel EF caută o coloană "IsLockedOut".
        /// </summary>
        [NotMapped]
        public bool IsLockedOut => LockoutEndsAt.HasValue && LockoutEndsAt.Value > DateTime.UtcNow;
    }
}