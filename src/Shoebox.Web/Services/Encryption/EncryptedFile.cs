using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Shoebox.Web.Services.Encryption;

/// <summary>
/// The on-disk format for an encrypted media file, and the writer for it.
///
/// The file is split into fixed-size chunks, each sealed with its own AES-GCM tag. Chunking is
/// what makes range requests work: the lightbox and video downloads ask for byte ranges, so the
/// reader has to be able to jump into the middle of a file without decrypting everything before
/// it. One tag over the whole file would make that impossible.
///
/// Layout:
///
///   header  magic "SBXE" ‖ version ‖ log2(chunk size) ‖ 2 reserved ‖ 16-byte salt   (24 bytes)
///   body    chunk₀ ‖ chunk₁ ‖ … ‖ chunkₙ    where chunk = ciphertext ‖ 16-byte tag
///
/// The salt derives a per-file key from the pool's data key, so every file gets fresh key
/// material and the per-chunk nonce can simply be the chunk index — no risk of reusing a
/// (key, nonce) pair across files.
///
/// Each chunk's associated data is the whole header, the chunk index, and whether the chunk is
/// the last one. That binds a chunk to its position in its file: chunks can't be reordered,
/// swapped between files, or dropped off the end, because truncating the file would make some
/// earlier chunk have to pass as the final one and its tag won't agree.
/// </summary>
internal static class EncryptedFile
{
    public static ReadOnlySpan<byte> Magic => "SBXE"u8;

    public const byte Version = 1;
    public const int SaltSize = 16;
    public const int HeaderSize = 4 + 1 + 1 + 2 + SaltSize;
    public const int TagSize = 16;
    public const int NonceSize = 12;

    /// <summary>64 KiB: small enough that a range request decrypts little extra, big enough that per-chunk overhead is noise.</summary>
    public const byte DefaultChunkSizeLog2 = 16;

    /// <summary>Bounds what a header is allowed to ask us to allocate per chunk.</summary>
    private const byte MaxChunkSizeLog2 = 24;

    private static ReadOnlySpan<byte> KeyDerivationInfo => "shoebox-file-v1"u8;

    /// <summary>
    /// True when the file begins with our header. Uploads are images and videos, which all carry
    /// their own magic bytes, so this can't collide with a real upload — and it is what lets
    /// files written before encryption was switched on keep being served untouched.
    /// </summary>
    public static bool HasHeader(ReadOnlySpan<byte> start) =>
        start.Length >= HeaderSize && start[..4].SequenceEqual(Magic) && start[4] == Version;

    public static byte[] BuildHeader(byte chunkSizeLog2, ReadOnlySpan<byte> salt)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = Version;
        header[5] = chunkSizeLog2;
        salt.CopyTo(header.AsSpan(8));
        return header;
    }

    public static int ChunkSize(ReadOnlySpan<byte> header)
    {
        var log2 = header[5];
        if (log2 is < 10 or > MaxChunkSizeLog2)
        {
            throw new CryptographicException($"Encrypted file declares an unsupported chunk size (2^{log2}).");
        }

        return 1 << log2;
    }

    public static byte[] DeriveFileKey(ReadOnlySpan<byte> dataKey, ReadOnlySpan<byte> header)
    {
        var fileKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, fileKey, header.Slice(8, SaltSize), KeyDerivationInfo);
        return fileKey;
    }

    /// <summary>Per-chunk nonce. The file key is unique per file, so the chunk index alone is a safe nonce.</summary>
    public static void Nonce(long chunkIndex, Span<byte> nonce)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], chunkIndex);
    }

    /// <summary>Header ‖ chunk index ‖ final flag — see the class remarks for why all three.</summary>
    public static void AssociatedData(ReadOnlySpan<byte> header, long chunkIndex, bool isFinal, Span<byte> destination)
    {
        header.CopyTo(destination);
        BinaryPrimitives.WriteInt64BigEndian(destination[HeaderSize..], chunkIndex);
        destination[HeaderSize + 8] = isFinal ? (byte)1 : (byte)0;
    }

    public const int AssociatedDataSize = HeaderSize + 8 + 1;

    /// <summary>
    /// How many plaintext bytes a body of this size holds. The writer always emits a final chunk
    /// (empty if the plaintext ended exactly on a chunk boundary), so the remainder is never zero
    /// and the length is unambiguous.
    /// </summary>
    public static long PlaintextLength(long bodyLength, int chunkSize)
    {
        var stride = chunkSize + TagSize;
        var whole = bodyLength / stride;
        var remainder = bodyLength % stride;
        if (remainder < TagSize)
        {
            throw new CryptographicException("Encrypted file is truncated: the last chunk is incomplete.");
        }

        return (whole * chunkSize) + (remainder - TagSize);
    }

    public static long ChunkCount(long bodyLength, int chunkSize) => (bodyLength / (chunkSize + TagSize)) + 1;

    /// <summary>
    /// Encrypts <paramref name="source"/> into <paramref name="destination"/> under
    /// <paramref name="dataKey"/>.
    /// </summary>
    public static async Task WriteAsync(Stream source, Stream destination, byte[] dataKey,
        CancellationToken ct = default)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var header = BuildHeader(DefaultChunkSizeLog2, salt);
        var chunkSize = ChunkSize(header);
        var fileKey = DeriveFileKey(dataKey, header);

        try
        {
            await destination.WriteAsync(header, ct);

            using var aes = new AesGcm(fileKey, TagSize);
            var plaintext = new byte[chunkSize];
            var chunk = new byte[chunkSize + TagSize];
            var nonce = new byte[NonceSize];
            var associatedData = new byte[AssociatedDataSize];

            for (long index = 0; ; index++)
            {
                // ReadAtLeastAsync only comes up short at end of stream, so a short read here
                // genuinely means "this is the last chunk" rather than a lazy underlying stream.
                var read = await source.ReadAtLeastAsync(plaintext, chunkSize, throwOnEndOfStream: false, ct);
                var isFinal = read < chunkSize;

                Nonce(index, nonce);
                AssociatedData(header, index, isFinal, associatedData);
                aes.Encrypt(
                    nonce,
                    plaintext.AsSpan(0, read),
                    chunk.AsSpan(0, read),
                    chunk.AsSpan(read, TagSize),
                    associatedData);

                await destination.WriteAsync(chunk.AsMemory(0, read + TagSize), ct);

                if (isFinal)
                {
                    break;
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }
}
