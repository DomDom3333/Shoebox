using Shoebox.Web.Data;
using Microsoft.Extensions.Options;

namespace Shoebox.Web.Services;

/// <summary>
/// Video clips: stored untouched, shown as a single poster frame, never played in the browser.
/// Nothing decodes them on the way in, so the container header is checked instead, and a clip
/// whose poster frame can't be grabbed is still worth keeping.
/// </summary>
public class VideoHandler(VideoRenderer renderer, IOptions<ShoeboxOptions> options) : IMediaHandler
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
    };

    public MediaKind Kind => MediaKind.Video;

    public string? ContentTypeFor(string extension) => ContentTypes.GetValueOrDefault(extension);

    public long MaxBytes => options.Value.MaxVideoFileSizeBytes;

    public string? Reject(string originalPath) => VideoRenderer.LooksLikeVideo(originalPath)
        ? null
        : "Couldn't read this video (it may be corrupt or an unsupported format)";

    // A clip with no poster frame (no ffmpeg on the host, or a codec it doesn't know) still
    // belongs in the box; it just gets a placeholder tile.
    public string? RenderFailureReason => null;

    public Task<ImageInfo?> RenderAsync(string originalPath, string thumbPath, string displayPath,
        CancellationToken ct) => renderer.RenderPosterAsync(originalPath, thumbPath, displayPath, ct);
}
