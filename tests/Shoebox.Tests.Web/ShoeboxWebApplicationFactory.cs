using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Shoebox.Tests.Web;

public sealed class ShoeboxWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string> settings;

    /// <param name="settings">Configuration overrides for this instance.</param>
    public ShoeboxWebApplicationFactory(IReadOnlyDictionary<string, string>? settings = null)
    {
        this.settings = settings ?? new Dictionary<string, string>();
        DataPath = Path.Combine(
            Path.GetTempPath(),
            $"shoebox-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DataPath);
    }

    public string DataPath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Shoebox:DataPath", DataPath);
        builder.UseSetting("Shoebox:CookieLifetimeDays", "1");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataPath))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(DataPath, recursive: true);
        }
    }
}
