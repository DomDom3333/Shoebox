using System.Security.Cryptography;

namespace Shoebox.Web.Services.Encryption;

/// <summary>
/// The one secret the whole at-rest story hangs off: media files, the SQLite database and the
/// cookie-signing key ring are all keyed from this.
///
/// It is supplied by the operator and never written to disk by the app. That is the point: the
/// key has to survive a restart or a redeploy without the container persisting it anywhere, so
/// a copy of <c>/data</c> (a backup, a volume snapshot, a stolen disk) is inert on its own.
///
/// Be honest about the boundary: the key still exists somewhere on the host, and root on a
/// running host can read it out of the process. This protects the data at rest, not against
/// someone who already owns the machine.
/// </summary>
public sealed class MasterKey
{
    public const int KeySizeBytes = 32;

    /// <summary>The environment variable holding a base64 (or hex) key.</summary>
    public const string KeyVariable = "Shoebox__EncryptionKey";

    /// <summary>
    /// The environment variable holding a *path* to a file containing the key. Preferred where
    /// it's available (Docker/Swarm/Kubernetes secrets): the file is mounted on tmpfs, stays out
    /// of <c>docker inspect</c>, and isn't inherited by child processes.
    /// </summary>
    public const string KeyFileVariable = "Shoebox__EncryptionKeyFile";

    private const string InlineSetting = "Shoebox:EncryptionKey";
    private const string FileSetting = "Shoebox:EncryptionKeyFile";

    private readonly byte[]? material;

    private MasterKey(byte[]? material, string source)
    {
        this.material = material;
        Source = source;
    }

    /// <summary>A key-less instance, for opening something deliberately unencrypted.</summary>
    public static MasterKey Disabled { get; } = new(null, "not configured");

    /// <summary>Whether an encryption key was supplied. When false the app stores everything in the clear, as it always did.</summary>
    public bool IsEnabled => material is not null;

    /// <summary>Where the key came from, for a startup log line. Never contains the key itself.</summary>
    public string Source { get; }

    public byte[] Material => material
        ?? throw new InvalidOperationException("Storage encryption is disabled; there is no master key.");

    /// <summary>
    /// Resolves the key from configuration. The key file wins when both are set, because a
    /// secrets mount is the stronger of the two and an operator moving to one shouldn't have to
    /// find and remove the old variable first.
    /// </summary>
    public static MasterKey Resolve(IConfiguration configuration)
    {
        var keyFile = configuration[FileSetting];
        var inline = configuration[InlineSetting];

        // Configuration has already been read, so take the key back out of the environment.
        // VideoRenderer spawns ffmpeg with the parent environment, and every one of those
        // children would otherwise carry the key in its own /proc/<pid>/environ.
        Environment.SetEnvironmentVariable(KeyVariable, null);

        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            if (!File.Exists(keyFile))
            {
                throw new InvalidOperationException(
                    $"{KeyFileVariable} points at '{keyFile}', which does not exist.");
            }

            return new MasterKey(Decode(File.ReadAllText(keyFile), $"the key file '{keyFile}'"), $"key file '{keyFile}'");
        }

        return string.IsNullOrWhiteSpace(inline)
            ? new MasterKey(null, "not configured")
            : new MasterKey(Decode(inline, KeyVariable), KeyVariable);
    }

    /// <summary>
    /// Accepts base64 or hex, because operators reach for whichever of
    /// <c>openssl rand -base64 32</c> / <c>-hex 32</c> they remember first.
    /// </summary>
    private static byte[] Decode(string value, string origin)
    {
        var trimmed = value.Trim();

        byte[]? bytes = null;
        if (trimmed.Length == KeySizeBytes * 2 && IsHex(trimmed))
        {
            bytes = Convert.FromHexString(trimmed);
        }
        else
        {
            try
            {
                bytes = Convert.FromBase64String(trimmed);
            }
            catch (FormatException)
            {
                // Falls through to the error below, which says what a good key looks like.
            }
        }

        if (bytes is null || bytes.Length != KeySizeBytes)
        {
            throw new InvalidOperationException(
                $"The encryption key in {origin} must be {KeySizeBytes} bytes, base64 or hex encoded. " +
                $"Generate one with: openssl rand -base64 {KeySizeBytes}");
        }

        return bytes;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Wraps a small secret (a pool's data key, a key-ring element) under the master key.
    /// Layout: nonce ‖ ciphertext ‖ tag.
    /// </summary>
    public byte[] Wrap(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var output = new byte[AesGcm.NonceByteSizes.MaxSize + plaintext.Length + AesGcm.TagByteSizes.MaxSize];
        var nonce = output.AsSpan(0, AesGcm.NonceByteSizes.MaxSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(Material, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(nonce.Length, plaintext.Length),
            output.AsSpan(nonce.Length + plaintext.Length),
            associatedData);

        return output;
    }

    /// <summary>Reverses <see cref="Wrap"/>. Throws <see cref="CryptographicException"/> on a wrong key or tampered blob.</summary>
    public byte[] Unwrap(ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> associatedData)
    {
        const int nonceSize = 12;
        const int tagSize = 16;
        if (wrapped.Length < nonceSize + tagSize)
        {
            throw new CryptographicException("Wrapped key blob is too short to be valid.");
        }

        var plaintext = new byte[wrapped.Length - nonceSize - tagSize];
        using var aes = new AesGcm(Material, tagSize);
        aes.Decrypt(
            wrapped[..nonceSize],
            wrapped.Slice(nonceSize, plaintext.Length),
            wrapped[(nonceSize + plaintext.Length)..],
            plaintext,
            associatedData);

        return plaintext;
    }
}
