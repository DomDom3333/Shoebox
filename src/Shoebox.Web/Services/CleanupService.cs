using Shoebox.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Shoebox.Web.Services;

/// <summary>Deletes expired pools (DB rows and files) once a day.</summary>
public class CleanupService(IServiceScopeFactory scopeFactory, ILogger<CleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Pool cleanup run failed");
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private async Task CleanupOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pools = scope.ServiceProvider.GetRequiredService<PoolService>();

        var now = DateTime.UtcNow;
        var expired = await db.Pools.Where(p => p.ExpiresAt != null && p.ExpiresAt < now).ToListAsync(ct);
        foreach (var pool in expired)
        {
            logger.LogInformation("Deleting expired pool {Code} ({Name})", pool.Code, pool.Name);
            db.Pools.Remove(pool);
            await db.SaveChangesAsync(ct);
            pools.DeletePoolFiles(pool.Id);
        }
    }
}
