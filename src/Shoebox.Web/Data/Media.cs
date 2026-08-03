namespace Shoebox.Web.Data;

/// <summary>
/// What was uploaded. Every kind is stored, served, liked, zipped and deleted the same way;
/// the kind only decides how the two renditions get made at upload time.
/// </summary>
public enum MediaKind
{
    Photo = 0,
    Video = 1,
}

/// <summary>One file in a box, plus the renditions derived from it.</summary>
public class Media
{
    public Guid Id { get; set; }
    public Guid PoolId { get; set; }
    public Pool Pool { get; set; } = null!;

    public MediaKind Kind { get; set; }

    public string OriginalFileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentHash { get; set; } = "";
    public bool HasThumbnail { get; set; }

    /// <summary>
    /// The display proxy is an animated WebP (an animated GIF or WebP was uploaded), so the
    /// lightbox plays it and the still thumbnail is only the first frame.
    /// </summary>
    public bool HasAnimation { get; set; }

    public string UploaderName { get; set; } = "";
    public Guid UploaderUid { get; set; }

    public DateTime UploadedAt { get; set; }
    public DateTime? TakenAt { get; set; }

    public DateTime SortDate => TakenAt ?? UploadedAt;
}
