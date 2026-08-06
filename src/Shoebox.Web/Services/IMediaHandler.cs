using Shoebox.Web.Data;

namespace Shoebox.Web.Services;

/// <summary>
/// Everything that differs between one kind of upload and another. Once a file is stored it is
/// served, liked, zipped and deleted identically whatever it is, so this is the only place a
/// new kind of media (audio, say) has to touch.
/// </summary>
public interface IMediaHandler
{
    MediaKind Kind { get; }

    /// <summary>
    /// The content type to store for this extension, or null when this handler doesn't take it.
    /// </summary>
    string? ContentTypeFor(string extension);

    /// <summary>
    /// Every extension this handler takes, so the browser can be told the ceiling that applies
    /// to a file before it starts uploading it.
    /// </summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>Per-file upload ceiling for this kind.</summary>
    long MaxBytes { get; }

    /// <summary>
    /// Checks that the stored bytes really are what the extension claims. Returns the reason to
    /// reject the upload, or null when it looks genuine.
    /// </summary>
    string? Reject(string originalPath);

    /// <summary>
    /// Writes the thumbnail and display proxy, returning the source dimensions and capture date,
    /// or null when no rendition could be made.
    /// </summary>
    Task<ImageInfo?> RenderAsync(string originalPath, string thumbPath, string displayPath,
        CancellationToken ct);

    /// <summary>
    /// Why to reject a file we couldn't render, or null when the file is worth keeping anyway.
    /// A photo that won't decode is corrupt; a video we couldn't grab a frame from is fine
    /// without one, and just gets a placeholder tile.
    /// </summary>
    string? RenderFailureReason { get; }
}

/// <summary>Finds the handler for an upload, by file extension or by stored kind.</summary>
public class MediaHandlers(IEnumerable<IMediaHandler> handlers)
{
    private readonly IMediaHandler[] all = handlers.ToArray();

    /// <summary>
    /// The handler that accepts this file extension along with the content type to store, or
    /// null when nothing accepts it.
    /// </summary>
    public (IMediaHandler Handler, string ContentType)? For(string extension)
    {
        foreach (var handler in all)
        {
            if (handler.ContentTypeFor(extension) is { } contentType)
            {
                return (handler, contentType);
            }
        }

        return null;
    }

    public IMediaHandler For(MediaKind kind) => all.First(h => h.Kind == kind);

    /// <summary>
    /// The per-file ceiling for every extension we accept. The browser checks a file against
    /// this before sending it: an over-limit body is cut off mid-upload by the request-size
    /// limit, and a connection reset reaches the page as a bare network error rather than as
    /// the reason the file was refused.
    /// </summary>
    public IReadOnlyDictionary<string, long> MaxBytesByExtension() => all
        .SelectMany(h => h.Extensions.Select(e => (Extension: e, h.MaxBytes)))
        .ToDictionary(x => x.Extension, x => x.MaxBytes, StringComparer.OrdinalIgnoreCase);
}
