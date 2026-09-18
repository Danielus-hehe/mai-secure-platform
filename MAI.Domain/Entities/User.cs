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
        /// Parola a fost aleasă de un administrator (la crearea contului sau la o
        /// resetare), deci nu e un secret al utilizatorului.
        ///
        /// Contează pentru criptarea end-to-end: cheile private se încuie cu o
        /// cheie derivată din parolă. Cu o parolă cunoscută de administrator,
        /// acesta ar putea descuia din baza de date cheile generate ulterior și
        /// ar putea citi, fără urmă, tot ce primește contul. Cât timp flag-ul e
        /// true, serverul refuză înregistrarea cheilor, iar frontend-ul cere
        /// schimbarea parolei înaintea oricărei alte acțiuni.
        /// </summary>
        public bool MustChangePassword { get; set; }

        // ── Invitație / confirmare email ──────────────────────────────────────
        //
        // La crearea contului de admin, dacă utilizatorul are email, contul
        // pornește cu EmailConfirmed=false și se trimite un link de activare.
        // Tokenul e stocat ca SHA-256 (hex) — nu în clar — ca și refresh token-ul.
        // Login-ul este blocat până la confirmare.

        /// <summary>SHA-256 (hex) al tokenului de invitație. Null după activare.</summary>
        public string? InvitationToken { get; set; }

        /// <summary>Expirul tokenului de invitație (72 h). Null după activare.</summary>
        public DateTime? InvitationTokenExpiry { get; set; }

        /// <summary>
        /// True dacă utilizatorul și-a confirmat adresa de email prin link.
        /// Conturile fără email pornesc cu true (fluxul clasic MustChangePassword).
        /// Login-ul este refuzat cât timp e false.
        /// </summary>
        public bool EmailConfirmed { get; set; } = true;

        /// <summary>
        /// Intervalul TOTP (Unix time / 30 s) al ultimului cod acceptat. Un cod
        /// dintr-un interval egal sau mai vechi e respins: același cod nu poate
        /// fi folosit de două ori (RFC 6238, 5.2). Null până la primul cod.
        /// </summary>
        public long? TwoFactorLastUsedStep { get; set; }

        // ── Chei criptografice pentru transferuri E2E ────────────────────────
        // Cheile publice sunt publice prin definiție. Cheile private ajung aici
        // DOAR criptate cu o cheie derivată din parola utilizatorului, în browser.
        // Serverul nu le poate descuia — asta e tot rostul.

        /// <summary>Cheia publică RSA-OAEP (SPKI, base64) — împachetează cheile de fișier.</summary>
        public string? PublicKeyEncryption { get; set; }

        /// <summary>Cheia publică RSA-PSS (SPKI, base64) — verifică semnăturile.</summary>
        public string? PublicKeySigning { get; set; }

        /// <summary>Cheile private, criptate AES-256-GCM cu cheia derivată din parolă.</summary>
        public string? EncryptedPrivateBundle { get; set; }

        /// <summary>Salt-ul PBKDF2 folosit la derivarea cheii de împachetare (base64).</summary>
        public string? KeyDerivationSalt { get; set; }

        /// <summary>Numărul de iterații PBKDF2 — stocat ca să putem crește pragul în timp.</summary>
        public int? KeyDerivationIterations { get; set; }

        /// <summary>IV-ul AES-GCM folosit la împachetarea cheilor private (base64).</summary>
        public string? KeyWrapIv { get; set; }

        /// <summary>Suita criptografică folosită, pentru migrări viitoare.</summary>
        public string? CryptoSuite { get; set; }

        public DateTime? KeysCreatedAt { get; set; }

        // ── Autentificare în doi pași (TOTP, RFC 6238) ───────────────────────
        //
        // Secretul TOTP este stocat CIFRAT (AES-256-GCM, cheie din variabilă de
        // mediu), nu ca hash. Diferența față de parolă e esențială: serverul
        // trebuie să poată reconstitui secretul ca să genereze codul așteptat,
        // deci hash-ul nu e o opțiune. Rămâne cifrarea, cu cheia ținută în afara
        // bazei de date.

        /// <summary>True dacă 2FA a fost activat și confirmat cu un cod valid.</summary>
        public bool TwoFactorEnabled { get; set; }

        /// <summary>Secretul TOTP activ, cifrat. Null dacă 2FA nu e activat.</summary>
        public string? TwoFactorSecret { get; set; }

        /// <summary>
        /// Secretul generat la înrolare, înainte de confirmare. Cifrat, la fel.
        ///
        /// Se ține separat de cel activ ca o înrolare abandonată sau eșuată să nu
        /// distrugă un 2FA deja funcțional: dacă utilizatorul își reconfigurează
        /// telefonul și se răzgândește la jumătate, vechiul secret rămâne intact.
        /// </summary>
        public string? TwoFactorPendingSecret { get; set; }

        public DateTime? TwoFactorEnrolledAt { get; set; }

        /// <summary>
        /// Hash-urile SHA-256 ale codurilor de recuperare neconsumate, separate
        /// prin ';'. Codurile în clar se afișează o singură dată, la activare.
        /// </summary>
        public string? TwoFactorRecoveryCodeHashes { get; set; }

        /// <summary>
        /// SHA-256 al provocării emise între parola corectă și codul TOTP.
        ///
        /// Provocarea NU este un JWT. Un token opac, cu stare pe server, nu poate
        /// fi confundat de middleware-ul de autentificare cu un access token
        /// valid — riscul cel mai mare al implementărilor de 2FA făcute cu un JWT
        /// „pe jumătate autentificat”.
        /// </summary>
        public string? TwoFactorChallengeHash { get; set; }

        public DateTime? TwoFactorChallengeExpiresAt { get; set; }

        /// <summary>
        /// Coduri greșite pe provocarea curentă. Fără contor, provocarea devine un
        /// oracol în care se pot încerca coduri de 6 cifre la nesfârșit.
        /// </summary>
        public int TwoFactorChallengeAttempts { get; set; }

        // ── Proprietăți calculate ────────────────────────────────────────────

        /// <summary>True dacă utilizatorul și-a generat cheile și poate primi fișiere.</summary>
        [NotMapped]
        public bool HasKeys => !string.IsNullOrEmpty(PublicKeyEncryption);

        /// <summary>
        /// True dacă contul este blocat chiar acum. Calculată, nu stocată —
        /// [NotMapped] este obligatoriu, altfel EF caută o coloană "IsLockedOut".
        /// </summary>
        [NotMapped]
        public bool IsLockedOut => LockoutEndsAt.HasValue && LockoutEndsAt.Value > DateTime.UtcNow;

        /// <summary>Câte coduri de recuperare mai sunt neconsumate.</summary>
        [NotMapped]
        public int RemainingRecoveryCodes =>
            string.IsNullOrEmpty(TwoFactorRecoveryCodeHashes)
                ? 0
                : TwoFactorRecoveryCodeHashes.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}