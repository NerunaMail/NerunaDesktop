using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Neruna.Core;
using Neruna.Core.Accounts;

namespace Neruna.Storage;

public sealed class SqliteAccountStore(IDbContextFactory<NerunaDbContext> contexts, MessageContentFiles files) : IAccountStore
{
    private static readonly JsonSerializerOptions AliasJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entities = await db.Accounts.AsNoTracking().Include(a => a.Connections).ToListAsync(cancellationToken);

        return entities
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new Account(
                a.Id,
                a.DisplayName,
                a.EmailAddress,
                a.Connections.Select(c => new ServiceConnection(
                    c.Id,
                    c.Kind,
                    c.ProviderId,
                    JsonSerializer.Deserialize<Dictionary<string, string>>(c.SettingsJson) ?? [])).ToList(),
                a.Label,
                a.AliasesJson is null ? null : JsonSerializer.Deserialize<List<MailIdentity>>(a.AliasesJson, AliasJson),
                a.CloudId))
            .ToList();
    }

    public async Task SetOrderAsync(IReadOnlyList<Guid> accountIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entities = await db.Accounts.ToListAsync(cancellationToken);
        var position = accountIds.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);

        // Accounts not mentioned keep their relative order behind the listed ones.
        var ordered = entities.OrderBy(e => position.TryGetValue(e.Id, out var p) ? p : int.MaxValue).ThenBy(e => e.SortOrder).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAccountAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var entity = await db.Accounts.Include(a => a.Connections).FirstOrDefaultAsync(a => a.Id == account.Id, cancellationToken);
        if (entity is null)
        {
            // New accounts go to the end.
            var last = await db.Accounts.Select(a => (int?)a.SortOrder).MaxAsync(cancellationToken);
            entity = new AccountEntity { Id = account.Id, DisplayName = account.DisplayName, SortOrder = (last ?? -1) + 1 };
            db.Accounts.Add(entity);
        }

        entity.DisplayName = account.DisplayName;
        entity.EmailAddress = account.EmailAddress;
        entity.Label = account.Label;
        entity.AliasesJson = account.Aliases is { Count: > 0 } aliases ? JsonSerializer.Serialize(aliases, AliasJson) : null;
        entity.CloudId = account.CloudId;

        // Removing a connection cascades to its cached folders, calendars and address books; its downloaded messages go too.
        var removed = entity.Connections.Where(c => account.Connections.All(n => n.Id != c.Id)).Select(c => c.Id).ToList();
        entity.Connections.RemoveAll(c => removed.Contains(c.Id));
        foreach (var connection in account.Connections)
        {
            var existing = entity.Connections.FirstOrDefault(c => c.Id == connection.Id);
            if (existing is null)
            {
                existing = new ConnectionEntity { Id = connection.Id, ProviderId = connection.ProviderId, SettingsJson = "{}" };
                entity.Connections.Add(existing);
                // It comes with its id already set: without this, EF Core would take it for an existing row (an edited
                // account that gains a calendar) and try to update it.
                db.Entry(existing).State = EntityState.Added;
            }

            existing.Kind = connection.Kind;
            existing.ProviderId = connection.ProviderId;
            existing.SettingsJson = JsonSerializer.Serialize(connection.Settings);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var id in removed)
        {
            files.DeleteConnection(id);
        }
    }

    public async Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entity = await db.Accounts.Include(a => a.Connections).FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        db.Accounts.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var connection in entity.Connections)
        {
            files.DeleteConnection(connection.Id);
        }
    }
}
