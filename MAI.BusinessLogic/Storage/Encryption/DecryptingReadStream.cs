using System.Security.Cryptography;

namespace MAI.BusinessLogic.Storage.Encryption
{
    /// <summary>
    /// Decriptează corpul unui obiect în flux, segment cu segment, cu memorie
    /// constantă (două buffere de mărimea unui segment), indiferent de mărimea
    /// fișierului.
    ///
    /// Fiecare segment se verifică (eticheta GCM) ÎNAINTE ca vreun octet din el
    /// să fie dat mai departe. O eroare de integritate apare ca
    /// <see cref="CryptographicException"/> la citirea segmentului afectat.
    /// </summary>
    internal sealed class DecryptingReadStream : Stream
    {
        private readonly Stream _source;
        private readonly AesGcm _gcm;
        private readonly byte[] _dek;
        private readonly byte[] _cipherBuffer;
        private readonly byte[] _plainBuffer;
        private readonly byte[] _nonce = new byte[StorageEnvelope.NonceSize];

        private ulong _counter;
        private int _plainOffset;
        private int _plainCount;
        private bool _finished;
        private bool _disposed;

        public DecryptingReadStream(Stream source, byte[] dek, int chunkSize)
        {
            _source       = source;
            _dek          = dek;
            _gcm          = new AesGcm(dek, StorageEnvelope.TagSize);
            _cipherBuffer = new byte[chunkSize + StorageEnvelope.TagSize];
            _plainBuffer  = new byte[chunkSize];
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty) return 0;

            if (_plainOffset == _plainCount)
            {
                if (_finished) return 0;
                var read = StorageEnvelope.Fill(_source, _cipherBuffer);
                DecryptChunk(read);
                if (_plainCount == 0) return 0;   // ultimul segment, gol
            }

            return CopyOut(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty) return 0;

            if (_plainOffset == _plainCount)
            {
                if (_finished) return 0;
                var read = await StorageEnvelope.FillAsync(_source, _cipherBuffer, cancellationToken);
                DecryptChunk(read);
                if (_plainCount == 0) return 0;
            }

            return CopyOut(buffer.Span);
        }

        private int CopyOut(Span<byte> destination)
        {
            var n = Math.Min(destination.Length, _plainCount - _plainOffset);
            _plainBuffer.AsSpan(_plainOffset, n).CopyTo(destination);
            _plainOffset += n;
            return n;
        }

        /// <summary>
        /// Un segment plin (dimensiune + etichetă) nu e ultimul; orice segment mai
        /// scurt este ultimul și trebuie să fie urmat de sfârșitul fluxului -
        /// <see cref="StorageEnvelope.Fill"/> întoarce mai puțin decât bufferul
        /// doar la sfârșit, deci condiția e verificată implicit.
        /// </summary>
        private void DecryptChunk(int read)
        {
            if (read < StorageEnvelope.TagSize)
                throw StorageEnvelope.Truncated();

            var last = read < _cipherBuffer.Length;
            var cipherLength = read - StorageEnvelope.TagSize;

            StorageEnvelope.ChunkNonce(_counter, last, _nonce);

            try
            {
                _gcm.Decrypt(
                    _nonce,
                    _cipherBuffer.AsSpan(0, cipherLength),
                    _cipherBuffer.AsSpan(cipherLength, StorageEnvelope.TagSize),
                    _plainBuffer.AsSpan(0, cipherLength),
                    StorageEnvelope.ChunkAssociatedData);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                CryptographicOperations.ZeroMemory(_plainBuffer);
                _plainOffset = _plainCount = 0;
                _finished = true;

                throw new CryptographicException(
                    $"Verificarea de integritate a eșuat la segmentul {_counter}: fișierul din depozit " +
                    "a fost modificat, trunchiat sau reordonat.", ex);
            }

            _counter++;
            _plainOffset = 0;
            _plainCount  = cipherLength;
            _finished    = last;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _gcm.Dispose();
                CryptographicOperations.ZeroMemory(_dek);
                CryptographicOperations.ZeroMemory(_plainBuffer);
                _source.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _gcm.Dispose();
                CryptographicOperations.ZeroMemory(_dek);
                CryptographicOperations.ZeroMemory(_plainBuffer);
                await _source.DisposeAsync();
            }
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Redă întâi octeții deja citiți la sondare, apoi restul sursei. Folosit
    /// pentru obiectele în clar: sondarea a consumat primii 8 octeți ca să
    /// caute magicul, iar ei trebuie să ajungă totuși la cititor.
    /// </summary>
    internal sealed class PrefixedReadStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _source;
        private int _prefixOffset;
        private bool _disposed;

        public PrefixedReadStream(byte[] prefix, Stream source)
        {
            _prefix = prefix;
            _source = source;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prefixOffset < _prefix.Length) return CopyPrefix(buffer);
            return _source.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prefixOffset < _prefix.Length) return ValueTask.FromResult(CopyPrefix(buffer.Span));
            return _source.ReadAsync(buffer, cancellationToken);
        }

        private int CopyPrefix(Span<byte> destination)
        {
            var n = Math.Min(destination.Length, _prefix.Length - _prefixOffset);
            _prefix.AsSpan(_prefixOffset, n).CopyTo(destination);
            _prefixOffset += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _source.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await _source.DisposeAsync();
            }
            GC.SuppressFinalize(this);
        }
    }
}
