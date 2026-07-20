using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;

namespace GroupPhoto.Web.Services;

public record ImageInfo(int Width, int Height, DateTime? TakenAt, bool ThumbnailCreated);

public class ThumbnailService(IOptions<GroupPhotoOptions> options, ILogger<ThumbnailService> logger)
{
    /// <summary>
    /// Reads dimensions and EXIF capture date from the original and writes a WebP thumbnail.
    /// Returns null when the format cannot be decoded (e.g. HEIC) — the upload still succeeds,
    /// the gallery just shows a placeholder tile for it.
    /// </summary>
    public async Task<ImageInfo?> ProcessAsync(string originalPath, string thumbPath, CancellationToken ct = default)
    {
        try
        {
            using var image = await Image.LoadAsync(originalPath, ct);

            var takenAt = ReadTakenAt(image.Metadata.ExifProfile);

            // Orientation is applied before reading Width/Height so portrait phone shots
            // report portrait dimensions.
            image.Mutate(x => x.AutoOrient());
            var (width, height) = (image.Width, image.Height);

            var size = options.Value.ThumbnailSize;
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(size, size),
            }));

            await image.SaveAsync(thumbPath, new WebpEncoder { Quality = 78 }, ct);
            return new ImageInfo(width, height, takenAt, ThumbnailCreated: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not generate thumbnail for {Path}", originalPath);
            return null;
        }
    }

    private static DateTime? ReadTakenAt(ExifProfile? exif)
    {
        if (exif is null)
        {
            return null;
        }

        foreach (var tag in new[] { ExifTag.DateTimeOriginal, ExifTag.DateTimeDigitized, ExifTag.DateTime })
        {
            if (exif.TryGetValue(tag, out var value) &&
                DateTime.TryParseExact(value.Value, "yyyy:MM:dd HH:mm:ss", null,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
