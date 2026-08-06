using System.Security.Cryptography;
using Shoebox.Web.Data;
using Shoebox.Web.Services.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Shoebox.Web.Services;

public record UploadResult(string FileName, string Status, Guid? MediaId = null, string? Reason = null)
{
    public static UploadResult Added(string fileName, Guid id) => new(fileName, "added", id);
    public static UploadResult Duplicate(string fileName) => new(fileName, "duplicate", Reason: "Already in this pool");
    public static UploadResult Rejected(string fileName, string reason) => new(fileName, "rejected", Reason: reason);
}

/// <summary>
/// Takes an upload through validation, rendering and storage.
///
/// The order matters once storage is encrypted: the file is streamed to a plaintext scratch
/// file, checked and rendered from there, and only then sealed into its final home. That keeps
/// ImageMagick and ffmpeg — both of which need a real file to open — completely unaware of
/// encryption, at the cost of one file that is briefly plaintext in <see cref="StoragePaths.TempDirectory"/>.
/// </summary>
public class MediaService(AppDbContext db, StoragePaths paths, MediaHandlers handlers, FileVault vault, PoolKeyRing keys)
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
        Directory.CreateDirectory(paths.TempDirectory);

        var tempOriginal = paths.NewTempFile(extension.ToLowerInvariant());
        var tempThumb = paths.NewTempFile(".webp");
        var tempDisplay = paths.NewTempFile(".webp");

        try
        {
            // Stream to the scratch file while hashing so dedupe never needs the file in memory.
            string hash;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var target = File.Create(tempOriginal))
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
            }

            if (await db.Media.AnyAsync(m => m.PoolId == pool.Id && m.ContentHash == hash, ct))
            {
                return UploadResult.Duplicate(fileName);
            }

            if (handler.Reject(tempOriginal) is { } reason)
            {
                // The extension claims something the bytes aren't; don't store it.
                return UploadResult.Rejected(fileName, reason);
            }

            var info = await handler.RenderAsync(tempOriginal, tempThumb, tempDisplay, ct);
            if (info is null && handler.RenderFailureReason is { } failure)
            {
                return UploadResult.Rejected(fileName, failure);
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

            if (info is not null)
            {
                media.Width = info.Width;
                media.Height = info.Height;
                media.TakenAt = info.TakenAt;
                media.HasThumbnail = true;
                media.HasAnimation = info.IsAnimated;
            }

            // The box's data key has to be committed before anything is written under it,
            // or a crash here would leave files nothing can ever open again.
            if (keys.EnsureKey(pool))
            {
                await db.SaveChangesAsync(ct);
            }

            await vault.StoreAsync(tempOriginal, paths.OriginalFile(pool.Id, media.Id, media.Extension), pool, ct);
            if (media.HasThumbnail)
            {
                await vault.StoreAsync(tempThumb, paths.ThumbFile(pool.Id, media.Id), pool, ct);
                await vault.StoreAsync(tempDisplay, paths.DisplayFile(pool.Id, media.Id), pool, ct);
            }

            db.Media.Add(media);
            await db.SaveChangesAsync(ct);
            return UploadResult.Added(fileName, media.Id);
        }
        finally
        {
            // Nothing plaintext outlives the request, on any path out of here.
            DeleteIfExists(tempOriginal);
            DeleteIfExists(tempThumb);
            DeleteIfExists(tempDisplay);
        }
    }

    public async Task<bool> ReprocessAsync(Media media, CancellationToken ct = default)
    {
        var storedPath = paths.OriginalFile(media.PoolId, media.Id, media.Extension);
        if (!File.Exists(storedPath))
            return false;

        Directory.CreateDirectory(paths.TempDirectory);
        var tempOriginal = paths.NewTempFile(media.Extension);
        var tempThumb = paths.NewTempFile(".webp");
        var tempDisplay = paths.NewTempFile(".webp");

        try
        {
            // The renderers need a real file, so the original comes back out in the clear
            // for as long as it takes to render it.
            await vault.ExtractAsync(storedPath, tempOriginal, media.Pool, ct);

            var info = await handlers.For(media.Kind).RenderAsync(tempOriginal, tempThumb, tempDisplay, ct);
            if (info is null)
                return false;

            await vault.StoreAsync(tempThumb, paths.ThumbFile(media.PoolId, media.Id), media.Pool, ct);
            await vault.StoreAsync(tempDisplay, paths.DisplayFile(media.PoolId, media.Id), media.Pool, ct);

            media.HasThumbnail = true;
            media.HasAnimation = info.IsAnimated;
            media.Width = info.Width;
            media.Height = info.Height;
            media.TakenAt ??= info.TakenAt;
            await db.SaveChangesAsync(ct);
            return true;
        }
        finally
        {
            DeleteIfExists(tempOriginal);
            DeleteIfExists(tempThumb);
            DeleteIfExists(tempDisplay);
        }
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
