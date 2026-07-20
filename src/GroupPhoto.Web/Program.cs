using System.Threading.RateLimiting;
using GroupPhoto.Web;
using GroupPhoto.Web.Api;
using GroupPhoto.Web.Data;
using GroupPhoto.Web.Services;
using ImageMagick;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GroupPhotoOptions>(builder.Configuration.GetSection(GroupPhotoOptions.SectionName));
var opts = builder.Configuration.GetSection(GroupPhotoOptions.SectionName).Get<GroupPhotoOptions>() ?? new();

var dataRoot = Path.GetFullPath(opts.DataPath);
Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(Path.Combine(dataRoot, "keys"));

// Cap what the image decoder will attempt, as a backstop against decode bombs
// (ImageRenderer also rejects oversized images up front from the header).
ResourceLimits.Width = (ulong)opts.MaxImageDimension;
ResourceLimits.Height = (ulong)opts.MaxImageDimension;
ResourceLimits.Area = (ulong)opts.MaxImagePixels;
ResourceLimits.Memory = 512UL * 1024 * 1024;

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataRoot, "groupphoto.db")}"));

// Signed cookies must survive container restarts, so keys live on the data volume.
builder.Services.AddDataProtection()
    .SetApplicationName("GroupPhoto")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys")));

builder.Services.AddSingleton<StoragePaths>();
builder.Services.AddSingleton<UploaderIdentity>();
builder.Services.AddSingleton<PoolAccessService>();
builder.Services.AddSingleton<ShareLinkService>();
builder.Services.AddSingleton<ImageRenderer>();
builder.Services.AddSingleton<ZipStreamService>();
builder.Services.AddScoped<PoolService>();
builder.Services.AddScoped<PhotoService>();
builder.Services.AddHostedService<CleanupService>();

builder.Services.AddRazorPages();

// Throttle password-unlock attempts (per client IP, per pool) to slow brute force.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var path = context.Request.Path.Value ?? "";
        var isUnlock = HttpMethods.IsPost(context.Request.Method)
            && path.StartsWith("/p/", StringComparison.Ordinal)
            && path.EndsWith("/unlock", StringComparison.Ordinal);
        if (!isUnlock)
        {
            return RateLimitPartition.GetNoLimiter("none");
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"unlock:{ip}:{path}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, opts.UnlockAttemptsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

// Uploads are sent one file per request from the browser; allow the max file size plus form overhead.
var uploadLimit = opts.MaxFileSizeBytes + 1024 * 1024;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = uploadLimit);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = uploadLimit);

// Correct scheme/host for share links and Secure cookies behind a reverse proxy.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<StoragePaths>().EnsureBaseDirectories();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

app.UseForwardedHeaders();

// Security headers on every response. No sniffing of the user files we serve,
// no framing (clickjacking), and a lean referrer policy. (A full CSP is left
// out: the theme-bootstrap inline script and a couple of inline handlers would
// need nonces; frame-ancestors below still covers clickjacking.)
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Content-Security-Policy"] = "frame-ancestors 'none'";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

app.MapRazorPages();
app.MapPhotoApi();

app.Run();
