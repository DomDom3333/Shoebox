using ImageMagick;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using ISExif = SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace GroupPhoto.Web.Services;

public record ImageInfo(int Width, int Height, DateTime? TakenAt, bool ThumbnailCreated, bool DisplayCreated);

public class ThumbnailService(IOptions<GroupPhotoOptions> options, ILogger<ThumbnailService> logger)
{
    // Formats ImageSharp cannot decode but Magick.NET (bundled libheif) can.
    private static readonly HashSet<string> MagickOnlyExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    /// <summary>
    /// Reads dimensions and EXIF capture date from the original and writes the derived
    /// renditions: a small WebP thumbnail for the grid and, when worthwhile, a larger
    /// web-safe WebP "display" proxy for the lightbox. ImageSharp handles the common
    /// formats; HEIC/HEIF (and anything else ImageSharp can't decode) fall back to
    /// Magick.NET. Returns null only when neither can read the file — the upload still
    /// succeeds and the gallery shows a placeholder tile.
    /// </summary>
    public async Task<ImageInfo?> ProcessAsync(string originalPath, string thumbPath, string displayPath,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(originalPath);

        // Route formats ImageSharp is known not to support straight to Magick.NET.
        if (!MagickOnlyExtensions.Contains(extension))
        {
            try
            {
                return await ProcessWithImageSharpAsync(originalPath, thumbPath, displayPath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unknown/unsupported format or a decode error — give Magick.NET a chance
                // before giving up (it reads a wider range of formats).
                logger.LogDebug(ex, "ImageSharp could not decode {Path}; trying Magick.NET", originalPath);
            }
        }

        try
        {
            return await ProcessWithMagickAsync(originalPath, thumbPath, displayPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not generate thumbnail for {Path}", originalPath);
            return null;
        }
    }

    private async Task<ImageInfo> ProcessWithImageSharpAsync(string originalPath, string thumbPath,
        string displayPath, CancellationToken ct)
    {
        using var image = await Image.LoadAsync(originalPath, ct);

        var takenAt = ReadTakenAt(image.Metadata.ExifProfile);

        // Orientation is applied before reading Width/Height so portrait phone shots
        // report portrait dimensions.
        image.Mutate(x => x.AutoOrient());
        var (width, height) = (image.Width, image.Height);

        // Display proxy: only worth making when the (web-safe) original is larger than the
        // display box — otherwise the lightbox just serves the original directly. Made
        // before the thumbnail so the thumbnail downsamples from it.
        var displaySize = options.Value.DisplaySize;
        var displayCreated = false;
        if (Math.Max(width, height) > displaySize)
        {
            image.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(displaySize, displaySize) }));
            await image.SaveAsync(displayPath, new WebpEncoder { Quality = 82 }, ct);
            displayCreated = true;
        }

        var size = options.Value.ThumbnailSize;
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(size, size),
        }));

        await image.SaveAsync(thumbPath, new WebpEncoder { Quality = 78 }, ct);
        return new ImageInfo(width, height, takenAt, ThumbnailCreated: true, DisplayCreated: displayCreated);
    }

    private async Task<ImageInfo> ProcessWithMagickAsync(string originalPath, string thumbPath,
        string displayPath, CancellationToken ct)
    {
        using var image = new MagickImage(originalPath);

        var takenAt = ReadTakenAt(image.GetExifProfile());

        image.AutoOrient();
        var (width, height) = ((int)image.Width, (int)image.Height);

        image.Format = MagickFormat.WebP;

        // These formats (HEIC/HEIF) aren't web-viewable, so always emit a display proxy —
        // even for small images — so the lightbox has something every browser can render.
        // Greater = only shrink, never upscale. Resize down progressively: display, then thumb.
        image.Resize(new MagickGeometry((uint)options.Value.DisplaySize, (uint)options.Value.DisplaySize) { Greater = true });
        image.Quality = 82;
        await image.WriteAsync(displayPath, ct);

        image.Resize(new MagickGeometry((uint)options.Value.ThumbnailSize, (uint)options.Value.ThumbnailSize) { Greater = true });
        image.Quality = 78;
        await image.WriteAsync(thumbPath, ct);
        return new ImageInfo(width, height, takenAt, ThumbnailCreated: true, DisplayCreated: true);
    }

    private static DateTime? ReadTakenAt(ISExif.ExifProfile? exif)
    {
        if (exif is null)
        {
            return null;
        }

        foreach (var tag in new[] { ISExif.ExifTag.DateTimeOriginal, ISExif.ExifTag.DateTimeDigitized, ISExif.ExifTag.DateTime })
        {
            if (exif.TryGetValue(tag, out var value) &&
                TryParseExifDate(value.Value, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTime? ReadTakenAt(IExifProfile? exif)
    {
        if (exif is null)
        {
            return null;
        }

        foreach (var tag in new[] { ImageMagick.ExifTag.DateTimeOriginal, ImageMagick.ExifTag.DateTimeDigitized, ImageMagick.ExifTag.DateTime })
        {
            var value = exif.GetValue(tag);
            if (value?.Value is string raw && TryParseExifDate(raw, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryParseExifDate(string? raw, out DateTime parsed)
    {
        parsed = default;
        return raw is not null && DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", null,
            System.Globalization.DateTimeStyles.AssumeLocal, out parsed);
    }
}
