using Microsoft.EntityFrameworkCore;
using Neruna.Core;
using Neruna.Core.Contacts;

namespace Neruna.Storage;

public sealed class SqliteContactStore(IDbContextFactory<NerunaDbContext> contexts) : IContactStore
{
    public async Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(Guid? connectionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = db.AddressBooks.AsNoTracking();
        if (connectionId is { } id)
        {
            query = query.Where(b => b.ConnectionId == id);
        }

        var books = await query.ToListAsync(cancellationToken);
        return books.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<AddressBookInfo>> MergeAddressBooksAsync(Guid connectionId, IReadOnlyList<AddressBookInfo> remoteAddressBooks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteAddressBooks);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var existing = await db.AddressBooks.Where(b => b.ConnectionId == connectionId).ToDictionaryAsync(b => b.RemoteId, StringComparer.Ordinal, cancellationToken);
        var remoteIds = remoteAddressBooks.Select(b => b.RemoteId).ToHashSet(StringComparer.Ordinal);
        db.AddressBooks.RemoveRange(existing.Values.Where(b => !remoteIds.Contains(b.RemoteId)));

        foreach (var remote in remoteAddressBooks)
        {
            if (!existing.TryGetValue(remote.RemoteId, out var entity))
            {
                entity = new AddressBookEntity { ConnectionId = connectionId, RemoteId = remote.RemoteId, Name = remote.Name };
                db.AddressBooks.Add(entity);
            }

            entity.Name = remote.Name;
            entity.IsReadOnly = remote.IsReadOnly;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAddressBooksAsync(connectionId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetContactVersionsAsync(Guid connectionId, string addressBookRemoteId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var bookId = await AddressBookIdAsync(db, connectionId, addressBookRemoteId, cancellationToken);
        return await db.Contacts.Where(c => c.AddressBookId == bookId)
            .ToDictionaryAsync(c => c.RemoteId, c => c.ETag, StringComparer.Ordinal, cancellationToken);
    }

    public async Task UpsertContactAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        ArgumentNullException.ThrowIfNull(contact);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var bookId = await AddressBookIdAsync(db, addressBook.ConnectionId, addressBook.RemoteId, cancellationToken);

        var row = await db.Contacts.FirstOrDefaultAsync(c => c.AddressBookId == bookId && c.RemoteId == contact.RemoteId, cancellationToken);
        if (row is null)
        {
            row = new ContactEntity { AddressBookId = bookId, RemoteId = contact.RemoteId, Data = contact.VCardData };
            db.Contacts.Add(row);
        }

        row.ETag = contact.ETag;
        row.Data = contact.VCardData;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteContactAsync(AddressBookInfo addressBook, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var bookId = await AddressBookIdAsync(db, addressBook.ConnectionId, addressBook.RemoteId, cancellationToken);
        await db.Contacts.Where(c => c.AddressBookId == bookId && c.RemoteId == remoteId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ApplySyncResultAsync(AddressBookInfo addressBook, AddressBookSyncResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        ArgumentNullException.ThrowIfNull(result);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var entity = await db.AddressBooks.SingleAsync(b => b.ConnectionId == addressBook.ConnectionId && b.RemoteId == addressBook.RemoteId, cancellationToken);
        if (result.IsFullResync)
        {
            await db.Contacts.Where(c => c.AddressBookId == entity.Id).ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            var removed = result.RemovedRemoteIds.ToList();
            await db.Contacts.Where(c => c.AddressBookId == entity.Id && removed.Contains(c.RemoteId)).ExecuteDeleteAsync(cancellationToken);
        }

        var changedIds = result.AddedOrChanged.Select(c => c.RemoteId).ToList();
        var existing = await db.Contacts
            .Where(c => c.AddressBookId == entity.Id && changedIds.Contains(c.RemoteId))
            .ToDictionaryAsync(c => c.RemoteId, StringComparer.Ordinal, cancellationToken);

        foreach (var contact in result.AddedOrChanged)
        {
            if (!existing.TryGetValue(contact.RemoteId, out var row))
            {
                row = new ContactEntity { AddressBookId = entity.Id, RemoteId = contact.RemoteId, Data = contact.VCardData };
                db.Contacts.Add(row);
                existing[contact.RemoteId] = row;
            }

            row.ETag = contact.ETag;
            row.Data = contact.VCardData;
        }

        entity.SyncState = result.NewSyncState;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContactObject>> GetContactsAsync(Guid connectionId, string addressBookRemoteId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var bookId = await AddressBookIdAsync(db, connectionId, addressBookRemoteId, cancellationToken);
        return await db.Contacts.AsNoTracking()
            .Where(c => c.AddressBookId == bookId)
            .Select(c => new ContactObject(c.RemoteId, c.ETag, c.Data))
            .ToListAsync(cancellationToken);
    }

    private static async Task<long> AddressBookIdAsync(NerunaDbContext db, Guid connectionId, string remoteId, CancellationToken cancellationToken) =>
        await db.AddressBooks.Where(b => b.ConnectionId == connectionId && b.RemoteId == remoteId).Select(b => (long?)b.Id).SingleOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException($"Address book '{remoteId}' is not known.");

    private static AddressBookInfo ToModel(AddressBookEntity b) => new(b.ConnectionId, b.RemoteId, b.Name, b.IsReadOnly, b.SyncState);
}
