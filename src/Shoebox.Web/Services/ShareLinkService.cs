using Shoebox.Web.Data;
using Microsoft.Extensions.Options;

namespace Shoebox.Web.Services;

public class ShareLinkService(IOptions<ShoeboxOptions> options)
{
    public string PoolUrl(HttpContext context, Pool pool)
        => $"{BaseUrl(context)}/p/{pool.Code}";

    public string AdminUrl(HttpContext context, Pool pool)
        => $"{BaseUrl(context)}/p/{pool.Code}/admin?key={pool.AdminKey:N}";

    private string BaseUrl(HttpContext context)
    {
        var configured = options.Value.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        var request = context.Request;
        return $"{request.Scheme}://{request.Host}";
    }
}
