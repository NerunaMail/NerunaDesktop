using Microsoft.EntityFrameworkCore;
using Neruna.Core.Security;

namespace Neruna.Storage;

public sealed class SqlitePgpKeyStore(IDbContextFactory<NerunaDbContext> contexts) : IPgpKeyStore
{
    public async Task<IReadOnlyList<StoredPgpKey>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.PgpKeys.AsNoTracking().ToListAsync(cancellationToken);
        return rows.Select(r => new StoredPgpKey(r.Fingerprint, r.PublicKeyRing, r.SecretKeyRing, r.Source, r.Verified, DateTimeOffset.FromUnixTimeMilliseconds(r.AddedUnixMs))).ToList();
    }

    public async Task SaveAsync(StoredPgpKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.PgpKeys.FindAsync([key.Fingerprint], cancellationToken);
        if (row is null)
        {
            db.PgpKeys.Add(new PgpKeyEntity
            {
                Fingerprint = key.Fingerprint,
                PublicKeyRing = key.PublicKeyRing,
                SecretKeyRing = key.SecretKeyRing,
                Source = key.Source,
                Verified = key.Verified,
                AddedUnixMs = key.AddedAt.ToUnixTimeMilliseconds(),
            });
        }
        else
        {
            row.PublicKeyRing = key.PublicKeyRing;
            // Never lose a secret key because the same key is seen again without it.
            row.SecretKeyRing = key.SecretKeyRing ?? row.SecretKeyRing;
            row.Source = key.Source;
            row.Verified = key.Verified;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.PgpKeys.Where(k => k.Fingerprint == fingerprint).ExecuteDeleteAsync(cancellationToken);
    }
}
