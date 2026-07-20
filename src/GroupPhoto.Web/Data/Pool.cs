namespace GroupPhoto.Web.Data;

public class Pool
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? PasswordHash { get; set; }
    public Guid AdminKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public List<Photo> Photos { get; set; } = [];

    public bool HasPassword => PasswordHash is not null;
}
