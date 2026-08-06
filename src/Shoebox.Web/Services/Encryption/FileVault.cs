using Shoebox.Web.Data;

namespace Shoebox.Web.Services.Encryption;

/// <summary>
/// The single door every stored media file goes through. Callers hand it a path and a box and
/// get a plaintext stream back, or hand it plaintext and have it written encrypted — nothing
/// else in the app needs to know whether encryption is on.
///
/// Reads sniff the header rather than trusting configuration, so a box that predates encryption
/// keeps serving its existing files while new uploads to the same box are encrypted.
/// </summary>
public sealed class FileVault(MasterKey masterKey, PoolKeyRing keys)
{
    private const int BufferSize = 64 * 1024;

    public bool IsEnabled => masterKey.IsEnabled;

    /// <summary>Opens a stored file as plaintext, decrypting only if it was written encrypted.</summary>
    public Stream OpenRead(string path, Pool pool)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        try
        {
            var head = new byte[EncryptedFile.HeaderSize];
            var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (!EncryptedFile.HasHeader(head.AsSpan(0, read)))
            {
                file.Position = 0;
                return file;
            }

            var dataKey = keys.DataKey(pool)
                ?? throw new InvalidOperationException(
                    $"'{path}' is encrypted but box {pool.Id} has no data key. " +
                    $"This data was written with a different {MasterKey.KeyVariable}.");

            return new DecryptingStream(file, dataKey);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Moves a rendered plaintext temp file into its final home, encrypting on the way when the
    /// box has a data key. The source is always removed.
    /// </summary>
    public async Task StoreAsync(string temporaryPath, string destinationPath, Pool pool,
        CancellationToken ct = default)
    {
        var dataKey = keys.DataKey(pool);
        if (dataKey is null)
        {
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return;
        }

        try
        {
            await using (var source = new FileStream(
                temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (var destination = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await EncryptedFile.WriteAsync(source, destination, dataKey, ct);
            }
        }
        finally
        {
            // The plaintext copy must not outlive the encrypted one, including when we failed
            // partway through writing it.
            File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Writes a stored file back out as plaintext, for the one case that needs a real file on
    /// disk: handing an original to ImageMagick or ffmpeg when re-rendering it.
    /// </summary>
    public async Task ExtractAsync(string storedPath, string destinationPath, Pool pool,
        CancellationToken ct = default)
    {
        await using var source = OpenRead(storedPath, pool);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await source.CopyToAsync(destination, ct);
    }
}
