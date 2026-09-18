using System.Security.Cryptography;
using System.Text;

namespace MAI.BusinessLogic.Security
{
    /// <summary>
    /// Cifreaza secrete scurte inainte sa ajunga in baza de date.
    ///
    /// Folosit pentru secretele TOTP. Motivul e concret: un secret TOTP stocat in
    /// clar are exact aceeasi valoare pentru un atacator ca pentru utilizator —
    /// cine citeste coloana poate genera coduri valide la infinit. Spre deosebire
    /// de parola, care e stocata ca hash si nu poate fi reconstruita, secretul
    /// TOTP TREBUIE sa fie reversibil ca serverul sa poata verifica codul. Deci
    /// singura protectie posibila e cifrarea cu o cheie tinuta in afara bazei.
    ///
    /// Consecinta care trebuie inteleasa: daca atacatorul obtine si baza de date,
    /// si cheia din variabila de mediu, 2FA cade. Cifrarea apara scenariul
    /// realist — dump de bază de date, backup pierdut, SQL injection — nu
    /// compromiterea totala a serverului.
    ///
    /// AES-256-GCM: cifrare autentificata. Un octet modificat in baza de date
    /// face decriptarea sa esueze, in loc sa produca un secret gresit cu care
    /// nicio autentificare nu ar mai functiona si nimeni nu ar sti de ce.
    /// </summary>
    public class SecretProtector
    {
        private const int NonceSize = 12;   // 96 biti, dimensiunea recomandata pentru GCM
        private const int TagSize   = 16;   // 128 biti

        private readonly byte[] _key;

        public SecretProtector(TwoFactorOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.EncryptionKey))
            {
                throw new InvalidOperationException(
                    "TwoFactor:EncryptionKey lipseste. Generati o cheie de 32 de octeti " +
                    "(openssl rand -base64 32) si puneti-o in variabila de mediu MAI_TWOFACTOR_KEY.");
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(options.EncryptionKey);
            }
            catch (FormatException)
            {
                // Acceptam si text simplu, dar il trecem prin SHA-256 ca sa obtinem
                // fix 32 de octeti. Nu adauga entropie — doar face configurarea
                // tolerabila. Cheia tot trebuie sa fie aleatorie.
                key = SHA256.HashData(Encoding.UTF8.GetBytes(options.EncryptionKey));
            }

            if (key.Length != 32)
                key = SHA256.HashData(key);

            _key = key;
        }

        /// <summary>
        /// Cifreaza si returneaza base64 peste nonce || ciphertext || tag.
        ///
        /// Nonce-ul e inclus in rezultat, nu stocat separat: un nonce reutilizat
        /// cu aceeasi cheie compromite GCM complet, iar tinerea lui in aceeasi
        /// valoare elimina orice sansa sa fie desincronizat de cifrotext.
        /// </summary>
        public string Protect(string plaintext)
        {
            ArgumentException.ThrowIfNullOrEmpty(plaintext);

            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce      = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher     = new byte[plainBytes.Length];
            var tag        = new byte[TagSize];

            using (var aes = new AesGcm(_key, TagSize))
                aes.Encrypt(nonce, plainBytes, cipher, tag);

            var output = new byte[NonceSize + cipher.Length + TagSize];
            Buffer.BlockCopy(nonce,  0, output, 0, NonceSize);
            Buffer.BlockCopy(cipher, 0, output, NonceSize, cipher.Length);
            Buffer.BlockCopy(tag,    0, output, NonceSize + cipher.Length, TagSize);

            return Convert.ToBase64String(output);
        }

        /// <summary>
        /// Descifreaza. Arunca <see cref="CryptographicException"/> daca valoarea
        /// a fost modificata sau daca cheia s-a schimbat.
        /// </summary>
        public string Unprotect(string protectedValue)
        {
            ArgumentException.ThrowIfNullOrEmpty(protectedValue);

            var raw = Convert.FromBase64String(protectedValue);

            if (raw.Length < NonceSize + TagSize)
                throw new CryptographicException("Valoare protejata prea scurta ca sa fie valida.");

            var cipherLength = raw.Length - NonceSize - TagSize;

            var nonce  = new byte[NonceSize];
            var cipher = new byte[cipherLength];
            var tag    = new byte[TagSize];

            Buffer.BlockCopy(raw, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(raw, NonceSize, cipher, 0, cipherLength);
            Buffer.BlockCopy(raw, NonceSize + cipherLength, tag, 0, TagSize);

            var plain = new byte[cipherLength];

            // În .NET 8, AesGcm aruncă AuthenticationTagMismatchException (o
            // subclasă). O normalizăm la CryptographicException cu mesaj propriu:
            // apelanții tratează un singur tip, iar mesajul nu divulgă dacă a
            // fost cheia greșită sau datele alterate — ambele arată la fel.
            try
            {
                using var aes = new AesGcm(_key, TagSize);
                aes.Decrypt(nonce, cipher, tag, plain);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                CryptographicOperations.ZeroMemory(plain);
                throw new CryptographicException(
                    "Valoarea protejata nu poate fi decriptata: cheie diferita sau date alterate.", ex);
            }

            return Encoding.UTF8.GetString(plain);
        }
    }
}