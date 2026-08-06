namespace Shoebox.Web.Data;

public class Pool
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? PasswordHash { get; set; }
    public Guid AdminKey { get; set; }

    /// <summary>
    /// This box's random data key, sealed under the operator's master key. Null when the box's
    /// files are stored in the clear: either encryption is off, or the box predates it being
    /// switched on and hasn't had an upload since.
    /// </summary>
    public byte[]? WrappedKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public List<Media> Media { get; set; } = [];

    public bool HasPassword => PasswordHash is not null;
}
