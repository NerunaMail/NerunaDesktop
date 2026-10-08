using Microsoft.EntityFrameworkCore;
using Neruna.Core.Calendar;

namespace Neruna.Storage;

public sealed class SqliteReminderStore(IDbContextFactory<NerunaDbContext> contexts) : IReminderStore
{
    public async Task<IReadOnlyDictionary<string, ReminderState>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.ReminderStates.AsNoTracking().ToListAsync(cancellationToken);
        return rows.ToDictionary(
            r => r.Key,
            r => new ReminderState(
                r.Key,
                r.SnoozedUntilUnixMs is { } snoozed ? DateTimeOffset.FromUnixTimeMilliseconds(snoozed) : null,
                r.Dismissed,
                DateTimeOffset.FromUnixTimeMilliseconds(r.ExpiresUnixMs)),
            StringComparer.Ordinal);
    }

    public async Task SetAsync(ReminderState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.ReminderStates.FindAsync([state.Key], cancellationToken);
        if (row is null)
        {
            row = new ReminderStateEntity { Key = state.Key };
            db.ReminderStates.Add(row);
        }

        row.SnoozedUntilUnixMs = state.SnoozedUntil?.ToUnixTimeMilliseconds();
        row.Dismissed = state.Dismissed;
        row.ExpiresUnixMs = state.Expires.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var limit = now.ToUnixTimeMilliseconds();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.ReminderStates.Where(r => r.ExpiresUnixMs < limit).ExecuteDeleteAsync(cancellationToken);
    }
}
