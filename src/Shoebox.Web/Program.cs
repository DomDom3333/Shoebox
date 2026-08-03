using System.Threading.RateLimiting;
using Shoebox.Web;
using Shoebox.Web.Api;
using Shoebox.Web.Data;
using Shoebox.Web.Services;
using ImageMagick;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ShoeboxOptions>(builder.Configuration.GetSection(ShoeboxOptions.SectionName));
var opts = builder.Configuration.GetSection(ShoeboxOptions.SectionName).Get<ShoeboxOptions>() ?? new();

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
    o.UseSqlite($"Data Source={Path.Combine(dataRoot, "shoebox.db")}"));

// Signed cookies must survive container restarts, so keys live on the data volume.
builder.Services.AddDataProtection()
    .SetApplicationName("Shoebox")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys")));

builder.Services.AddSingleton<StoragePaths>();
builder.Services.AddSingleton<UploaderIdentity>();
builder.Services.AddSingleton<PoolAccessService>();
builder.Services.AddSingleton<ShareLinkService>();
builder.Services.AddSingleton<ImageRenderer>();
builder.Services.AddSingleton<VideoRenderer>();
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

// Uploads are sent one file per request from the browser; allow the largest accepted file
// (videos are allowed to be bigger than photos) plus form overhead.
var uploadLimit = Math.Max(opts.MaxFileSizeBytes, opts.MaxVideoFileSizeBytes) + 1024 * 1024;
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
    var appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await appDb.Database.EnsureCreatedAsync();

    // EnsureCreated builds the full schema only for a brand-new database; it never
    // alters one that already has tables. This project has no migrations, so tables
    // added after the first release are created here, idempotently, for existing DBs.
    await appDb.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS "Likes" (
            "PhotoId" TEXT NOT NULL,
            "UploaderUid" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            CONSTRAINT "PK_Likes" PRIMARY KEY ("PhotoId", "UploaderUid"),
            CONSTRAINT "FK_Likes_Photos_PhotoId" FOREIGN KEY ("PhotoId")
                REFERENCES "Photos" ("Id") ON DELETE CASCADE
        );
        """);
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

public partial class Program;
