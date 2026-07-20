namespace GroupPhoto.Web;

public class GroupPhotoOptions
{
    public const string SectionName = "GroupPhoto";

    /// <summary>Root directory for the SQLite DB, image files and data-protection keys.</summary>
    public string DataPath { get; set; } = "data";

    public int MaxFileSizeMb { get; set; } = 50;

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

    public long MaxFileSizeBytes => MaxFileSizeMb * 1024L * 1024L;
}
