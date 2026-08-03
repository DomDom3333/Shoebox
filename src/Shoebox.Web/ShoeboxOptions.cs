namespace Shoebox.Web;

public class ShoeboxOptions
{
    public const string SectionName = "Shoebox";

    /// <summary>Root directory for the SQLite DB, image files and data-protection keys.</summary>
    public string DataPath { get; set; } = "data";

    public int MaxFileSizeMb { get; set; } = 50;

    /// <summary>
    /// Per-file upload limit for videos, which are much larger than photos. Kept separate
    /// so raising it doesn't also raise the photo limit.
    /// </summary>
    public int MaxVideoFileSizeMb { get; set; } = 200;

    /// <summary>
    /// Reject images larger than this many pixels (width × height) before decoding,
    /// to stop decompression-bomb / pixel-flood uploads. 100 MP clears any real camera.
    /// </summary>
    public long MaxImagePixels { get; set; } = 100_000_000;

    /// <summary>Reject images whose width or height exceeds this many pixels.</summary>
    public int MaxImageDimension { get; set; } = 30_000;

    /// <summary>
    /// Budget for keeping an animation moving, counted across every frame (width × height ×
    /// frames). Every frame has to be decoded, resized and re-encoded, so this bounds how long
    /// one upload can spend rendering. Animations over the budget are still accepted; they just
    /// get a single-frame proxy like any other photo.
    /// </summary>
    public long MaxAnimationPixels { get; set; } = 40_000_000;

    /// <summary>Password-unlock attempts allowed per client IP per pool per minute.</summary>
    public int UnlockAttemptsPerMinute { get; set; } = 10;

    /// <summary>Longest edge of generated thumbnails, in pixels.</summary>
    public int ThumbnailSize { get; set; } = 480;

    /// <summary>
    /// Longest edge of the web-safe "display" proxy shown in the lightbox, in pixels.
    /// Big enough to look full-screen sharp, small enough to load fast; the untouched
    /// original is kept for downloads. Originals already web-safe and no larger than this
    /// are served directly instead of duplicating them.
    /// </summary>
    public int DisplaySize { get; set; } = 1600;

    /// <summary>Expiry preselected on the create form; 0 = "never" preselected.</summary>
    public int DefaultExpiryDays { get; set; } = 0;

    /// <summary>How long unlock/admin/identity cookies last.</summary>
    public int CookieLifetimeDays { get; set; } = 90;

    /// <summary>
    /// Overrides the base URL used in share links and QR codes, e.g. "https://photos.example.com".
    /// Leave empty to derive it from the incoming request (honours X-Forwarded-* headers).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// ffmpeg executable used to grab the poster frame from an uploaded video. Looked up on
    /// PATH by default (the Docker image ships it). Videos still upload and download without
    /// it; they just fall back to a placeholder tile instead of a frame.
    /// </summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>How far into a video the poster frame is taken, in seconds.</summary>
    public double VideoPosterSeconds { get; set; } = 1;

    public long MaxFileSizeBytes => MaxFileSizeMb * 1024L * 1024L;

    public long MaxVideoFileSizeBytes => MaxVideoFileSizeMb * 1024L * 1024L;
}
