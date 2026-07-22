using Shoebox.Web.Data;
using Shoebox.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Shoebox.Web.Pages.Pool;

public record PhotoTile(
    Guid Id, string UploaderName, string FileName, bool Mine, bool HasThumbnail,
    int LikeCount, bool LikedByMe);
public record UploaderSummary(string Name, int Count);

public class GalleryModel(
    AppDbContext db,
    PoolService pools,
    PoolAccessService access,
    UploaderIdentity identity,
    ShareLinkService links) : PageModel
{
    public Data.Pool Pool { get; set; } = null!;
    public List<PhotoTile> Photos { get; set; } = [];
    public List<UploaderSummary> Uploaders { get; set; } = [];
    public string UploaderName { get; set; } = "";
    public string ShareUrl { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool ExpiresSoon { get; set; }
    public string ExpiryLabel { get; set; } = "";
    public string ExpiryUrgency { get; set; } = "";

    // Whether the box holds any photos the current visitor did not upload, so the
    // "everyone else's" download only offers itself when it would actually return files.
    public bool HasOthers { get; set; }

    public async Task<IActionResult> OnGetAsync(string code)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return RedirectToPage("/Index");
        }

        if (!access.CanView(HttpContext, pool))
        {
            return RedirectToPage("/Pool/Unlock", new { code = pool.Code });
        }

        var uid = identity.GetOrCreateUid(HttpContext);
        var photos = await db.Photos
            .Where(p => p.PoolId == pool.Id)
            .OrderBy(p => p.TakenAt ?? p.UploadedAt)
            .ToListAsync();

        var photoIds = photos.Select(p => p.Id).ToList();
        var likeCounts = await db.Likes
            .Where(l => photoIds.Contains(l.PhotoId))
            .GroupBy(l => l.PhotoId)
            .Select(g => new { PhotoId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PhotoId, x => x.Count);
        var myLikes = (await db.Likes
            .Where(l => l.UploaderUid == uid && photoIds.Contains(l.PhotoId))
            .Select(l => l.PhotoId)
            .ToListAsync()).ToHashSet();

        Pool = pool;
        Photos = photos
            .Select(p => new PhotoTile(
                p.Id, p.UploaderName, p.OriginalFileName, p.UploaderUid == uid, p.HasThumbnail,
                likeCounts.GetValueOrDefault(p.Id), myLikes.Contains(p.Id)))
            .ToList();
        HasOthers = Photos.Any(p => !p.Mine);
        Uploaders = photos
            .GroupBy(p => p.UploaderName)
            .Select(g => new UploaderSummary(g.Key, g.Count()))
            .OrderBy(u => u.Name)
            .ToList();
        UploaderName = identity.GetName(HttpContext) ?? "";
        ShareUrl = links.PoolUrl(HttpContext, pool);
        IsAdmin = access.IsAdmin(HttpContext, pool.Id);
        if (pool.ExpiresAt is { } exp)
        {
            ExpiresSoon = exp < DateTime.UtcNow.AddDays(7);
            ExpiryLabel = BuildExpiryLabel(exp);
            ExpiryUrgency = BuildExpiryUrgency(exp);
        }
        return Page();
    }

    private static string BuildExpiryLabel(DateTime expiresAt)
    {
        var diff = expiresAt - DateTime.UtcNow;
        if (diff.TotalHours < 24) return "today";
        if (diff.TotalDays < 2) return "tomorrow";
        if (diff.TotalDays < 7) return $"in {(int)diff.TotalDays} day{((int)diff.TotalDays == 1 ? "" : "s")}";
        if (diff.TotalDays < 14) return "in 1 week";
        if (diff.TotalDays < 30) return $"in {(int)(diff.TotalDays / 7)} weeks";
        if (diff.TotalDays < 60) return "in 1 month";
        if (diff.TotalDays < 365) return $"in {(int)(diff.TotalDays / 30)} months";
        if (diff.TotalDays < 730) return "in 1 year";
        return $"in {(int)(diff.TotalDays / 365)} years";
    }

    private static string BuildExpiryUrgency(DateTime expiresAt)
    {
        var days = (expiresAt - DateTime.UtcNow).TotalDays;
        if (days <= 7) return "critical";
        if (days <= 30) return "warning";
        if (days <= 90) return "notice";
        return "info";
    }
}
