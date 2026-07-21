using Shoebox.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Shoebox.Web.Pages;

public class CreateModel(PoolService pools, PoolAccessService access, IOptions<ShoeboxOptions> options) : PageModel
{
    public (int Days, string Text)[] ExpiryChoices { get; } =
    [
        (0, "Never"),
        (30, "1 month"),
        (90, "3 months"),
        (180, "6 months"),
        (365, "1 year"),
    ];

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int SelectedExpiryDays { get; set; }
    public string? Error { get; set; }

    public void OnGet()
    {
        SelectedExpiryDays = options.Value.DefaultExpiryDays;
    }

    public async Task<IActionResult> OnPostAsync(string name, string? description, string? password, int expiryDays)
    {
        Name = name?.Trim() ?? "";
        Description = description ?? "";
        SelectedExpiryDays = expiryDays;

        if (Name.Length is 0 or > 120)
        {
            Error = "Please give the pool a name (up to 120 characters).";
            return Page();
        }

        var expiresAt = expiryDays > 0 ? DateTime.UtcNow.AddDays(expiryDays) : (DateTime?)null;
        var pool = await pools.CreateAsync(Name, description, password, expiresAt);

        // The creator's browser is the pool admin from the start.
        access.GrantAdmin(HttpContext, pool.Id);

        return RedirectToPage("/Pool/Admin", new { code = pool.Code, created = 1 });
    }
}
