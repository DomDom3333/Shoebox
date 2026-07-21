using Microsoft.Extensions.Options;

namespace Shoebox.Web.Services;

/// <summary>
/// Account-free identity: a random GUID in a long-lived cookie identifies the browser,
/// and a second cookie remembers the display name last used for uploading.
/// </summary>
public class UploaderIdentity(IOptions<ShoeboxOptions> options)
{
    private const string UidCookie = "gp_uid";
    private const string NameCookie = "gp_name";

    private TimeSpan Lifetime => TimeSpan.FromDays(options.Value.CookieLifetimeDays);

    public Guid GetOrCreateUid(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(UidCookie, out var raw) && Guid.TryParse(raw, out var uid))
        {
            return uid;
        }

        uid = Guid.NewGuid();
        context.Response.Cookies.Append(UidCookie, uid.ToString(), CookieOptionsFor(context));
        return uid;
    }

    public string? GetName(HttpContext context)
        => context.Request.Cookies.TryGetValue(NameCookie, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : null;

    public void RememberName(HttpContext context, string name)
        => context.Response.Cookies.Append(NameCookie, name.Trim(), CookieOptionsFor(context));

    private CookieOptions CookieOptionsFor(HttpContext context) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        MaxAge = Lifetime,
        IsEssential = true,
    };
}
