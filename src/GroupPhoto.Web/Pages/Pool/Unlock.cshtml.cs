using GroupPhoto.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GroupPhoto.Web.Pages.Pool;

public class UnlockModel(PoolService pools, PoolAccessService access) : PageModel
{
    public string PoolName { get; set; } = "";
    public bool WrongPassword { get; set; }

    public async Task<IActionResult> OnGetAsync(string code)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return RedirectToPage("/Index");
        }

        if (access.CanView(HttpContext, pool))
        {
            return RedirectToPage("/Pool/Gallery", new { code = pool.Code });
        }

        PoolName = pool.Name;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string code, string password)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return RedirectToPage("/Index");
        }

        if (!pools.VerifyPassword(pool, password ?? ""))
        {
            PoolName = pool.Name;
            WrongPassword = true;
            return Page();
        }

        access.GrantAccess(HttpContext, pool.Id);
        return RedirectToPage("/Pool/Gallery", new { code = pool.Code });
    }
}
