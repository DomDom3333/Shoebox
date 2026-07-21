using ImageMagick;
using Microsoft.Extensions.Options;

namespace Shoebox.Web.Services;

public record ImageInfo(int Width, int Height, DateTime? TakenAt);

/// <summary>
/// Renders the derived images for every upload with Magick.NET (which decodes everything
/// we accept, including HEIC/HEIF via bundled libheif).
/// </summary>
public class ImageRenderer(IOptions<ShoeboxOptions> options, ILogger<ImageRenderer> logger)
{
    /// <summary>
    /// Decodes the original and writes both WebP renditions: a small thumbnail for the grid
    /// and a larger web-safe display proxy for the lightbox. Returns the source dimensions
    /// and EXIF capture date, or null when the file can't be decoded (the upload still
    /// succeeds and the gallery shows a placeholder tile).
    /// </summary>
    public async Task<ImageInfo?> ProcessAsync(string originalPath, string thumbPath, string displayPath,
        CancellationToken ct = default)
    {
        try
        {
            // Cheap header read first: reject pixel-flood / decompression-bomb images
            // before allocating anything to decode them.
            var probe = new MagickImageInfo(originalPath);
            if (probe.Width > (uint)options.Value.MaxImageDimension ||
                probe.Height > (uint)options.Value.MaxImageDimension ||
                (long)probe.Width * probe.Height > options.Value.MaxImagePixels)
            {
                logger.LogWarning("Rejected oversized image {W}x{H} for {Path}",
                    probe.Width, probe.Height, originalPath);
                return null;
            }

            using var image = new MagickImage(originalPath);

            var takenAt = ReadTakenAt(image.GetExifProfile());

            // AutoOrient before reading dimensions so portrait phone shots report as portrait.
            image.AutoOrient();
            var (width, height) = ((int)image.Width, (int)image.Height);

            image.Format = MagickFormat.WebP;

            // Resize down progressively, never upscaling (Greater = only shrink):
            // the display proxy first, then the thumbnail from it.
            image.Resize(new MagickGeometry((uint)options.Value.DisplaySize, (uint)options.Value.DisplaySize) { Greater = true });
            image.Quality = 82;
            await image.WriteAsync(displayPath, ct);

            image.Resize(new MagickGeometry((uint)options.Value.ThumbnailSize, (uint)options.Value.ThumbnailSize) { Greater = true });
            image.Quality = 78;
            await image.WriteAsync(thumbPath, ct);

            return new ImageInfo(width, height, takenAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not render image {Path}", originalPath);
            return null;
        }
    }

    private static DateTime? ReadTakenAt(IExifProfile? exif)
    {
        if (exif is null)
        {
            return null;
        }

        foreach (var tag in new[] { ExifTag.DateTimeOriginal, ExifTag.DateTimeDigitized, ExifTag.DateTime })
        {
            if (exif.GetValue(tag)?.Value is string raw &&
                DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", null,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
