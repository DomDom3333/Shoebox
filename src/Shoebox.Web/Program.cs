using System.Threading.RateLimiting;
using Shoebox.Web;
using Shoebox.Web.Api;
using Shoebox.Web.Data;
using Shoebox.Web.Services;
using Shoebox.Web.Services.Encryption;
using ImageMagick;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ShoeboxOptions>(builder.Configuration.GetSection(ShoeboxOptions.SectionName));
var opts = builder.Configuration.GetSection(ShoeboxOptions.SectionName).Get<ShoeboxOptions>() ?? new();

var dataRoot = Path.GetFullPath(opts.DataPath);
Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(Path.Combine(dataRoot, "keys"));

// Resolved before anything opens the data directory: the media files, the database and the
// cookie-signing key ring are all keyed from this, and it must be read (and taken back out of
// the environment) before any child process could inherit it.
var masterKey = MasterKey.Resolve(builder.Configuration);
builder.Services.AddSingleton(masterKey);

// Cap what the image decoder will attempt, as a backstop against decode bombs
// (ImageRenderer also rejects oversized images up front from the header).
ResourceLimits.Width = (ulong)opts.MaxImageDimension;
ResourceLimits.Height = (ulong)opts.MaxImageDimension;
ResourceLimits.Area = (ulong)opts.MaxImagePixels;
ResourceLimits.Memory = 512UL * 1024 * 1024;

var databaseFile = Path.Combine(dataRoot, "shoebox.db");
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(DatabaseEncryption.ConnectionString(databaseFile, masterKey)));

// Signed cookies must survive container restarts, so keys live on the data volume.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Shoebox")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys")));

if (masterKey.IsEnabled)
{
    // Key-ring elements already on disk in the clear keep loading; new ones are written sealed.
    dataProtection.Services.Configure<KeyManagementOptions>(
        o => o.XmlEncryptor = new MasterKeyXmlEncryptor(masterKey));
}

builder.Services.AddSingleton<StoragePaths>();
builder.Services.AddSingleton<PoolKeyRing>();
builder.Services.AddSingleton<FileVault>();
builder.Services.AddSingleton<UploaderIdentity>();
builder.Services.AddSingleton<PoolAccessService>();
builder.Services.AddSingleton<ShareLinkService>();
builder.Services.AddSingleton<ImageRenderer>();
builder.Services.AddSingleton<VideoRenderer>();
builder.Services.AddSingleton<ZipStreamService>();

// One handler per kind of upload: everything that differs between a photo and a video
// lives behind this, so nothing downstream has to care which it got.
builder.Services.AddSingleton<IMediaHandler, PhotoHandler>();
builder.Services.AddSingleton<IMediaHandler, VideoHandler>();
builder.Services.AddSingleton<MediaHandlers>();

builder.Services.AddScoped<PoolService>();
builder.Services.AddScoped<MediaService>();
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
    var paths = scope.ServiceProvider.GetRequiredService<StoragePaths>();
    var startupLog = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Shoebox.Storage");

    paths.EnsureBaseDirectories();

    // Plaintext scratch from an upload that was interrupted by a crash or a restart.
    paths.ClearTempDirectory();

    if (masterKey.IsEnabled)
    {
        startupLog.LogInformation("Storage encryption is on (key from {Source})", masterKey.Source);
    }
    else
    {
        startupLog.LogWarning(
            "Storage encryption is off: uploads and the database are stored in the clear. " +
            "Set {Variable} to a base64 32-byte key to turn it on (openssl rand -base64 32)",
            MasterKey.KeyVariable);
    }

    // Must run before EF opens the database: this is what encrypts an existing plaintext
    // database in place, and what refuses to start on a missing or wrong key.
    DatabaseEncryption.Prepare(paths.DatabaseFile, masterKey, startupLog);

    await scope.ServiceProvider.GetRequiredService<AppDbContext>().UpgradeAsync();
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
app.MapMediaApi();

app.Run();

public partial class Program;
