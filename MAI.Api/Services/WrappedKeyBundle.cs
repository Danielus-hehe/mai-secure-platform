using MAI.Domain.Entities;

namespace MAI.Api.Services
{
    /// <summary>
    /// Regulile pentru un pachet de chei private reîmpachetat în browser.
    ///
    /// Folosite din două locuri, care trebuie să accepte exact același lucru:
    /// schimbarea parolei (AuthController, atomic cu hash-ul nou) și
    /// reîmpachetarea după o schimbare de parolă în Active Directory
    /// (KeysController.Rewrap). Serverul nu poate verifica dacă blobul se
    /// descuie - nu are parola în forma în care ar trebui -, deci verifică doar
    /// că e complet și că parametrii de derivare nu sunt slăbiți.
    /// </summary>
    public static class WrappedKeyBundle
    {
        /// <summary>Sub acest prag, PBKDF2 nu mai protejează blobul la o copie a bazei.</summary>
        public const int MinIterations = 100_000;

        /// <summary>Mesajul de eroare pentru client, sau null dacă pachetul e acceptabil.</summary>
        public static string? Validate(string? encryptedPrivateBundle, string? salt, string? wrapIv, int iterations)
        {
            if (string.IsNullOrWhiteSpace(encryptedPrivateBundle) ||
                string.IsNullOrWhiteSpace(salt) ||
                string.IsNullOrWhiteSpace(wrapIv))
            {
                return "Pachet de chei incomplet.";
            }

            if (iterations < MinIterations)
                return "Numărul de iterații PBKDF2 este prea mic (minim 100.000).";

            return null;
        }

        /// <summary>Pune pachetul pe cont. Nu salvează: apelantul decide tranzacția.</summary>
        public static void Apply(User user, string encryptedPrivateBundle, string salt, string wrapIv, int iterations)
        {
            user.EncryptedPrivateBundle  = encryptedPrivateBundle;
            user.KeyDerivationSalt       = salt;
            user.KeyDerivationIterations = iterations;
            user.KeyWrapIv               = wrapIv;

            // Momentul noii împachetări. Pentru conturile de domeniu, asta e
            // exact ce oprește cererea repetată de reîmpachetare la fiecare
            // autentificare de după schimbarea parolei în AD.
            user.KeysWrappedAt = DateTime.UtcNow;
        }
    }
}
