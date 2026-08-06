using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Shoebox.Tests.Web;

public sealed class ShoeboxWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly bool ownsDataPath;

    /// <param name="encryptionKey">Base64 32-byte key, or null to run with encryption off.</param>
    /// <param name="dataPath">
    /// An existing data directory to reuse, for tests that restart the app over the same volume.
    /// The factory only deletes a directory it created itself.
    /// </param>
    public ShoeboxWebApplicationFactory(string? encryptionKey = null, string? dataPath = null)
    {
        EncryptionKey = encryptionKey;
        ownsDataPath = dataPath is null;
        DataPath = dataPath ?? Path.Combine(
            Path.GetTempPath(),
            $"shoebox-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DataPath);
    }

    public string DataPath { get; }

    public string? EncryptionKey { get; }

    /// <summary>A fresh key, as an operator would generate with <c>openssl rand -base64 32</c>.</summary>
    public static string NewKey() => Convert.ToBase64String(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Shoebox:DataPath", DataPath);
        builder.UseSetting("Shoebox:CookieLifetimeDays", "1");
        if (EncryptionKey is not null)
        {
            builder.UseSetting("Shoebox:EncryptionKey", EncryptionKey);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            if (ownsDataPath && Directory.Exists(DataPath))
            {
                Directory.Delete(DataPath, recursive: true);
            }
        }
    }
}
