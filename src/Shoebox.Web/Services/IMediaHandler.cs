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

    /// <summary>What to call this kind when telling someone their file didn't fit.</summary>
    string Label { get; }

    /// <summary>
    /// Every extension this handler takes, mapped to the content type to store for it. Looked
    /// up case-insensitively, so implementations build it with an ordinal-ignore-case comparer.
    /// </summary>
    IReadOnlyDictionary<string, string> ContentTypes { get; }

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

/// <summary>
/// What the browser needs to turn a file away before sending it, plus the words to use when
/// something doesn't fit. A video can be hundreds of megabytes, and a file the server won't
/// take is worth saying no to in the moment it's picked — not after a long upload, and
/// certainly not by having the connection cut for exceeding the request-body limit, which
/// reaches the user as nothing more useful than "network error".
/// </summary>
/// <param name="MaxBytesByExtension">Accepted extensions (lowercase, dotted) and their ceilings.</param>
/// <param name="Accept">Value for the file input's accept attribute.</param>
/// <param name="Summary">Plain-language "what fits", e.g. for the upload card and rejections.</param>
public record UploadPolicy(
    IReadOnlyDictionary<string, long> MaxBytesByExtension,
    string Accept,
    string Summary)
{
    /// <summary>The reason to give for a file whose extension nothing here takes.</summary>
    public string RejectionFor(string extension) =>
        $"Can't take {(string.IsNullOrWhiteSpace(extension) ? "files with no extension" : extension.ToLowerInvariant() + " files")} — {Summary}";

    /// <summary>Sizes are only ever shown to people, so one decimal of MB is plenty.</summary>
    public static string DescribeSize(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";
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
            if (handler.ContentTypes.GetValueOrDefault(extension) is { } contentType)
            {
                return (handler, contentType);
            }
        }

        return null;
    }

    public IMediaHandler For(MediaKind kind) => all.First(h => h.Kind == kind);

    /// <summary>
    /// What this build accepts, assembled from the handlers so the browser, the upload card and
    /// the rejection messages can never drift from what the server actually stores.
    /// </summary>
    public UploadPolicy Policy => new(
        all.SelectMany(h => h.ContentTypes.Keys.Select(e => (Extension: e.ToLowerInvariant(), h.MaxBytes)))
            .ToDictionary(x => x.Extension, x => x.MaxBytes, StringComparer.OrdinalIgnoreCase),
        BuildAccept(),
        string.Join(", ", all.Select(h =>
            $"{h.Label}s ({string.Join(", ", h.ContentTypes.Keys.Select(e => e.TrimStart('.')))}) "
            + $"up to {UploadPolicy.DescribeSize(h.MaxBytes)}")));

    /// <summary>
    /// The wildcards (image/*, video/*) keep phone pickers showing the camera roll; the explicit
    /// extensions cover the formats a picker may not map to one (HEIC and MKV in particular).
    /// Nothing here is a security control — it only steers the picker, and the drop zone ignores
    /// it entirely — so the real check is <see cref="For(string)"/> on the way in.
    /// </summary>
    private string BuildAccept()
    {
        var wildcards = all
            .SelectMany(h => h.ContentTypes.Values)
            .Select(type => type[..(type.IndexOf('/') + 1)] + "*")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var extensions = all
            .SelectMany(h => h.ContentTypes.Keys.Select(e => e.ToLowerInvariant()))
            .Distinct(StringComparer.Ordinal);
        return string.Join(",", wildcards.Concat(extensions));
    }
}
