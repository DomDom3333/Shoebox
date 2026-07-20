using GroupPhoto.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GroupPhoto.Web.Pages.Pool;

public class AdminModel(PoolService pools, PoolAccessService access, ShareLinkService links) : PageModel
{
    // Sentinel expiry choice meaning "leave the current expiry untouched".
    private const int KeepCurrent = -1;

    public List<(int Days, string Text)> ExpiryChoices { get; private set; } = [];

    public Data.Pool Pool { get; set; } = null!;
    public string ShareUrl { get; set; } = "";
    public string AdminUrl { get; set; } = "";
    public int SelectedExpiryDays { get; set; }
    public bool JustCreated { get; set; }
    public bool Saved { get; set; }

    public async Task<IActionResult> OnGetAsync(string code, string? key, int created = 0, int saved = 0)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return RedirectToPage("/Index");
        }

        // The admin key is a one-time credential carried in the private link the creator
        // saved. Consume a valid key into the signed admin cookie, then redirect to the
        // clean URL so the key never lingers in the address bar, browser history,
        // bookmarks or proxy/server logs. From here on, settings access is gated purely
        // by the signed cookie, exactly like the pool-unlock flow.
        if (key is not null)
        {
            if (Guid.TryParse(key, out var parsedKey) && parsedKey == pool.AdminKey)
            {
                access.GrantAdmin(HttpContext, pool.Id);
            }

            return RedirectToPage(new { code = pool.Code, created });
        }

        if (!access.IsAdmin(HttpContext, pool.Id))
        {
            return RedirectToPage("/Pool/Gallery", new { code = pool.Code });
        }

        Populate(pool);
        JustCreated = created == 1;
        Saved = saved == 1;
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(string code, string name, string? description,
        int expiryDays, bool changePassword = false, string? newPassword = null)
    {
        var pool = await LoadAdminPoolAsync(code);
        if (pool is null)
        {
            return RedirectToPage("/Pool/Gallery", new { code });
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return RedirectToPage(new { code });
        }

        DateTime? expiresAt = expiryDays switch
        {
            KeepCurrent => pool.ExpiresAt,                    // leave the existing expiry as-is
            > 0 => DateTime.UtcNow.AddDays(expiryDays),       // set a new expiry from today
            _ => null,                                        // 0 = Never
        };
        await pools.UpdateAsync(pool, name, description, expiresAt, changePassword, newPassword);
        return RedirectToPage(new { code, saved = 1 });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string code)
    {
        var pool = await LoadAdminPoolAsync(code);
        if (pool is null)
        {
            return RedirectToPage("/Pool/Gallery", new { code });
        }

        access.RevokeAll(HttpContext, pool.Id);
        await pools.DeleteAsync(pool);
        return RedirectToPage("/Index");
    }

    /// <summary>
    /// Loads the pool only if the caller holds a valid signed admin cookie for it.
    /// The admin key in the URL is never accepted here; it is exchanged for the cookie
    /// once, on GET, so settings changes can only come from an already-authorized device.
    /// </summary>
    private async Task<Data.Pool?> LoadAdminPoolAsync(string code)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null || !access.IsAdmin(HttpContext, pool.Id))
        {
            return null;
        }

        Populate(pool);
        return pool;
    }

    private void Populate(Data.Pool pool)
    {
        Pool = pool;
        ShareUrl = links.PoolUrl(HttpContext, pool);
        AdminUrl = links.AdminUrl(HttpContext, pool);

        // When the pool already has an expiry, default the dropdown to "Keep current" so
        // saving other settings never silently clears it.
        ExpiryChoices = [];
        if (pool.ExpiresAt is { } exp)
        {
            ExpiryChoices.Add((KeepCurrent, $"Keep current ({exp:d MMM yyyy})"));
            SelectedExpiryDays = KeepCurrent;
        }
        else
        {
            SelectedExpiryDays = 0;
        }

        ExpiryChoices.Add((0, "Never"));
        ExpiryChoices.Add((30, "In 1 month"));
        ExpiryChoices.Add((90, "In 3 months"));
        ExpiryChoices.Add((180, "In 6 months"));
        ExpiryChoices.Add((365, "In 1 year"));
    }
}
