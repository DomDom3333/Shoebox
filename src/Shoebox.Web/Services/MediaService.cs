using System.Security.Cryptography;
using Shoebox.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Shoebox.Web.Services;

public record UploadResult(string FileName, string Status, Guid? MediaId = null, string? Reason = null)
{
    public static UploadResult Added(string fileName, Guid id) => new(fileName, "added", id);
    public static UploadResult Duplicate(string fileName) => new(fileName, "duplicate", Reason: "Already in this pool");
    public static UploadResult Rejected(string fileName, string reason) => new(fileName, "rejected", Reason: reason);
}

public class MediaService(AppDbContext db, StoragePaths paths, MediaHandlers handlers)
{
    public async Task<UploadResult> SaveAsync(Pool pool, IFormFile file, string uploaderName, Guid uploaderUid,
        CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(fileName);

        var match = handlers.For(extension);
        if (match is null)
        {
            return UploadResult.Rejected(fileName, "Unsupported file type");
        }

        var (handler, contentType) = match.Value;

        if (file.Length == 0)
        {
            return UploadResult.Rejected(fileName, "Empty file");
        }

        if (file.Length > handler.MaxBytes)
        {
            return UploadResult.Rejected(fileName, $"Larger than {handler.MaxBytes / (1024 * 1024)} MB");
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

            if (await db.Media.AnyAsync(m => m.PoolId == pool.Id && m.ContentHash == hash, ct))
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

        var media = new Media
        {
            Id = Guid.NewGuid(),
            PoolId = pool.Id,
            Kind = handler.Kind,
            OriginalFileName = fileName,
            Extension = extension.ToLowerInvariant(),
            ContentType = contentType,
            SizeBytes = file.Length,
            ContentHash = hash,
            UploaderName = uploaderName.Trim(),
            UploaderUid = uploaderUid,
            UploadedAt = DateTime.UtcNow,
        };

        var originalPath = paths.OriginalFile(pool.Id, media.Id, media.Extension);
        var thumbPath = paths.ThumbFile(pool.Id, media.Id);
        var displayPath = paths.DisplayFile(pool.Id, media.Id);
        File.Move(tempPath, originalPath);

        if (handler.Reject(originalPath) is { } reason)
        {
            // The extension claims something the bytes aren't; don't store it.
            DeleteIfExists(originalPath);
            return UploadResult.Rejected(fileName, reason);
        }

        var info = await handler.RenderAsync(originalPath, thumbPath, displayPath, ct);
        if (info is null)
        {
            DeleteIfExists(thumbPath);
            DeleteIfExists(displayPath);

            if (handler.RenderFailureReason is { } failure)
            {
                DeleteIfExists(originalPath);
                return UploadResult.Rejected(fileName, failure);
            }
        }
        else
        {
            media.Width = info.Width;
            media.Height = info.Height;
            media.TakenAt = info.TakenAt;
            media.HasThumbnail = true;
            media.HasAnimation = info.IsAnimated;
        }

        db.Media.Add(media);
        await db.SaveChangesAsync(ct);
        return UploadResult.Added(fileName, media.Id);
    }

    public async Task<bool> ReprocessAsync(Media media, CancellationToken ct = default)
    {
        var originalPath = paths.OriginalFile(media.PoolId, media.Id, media.Extension);
        if (!File.Exists(originalPath))
            return false;

        var info = await handlers.For(media.Kind).RenderAsync(
            originalPath, paths.ThumbFile(media.PoolId, media.Id), paths.DisplayFile(media.PoolId, media.Id), ct);
        if (info is null)
            return false;

        media.HasThumbnail = true;
        media.HasAnimation = info.IsAnimated;
        media.Width = info.Width;
        media.Height = info.Height;
        media.TakenAt ??= info.TakenAt;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DeleteAsync(Media media)
    {
        db.Media.Remove(media);
        await db.SaveChangesAsync();

        DeleteIfExists(paths.OriginalFile(media.PoolId, media.Id, media.Extension));
        DeleteIfExists(paths.ThumbFile(media.PoolId, media.Id));
        DeleteIfExists(paths.DisplayFile(media.PoolId, media.Id));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
