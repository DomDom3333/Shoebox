using Microsoft.EntityFrameworkCore;

namespace Shoebox.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Pool> Pools => Set<Pool>();
    public DbSet<Media> Media => Set<Media>();
    public DbSet<MediaLike> Likes => Set<MediaLike>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Pool>(pool =>
        {
            pool.HasIndex(p => p.Code).IsUnique();
            pool.Property(p => p.Name).HasMaxLength(120);
            pool.Property(p => p.Code).HasMaxLength(16);
            pool.Property(p => p.Description).HasMaxLength(2000);
            pool.HasMany(p => p.Media)
                .WithOne(m => m.Pool)
                .HasForeignKey(m => m.PoolId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Media>(media =>
        {
            media.HasIndex(m => new { m.PoolId, m.ContentHash });
            media.Property(m => m.OriginalFileName).HasMaxLength(260);
            media.Property(m => m.UploaderName).HasMaxLength(80);
        });

        modelBuilder.Entity<MediaLike>(like =>
        {
            // (item, browser) is the key, so a browser can like something only once.
            like.HasKey(l => new { l.MediaId, l.UploaderUid });
            like.HasOne(l => l.Media)
                .WithMany()
                .HasForeignKey(l => l.MediaId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
