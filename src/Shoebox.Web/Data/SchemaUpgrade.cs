using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Shoebox.Web.Data;

public static class SchemaUpgrade
{
    /// <summary>
    /// Every database built before this project had migrations was created by EnsureCreated, so
    /// it holds the right tables but no migrations history. Re-running the first migration
    /// against it would fail on tables that are already there, so it is recorded as applied
    /// instead, and everything after it migrates normally.
    /// </summary>
    private const string LegacyTable = "Pools";

    /// <summary>
    /// The Likes table used to be created by hand at startup, because it arrived after the first
    /// release. A database old enough to predate it needs it before the baseline can honestly be
    /// called applied.
    /// </summary>
    private const string LegacyLikesTable =
        """
        CREATE TABLE IF NOT EXISTS "Likes" (
            "PhotoId" TEXT NOT NULL,
            "UploaderUid" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            CONSTRAINT "PK_Likes" PRIMARY KEY ("PhotoId", "UploaderUid"),
            CONSTRAINT "FK_Likes_Photos_PhotoId" FOREIGN KEY ("PhotoId")
                REFERENCES "Photos" ("Id") ON DELETE CASCADE
        );
        """;

    /// <summary>Brings the database schema up to date, creating it if it isn't there yet.</summary>
    public static async Task UpgradeAsync(this AppDbContext db, CancellationToken ct = default)
    {
        var history = db.GetService<IHistoryRepository>();
        if (!await history.ExistsAsync(ct) && await TableExistsAsync(db, LegacyTable, ct))
        {
            await db.Database.ExecuteSqlRawAsync(LegacyLikesTable, ct);
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            await db.Database.ExecuteSqlRawAsync(
                history.GetInsertScript(new HistoryRow(db.Database.GetMigrations().First(), ProductInfo.GetVersion())),
                ct);
        }

        await db.Database.MigrateAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string name, CancellationToken ct)
    {
        var matches = await db.Database
            .SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = {name}")
            .ToListAsync(ct);
        return matches.Count > 0;
    }
}
