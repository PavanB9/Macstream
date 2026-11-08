using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace MacstreamViewer.Security;

/// <summary>
/// AES-256-GCM record layer over the TCP stream, after the handshake (matches SessionCipher in
/// Mac/.../SecureChannel.swift). Record: u32 LE plaintext length | ciphertext | 16-byte tag; the
/// length is authenticated. Nonces are per-direction counters, never sent or reused. Any
/// tampering, reordering, replay or loss fails authentication and ends the connection.
///
/// Each Write is one record, so one packet per call. Reads may ask for any size. Writes must
/// not overlap each other (FrameReceiver has one writer), nor reads each other.
/// </summary>
public sealed class EncryptedStream : Stream
{
    private const int TagSize = 16;
    private const int NonceSize = 12;

    private readonly Stream _inner;
    private readonly AesGcm _sendCipher;
    private readonly AesGcm _receiveCipher;
    private readonly int _maxRecord;
    private ulong _sendCounter;
    private ulong _receiveCounter;

    private readonly byte[] _lengthBuffer = new byte[4];
    private readonly byte[] _receiveNonce = new byte[NonceSize];
    private readonly byte[] _receiveTag = new byte[TagSize];
    private byte[] _cipherBuffer = Array.Empty<byte>();
    private byte[] _plainBuffer = Array.Empty<byte>();
    private int _plainOffset;
    private int _plainLength;

    /// <summary>Raised once, after the first record from the peer decrypted and verified.</summary>
    public event Action? FirstRecordAuthenticated;

    public EncryptedStream(Stream inner, byte[] sendKey, byte[] receiveKey, int maxRecord)
    {
        _inner = inner;
        _sendCipher = new AesGcm(sendKey, TagSize);
        _receiveCipher = new AesGcm(receiveKey, TagSize);
        _maxRecord = maxRecord;
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(receiveKey);
    }

    private static void FillNonce(Span<byte> nonce, ulong counter)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var record = Seal(buffer.Span);
        await _inner.WriteAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(Seal(buffer.AsSpan(offset, count)));

    private byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        if (_sendCounter == ulong.MaxValue)
            throw new IOException("Encryption counter exhausted");
        var record = new byte[4 + plaintext.Length + TagSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)plaintext.Length);
        Span<byte> nonce = stackalloc byte[NonceSize];
        FillNonce(nonce, _sendCounter++);
        _sendCipher.Encrypt(nonce, plaintext,
            record.AsSpan(4, plaintext.Length),
            record.AsSpan(4 + plaintext.Length, TagSize),
            record.AsSpan(0, 4));
        return record;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return 0;
        if (_plainOffset == _plainLength && !await FillAsync(cancellationToken).ConfigureAwait(false))
            return 0;   // the Mac closed the connection between records

        var n = Math.Min(buffer.Length, _plainLength - _plainOffset);
        _plainBuffer.AsMemory(_plainOffset, n).CopyTo(buffer);
        _plainOffset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <summary>Reads and decrypts the next record. False on a clean close before a record.</summary>
    private async Task<bool> FillAsync(CancellationToken ct)
    {
        // Records may be empty (an empty packet is never sent, but be safe).
        do
        {
            if (!await ReadExactAsync(_lengthBuffer, 4, allowCleanEof: true, ct).ConfigureAwait(false))
                return false;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(_lengthBuffer);
            if (length < 0 || length > _maxRecord)
                throw new InvalidDataException($"Encrypted record too large: {length}");

            if (_cipherBuffer.Length < length)
                _cipherBuffer = new byte[length];
            if (_plainBuffer.Length < length)
                _plainBuffer = new byte[length];

            await ReadExactAsync(_cipherBuffer, length, allowCleanEof: false, ct).ConfigureAwait(false);
            await ReadExactAsync(_receiveTag, TagSize, allowCleanEof: false, ct).ConfigureAwait(false);

            FillNonce(_receiveNonce, _receiveCounter++);
            try
            {
                _receiveCipher.Decrypt(_receiveNonce, _cipherBuffer.AsSpan(0, length), _receiveTag,
                    _plainBuffer.AsSpan(0, length), _lengthBuffer);
            }
            catch (CryptographicException)
            {
                throw new IOException("Received data failed authentication (tampered, or not the paired Mac); disconnecting.");
            }
            _plainOffset = 0;
            _plainLength = length;
            if (_receiveCounter == 1)
            {
                var handler = FirstRecordAuthenticated;
                FirstRecordAuthenticated = null;
                handler?.Invoke();
            }
        }
        while (_plainLength == 0);
        return true;
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, int count, bool allowCleanEof, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await _inner.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (allowCleanEof && read == 0)
                    return false;
                throw new IOException("Connection closed in the middle of an encrypted record");
            }
            read += n;
        }
        return true;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sendCipher.Dispose();
            _receiveCipher.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
