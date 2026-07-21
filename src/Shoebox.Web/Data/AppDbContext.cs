using Microsoft.EntityFrameworkCore;

namespace Shoebox.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Pool> Pools => Set<Pool>();
    public DbSet<Photo> Photos => Set<Photo>();
    public DbSet<PhotoLike> Likes => Set<PhotoLike>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Pool>(pool =>
        {
            pool.HasIndex(p => p.Code).IsUnique();
            pool.Property(p => p.Name).HasMaxLength(120);
            pool.Property(p => p.Code).HasMaxLength(16);
            pool.Property(p => p.Description).HasMaxLength(2000);
            pool.HasMany(p => p.Photos)
                .WithOne(p => p.Pool)
                .HasForeignKey(p => p.PoolId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Photo>(photo =>
        {
            photo.HasIndex(p => new { p.PoolId, p.ContentHash });
            photo.Property(p => p.OriginalFileName).HasMaxLength(260);
            photo.Property(p => p.UploaderName).HasMaxLength(80);
        });

        modelBuilder.Entity<PhotoLike>(like =>
        {
            // (photo, browser) is the key, so a browser can like a photo only once.
            like.HasKey(l => new { l.PhotoId, l.UploaderUid });
            like.HasOne(l => l.Photo)
                .WithMany()
                .HasForeignKey(l => l.PhotoId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
