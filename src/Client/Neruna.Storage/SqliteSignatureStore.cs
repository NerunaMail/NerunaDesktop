using Microsoft.EntityFrameworkCore;
using Neruna.Core.Mail;

namespace Neruna.Storage;

public sealed class SqliteSignatureStore(IDbContextFactory<NerunaDbContext> contexts) : ISignatureStore
{
    public async Task<IReadOnlyList<Signature>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Signatures.AsNoTracking().ToListAsync(cancellationToken);
        return rows.Select(r => new Signature(r.Id, r.Name, r.Html, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedUnixMs), r.Source)).ToList();
    }

    public async Task SaveAsync(Signature signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Signatures.FindAsync([signature.Id], cancellationToken);
        if (row is null)
        {
            row = new SignatureEntity { Id = signature.Id, Name = signature.Name, Html = signature.Html, Source = signature.Source };
            db.Signatures.Add(row);
        }

        row.Name = signature.Name;
        row.Html = signature.Html;
        row.Source = signature.Source;
        row.UpdatedUnixMs = signature.UpdatedAt.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Signatures.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}
