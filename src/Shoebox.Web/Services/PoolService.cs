using System.Security.Cryptography;
using Shoebox.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Shoebox.Web.Services;

public class PoolService(AppDbContext db, StoragePaths paths)
{
    // No 0/O/1/I/L to keep codes easy to read aloud and type from a phone.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 8;

    public async Task<Pool> CreateAsync(string name, string? description, string? password, DateTime? expiresAt)
    {
        var pool = new Pool
        {
            Id = Guid.NewGuid(),
            Code = await GenerateUniqueCodeAsync(),
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            PasswordHash = string.IsNullOrEmpty(password) ? null : PasswordHash.Hash(password),
            AdminKey = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
        };

        db.Pools.Add(pool);
        await db.SaveChangesAsync();

        Directory.CreateDirectory(paths.OriginalsDirectory(pool.Id));
        Directory.CreateDirectory(paths.ThumbsDirectory(pool.Id));
        return pool;
    }

    public Task<Pool?> FindByCodeAsync(string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return db.Pools.FirstOrDefaultAsync(p => p.Code == normalized);
    }

    public bool VerifyPassword(Pool pool, string password)
        => pool.PasswordHash is not null && PasswordHash.Verify(password, pool.PasswordHash);

    public async Task UpdateAsync(Pool pool, string name, string? description, DateTime? expiresAt,
        bool changePassword, string? newPassword)
    {
        pool.Name = name.Trim();
        pool.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        pool.ExpiresAt = expiresAt;
        if (changePassword)
        {
            pool.PasswordHash = string.IsNullOrEmpty(newPassword) ? null : PasswordHash.Hash(newPassword);
        }

        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Pool pool)
    {
        db.Pools.Remove(pool);
        await db.SaveChangesAsync();
        DeletePoolFiles(pool.Id);
    }

    public void DeletePoolFiles(Guid poolId)
    {
        var dir = paths.PoolDirectory(poolId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private async Task<string> GenerateUniqueCodeAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var chars = new char[CodeLength];
            for (var i = 0; i < CodeLength; i++)
            {
                chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            }

            var code = new string(chars);
            if (!await db.Pools.AnyAsync(p => p.Code == code))
            {
                return code;
            }
        }

        throw new InvalidOperationException("Could not generate a unique pool code.");
    }
}
