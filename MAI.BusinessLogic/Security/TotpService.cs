using System.Security.Cryptography;
using System.Text;

namespace MAI.BusinessLogic.Security
{
    /// <summary>
    /// TOTP conform RFC 6238 (peste HOTP, RFC 4226).
    ///
    /// Scris de mana, fara pachet extern. Algoritmul are vreo 15 linii utile, iar
    /// o dependinta in plus intr-un proiect de securitate inseamna inca un lant
    /// de aprovizionare de verificat. Compatibil cu Google Authenticator, Aegis,
    /// FreeOTP, 1Password, Microsoft Authenticator.
    ///
    /// De ce HMAC-SHA1 si nu SHA-256: RFC 6238 permite ambele, dar aplicatiile de
    /// autentificare de pe telefon ignora in practica parametrul "algorithm" din
    /// URI si presupun SHA1. Folosirea SHA-256 ar produce coduri pe care
    /// utilizatorul nu le poate genera. SHA1 este slabit pentru rezistenta la
    /// coliziuni; TOTP il foloseste ca HMAC, unde slabiciunile de coliziune nu se
    /// aplica, iar codul e oricum valabil 30 de secunde.
    /// </summary>
    public class TotpService
    {
        private readonly TwoFactorOptions _options;

        public TotpService(TwoFactorOptions options) => _options = options;

        // ── Generarea secretului ─────────────────────────────────────────────

        /// <summary>
        /// Secret nou de 20 de octeti (160 biti), codificat Base32.
        ///
        /// 160 de biti este dimensiunea recomandata de RFC 4226 si exact cat
        /// produce HMAC-SHA1. Mai putin ar reduce inutil entropia; mai mult ar fi
        /// trunchiat oricum de HMAC.
        /// </summary>
        public string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

        /// <summary>
        /// URI otpauth:// pentru codul QR.
        ///
        /// Eticheta e "Emitent:utilizator", iar parametrul issuer se repeta —
        /// redundanta e ceruta de specificatie, pentru ca unele aplicatii citesc
        /// doar unul din cele doua locuri.
        /// </summary>
        public string BuildOtpAuthUri(string username, string secretBase32)
        {
            var issuer = Uri.EscapeDataString(_options.Issuer);
            var label  = Uri.EscapeDataString($"{_options.Issuer}:{username}");

            return $"otpauth://totp/{label}" +
                   $"?secret={secretBase32}" +
                   $"&issuer={issuer}" +
                   $"&algorithm=SHA1" +
                   $"&digits={_options.Digits}" +
                   $"&period={_options.PeriodSeconds}";
        }

        // ── Verificarea codului ──────────────────────────────────────────────

        /// <summary>
        /// Verifica un cod, acceptand o fereastra de ±WindowSteps intervale.
        ///
        /// Compararea se face in timp constant si abia dupa ce s-au calculat toate
        /// codurile candidate. Un scurtcircuit la prima potrivire ar scurge, prin
        /// diferenta de timp, in ce interval a nimerit codul — informatie mica,
        /// dar gratuita de eliminat.
        /// </summary>
        public bool VerifyCode(string secretBase32, string? code) =>
            MatchStep(secretBase32, code) is not null;

        /// <summary>
        /// Verifică un cod și refuză reutilizarea lui (RFC 6238, secțiunea 5.2:
        /// verificatorul NU trebuie să accepte a doua oară același cod după o
        /// validare reușită).
        ///
        /// <paramref name="lastUsedStep"/> este intervalul ultimului cod acceptat
        /// pentru acest cont. Un cod dintr-un interval egal sau mai vechi e respins
        /// ca reluare, chiar dacă e încă în fereastra de toleranță. Consecință
        /// practică: după o autentificare reușită, următoarea cere codul nou din
        /// aplicație (cel mult 30 de secunde de așteptare).
        /// </summary>
        public TotpVerification Verify(string secretBase32, string? code, long? lastUsedStep)
        {
            var step = MatchStep(secretBase32, code);

            if (step is null)
                return new TotpVerification(TotpVerificationStatus.Invalid, null);

            if (lastUsedStep is long last && step.Value <= last)
                return new TotpVerification(TotpVerificationStatus.Replayed, step);

            return new TotpVerification(TotpVerificationStatus.Accepted, step);
        }

        /// <summary>
        /// Intervalul în care se potrivește codul, sau null. Parcurge toată
        /// fereastra și compară în timp constant; dacă mai multe intervale se
        /// potrivesc (coincidență de 1 la un milion), îl reține pe cel mai recent.
        /// </summary>
        private long? MatchStep(string secretBase32, string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            // Utilizatorii copiaza codul cu spatii ("123 456") din aplicatie.
            var normalized = new string(code.Where(char.IsDigit).ToArray());

            if (normalized.Length != _options.Digits) return null;

            byte[] secret;
            try
            {
                secret = Base32Decode(secretBase32);
            }
            catch (FormatException)
            {
                return null;
            }

            var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / _options.PeriodSeconds;

            long? matched = null;

            for (var offset = -_options.WindowSteps; offset <= _options.WindowSteps; offset++)
            {
                var step     = currentStep + offset;
                var expected = ComputeCode(secret, step);

                // Fără return la prima potrivire: parcurgem întotdeauna toată
                // fereastra, ca timpul să nu depindă de intervalul nimerit.
                var equal = CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(normalized));

                matched = equal ? step : matched;
            }

            return matched;
        }

        /// <summary>Codul pentru un interval dat. Public pentru teste.</summary>
        public string ComputeCode(byte[] secret, long step)
        {
            // Contorul pe 8 octeti, big-endian, conform RFC 4226.
            var counter = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian) Array.Reverse(counter);

            using var hmac = new HMACSHA1(secret);
            var hash = hmac.ComputeHash(counter);

            // Trunchiere dinamica: ultimii 4 biti aleg de unde se citesc cei 31 de
            // biti ai codului. Fara asta, codul ar folosi mereu aceiasi octeti.
            var offset = hash[^1] & 0x0F;

            var binary =
                ((hash[offset]     & 0x7F) << 24) |
                ((hash[offset + 1] & 0xFF) << 16) |
                ((hash[offset + 2] & 0xFF) << 8)  |
                 (hash[offset + 3] & 0xFF);

            var modulo = (int)Math.Pow(10, _options.Digits);

            return (binary % modulo).ToString().PadLeft(_options.Digits, '0');
        }

        // ── Coduri de recuperare ─────────────────────────────────────────────

        /// <summary>
        /// Genereaza coduri de recuperare in clar. Se afiseaza O SINGURA DATA,
        /// la activare; in baza de date ajung doar hash-urile.
        ///
        /// Format: XXXX-XXXX, alfabet fara caractere ambigue (fara 0/O, 1/I/L),
        /// pentru ca utilizatorul le va scrie pe hartie si le va tasta sub stres,
        /// exact atunci cand si-a pierdut telefonul.
        /// </summary>
        public List<string> GenerateRecoveryCodes()
        {
            const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

            var codes = new List<string>(_options.RecoveryCodeCount);

            for (var i = 0; i < _options.RecoveryCodeCount; i++)
            {
                var chars = new char[8];
                for (var j = 0; j < 8; j++)
                    chars[j] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

                codes.Add($"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}");
            }

            return codes;
        }

        /// <summary>
        /// SHA-256 peste codul normalizat.
        ///
        /// SHA-256 simplu, nu Argon2: codul are 8 caractere dintr-un alfabet de
        /// 31, adica ~40 de biti de entropie generata criptografic. Nu exista
        /// dictionar de atacat, iar verificarea trebuie sa fie ieftina.
        /// </summary>
        public static string HashRecoveryCode(string code)
        {
            var normalized = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        }

        // ── Base32 (RFC 4648, fara padding) ──────────────────────────────────
        // Aplicatiile de autentificare accepta doar Base32. Base64 nu merge.

        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string Base32Encode(byte[] data)
        {
            var result = new StringBuilder((data.Length * 8 + 4) / 5);

            int buffer = 0, bitsLeft = 0;

            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bitsLeft += 8;

                while (bitsLeft >= 5)
                {
                    result.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                    bitsLeft -= 5;
                }
            }

            if (bitsLeft > 0)
                result.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);

            return result.ToString();
        }

        public static byte[] Base32Decode(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new FormatException("Secret Base32 gol.");

            var cleaned = input.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", string.Empty);

            var output = new List<byte>(cleaned.Length * 5 / 8);

            int buffer = 0, bitsLeft = 0;

            foreach (var c in cleaned)
            {
                var index = Base32Alphabet.IndexOf(c);
                if (index < 0) throw new FormatException($"Caracter invalid in secretul Base32: '{c}'.");

                buffer = (buffer << 5) | index;
                bitsLeft += 5;

                if (bitsLeft >= 8)
                {
                    output.Add((byte)(buffer >> (bitsLeft - 8)));
                    bitsLeft -= 8;
                }
            }

            return [.. output];
        }

        /// <summary>Secretul in grupuri de 4, pentru introducere manuala cand QR-ul nu se poate scana.</summary>
        public static string FormatSecretForDisplay(string secretBase32) =>
            string.Join(' ', Enumerable
                .Range(0, (secretBase32.Length + 3) / 4)
                .Select(i => secretBase32.Substring(i * 4, Math.Min(4, secretBase32.Length - i * 4))));
    }

    /// <summary>Rezultatul verificării unui cod TOTP.</summary>
    public enum TotpVerificationStatus
    {
        /// <summary>Codul nu corespunde niciunui interval din fereastră.</summary>
        Invalid,

        /// <summary>Codul e corect, dar intervalul lui a fost deja folosit.</summary>
        Replayed,

        /// <summary>Codul e corect și nefolosit.</summary>
        Accepted,
    }

    /// <param name="Status">Ce s-a constatat.</param>
    /// <param name="Step">Intervalul potrivit; null dacă codul e invalid.</param>
    public readonly record struct TotpVerification(TotpVerificationStatus Status, long? Step)
    {
        public bool IsAccepted => Status == TotpVerificationStatus.Accepted;
    }
}
