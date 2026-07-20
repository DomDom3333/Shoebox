using GroupPhoto.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GroupPhoto.Web.Pages.Pool;

public class AdminModel(PoolService pools, PoolAccessService access, ShareLinkService links) : PageModel
{
    public (int Days, string Text)[] ExpiryChoices { get; } =
    [
        (0, "Never"),
        (30, "In 1 month"),
        (90, "In 3 months"),
        (180, "In 6 months"),
        (365, "In 1 year"),
    ];

    public Data.Pool Pool { get; set; } = null!;
    public string ShareUrl { get; set; } = "";
    public string AdminUrl { get; set; } = "";
    public int SelectedExpiryDays { get; set; }
    public bool JustCreated { get; set; }
    public bool Saved { get; set; }

    public async Task<IActionResult> OnGetAsync(string code, string? key, int created = 0, int saved = 0)
    {
        var result = await AuthorizeAsync(code, key);
        if (result is not null)
        {
            return result;
        }

        JustCreated = created == 1;
        Saved = saved == 1;
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(string code, string name, string? description,
        int expiryDays, bool changePassword = false, string? newPassword = null)
    {
        var result = await AuthorizeAsync(code, key: null);
        if (result is not null)
        {
            return result;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return RedirectToPage(new { code });
        }

        var expiresAt = expiryDays > 0 ? DateTime.UtcNow.AddDays(expiryDays) : (DateTime?)null;
        await pools.UpdateAsync(Pool, name, description, expiresAt, changePassword, newPassword);
        return RedirectToPage(new { code, saved = 1 });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string code)
    {
        var result = await AuthorizeAsync(code, key: null);
        if (result is not null)
        {
            return result;
        }

        access.RevokeAll(HttpContext, Pool.Id);
        await pools.DeleteAsync(Pool);
        return RedirectToPage("/Index");
    }

    /// <summary>Loads the pool and enforces admin rights; returns a redirect when not authorized.</summary>
    private async Task<IActionResult?> AuthorizeAsync(string code, string? key)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return RedirectToPage("/Index");
        }

        if (key is not null && Guid.TryParse(key, out var parsedKey) && parsedKey == pool.AdminKey)
        {
            access.GrantAdmin(HttpContext, pool.Id);
        }

        if (!access.IsAdmin(HttpContext, pool.Id))
        {
            return RedirectToPage("/Pool/Gallery", new { code = pool.Code });
        }

        Pool = pool;
        ShareUrl = links.PoolUrl(HttpContext, pool);
        AdminUrl = links.AdminUrl(HttpContext, pool);
        SelectedExpiryDays = 0;
        return null;
    }
}
