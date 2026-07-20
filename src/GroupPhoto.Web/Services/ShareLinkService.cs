using GroupPhoto.Web.Data;
using Microsoft.Extensions.Options;

namespace GroupPhoto.Web.Services;

public class ShareLinkService(IOptions<GroupPhotoOptions> options)
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
