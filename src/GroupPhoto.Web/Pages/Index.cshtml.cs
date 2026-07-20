using GroupPhoto.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GroupPhoto.Web.Pages;

public class IndexModel(PoolService pools) : PageModel
{
    public string Code { get; set; } = "";
    public bool CodeNotFound { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostJoinAsync(string code)
    {
        var pool = await pools.FindByCodeAsync(code ?? "");
        if (pool is null)
        {
            Code = code ?? "";
            CodeNotFound = true;
            return Page();
        }

        return RedirectToPage("/Pool/Gallery", new { code = pool.Code });
    }
}
