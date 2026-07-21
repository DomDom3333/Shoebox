using Shoebox.Web.Data;
using Shoebox.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Shoebox.Web.Pages;

public record BoxSummary(string Code, string Name, bool IsAdmin, int PhotoCount, DateTime? ExpiresAt);

public class IndexModel(AppDbContext db, PoolService pools, PoolAccessService access) : PageModel
{
    public string Code { get; set; } = "";
    public bool CodeNotFound { get; set; }

    // Boxes this browser holds cookies for (unlocked or administers).
    public List<BoxSummary> MyBoxes { get; set; } = [];

    public Task OnGetAsync() => LoadMyBoxesAsync();

    public async Task<IActionResult> OnPostJoinAsync(string code)
    {
        var pool = await pools.FindByCodeAsync(code ?? "");
        if (pool is null)
        {
            Code = code ?? "";
            CodeNotFound = true;
            await LoadMyBoxesAsync();
            return Page();
        }

        return RedirectToPage("/Pool/Gallery", new { code = pool.Code });
    }

    private async Task LoadMyBoxesAsync()
    {
        var ids = access.AccessiblePoolIds(HttpContext).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        // Stale cookie entries (deleted or expired boxes) simply don't resolve here.
        var boxes = await db.Pools
            .Where(p => ids.Contains(p.Id))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.Code, p.Name, p.ExpiresAt, PhotoCount = p.Photos.Count })
            .ToListAsync();

        MyBoxes = boxes
            .Select(b => new BoxSummary(b.Code, b.Name, access.IsAdmin(HttpContext, b.Id), b.PhotoCount, b.ExpiresAt))
            .ToList();
    }
}
