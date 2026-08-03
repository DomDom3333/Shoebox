using ImageMagick;
using Microsoft.Extensions.Options;

namespace Shoebox.Web.Services;

public record ImageInfo(int Width, int Height, DateTime? TakenAt, bool IsAnimated = false);

/// <summary>
/// Renders the derived images for every upload with Magick.NET (which decodes everything
/// we accept, including HEIC/HEIF via bundled libheif).
/// </summary>
public class ImageRenderer(IOptions<ShoeboxOptions> options, ILogger<ImageRenderer> logger)
{
    /// <summary>
    /// Decodes the original and writes both WebP renditions: a small thumbnail for the grid
    /// and a larger web-safe display proxy for the lightbox. An animation (GIF or WebP) keeps
    /// moving in the proxy and holds still in the thumbnail. Returns the source dimensions
    /// and EXIF capture date, or null when the file can't be decoded (the caller should
    /// reject or clean up the upload in that case).
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

            if (ShouldAnimate(originalPath, probe))
            {
                return await RenderAnimationAsync(originalPath, thumbPath, displayPath, ct);
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

    /// <summary>
    /// Whether this upload is an animation worth keeping in motion: more than one frame, in a
    /// format where extra frames actually mean animation (a HEIC's extra images are depth maps
    /// and previews, not frames), and cheap enough in total to decode.
    /// </summary>
    private bool ShouldAnimate(string originalPath, IMagickImageInfo probe)
    {
        if (probe.Format is not (MagickFormat.Gif or MagickFormat.Gif87 or MagickFormat.WebP))
        {
            return false;
        }

        // Frame headers only: enough to count frames and add up their pixels without decoding.
        var frames = MagickImageInfo.ReadCollection(originalPath).ToList();
        if (frames.Count < 2)
        {
            return false;
        }

        var totalPixels = frames.Sum(f => (long)f.Width * f.Height);
        if (totalPixels > options.Value.MaxAnimationPixels)
        {
            // A few frames of enormous, or an enormous number of frames. Still worth showing,
            // just not worth decoding every frame of: fall through to the single-frame path.
            logger.LogWarning("Animation {Path} has {Frames} frames totalling {Pixels} pixels; keeping a still instead",
                originalPath, frames.Count, totalPixels);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes an animated WebP display proxy (played by the lightbox, and by hovering a tile)
    /// plus a still thumbnail, so a busy gallery grid doesn't animate all at once.
    /// </summary>
    private async Task<ImageInfo> RenderAnimationAsync(string originalPath, string thumbPath, string displayPath,
        CancellationToken ct)
    {
        using var frames = new MagickImageCollection(originalPath);

        // GIF frames are usually partial deltas against the frame before; coalescing makes each
        // one whole, so resizing can't smear the pieces.
        frames.Coalesce();

        var takenAt = ReadTakenAt(frames[0].GetExifProfile());
        var (width, height) = ((int)frames[0].Width, (int)frames[0].Height);

        foreach (var frame in frames)
        {
            frame.Resize(new MagickGeometry((uint)options.Value.DisplaySize, (uint)options.Value.DisplaySize) { Greater = true });
            frame.Quality = 82;
        }

        // Taken before Optimize, while every frame is still a whole image.
        using var still = frames[0].Clone();

        // Back to deltas now that the frames are display-sized: much smaller, same animation.
        frames.Optimize();
        await frames.WriteAsync(displayPath, MagickFormat.WebP, ct);

        still.Resize(new MagickGeometry((uint)options.Value.ThumbnailSize, (uint)options.Value.ThumbnailSize) { Greater = true });
        still.Quality = 78;
        await still.WriteAsync(thumbPath, MagickFormat.WebP, ct);

        return new ImageInfo(width, height, takenAt, IsAnimated: true);
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
