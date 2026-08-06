using System.Collections.Concurrent;
using System.Security.Cryptography;
using Shoebox.Web.Data;

namespace Shoebox.Web.Services.Encryption;

/// <summary>
/// Every box gets its own random data key, stored in the database wrapped under the master key.
///
/// Two reasons it works this way rather than encrypting files with the master key directly.
/// Rotating the master key only has to re-wrap one small blob per box instead of rewriting every
/// photo. And deleting a box's wrapped key makes its files unrecoverable on their own, so an
/// expired box is genuinely gone even if its bytes survive in an old backup.
/// </summary>
public sealed class PoolKeyRing(MasterKey masterKey)
{
    private const int DataKeySize = 32;

    private readonly ConcurrentDictionary<Guid, byte[]> unwrapped = new();

    /// <summary>
    /// Gives <paramref name="pool"/> a data key if it doesn't have one yet, returning true when
    /// it assigned one so the caller knows to save. Boxes created before encryption was switched
    /// on land here on their first upload; their existing files stay readable as plaintext.
    /// </summary>
    public bool EnsureKey(Pool pool)
    {
        if (!masterKey.IsEnabled || pool.WrappedKey is not null)
        {
            return false;
        }

        // GetOrAdd, not a plain assignment: two uploads arriving together at a box that predates
        // encryption would otherwise generate competing keys, and whichever save landed second
        // would leave the other's files permanently unreadable. Sharing one key means both
        // wrappings unwrap to the same thing and it doesn't matter which save wins.
        var dataKey = unwrapped.GetOrAdd(pool.Id, static _ => RandomNumberGenerator.GetBytes(DataKeySize));
        pool.WrappedKey = masterKey.Wrap(dataKey, pool.Id.ToByteArray());
        return true;
    }

    /// <summary>
    /// The pool's data key, or null when the box has none — which means everything in it was
    /// stored in the clear and should be read back that way.
    /// </summary>
    public byte[]? DataKey(Pool pool)
    {
        if (!masterKey.IsEnabled || pool.WrappedKey is null)
        {
            return null;
        }

        return unwrapped.GetOrAdd(pool.Id, static (_, state) =>
        {
            try
            {
                return state.Key.Unwrap(state.Wrapped, state.Id.ToByteArray());
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    $"The data key for box {state.Id} could not be unwrapped with the configured " +
                    $"{MasterKey.KeyVariable}. This is the wrong key for this data.", ex);
            }
        }, (Key: masterKey, Wrapped: pool.WrappedKey, Id: pool.Id));
    }

    /// <summary>
    /// Drops a deleted box's key from the cache. Deliberately not zeroed: an in-flight upload or
    /// download may still be holding the same array, and wiping it underneath them would corrupt
    /// a write. The row is gone, which is what actually makes the box unrecoverable.
    /// </summary>
    public void Forget(Guid poolId) => unwrapped.TryRemove(poolId, out _);
}
