using System.Security.Cryptography;
using Shoebox.Web.Services.Encryption;
using Xunit;

namespace Shoebox.Tests.Web;

/// <summary>
/// The encrypted-file format, tested directly. These cover the two things the rest of the app
/// trusts it for: that a stored file reads back byte-for-byte from any offset, and that a file
/// which has been altered on disk fails loudly instead of decoding to plausible garbage.
/// </summary>
public class EncryptedFileTests
{
    private const int ChunkSize = 1 << EncryptedFile.DefaultChunkSizeLog2;

    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    private static byte[] Content(int length)
    {
        // Deterministic but non-repeating, so a chunk decrypted at the wrong offset can't
        // accidentally match the expected bytes.
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) ^ (i >> 8));
        }

        return bytes;
    }

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, byte[] key)
    {
        using var source = new MemoryStream(plaintext);
        using var destination = new MemoryStream();
        await EncryptedFile.WriteAsync(source, destination, key);
        return destination.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, byte[] key)
    {
        using var stream = new DecryptingStream(new MemoryStream(encrypted), key);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ChunkSize - 1)]
    [InlineData(ChunkSize)]           // exactly one chunk: the writer still emits an empty final chunk
    [InlineData(ChunkSize + 1)]
    [InlineData(ChunkSize * 2)]
    [InlineData((ChunkSize * 2) + 12345)]
    public async Task Round_trips_content_of_any_length(int length)
    {
        var key = Key();
        var plaintext = Content(length);

        var encrypted = await EncryptAsync(plaintext, key);

        using var stream = new DecryptingStream(new MemoryStream(encrypted), key);
        Assert.Equal(length, stream.Length);
        Assert.Equal(plaintext, Decrypt(encrypted, key));
    }

    /// <summary>
    /// What HTTP range requests actually do: jump to an offset and read a slice. The lightbox
    /// and video downloads depend on this being right at every chunk boundary.
    /// </summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 10)]
    [InlineData(ChunkSize - 5, 10)]           // spans a chunk boundary
    [InlineData(ChunkSize, 1)]                // first byte of the second chunk
    [InlineData(ChunkSize + 7, ChunkSize)]    // spans two boundaries
    [InlineData((ChunkSize * 2) + 100, 500)]
    public async Task Reads_the_right_bytes_from_an_arbitrary_offset(int offset, int count)
    {
        var key = Key();
        var plaintext = Content((ChunkSize * 3) + 999);
        var encrypted = await EncryptAsync(plaintext, key);

        using var stream = new DecryptingStream(new MemoryStream(encrypted), key);
        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[count];
        var read = stream.Read(buffer);

        Assert.Equal(count, read);
        Assert.Equal(plaintext.AsSpan(offset, count).ToArray(), buffer);
    }

    [Fact]
    public async Task Seeking_backwards_and_forwards_stays_consistent()
    {
        var key = Key();
        var plaintext = Content(ChunkSize * 3);
        var encrypted = await EncryptAsync(plaintext, key);

        using var stream = new DecryptingStream(new MemoryStream(encrypted), key);
        foreach (var offset in new[] { ChunkSize * 2, 5, ChunkSize + 1, 0, (ChunkSize * 2) + 50 })
        {
            stream.Position = offset;
            var buffer = new byte[64];
            stream.ReadExactly(buffer);
            Assert.Equal(plaintext.AsSpan(offset, 64).ToArray(), buffer);
        }
    }

    [Fact]
    public async Task Rejects_the_wrong_key()
    {
        var encrypted = await EncryptAsync(Content(5000), Key());
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(encrypted, Key()));
    }

    [Fact]
    public async Task Rejects_a_flipped_bit()
    {
        var key = Key();
        var encrypted = await EncryptAsync(Content(5000), key);

        encrypted[EncryptedFile.HeaderSize + 100] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => Decrypt(encrypted, key));
    }

    /// <summary>
    /// Truncation must never pass as a shorter file. Two different mechanisms catch it: a cut on
    /// a chunk boundary leaves a body length the format can't account for, and a cut mid-chunk
    /// leaves a chunk that has to pass as the final one — which is what the "is this the last
    /// chunk" flag in each chunk's associated data exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(0)]      // exactly on a chunk boundary
    [InlineData(-1000)]  // mid-chunk
    [InlineData(-1)]
    public async Task Rejects_truncation(int adjustment)
    {
        var key = Key();
        var encrypted = await EncryptAsync(Content(ChunkSize * 3), key);

        var cut = EncryptedFile.HeaderSize + ChunkSize + EncryptedFile.TagSize + adjustment;

        Assert.ThrowsAny<CryptographicException>(() => Decrypt(encrypted[..cut], key));
    }

    /// <summary>
    /// A chunk must not be interchangeable with the chunk at the same position in another file,
    /// even one encrypted under the same box key — otherwise someone with write access to the
    /// volume could splice one photo's content into another's.
    /// </summary>
    [Fact]
    public async Task Rejects_a_chunk_moved_from_another_file()
    {
        var key = Key();
        var first = await EncryptAsync(Content(ChunkSize * 2), key);
        var second = await EncryptAsync(Content(ChunkSize * 2), key);

        var stride = ChunkSize + EncryptedFile.TagSize;
        Array.Copy(second, EncryptedFile.HeaderSize, first, EncryptedFile.HeaderSize, stride);

        Assert.ThrowsAny<CryptographicException>(() => Decrypt(first, key));
    }

    [Fact]
    public async Task Rejects_reordered_chunks()
    {
        var key = Key();
        var encrypted = await EncryptAsync(Content(ChunkSize * 3), key);

        var stride = ChunkSize + EncryptedFile.TagSize;
        var first = encrypted[EncryptedFile.HeaderSize..(EncryptedFile.HeaderSize + stride)];
        var second = encrypted[(EncryptedFile.HeaderSize + stride)..(EncryptedFile.HeaderSize + (stride * 2))];
        Array.Copy(second, 0, encrypted, EncryptedFile.HeaderSize, stride);
        Array.Copy(first, 0, encrypted, EncryptedFile.HeaderSize + stride, stride);

        Assert.ThrowsAny<CryptographicException>(() => Decrypt(encrypted, key));
    }

    [Fact]
    public async Task Encrypted_output_does_not_contain_the_plaintext()
    {
        var plaintext = "the-secret-marker"u8.ToArray();

        var encrypted = await EncryptAsync(plaintext, Key());

        Assert.True(
            encrypted.AsSpan().IndexOf(plaintext) < 0,
            "the plaintext appears verbatim in the encrypted file");
    }
}
