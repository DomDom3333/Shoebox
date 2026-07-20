using System.Security.Cryptography;
using GroupPhoto.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GroupPhoto.Web.Services;

public record UploadResult(string FileName, string Status, Guid? PhotoId = null, string? Reason = null)
{
    public static UploadResult Added(string fileName, Guid id) => new(fileName, "added", id);
    public static UploadResult Duplicate(string fileName) => new(fileName, "duplicate", Reason: "Already in this pool");
    public static UploadResult Rejected(string fileName, string reason) => new(fileName, "rejected", Reason: reason);
}

public class PhotoService(
    AppDbContext db,
    StoragePaths paths,
    ImageRenderer renderer,
    IOptions<GroupPhotoOptions> options)
{
    private static readonly Dictionary<string, string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
    };

    public async Task<UploadResult> SaveAsync(Pool pool, IFormFile file, string uploaderName, Guid uploaderUid,
        CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(fileName);

        if (!AllowedExtensions.TryGetValue(extension, out var contentType))
        {
            return UploadResult.Rejected(fileName, "Unsupported file type");
        }

        if (file.Length == 0)
        {
            return UploadResult.Rejected(fileName, "Empty file");
        }

        if (file.Length > options.Value.MaxFileSizeBytes)
        {
            return UploadResult.Rejected(fileName, $"Larger than {options.Value.MaxFileSizeMb} MB");
        }

        Directory.CreateDirectory(paths.OriginalsDirectory(pool.Id));
        Directory.CreateDirectory(paths.ThumbsDirectory(pool.Id));
        Directory.CreateDirectory(paths.DisplaysDirectory(pool.Id));

        // Stream to a temp file while hashing so dedupe never needs the file in memory.
        var tempPath = Path.Combine(paths.OriginalsDirectory(pool.Id), $"upload_{Guid.NewGuid():N}.tmp");
        string hash;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = File.Create(tempPath))
            await using (var source = file.OpenReadStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            hash = Convert.ToHexString(hasher.GetHashAndReset());

            if (await db.Photos.AnyAsync(p => p.PoolId == pool.Id && p.ContentHash == hash, ct))
            {
                File.Delete(tempPath);
                return UploadResult.Duplicate(fileName);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        var photo = new Photo
        {
            Id = Guid.NewGuid(),
            PoolId = pool.Id,
            OriginalFileName = fileName,
            Extension = extension.ToLowerInvariant(),
            ContentType = contentType,
            SizeBytes = file.Length,
            ContentHash = hash,
            UploaderName = uploaderName.Trim(),
            UploaderUid = uploaderUid,
            UploadedAt = DateTime.UtcNow,
        };

        var originalPath = paths.OriginalFile(pool.Id, photo.Id, photo.Extension);
        File.Move(tempPath, originalPath);

        var info = await renderer.ProcessAsync(
            originalPath, paths.ThumbFile(pool.Id, photo.Id), paths.DisplayFile(pool.Id, photo.Id), ct);
        if (info is null)
        {
            // Not a readable image within limits: corrupt, mislabelled, or an
            // oversized decode bomb. Don't store or serve it.
            DeleteIfExists(originalPath);
            DeleteIfExists(paths.ThumbFile(pool.Id, photo.Id));
            DeleteIfExists(paths.DisplayFile(pool.Id, photo.Id));
            return UploadResult.Rejected(fileName, "Couldn't read this image (it may be corrupt or too large)");
        }

        photo.Width = info.Width;
        photo.Height = info.Height;
        photo.TakenAt = info.TakenAt;
        photo.HasThumbnail = true;

        db.Photos.Add(photo);
        await db.SaveChangesAsync(ct);
        return UploadResult.Added(fileName, photo.Id);
    }

    public async Task DeleteAsync(Photo photo)
    {
        db.Photos.Remove(photo);
        await db.SaveChangesAsync();

        DeleteIfExists(paths.OriginalFile(photo.PoolId, photo.Id, photo.Extension));
        DeleteIfExists(paths.ThumbFile(photo.PoolId, photo.Id));
        DeleteIfExists(paths.DisplayFile(photo.PoolId, photo.Id));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
