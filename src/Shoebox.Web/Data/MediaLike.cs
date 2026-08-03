namespace Shoebox.Web.Data;

/// <summary>
/// One ❤️ from one browser on one photo or video. The browser's cookie identity
/// (<see cref="UploaderUid"/>) plus the item form the primary key, so a browser can
/// like something at most once; liking again just removes the row.
/// </summary>
public class MediaLike
{
    public Guid MediaId { get; set; }
    public Media Media { get; set; } = null!;

    public Guid UploaderUid { get; set; }

    public DateTime CreatedAt { get; set; }
}
