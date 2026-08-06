using System.Security.Cryptography;

namespace Shoebox.Web.Services.Encryption;

/// <summary>
/// Reads a file written by <see cref="EncryptedFile"/> as if it were the plaintext.
///
/// Seekable, with an accurate <see cref="Length"/>, which is the whole point: ASP.NET handles
/// HTTP range requests itself given a seekable stream, so the endpoints keep passing
/// <c>enableRangeProcessing: true</c> and video scrubbing and resumed downloads carry on working.
///
/// One decrypted chunk is held at a time, so a sequential read decrypts each chunk exactly once
/// and a range read only touches the chunks it overlaps.
/// </summary>
internal sealed class DecryptingStream : Stream
{
    private readonly Stream inner;
    private readonly byte[] header;
    private readonly byte[] fileKey;
    private readonly AesGcm aes;
    private readonly int chunkSize;
    private readonly long bodyLength;
    private readonly long chunkCount;

    private readonly byte[] plainChunk;
    private readonly byte[] cipherChunk;
    private readonly byte[] nonce = new byte[EncryptedFile.NonceSize];
    private readonly byte[] associatedData = new byte[EncryptedFile.AssociatedDataSize];

    private long cachedIndex = -1;
    private int cachedLength;
    private long position;

    /// <summary>Takes ownership of <paramref name="inner"/>, which must be seekable and positioned anywhere.</summary>
    public DecryptingStream(Stream inner, byte[] dataKey)
    {
        this.inner = inner;

        header = new byte[EncryptedFile.HeaderSize];
        inner.Position = 0;
        inner.ReadExactly(header);

        if (!EncryptedFile.HasHeader(header))
        {
            throw new CryptographicException("File is not in the expected encrypted format.");
        }

        chunkSize = EncryptedFile.ChunkSize(header);
        bodyLength = inner.Length - EncryptedFile.HeaderSize;
        if (bodyLength < EncryptedFile.TagSize)
        {
            throw new CryptographicException("Encrypted file is truncated: no complete chunk.");
        }

        Length = EncryptedFile.PlaintextLength(bodyLength, chunkSize);
        chunkCount = EncryptedFile.ChunkCount(bodyLength, chunkSize);

        fileKey = EncryptedFile.DeriveFileKey(dataKey, header);
        aes = new AesGcm(fileKey, EncryptedFile.TagSize);

        plainChunk = new byte[chunkSize];
        cipherChunk = new byte[chunkSize + EncryptedFile.TagSize];
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => position;
        set => position = value < 0
            ? throw new ArgumentOutOfRangeException(nameof(value))
            : value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (!buffer.IsEmpty && position < Length)
        {
            var index = position / chunkSize;
            LoadChunk(index);

            var offset = (int)(position % chunkSize);
            var take = Math.Min(cachedLength - offset, buffer.Length);
            if (take <= 0)
            {
                break;
            }

            plainChunk.AsSpan(offset, take).CopyTo(buffer);
            buffer = buffer[take..];
            position += take;
            total += take;
        }

        return total;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (!buffer.IsEmpty && position < Length)
        {
            var index = position / chunkSize;
            await LoadChunkAsync(index, cancellationToken);

            var offset = (int)(position % chunkSize);
            var take = Math.Min(cachedLength - offset, buffer.Length);
            if (take <= 0)
            {
                break;
            }

            plainChunk.AsMemory(offset, take).CopyTo(buffer);
            buffer = buffer[take..];
            position += take;
            total += take;
        }

        return total;
    }

    private void LoadChunk(long index)
    {
        if (cachedIndex == index)
        {
            return;
        }

        var stored = SeekToChunk(index);
        inner.ReadExactly(cipherChunk, 0, stored);
        OpenChunk(index, stored);
    }

    private async ValueTask LoadChunkAsync(long index, CancellationToken ct)
    {
        if (cachedIndex == index)
        {
            return;
        }

        var stored = SeekToChunk(index);
        await inner.ReadExactlyAsync(cipherChunk.AsMemory(0, stored), ct);
        OpenChunk(index, stored);
    }

    private int SeekToChunk(long index)
    {
        var stride = chunkSize + EncryptedFile.TagSize;
        var offset = index * stride;
        inner.Position = EncryptedFile.HeaderSize + offset;

        // Only the last chunk is short.
        return index == chunkCount - 1 ? (int)(bodyLength - offset) : stride;
    }

    private void OpenChunk(long index, int stored)
    {
        // A failed tag check throws, so a tampered or truncated file surfaces as an error
        // rather than as plausible-looking garbage handed to the browser.
        var isFinal = index == chunkCount - 1;
        var cipherLength = stored - EncryptedFile.TagSize;

        EncryptedFile.Nonce(index, nonce);
        EncryptedFile.AssociatedData(header, index, isFinal, associatedData);
        aes.Decrypt(
            nonce,
            cipherChunk.AsSpan(0, cipherLength),
            cipherChunk.AsSpan(cipherLength, EncryptedFile.TagSize),
            plainChunk.AsSpan(0, cipherLength),
            associatedData);

        cachedIndex = index;
        cachedLength = cipherLength;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            aes.Dispose();
            inner.Dispose();
            CryptographicOperations.ZeroMemory(fileKey);
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        aes.Dispose();
        await inner.DisposeAsync();
        CryptographicOperations.ZeroMemory(fileKey);
        await base.DisposeAsync();
    }
}
