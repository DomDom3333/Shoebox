using Microsoft.Extensions.Options;

namespace Shoebox.Web.Services;

/// <summary>Central place for every filesystem location the app touches.</summary>
public class StoragePaths(IOptions<ShoeboxOptions> options)
{
    public string Root { get; } = Path.GetFullPath(options.Value.DataPath);

    public string DatabaseFile => Path.Combine(Root, "shoebox.db");
    public string KeysDirectory => Path.Combine(Root, "keys");
    public string PoolsDirectory => Path.Combine(Root, "pools");

    /// <summary>
    /// Scratch space for the plaintext an upload passes through on its way in: ImageMagick and
    /// ffmpeg need a real file to read, so a file is briefly unencrypted here before being
    /// sealed into place. Deliberately its own directory, so an operator who cares can mount it
    /// on tmpfs and keep plaintext off the disk entirely.
    /// </summary>
    public string TempDirectory => Path.Combine(Root, "tmp");

    public string PoolDirectory(Guid poolId) => Path.Combine(PoolsDirectory, poolId.ToString("N"));
    public string OriginalsDirectory(Guid poolId) => Path.Combine(PoolDirectory(poolId), "orig");
    public string ThumbsDirectory(Guid poolId) => Path.Combine(PoolDirectory(poolId), "thumb");
    public string DisplaysDirectory(Guid poolId) => Path.Combine(PoolDirectory(poolId), "display");

    public string OriginalFile(Guid poolId, Guid photoId, string extension)
        => Path.Combine(OriginalsDirectory(poolId), photoId.ToString("N") + extension);

    public string ThumbFile(Guid poolId, Guid photoId)
        => Path.Combine(ThumbsDirectory(poolId), photoId.ToString("N") + ".webp");

    public string DisplayFile(Guid poolId, Guid photoId)
        => Path.Combine(DisplaysDirectory(poolId), photoId.ToString("N") + ".webp");

    /// <summary>A fresh scratch path in <see cref="TempDirectory"/>. Callers are responsible for deleting it.</summary>
    public string NewTempFile(string suffix = ".tmp")
        => Path.Combine(TempDirectory, $"{Guid.NewGuid():N}{suffix}");

    public void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(KeysDirectory);
        Directory.CreateDirectory(PoolsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    /// <summary>
    /// Clears plaintext scratch files left behind by an upload that was interrupted by a crash
    /// or a restart. Runs at startup, when nothing can be mid-upload.
    /// </summary>
    public void ClearTempDirectory()
    {
        if (!Directory.Exists(TempDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(TempDirectory))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or not ours; it'll be caught by the next startup.
            }
        }
    }
}
