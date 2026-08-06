using Shoebox.Web.Data;
using Microsoft.Extensions.Options;

namespace Shoebox.Web.Services;

/// <summary>
/// Still images and animations. Magick.NET decodes everything here, so a file that won't decode
/// is corrupt, mislabelled or a decode bomb, and is rejected rather than stored.
/// </summary>
public class PhotoHandler(ImageRenderer renderer, IOptions<ShoeboxOptions> options) : IMediaHandler
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
    };

    public MediaKind Kind => MediaKind.Photo;

    public string? ContentTypeFor(string extension) => ContentTypes.GetValueOrDefault(extension);

    public IReadOnlyCollection<string> Extensions => ContentTypes.Keys;

    public long MaxBytes => options.Value.MaxFileSizeBytes;

    // Decoding is the check: anything that isn't really an image fails to render.
    public string? Reject(string originalPath) => null;

    public string RenderFailureReason => "Couldn't read this image (it may be corrupt or too large)";

    public Task<ImageInfo?> RenderAsync(string originalPath, string thumbPath, string displayPath,
        CancellationToken ct) => renderer.ProcessAsync(originalPath, thumbPath, displayPath, ct);
}
