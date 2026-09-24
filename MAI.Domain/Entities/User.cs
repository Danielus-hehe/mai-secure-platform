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

        /// <summary>
        /// Subdiviziunea din care face parte utilizatorul. Înlocuiește vechiul
        /// câmp text liber Department (vezi OrgUnit). Null = neîncadrat încă.
        /// </summary>
        public Guid? OrgUnitId { get; set; }
        public OrgUnit? OrgUnit { get; set; }

        public UserRole Role { get; set; } = UserRole.Utilizator;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Token de concurență: coloana de sistem xmin din PostgreSQL, schimbată
        /// de server la fiecare UPDATE. Nu se scrie niciodată din cod.
        ///
        /// Rândul utilizatorului ține contoare care se citesc și se rescriu în
        /// aceeași cerere: încercările eșuate (blocarea contului), încercările
        /// pe provocarea 2FA, ultimul pas TOTP acceptat, codurile de recuperare.
        /// Fără token, două cereri paralele citeau aceeași valoare și o scriau
        /// pe rând: ultimul câștiga, iar contorul pierdea incrementări. Așa se
        /// puteau încerca mai multe coduri 2FA decât limita, iar același cod de
        /// recuperare putea fi folosit de două ori. Cu xmin, a doua salvare
        /// eșuează (409), deci o singură cerere concurentă ajunge să conteze.
        /// </summary>
        public uint Version { get; set; }

        // ── Cont de domeniu (Active Directory) ───────────────────────────────
        // Un cont de domeniu nu are parolă la noi: PasswordHash rămâne gol, iar
        // verificarea se face printr-un bind LDAPS. Diferența e importantă la
        // plecarea unui angajat - contul dezactivat în AD nu mai poate intra,
        // fără să depindă de sincronizarea unei copii locale a parolei.

        /// <summary>Cine verifică parola: baza noastră (Local) sau domeniul (Ldap).</summary>
        public AuthProvider AuthProvider { get; set; } = Enums.AuthProvider.Local;

        /// <summary>
        /// objectGUID-ul contului din AD, ca text. Este singurul identificator
        /// stabil: sAMAccountName-ul și DN-ul se schimbă la redenumire sau la
        /// mutarea în altă unitate organizatorică, GUID-ul nu.
        /// </summary>
        public string? DirectoryObjectId { get; set; }

        /// <summary>DN-ul complet din AD, folosit la bind și afișat administratorului.</summary>
        public string? DirectoryDn { get; set; }

        /// <summary>
        /// Momentul ultimei schimbări de parolă în domeniu (atributul pwdLastSet).
        ///
        /// Cheile private E2EE sunt încuiate cu o cheie derivată din parolă. Când
        /// parola se schimbă în AD, noi nu suntem anunțați: utilizatorul intră cu
        /// parola nouă, dar blobul rămâne încuiat cu cea veche. Comparat cu
        /// <see cref="KeysWrappedAt"/>, câmpul acesta ne spune exact când s-a
        /// întâmplat, deci putem cere reîmpachetarea în loc să lăsăm descuierea
        /// să eșueze cu „parolă greșită”.
        /// </summary>
        public DateTime? DirectoryPasswordSetAt { get; set; }

        /// <summary>Ultima sincronizare a atributelor din AD (nume, email, grupuri, subdiviziune).</summary>
        public DateTime? DirectorySyncedAt { get; set; }

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

        /// <summary>Ultima încercare eșuată (UTC) - pentru audit și rapoarte.</summary>
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
        // Tokenul e stocat ca SHA-256 (hex) - nu în clar - ca și refresh token-ul.
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
        // Serverul nu le poate descuia - asta e tot rostul.

        /// <summary>Cheia publică RSA-OAEP (SPKI, base64) - împachetează cheile de fișier.</summary>
        public string? PublicKeyEncryption { get; set; }

        /// <summary>Cheia publică RSA-PSS (SPKI, base64) - verifică semnăturile.</summary>
        public string? PublicKeySigning { get; set; }

        /// <summary>Cheile private, criptate AES-256-GCM cu cheia derivată din parolă.</summary>
        public string? EncryptedPrivateBundle { get; set; }

        /// <summary>Salt-ul PBKDF2 folosit la derivarea cheii de împachetare (base64).</summary>
        public string? KeyDerivationSalt { get; set; }

        /// <summary>Numărul de iterații PBKDF2 - stocat ca să putem crește pragul în timp.</summary>
        public int? KeyDerivationIterations { get; set; }

        /// <summary>IV-ul AES-GCM folosit la împachetarea cheilor private (base64).</summary>
        public string? KeyWrapIv { get; set; }

        /// <summary>Suita criptografică folosită, pentru migrări viitoare.</summary>
        public string? CryptoSuite { get; set; }

        public DateTime? KeysCreatedAt { get; set; }

        /// <summary>
        /// Când a fost încuiat ultima dată blobul de chei private cu parola
        /// curentă (la generare sau la reîmpachetare). Separat de
        /// <see cref="KeysCreatedAt"/>, care marchează nașterea perechilor RSA și
        /// nu trebuie să se schimbe: amprenta cheii publice rămâne aceeași.
        /// </summary>
        public DateTime? KeysWrappedAt { get; set; }

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
        /// valid - riscul cel mai mare al implementărilor de 2FA făcute cu un JWT
        /// „pe jumătate autentificat”.
        /// </summary>
        public string? TwoFactorChallengeHash { get; set; }

        public DateTime? TwoFactorChallengeExpiresAt { get; set; }

        /// <summary>
        /// Coduri greșite pe provocarea curentă. Fără contor, provocarea devine un
        /// oracol în care se pot încerca coduri de 6 cifre la nesfârșit.
        /// </summary>
        public int TwoFactorChallengeAttempts { get; set; }

        // ── Resetarea parolei prin email ─────────────────────────────────────
        // Separat de InvitationToken: invitația activează un cont nou (și poate
        // fi retrimisă), resetarea înlocuiește parola unui cont existent. Un
        // singur câmp pentru amândouă ar face ca retrimiterea invitației să
        // anuleze o resetare în curs, și invers.

        /// <summary>SHA-256 (hex) al tokenului din linkul de resetare. Niciodată tokenul brut.</summary>
        public string? PasswordResetToken { get; set; }

        public DateTime? PasswordResetTokenExpiry { get; set; }

        // ── Proprietăți calculate ────────────────────────────────────────────

        /// <summary>True dacă utilizatorul și-a generat cheile și poate primi fișiere.</summary>
        [NotMapped]
        public bool HasKeys => !string.IsNullOrEmpty(PublicKeyEncryption);

        /// <summary>Contul se autentifică în Active Directory, nu cu o parolă ținută la noi.</summary>
        [NotMapped]
        public bool IsDirectoryAccount => AuthProvider == Enums.AuthProvider.Ldap;

        /// <summary>
        /// Parola din domeniu s-a schimbat după ultima împachetare a cheilor,
        /// deci blobul nu se mai poate descuia cu parola de azi. Frontend-ul
        /// cere parola veche o singură dată și reîmpachetează.
        ///
        /// Fără marginea de un minut, o reîmpachetare făcută în aceeași secundă
        /// cu schimbarea parolei (utilizatorul intră imediat după) ar putea fi
        /// raportată la nesfârșit ca necesară, din cauza rotunjirii FILETIME.
        /// </summary>
        [NotMapped]
        public bool KeyRewrapRequired =>
            IsDirectoryAccount
            && HasKeys
            && DirectoryPasswordSetAt.HasValue
            && KeysWrappedAt.HasValue
            && DirectoryPasswordSetAt.Value > KeysWrappedAt.Value.AddMinutes(1);

        /// <summary>
        /// True dacă contul este blocat chiar acum. Calculată, nu stocată -
        /// [NotMapped] este obligatoriu, altfel EF caută o coloană "IsLockedOut".
        /// </summary>
        [NotMapped]
        public bool IsLockedOut => LockoutEndsAt.HasValue && LockoutEndsAt.Value > DateTime.UtcNow;

        /// <summary>
        /// Șterge materialul de chei E2EE. Necesar la orice schimbare de parolă
        /// făcută FĂRĂ parola veche (resetare de administrator, link de resetare):
        /// cheile private sunt încuiate cu o cheie derivată din parola veche, deci
        /// nu mai pot fi descuiate. Lăsate pe loc, contul ar intra în impas -
        /// descuierea eșuează, iar serverul refuză chei noi fiindcă „există deja”.
        /// </summary>
        /// <returns>True dacă existau chei.</returns>
        public bool ClearEncryptionKeys()
        {
            var had = HasKeys;
            PublicKeyEncryption     = null;
            PublicKeySigning        = null;
            EncryptedPrivateBundle  = null;
            KeyDerivationSalt       = null;
            KeyDerivationIterations = null;
            KeyWrapIv               = null;
            CryptoSuite             = null;
            KeysCreatedAt           = null;
            return had;
        }

        /// <summary>Câte coduri de recuperare mai sunt neconsumate.</summary>
        [NotMapped]
        public int RemainingRecoveryCodes =>
            string.IsNullOrEmpty(TwoFactorRecoveryCodeHashes)
                ? 0
                : TwoFactorRecoveryCodeHashes.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}