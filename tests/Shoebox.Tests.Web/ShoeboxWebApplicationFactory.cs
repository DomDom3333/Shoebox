using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Shoebox.Tests.Web;

public sealed class ShoeboxWebApplicationFactory : WebApplicationFactory<Program>
{
    public ShoeboxWebApplicationFactory()
    {
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
