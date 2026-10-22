using Neruna.Core.Accounts;
using Neruna.Core.Contacts;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Special;

/// <summary>
/// An address book that lives on this device only ("Verwendete Adressen"): there is no server, the contacts are what
/// Neruna stores locally anyway. Syncing changes nothing; saving gives a new contact its id.
/// </summary>
public sealed class LocalAddressBookProvider(Guid connectionId) : IContactProvider
{
    public ContactProviderCapabilities Capabilities => ContactProviderCapabilities.Write;

    public Task TestConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AddressBookInfo>>([new AddressBookInfo(connectionId, AddressCollector.AddressBookId, T("Verwendete Adressen"), false)]);

    public Task<AddressBookSyncResult> SyncAddressBookAsync(AddressBookInfo addressBook, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AddressBookSyncResult(addressBook?.SyncState ?? "local", false, [], []));

    public Task<ContactObject> SaveAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return Task.FromResult(string.IsNullOrEmpty(contact.RemoteId) ? contact with { RemoteId = Guid.NewGuid().ToString("N") + ".vcf" } : contact);
    }

    public Task DeleteAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LocalAddressBookProviderFactory : IProviderFactory<IContactProvider>
{
    public string ProviderId => ProviderIds.LocalContacts;

    public string DisplayName => T("Verwendete Adressen");

    public IContactProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new LocalAddressBookProvider(connection.Id);
    }
}
