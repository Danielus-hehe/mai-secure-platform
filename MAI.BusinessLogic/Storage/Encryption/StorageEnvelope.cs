using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MAI.BusinessLogic.Storage.Encryption
{
    /// <summary>
    /// Formatul unui obiect criptat în depozit (versiunea 1).
    ///
    /// <code>
    /// Antet
    ///   0   7  magic „SGDMENC”
    ///   7   1  versiunea formatului (1)
    ///   8   1  L = lungimea identificatorului cheii principale (1-32)
    ///   9   L  identificatorul cheii principale (ASCII)
    ///   .   4  dimensiunea segmentului în octeți (big-endian)
    ///   .  12  nonce-ul împachetării
    ///   .  32  cheia fișierului (DEK), criptată AES-256-GCM cu cheia principală
    ///   .  16  eticheta GCM a împachetării
    /// Corp
    ///   segmente: [cifrotext (dimensiunea segmentului) | etichetă 16], ultimul
    ///   segment mai scurt decât dimensiunea (eventual gol, doar eticheta)
    /// </code>
    ///
    /// Deciziile și motivele lor:
    /// <list type="bullet">
    ///   <item>O cheie aleatorie per fișier (DEK). Nonce-urile segmentelor pot fi
    ///   atunci deterministe (contor) fără risc de reutilizare: două fișiere nu
    ///   împart niciodată cheia. Și nicio limită GCM pe volumul criptat cu o
    ///   singură cheie nu se apropie, oricâte documente s-ar stoca.</item>
    ///   <item>Împachetarea DEK are ca date asociate (AAD) antetul până la ea:
    ///   magic, versiune, identificatorul cheii, dimensiunea segmentului. Un
    ///   antet modificat (alt identificator, altă dimensiune) face despachetarea
    ///   să eșueze.</item>
    ///   <item>Segmente de 64 KiB autentificate separat: fișierul se decriptează
    ///   în flux, cu memorie constantă, nu încărcat integral. Nonce-ul
    ///   segmentului = contorul (8 octeți) + un octet „ultimul segment”. Contorul
    ///   împiedică reordonarea segmentelor, octetul final împiedică trunchierea:
    ///   un fișier tăiat exact la granița unui segment nu mai are un segment
    ///   marcat „ultimul” și este respins.</item>
    ///   <item>Datele asociate ale segmentelor NU includ cheia împachetată. Așa,
    ///   rotirea cheii principale rescrie doar antetul (câteva zeci de octeți)
    ///   și copiază corpul neatins - conținutul nu se decriptează deloc la
    ///   rotire. Un antet mutat de pe alt fișier aduce alt DEK, deci etichetele
    ///   segmentelor pică oricum.</item>
    /// </list>
    /// </summary>
    public static class StorageEnvelope
    {
        public const byte FormatVersion = 1;
        public const int NonceSize = 12;
        public const int TagSize = 16;
        public const int DekSize = 32;
        public const int MinChunkSize = 4 * 1024;
        public const int MaxChunkSize = 4 * 1024 * 1024;

        private static readonly byte[] Magic = "SGDMENC"u8.ToArray();

        /// <summary>Magic + versiune: primii 8 octeți ai oricărui obiect criptat.</summary>
        public const int PrefixLength = 8;

        /// <summary>Datele asociate ale fiecărui segment: magic + versiune.</summary>
        private static readonly byte[] ChunkAad = [.. Magic, FormatVersion];

        /// <summary>Lungimea antetului pentru un identificator de cheie de lungimea dată.</summary>
        public static int HeaderLength(int keyIdLength) =>
            PrefixLength + 1 + keyIdLength + 4 + NonceSize + DekSize + TagSize;

        /// <summary>
        /// Dimensiunea exactă a obiectului criptat. Se poate calcula înainte de
        /// criptare, deci depozitul primește Content-Length corect.
        /// </summary>
        public static long GetEncryptedLength(long plainLength, int keyIdLength, int chunkSize)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(plainLength);
            var chunks = plainLength / chunkSize + 1;     // ultimul, mai scurt, există mereu
            return HeaderLength(keyIdLength) + plainLength + chunks * TagSize;
        }

        /// <summary>True dacă primii octeți sunt magicul formatului (orice versiune).</summary>
        public static bool HasMagic(ReadOnlySpan<byte> prefix) =>
            prefix.Length >= Magic.Length && prefix[..Magic.Length].SequenceEqual(Magic);

        // ═════════════════════════════════════════════════════════════════════
        // Criptare
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Criptează <paramref name="plaintext"/> în <paramref name="output"/>,
        /// cu cheia principală activă. Întoarce numărul de octeți în clar.
        /// </summary>
        /// <param name="plaintextHash">
        /// Opțional: primește octeții în clar pe măsură ce trec (SHA-256 calculat
        /// într-o singură trecere, folosit de scriptul de recriptare).
        /// </param>
        public static async Task<long> EncryptAsync(
            Stream plaintext,
            Stream output,
            MasterKeyRing ring,
            int chunkSize,
            IncrementalHash? plaintextHash = null,
            CancellationToken ct = default)
        {
            ValidateChunkSize(chunkSize);

            var dek = RandomNumberGenerator.GetBytes(DekSize);
            var plainBuffer  = new byte[chunkSize];
            var cipherBuffer = new byte[chunkSize];
            var tag          = new byte[TagSize];
            var nonce        = new byte[NonceSize];

            try
            {
                var header = EnvelopeHeader.Create(ring.ActiveKeyId, chunkSize, dek, ring.ActiveKey);
                await output.WriteAsync(header.ToBytes(), ct);

                using var gcm = new AesGcm(dek, TagSize);

                long total = 0;
                ulong counter = 0;

                while (true)
                {
                    var read = await FillAsync(plaintext, plainBuffer, ct);
                    var last = read < chunkSize;

                    plaintextHash?.AppendData(plainBuffer, 0, read);

                    ChunkNonce(counter, last, nonce);
                    gcm.Encrypt(nonce, plainBuffer.AsSpan(0, read), cipherBuffer.AsSpan(0, read), tag, ChunkAad);

                    await output.WriteAsync(cipherBuffer.AsMemory(0, read), ct);
                    await output.WriteAsync(tag, ct);

                    total += read;
                    counter++;

                    if (last) break;
                }

                return total;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
                CryptographicOperations.ZeroMemory(plainBuffer);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Citire
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Citește începutul obiectului și decide dacă e criptat. Pentru un obiect
        /// criptat citește și validează structural antetul (fără chei); pentru unul
        /// în clar întoarce octeții deja consumați, ca să poată fi redați.
        /// </summary>
        public static async Task<EnvelopeProbe> ProbeAsync(Stream source, CancellationToken ct = default)
        {
            var prefix = new byte[PrefixLength];
            var read = await FillAsync(source, prefix, ct);

            if (read < PrefixLength || !HasMagic(prefix))
                return new EnvelopeProbe(null, prefix.AsSpan(0, read).ToArray());

            if (prefix[PrefixLength - 1] != FormatVersion)
                throw new CryptographicException(
                    $"Obiect criptat într-o versiune de format necunoscută ({prefix[PrefixLength - 1]}).");

            var lengthByte = new byte[1];
            if (await FillAsync(source, lengthByte, ct) != 1)
                throw Truncated();

            int keyIdLength = lengthByte[0];
            if (keyIdLength is < 1 or > MasterKeyRing.MaxKeyIdLength)
                throw new CryptographicException("Antet de criptare invalid: identificatorul cheii are o lungime imposibilă.");

            var rest = new byte[keyIdLength + 4 + NonceSize + DekSize + TagSize];
            if (await FillAsync(source, rest, ct) != rest.Length)
                throw Truncated();

            var headerBytes = new byte[HeaderLength(keyIdLength)];
            prefix.CopyTo(headerBytes, 0);
            headerBytes[PrefixLength] = lengthByte[0];
            rest.CopyTo(headerBytes, PrefixLength + 1);

            return new EnvelopeProbe(EnvelopeHeader.Parse(headerBytes), headerBytes);
        }

        /// <summary>
        /// Deschide un obiect pentru citire: decriptează dacă e criptat, altfel
        /// îl redă ca atare (doar dacă <paramref name="allowPlaintext"/>).
        /// Stream-ul sursă trece în proprietatea rezultatului.
        /// </summary>
        public static async Task<OpenedObject> OpenAsync(
            Stream source,
            MasterKeyRing? ring,
            bool allowPlaintext,
            CancellationToken ct = default)
        {
            EnvelopeProbe probe;
            try
            {
                probe = await ProbeAsync(source, ct);
            }
            catch
            {
                await source.DisposeAsync();
                throw;
            }

            if (probe.Header is null)
            {
                if (!allowPlaintext)
                {
                    await source.DisposeAsync();
                    throw new PlaintextObjectRejectedException();
                }

                return new OpenedObject(new PrefixedReadStream(probe.ConsumedBytes, source), null);
            }

            if (ring is null)
            {
                await source.DisposeAsync();
                throw new CryptographicException(
                    $"Obiectul este criptat cu cheia principală '{probe.Header.KeyId}', dar nu este " +
                    "configurată nicio cheie (MAI_STORAGE_MASTER_KEYS).");
            }

            byte[] dek;
            try
            {
                dek = probe.Header.UnwrapDek(ring.GetKey(probe.Header.KeyId));
            }
            catch
            {
                await source.DisposeAsync();
                throw;
            }

            return new OpenedObject(new DecryptingReadStream(source, dek, probe.Header.ChunkSize), probe.Header);
        }

        /// <summary>
        /// Antetul re-împachetat cu cheia activă: aceeași cheie de fișier, altă
        /// cheie principală. Corpul rămâne valid neschimbat (vezi descrierea clasei).
        /// </summary>
        public static byte[] RewrapHeader(EnvelopeHeader header, MasterKeyRing ring)
        {
            var dek = header.UnwrapDek(ring.GetKey(header.KeyId));
            try
            {
                return EnvelopeHeader.Create(ring.ActiveKeyId, header.ChunkSize, dek, ring.ActiveKey).ToBytes();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Utilitare interne
        // ═════════════════════════════════════════════════════════════════════

        internal static void ChunkNonce(ulong counter, bool last, Span<byte> nonce)
        {
            nonce.Clear();
            BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(3, 8), counter);
            nonce[NonceSize - 1] = last ? (byte)1 : (byte)0;
        }

        internal static ReadOnlySpan<byte> ChunkAssociatedData => ChunkAad;

        internal static void ValidateChunkSize(int chunkSize)
        {
            if (chunkSize is < MinChunkSize or > MaxChunkSize)
                throw new ArgumentOutOfRangeException(nameof(chunkSize),
                    $"Dimensiunea segmentului trebuie să fie între {MinChunkSize} și {MaxChunkSize} octeți.");
        }

        internal static CryptographicException Truncated() =>
            new("Obiect criptat trunchiat: lipsesc octeți din antet sau din ultimul segment.");

        /// <summary>Citește până umple bufferul sau până la sfârșitul fluxului.</summary>
        internal static async Task<int> FillAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer[total..], ct);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        internal static int Fill(Stream stream, Span<byte> buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer[total..]);
                if (read == 0) break;
                total += read;
            }
            return total;
        }
    }

    /// <summary>Antetul parsat al unui obiect criptat.</summary>
    public sealed class EnvelopeHeader
    {
        private EnvelopeHeader(string keyId, int chunkSize, byte[] wrapNonce, byte[] wrappedDek, byte[] wrapTag)
        {
            KeyId      = keyId;
            ChunkSize  = chunkSize;
            WrapNonce  = wrapNonce;
            WrappedDek = wrappedDek;
            WrapTag    = wrapTag;
        }

        public string KeyId { get; }
        public int ChunkSize { get; }
        private byte[] WrapNonce { get; }
        private byte[] WrappedDek { get; }
        private byte[] WrapTag { get; }

        public int Length => StorageEnvelope.HeaderLength(KeyId.Length);

        internal static EnvelopeHeader Create(string keyId, int chunkSize, byte[] dek, byte[] masterKey)
        {
            StorageEnvelope.ValidateChunkSize(chunkSize);

            var nonce   = RandomNumberGenerator.GetBytes(StorageEnvelope.NonceSize);
            var wrapped = new byte[StorageEnvelope.DekSize];
            var tag     = new byte[StorageEnvelope.TagSize];

            using (var gcm = new AesGcm(masterKey, StorageEnvelope.TagSize))
                gcm.Encrypt(nonce, dek, wrapped, tag, WrapAad(keyId, chunkSize));

            return new EnvelopeHeader(keyId, chunkSize, nonce, wrapped, tag);
        }

        internal static EnvelopeHeader Parse(byte[] bytes)
        {
            var offset = StorageEnvelope.PrefixLength;
            int keyIdLength = bytes[offset++];

            var keyId = Encoding.ASCII.GetString(bytes, offset, keyIdLength);
            offset += keyIdLength;

            if (!MasterKeyRing.IsValidKeyId(keyId))
                throw new CryptographicException("Antet de criptare invalid: identificatorul cheii conține caractere nepermise.");

            var chunkSize = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            offset += 4;

            if (chunkSize is < StorageEnvelope.MinChunkSize or > StorageEnvelope.MaxChunkSize)
                throw new CryptographicException("Antet de criptare invalid: dimensiunea segmentului este în afara limitelor.");

            var nonce = bytes.AsSpan(offset, StorageEnvelope.NonceSize).ToArray();
            offset += StorageEnvelope.NonceSize;
            var wrapped = bytes.AsSpan(offset, StorageEnvelope.DekSize).ToArray();
            offset += StorageEnvelope.DekSize;
            var tag = bytes.AsSpan(offset, StorageEnvelope.TagSize).ToArray();

            return new EnvelopeHeader(keyId, chunkSize, nonce, wrapped, tag);
        }

        public byte[] ToBytes()
        {
            var output = new byte[Length];
            var aad = WrapAad(KeyId, ChunkSize);
            aad.CopyTo(output, 0);

            var offset = aad.Length;
            WrapNonce.CopyTo(output, offset);  offset += StorageEnvelope.NonceSize;
            WrappedDek.CopyTo(output, offset); offset += StorageEnvelope.DekSize;
            WrapTag.CopyTo(output, offset);

            return output;
        }

        /// <summary>Despachetează cheia fișierului. Apelantul o șterge după folosire.</summary>
        internal byte[] UnwrapDek(byte[] masterKey)
        {
            var dek = new byte[StorageEnvelope.DekSize];
            try
            {
                using var gcm = new AesGcm(masterKey, StorageEnvelope.TagSize);
                gcm.Decrypt(WrapNonce, WrappedDek, WrapTag, dek, WrapAad(KeyId, ChunkSize));
                return dek;
            }
            catch (AuthenticationTagMismatchException ex)
            {
                CryptographicOperations.ZeroMemory(dek);
                throw new CryptographicException(
                    $"Cheia fișierului nu poate fi despachetată cu cheia principală '{KeyId}': " +
                    "antet alterat sau cheie principală diferită de cea folosită la criptare.", ex);
            }
        }

        /// <summary>magic | versiune | L | id | dimensiune segment - antetul până la împachetare.</summary>
        private static byte[] WrapAad(string keyId, int chunkSize)
        {
            var id  = Encoding.ASCII.GetBytes(keyId);
            var aad = new byte[StorageEnvelope.PrefixLength + 1 + id.Length + 4];

            "SGDMENC"u8.CopyTo(aad);
            aad[7] = StorageEnvelope.FormatVersion;
            aad[8] = (byte)id.Length;
            id.CopyTo(aad, 9);
            BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(9 + id.Length, 4), chunkSize);

            return aad;
        }
    }

    /// <summary>
    /// Rezultatul sondării: <see cref="Header"/> null înseamnă obiect în clar,
    /// iar <see cref="ConsumedBytes"/> sunt octeții deja citiți din el.
    /// </summary>
    public sealed record EnvelopeProbe(EnvelopeHeader? Header, byte[] ConsumedBytes)
    {
        public bool IsEncrypted => Header is not null;
    }

    /// <summary>Stream-ul de citit și antetul, dacă obiectul era criptat.</summary>
    public sealed record OpenedObject(Stream Stream, EnvelopeHeader? Header)
    {
        public bool WasEncrypted => Header is not null;
    }

    /// <summary>
    /// Obiect în clar sub un prefix criptat, cu citirea în clar dezactivată
    /// (STORAGE_ENCRYPTION_ALLOW_PLAINTEXT=false).
    /// </summary>
    public sealed class PlaintextObjectRejectedException : CryptographicException
    {
        public PlaintextObjectRejectedException()
            : base("Obiectul nu este criptat, iar citirea obiectelor în clar este dezactivată. " +
                   "Fie a fost înlocuit direct în depozit, fie nu a trecut încă prin recriptare.")
        {
        }
    }
}
