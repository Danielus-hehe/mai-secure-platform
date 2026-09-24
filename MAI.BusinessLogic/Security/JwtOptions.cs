using System.Text;

namespace MAI.BusinessLogic.Security
{
    /// <summary>
    /// Parametrii de emitere și validare a tokenurilor.
    ///
    /// Există ca tip separat pentru că înainte aceleași valori erau citite din
    /// <c>IConfiguration</c> în două locuri: Program.cs le folosea la validare,
    /// AuthController la emitere. Două locuri care trebuie să rămână identice
    /// sunt un loc unde diverg - iar aici divergența înseamnă tokenuri emise pe
    /// care serverul propriu le respinge.
    /// </summary>
    public class JwtOptions
    {
        /// <summary>Cheia de semnare HS256. Minim 32 de octeți de entropie reală.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Cine a emis tokenul. Validat la primire: fără el, orice token semnat cu
        /// aceeași cheie - inclusiv unul emis de alt serviciu al aceleiași
        /// instituții, sau de un proiect unde cheia s-a scurs - ar fi acceptat aici.
        /// </summary>
        public string Issuer { get; set; } = "SGDM";

        /// <summary>Pentru cine a fost emis. Împiedică refolosirea unui token între aplicații.</summary>
        public string Audience { get; set; } = "sgdm-frontend";

        /// <summary>
        /// Durata tokenului de acces. Scurtă intenționat: revocarea reală se face
        /// pe refresh token, care e verificat în baza de date la fiecare rotație.
        /// </summary>
        public int AccessTokenMinutes { get; set; } = 15;

        /// <summary>
        /// Cât trăiește un refresh token de la emitere. Fiecare rotație emite
        /// unul nou, deci valoarea spune cât poate lipsi un utilizator (laptop
        /// închis peste weekend) fără să se autentifice din nou.
        /// </summary>
        public int RefreshTokenDays { get; set; } = 7;

        /// <summary>
        /// Durata absolută a unei sesiuni, în ore, de la autentificare.
        ///
        /// Rotația prelungea sesiunea cu <see cref="RefreshTokenDays"/> la
        /// fiecare /refresh, deci o sesiune activă nu expira niciodată. Un
        /// refresh token copiat rămânea bun cât timp hoțul îl folosea măcar o
        /// dată pe săptămână. Limita fixă îl obligă pe oricine, titular sau nu,
        /// să treacă din nou prin parolă (și 2FA) după o zi de lucru.
        /// Implicit 12: o tură lungă, fără ca utilizatorul să fie deconectat în
        /// mijlocul ei. Din .env: SESSION_ABSOLUTE_HOURS.
        /// </summary>
        public int SessionAbsoluteHours { get; set; } = 12;

        /// <summary>Valoarea-șablon din appsettings.json versionat.</summary>
        public const string PlaceholderKey = "YOUR_JWT_SECRET_KEY_HERE";

        /// <summary>
        /// Oprește pornirea aplicației dacă tokenurile ar fi falsificabile.
        /// Un server care rulează cu o cheie de 20 de caractere e mai rău decât
        /// unul care nu pornește: primul pare că funcționează.
        /// </summary>
        public void Validate() => Validate(allowTemplateKey: false);

        /// <param name="allowTemplateKey">
        /// True doar în Development: o cheie-șablon din fișierele versionate este
        /// tolerată local, cu avertisment în log, ca un mediu de lucru existent să
        /// nu se oprească brusc. În orice alt mediu, Program.cs trece false.
        /// </param>
        public void Validate(bool allowTemplateKey)
        {
            if (string.IsNullOrWhiteSpace(Key) || Key == PlaceholderKey)
            {
                throw new InvalidOperationException(
                    "Jwt:Key lipsește sau are valoarea-șablon. " +
                    "Setați variabila de mediu MAI_JWT_KEY. Generați: openssl rand -base64 48");
            }

            // Orice altă valoare-șablon, de exemplu cea din .env.example. Are peste
            // 32 de octeți, deci verificarea de lungime de mai jos o accepta: cine
            // copia șablonul fără să-l completeze rula cu o cheie publicată pe
            // GitHub, iar oricine putea semna un token de Administrator.
            if (!allowTemplateKey && PlaceholderSecrets.IsPlaceholder(Key))
            {
                throw new InvalidOperationException(
                    "Jwt:Key are valoarea-șablon din repository, deci este publică. " +
                    "Setați MAI_JWT_KEY la o valoare generată cu: openssl rand -base64 48");
            }

            // HS256 cu o cheie scurtă se poate sparge offline pornind de la un
            // singur token interceptat.
            if (Encoding.UTF8.GetByteCount(Key) < 32)
            {
                throw new InvalidOperationException(
                    "Jwt:Key trebuie să aibă cel puțin 32 de octeți. Generați: openssl rand -base64 48");
            }

            if (string.IsNullOrWhiteSpace(Issuer))
                throw new InvalidOperationException("Jwt:Issuer nu poate fi gol - este validat la fiecare cerere.");

            if (string.IsNullOrWhiteSpace(Audience))
                throw new InvalidOperationException("Jwt:Audience nu poate fi gol - este validat la fiecare cerere.");

            if (AccessTokenMinutes <= 0)  AccessTokenMinutes = 15;
            if (RefreshTokenDays   <= 0)  RefreshTokenDays   = 7;
            if (SessionAbsoluteHours <= 0) SessionAbsoluteHours = 12;

            // O sesiune mai scurtă decât un token de acces ar expira înaintea
            // primului refresh: utilizatorul ar fi deconectat la fiecare cerere
            // după primul sfert de oră, fără vreo explicație.
            if (SessionAbsoluteHours * 60 < AccessTokenMinutes)
            {
                throw new InvalidOperationException(
                    $"Jwt:SessionAbsoluteHours ({SessionAbsoluteHours} h) este mai mică decât durata " +
                    $"tokenului de acces ({AccessTokenMinutes} min). Măriți SESSION_ABSOLUTE_HOURS.");
            }

            // Peste 30 de zile, limita absolută nu mai limitează nimic practic:
            // e mai probabil o unitate greșită (minute în loc de ore).
            if (SessionAbsoluteHours > 720)
            {
                throw new InvalidOperationException(
                    $"Jwt:SessionAbsoluteHours = {SessionAbsoluteHours} depășește 720 (30 de zile). " +
                    "Valoarea este în ore; verificați SESSION_ABSOLUTE_HOURS.");
            }

            // Un token de acces cu viață lungă anulează rostul refresh token-ului:
            // revocarea la logout n-ar avea efect până la expirare.
            if (AccessTokenMinutes > 120)
            {
                throw new InvalidOperationException(
                    "Jwt:AccessTokenMinutes peste 120 anulează revocarea: un token furat rămâne " +
                    "valid ore întregi după delogare. Reduceți valoarea.");
            }
        }
    }
}
