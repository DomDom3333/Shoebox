using Shoebox.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Shoebox.Web.Services;

/// <summary>
/// Tracks which password-protected pools a browser has unlocked and which pools it
/// administers, via tamper-proof (Data Protection signed) cookies. This is what makes
/// image/download URLs useless to anyone who never entered the password: every file
/// endpoint asks this service before serving a single byte.
/// </summary>
public class PoolAccessService(IDataProtectionProvider dataProtection, IOptions<ShoeboxOptions> options)
{
    private const string AccessCookie = "gp_access";
    private const string AdminCookie = "gp_admin";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Shoebox.PoolAccess.v1");

    private TimeSpan Lifetime => TimeSpan.FromDays(options.Value.CookieLifetimeDays);

    public bool CanView(HttpContext context, Pool pool)
        => !pool.HasPassword || ReadIds(context, AccessCookie).Contains(pool.Id) || IsAdmin(context, pool.Id);

    public bool IsAdmin(HttpContext context, Guid poolId)
        => ReadIds(context, AdminCookie).Contains(poolId);

    public void GrantAccess(HttpContext context, Guid poolId)
        => AddId(context, AccessCookie, poolId);

    public void GrantAdmin(HttpContext context, Guid poolId)
    {
        AddId(context, AdminCookie, poolId);
        AddId(context, AccessCookie, poolId);
    }

    public void RevokeAll(HttpContext context, Guid poolId)
    {
        WriteIds(context, AccessCookie, ReadIds(context, AccessCookie).Where(id => id != poolId).ToHashSet());
        WriteIds(context, AdminCookie, ReadIds(context, AdminCookie).Where(id => id != poolId).ToHashSet());
    }

    private void AddId(HttpContext context, string cookieName, Guid poolId)
    {
        var ids = ReadIds(context, cookieName);
        if (ids.Add(poolId))
        {
            WriteIds(context, cookieName, ids);
        }
    }

    private HashSet<Guid> ReadIds(HttpContext context, string cookieName)
    {
        // A grant earlier in the same request must be visible immediately, before the
        // response cookie makes it back to the browser.
        if (context.Items[ItemsKey(cookieName)] is HashSet<Guid> cached)
        {
            return cached;
        }

        var ids = new HashSet<Guid>();
        if (context.Request.Cookies.TryGetValue(cookieName, out var raw))
        {
            try
            {
                foreach (var part in _protector.Unprotect(raw).Split(','))
                {
                    if (Guid.TryParse(part, out var id))
                    {
                        ids.Add(id);
                    }
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Tampered or from a rotated key ring; treat as no access.
            }
        }

        context.Items[ItemsKey(cookieName)] = ids;
        return ids;
    }

    private void WriteIds(HttpContext context, string cookieName, HashSet<Guid> ids)
    {
        context.Items[ItemsKey(cookieName)] = ids;
        if (ids.Count == 0)
        {
            context.Response.Cookies.Delete(cookieName);
            return;
        }

        var payload = _protector.Protect(string.Join(',', ids));
        context.Response.Cookies.Append(cookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            MaxAge = Lifetime,
            IsEssential = true,
        });
    }

    private static string ItemsKey(string cookieName) => "gp_ids_" + cookieName;
}
