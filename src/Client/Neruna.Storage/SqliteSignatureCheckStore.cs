using Microsoft.EntityFrameworkCore;
using Neruna.Core.Security;

namespace Neruna.Storage;

public sealed class SqliteSignatureCheckStore(IDbContextFactory<NerunaDbContext> contexts) : ISignatureCheckStore
{
    public async Task<StoredSignatureCheck?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.SignatureChecks.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, cancellationToken);
        return row is null ? null : new StoredSignatureCheck(row.Key, row.Epoch, DateTimeOffset.FromUnixTimeMilliseconds(row.CheckedUnixMs), row.Json);
    }

    public async Task SaveAsync(StoredSignatureCheck check, DateTimeOffset dropBefore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var limit = dropBefore.ToUnixTimeMilliseconds();
        await db.SignatureChecks.Where(c => c.CheckedUnixMs < limit || c.Key == check.Key).ExecuteDeleteAsync(cancellationToken);
        db.SignatureChecks.Add(new SignatureCheckEntity
        {
            Key = check.Key,
            Epoch = check.Epoch,
            CheckedUnixMs = check.CheckedAt.ToUnixTimeMilliseconds(),
            Json = check.Json,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same mail checked twice at once (opened and prepared ahead): the other result is just as good.
        }
    }
}
