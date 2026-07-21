namespace Shoebox.Web.Data;

/// <summary>
/// One ❤️ from one browser on one photo. The browser's cookie identity
/// (<see cref="UploaderUid"/>) plus the photo form the primary key, so a
/// browser can like a photo at most once; liking again just removes the row.
/// </summary>
public class PhotoLike
{
    public Guid PhotoId { get; set; }
    public Photo Photo { get; set; } = null!;

    public Guid UploaderUid { get; set; }

    public DateTime CreatedAt { get; set; }
}
