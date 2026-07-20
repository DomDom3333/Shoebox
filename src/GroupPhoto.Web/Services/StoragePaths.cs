using Microsoft.Extensions.Options;

namespace GroupPhoto.Web.Services;

/// <summary>Central place for every filesystem location the app touches.</summary>
public class StoragePaths(IOptions<GroupPhotoOptions> options)
{
    public string Root { get; } = Path.GetFullPath(options.Value.DataPath);

    public string DatabaseFile => Path.Combine(Root, "groupphoto.db");
    public string KeysDirectory => Path.Combine(Root, "keys");
    public string PoolsDirectory => Path.Combine(Root, "pools");

    public string PoolDirectory(Guid poolId) => Path.Combine(PoolsDirectory, poolId.ToString("N"));
    public string OriginalsDirectory(Guid poolId) => Path.Combine(PoolDirectory(poolId), "orig");
    public string ThumbsDirectory(Guid poolId) => Path.Combine(PoolDirectory(poolId), "thumb");

    public string OriginalFile(Guid poolId, Guid photoId, string extension)
        => Path.Combine(OriginalsDirectory(poolId), photoId.ToString("N") + extension);

    public string ThumbFile(Guid poolId, Guid photoId)
        => Path.Combine(ThumbsDirectory(poolId), photoId.ToString("N") + ".webp");

    public void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(KeysDirectory);
        Directory.CreateDirectory(PoolsDirectory);
    }
}
