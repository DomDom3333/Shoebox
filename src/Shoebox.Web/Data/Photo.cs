namespace Shoebox.Web.Data;

public class Photo
{
    public Guid Id { get; set; }
    public Guid PoolId { get; set; }
    public Pool Pool { get; set; } = null!;

    public string OriginalFileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentHash { get; set; } = "";
    public bool HasThumbnail { get; set; }

    public string UploaderName { get; set; } = "";
    public Guid UploaderUid { get; set; }

    public DateTime UploadedAt { get; set; }
    public DateTime? TakenAt { get; set; }

    public DateTime SortDate => TakenAt ?? UploadedAt;
}
