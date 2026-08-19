using System.Text.Json;
using Shoebox.Web.Data;
using Shoebox.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Shoebox.Web.Pages.Pool;

public record MediaTile(
    Guid Id, string UploaderName, string FileName, bool Mine, bool HasThumbnail,
    int LikeCount, bool LikedByMe, bool IsVideo, bool IsAnimated,
    long SortAt, long UploadedAt);
public record TileList(IReadOnlyList<MediaTile> Items, bool IsAdmin);
public record UploaderSummary(string Name, int Count);

public class GalleryModel(
    AppDbContext db,
    PoolService pools,
    PoolAccessService access,
    UploaderIdentity identity,
    ShareLinkService links,
    MediaHandlers handlers) : PageModel
{
    /// <summary>
    /// How far back of the browser's cursor the live feed looks. Two uploads landing together
    /// can commit a hair out of order, so it re-offers a moment's worth of prints and the page
    /// drops the ids it already has.
    /// </summary>
    private static readonly TimeSpan CursorOverlap = TimeSpan.FromSeconds(30);

    private const int LiveBatchLimit = 200;
    private const long MaxCursorMilliseconds = 253_402_300_799_000; // the end of year 9999

    public Data.Pool Pool { get; set; } = null!;
    public List<MediaTile> Items { get; set; } = [];
    public List<UploaderSummary> Uploaders { get; set; } = [];
    public string UploaderName { get; set; } = "";
    public string ShareUrl { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool ExpiresSoon { get; set; }
    public string ExpiryLabel { get; set; } = "";
    public string ExpiryUrgency { get; set; } = "";

    // Whether the box holds anything the current visitor did not upload, so the
    // "everyone else's" download only offers itself when it would actually return files.
    public bool HasOthers { get; set; }

    // Handed to the page so the browser can turn a file away before sending it.
    public UploadPolicy UploadPolicy => handlers.Policy;

    public string UploadLimitsJson => JsonSerializer.Serialize(UploadPolicy.MaxBytesByExtension);

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

        var items = await db.Media
            .Where(m => m.PoolId == pool.Id)
            .OrderBy(m => m.TakenAt ?? m.UploadedAt)
            .ToListAsync();

        Pool = pool;
        Items = await ToTilesAsync(items);
        HasOthers = Items.Any(m => !m.Mine);
        Uploaders = items
            .GroupBy(m => m.UploaderName)
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

    /// <summary>
    /// The prints that landed after the browser's cursor, drawn as tiles for the page to hang.
    /// A print is only committed once its thumbnail and proxy are written, so anything offered
    /// here can already be looked at.
    /// </summary>
    public async Task<IActionResult> OnGetSinceAsync(string code, long since = 0)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return NotFound();
        }

        if (!access.CanView(HttpContext, pool))
        {
            // The box locked itself (or the cookie expired) while the page sat open.
            return Unauthorized();
        }

        // Cursors are Unix milliseconds: exact in a browser's numbers, where ticks would not be.
        var floor = DateTime.UnixEpoch.AddMilliseconds(
            Math.Clamp(since, 0, MaxCursorMilliseconds) - CursorOverlap.TotalMilliseconds);
        var items = await db.Media
            .Where(m => m.PoolId == pool.Id && m.UploadedAt > floor)
            .OrderBy(m => m.UploadedAt)
            .ThenBy(m => m.Id)
            .Take(LiveBatchLimit)
            .ToListAsync();

        Response.Headers.CacheControl = "no-store";
        return Partial("_Tiles", new TileList(await ToTilesAsync(items), access.IsAdmin(HttpContext, pool.Id)));
    }

    private async Task<List<MediaTile>> ToTilesAsync(List<Media> items)
    {
        var uid = identity.GetOrCreateUid(HttpContext);
        if (items.Count == 0)
        {
            return [];
        }

        var itemIds = items.Select(m => m.Id).ToList();
        var likeCounts = await db.Likes
            .Where(l => itemIds.Contains(l.MediaId))
            .GroupBy(l => l.MediaId)
            .Select(g => new { MediaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.MediaId, x => x.Count);
        var myLikes = (await db.Likes
            .Where(l => l.UploaderUid == uid && itemIds.Contains(l.MediaId))
            .Select(l => l.MediaId)
            .ToListAsync()).ToHashSet();

        return items
            .Select(m => new MediaTile(
                m.Id, m.UploaderName, m.OriginalFileName, m.UploaderUid == uid, m.HasThumbnail,
                likeCounts.GetValueOrDefault(m.Id), myLikes.Contains(m.Id),
                m.Kind == MediaKind.Video, m.HasAnimation,
                UnixMilliseconds(m.SortDate), UnixMilliseconds(m.UploadedAt)))
            .ToList();
    }

    private static long UnixMilliseconds(DateTime value) =>
        (long)(value - DateTime.UnixEpoch).TotalMilliseconds;

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
