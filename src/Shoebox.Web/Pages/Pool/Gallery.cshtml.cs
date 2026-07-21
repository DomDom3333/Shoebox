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
        ExpiresSoon = pool.ExpiresAt is { } exp && exp < DateTime.UtcNow.AddDays(7);
        return Page();
    }
}
