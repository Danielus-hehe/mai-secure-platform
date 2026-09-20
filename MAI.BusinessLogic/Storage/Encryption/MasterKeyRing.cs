using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MAI.BusinessLogic.Security;

namespace MAI.BusinessLogic.Storage.Encryption
{
    /// <summary>
    /// Setul de chei principale (KEK), fiecare cu un identificator scurt.
    ///
    /// Identificatorul ajunge în antetul fiecărui fișier criptat, deci la
    /// citire se știe exact ce cheie trebuie folosită, fără încercări. Asta face
    /// rotirea posibilă: se adaugă o cheie nouă, devine activă, iar cele vechi
    /// rămân doar pentru citire până când scriptul de recriptare mută toate
    /// fișierele pe cea nouă. Abia apoi cheia veche se scoate din .env.
    ///
    /// Mesajele de eroare nu conțin niciodată valoarea unei chei, doar
    /// identificatorul și poziția ei în listă: excepțiile de la pornire ajung în
    /// loguri, iar logurile au mult mai puține controale de acces decât .env.
    /// </summary>
    public sealed class MasterKeyRing
    {
        public const int KeySizeBytes = 32;
        public const int MaxKeyIdLength = 32;

        private static readonly Regex KeyIdPattern =
            new("^[A-Za-z0-9._-]{1,32}$", RegexOptions.CultureInvariant);

        private readonly Dictionary<string, byte[]> _keys;

        private MasterKeyRing(Dictionary<string, byte[]> keys, string activeKeyId)
        {
            _keys       = keys;
            ActiveKeyId = activeKeyId;
        }

        /// <summary>Cheia cu care se împachetează fișierele noi.</summary>
        public string ActiveKeyId { get; }

        /// <summary>Toți identificatorii configurați, activul inclus.</summary>
        public IReadOnlyCollection<string> KeyIds => _keys.Keys;

        public bool Contains(string keyId) => _keys.ContainsKey(keyId);

        /// <summary>Validează un identificator de cheie (litere, cifre, „.”, „_”, „-”, maxim 32).</summary>
        public static bool IsValidKeyId(string? keyId) =>
            !string.IsNullOrEmpty(keyId) && KeyIdPattern.IsMatch(keyId);

        /// <summary>
        /// Interpretează „id:base64;id2:base64” (separatori acceptați: „;”, „,”,
        /// linie nouă). Aruncă <see cref="InvalidOperationException"/> cu un
        /// mesaj explicit la orice problemă.
        /// </summary>
        public static MasterKeyRing Parse(string? spec, string? activeKeyId)
        {
            if (string.IsNullOrWhiteSpace(spec))
                throw new InvalidOperationException("MAI_STORAGE_MASTER_KEYS este gol.");

            if (PlaceholderSecrets.IsPlaceholder(spec))
                throw new InvalidOperationException(
                    "MAI_STORAGE_MASTER_KEYS are încă valoarea-șablon din repository. " +
                    "Generați o cheie: dotnet run --project MAI.Api -- storage:generate-key");

            var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var entries = spec.Split(new[] { ';', ',', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var position = i + 1;

                // Base64 nu conține „:”, deci primul „:” separă sigur id-ul.
                var colon = entry.IndexOf(':');
                if (colon <= 0 || colon == entry.Length - 1)
                    throw new InvalidOperationException(
                        $"MAI_STORAGE_MASTER_KEYS, intrarea {position}: formatul este <id>:<cheie base64>.");

                var id  = entry[..colon].Trim();
                var b64 = entry[(colon + 1)..].Trim();

                if (!IsValidKeyId(id))
                    throw new InvalidOperationException(
                        $"MAI_STORAGE_MASTER_KEYS, intrarea {position}: identificatorul trebuie să aibă " +
                        "1-32 caractere din A-Z, a-z, 0-9, „.”, „_”, „-”.");

                if (keys.ContainsKey(id))
                    throw new InvalidOperationException(
                        $"MAI_STORAGE_MASTER_KEYS: identificatorul '{id}' apare de două ori.");

                byte[] key;
                try
                {
                    key = Convert.FromBase64String(b64);
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException(
                        $"MAI_STORAGE_MASTER_KEYS, cheia '{id}': nu este base64 valid.");
                }

                // Strict 32 de octeți, fără derivare din text: o cheie principală
                // scrisă de mână („parola123”) ar trece altfel neobservată, iar
                // toată criptarea depozitului ar sta pe ea.
                if (key.Length != KeySizeBytes)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new InvalidOperationException(
                        $"MAI_STORAGE_MASTER_KEYS, cheia '{id}': are {key.Length} octeți, trebuie exact " +
                        $"{KeySizeBytes} (openssl rand -base64 32).");
                }

                if (key.All(b => b == 0))
                    throw new InvalidOperationException(
                        $"MAI_STORAGE_MASTER_KEYS, cheia '{id}': este formată doar din zerouri.");

                keys[id] = key;
            }

            if (keys.Count == 0)
                throw new InvalidOperationException("MAI_STORAGE_MASTER_KEYS nu conține nicio cheie.");

            var active = activeKeyId?.Trim();
            if (string.IsNullOrEmpty(active))
            {
                // O singură cheie: nu e nicio ambiguitate. Cu mai multe, alegerea
                // trebuie să fie explicită - altfel ordinea din .env ar decide.
                if (keys.Count == 1) active = keys.Keys.First();
                else throw new InvalidOperationException(
                    "Sunt configurate mai multe chei principale, dar STORAGE_ENCRYPTION_ACTIVE_KEY lipsește.");
            }

            if (!keys.ContainsKey(active))
                throw new InvalidOperationException(
                    $"STORAGE_ENCRYPTION_ACTIVE_KEY = '{active}', dar nu există o cheie cu acest " +
                    $"identificator în MAI_STORAGE_MASTER_KEYS (configurate: {string.Join(", ", keys.Keys)}).");

            return new MasterKeyRing(keys, active);
        }

        /// <summary>
        /// Cheia cu identificatorul dat. Aruncă <see cref="CryptographicException"/>
        /// dacă lipsește: fișierul a fost împachetat cu o cheie scoasă din .env.
        /// </summary>
        internal byte[] GetKey(string keyId)
        {
            if (_keys.TryGetValue(keyId, out var key)) return key;

            throw new CryptographicException(
                $"Fișierul este criptat cu cheia principală '{keyId}', care nu mai este configurată. " +
                "Adăugați-o înapoi în MAI_STORAGE_MASTER_KEYS (poate rămâne inactivă).");
        }

        internal byte[] ActiveKey => _keys[ActiveKeyId];

        /// <summary>Generează o cheie nouă, gata de pus în .env: „id:base64”.</summary>
        public static string GenerateEntry(string keyId)
        {
            if (!IsValidKeyId(keyId))
                throw new ArgumentException("Identificator de cheie invalid.", nameof(keyId));

            var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
            try
            {
                return $"{keyId}:{Convert.ToBase64String(key)}";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}
