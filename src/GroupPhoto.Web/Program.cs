using GroupPhoto.Web;
using GroupPhoto.Web.Api;
using GroupPhoto.Web.Data;
using GroupPhoto.Web.Services;
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapPhotoApi();

app.Run();
