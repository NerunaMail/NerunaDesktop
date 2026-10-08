namespace Neruna.Core.Contacts;

[Flags]
public enum ContactProviderCapabilities
{
    None = 0,
    Write = 1,
    ServerSearch = 2,
}

/// <summary>
/// An address book backend (CardDAV, later LDAP, EWS, Neruna global contacts …) bound to one connection.
/// Contacts travel as vCard text.
/// </summary>
public interface IContactProvider : IAsyncDisposable
{
    ContactProviderCapabilities Capabilities { get; }

    Task TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(CancellationToken cancellationToken = default);

    /// <param name="knownVersions">Locally stored contacts: remote id → ETag (null if the backend has none).</param>
    Task<AddressBookSyncResult> SyncAddressBookAsync(AddressBookInfo addressBook, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default);

    /// <summary>Creates (<paramref name="contact"/>.RemoteId empty) or updates a contact, guarded by its ETag.</summary>
    /// <exception cref="NotSupportedException">The provider lacks <see cref="ContactProviderCapabilities.Write"/>.</exception>
    /// <exception cref="RemoteConflictException">The contact was changed or deleted on the server meanwhile.</exception>
    Task<ContactObject> SaveAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default);

    /// <exception cref="NotSupportedException">The provider lacks <see cref="ContactProviderCapabilities.Write"/>.</exception>
    /// <exception cref="RemoteConflictException">The contact was changed on the server meanwhile.</exception>
    Task DeleteAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default);
}

public sealed record AddressBookInfo(
    Guid ConnectionId,
    string RemoteId,
    string Name,
    bool IsReadOnly,
    string? SyncState = null);

/// <param name="VCardData">Complete vCard text; the source of truth.</param>
public sealed record ContactObject(
    string RemoteId,
    string? ETag,
    string VCardData);

public sealed record AddressBookSyncResult(
    string NewSyncState,
    bool IsFullResync,
    IReadOnlyList<ContactObject> AddedOrChanged,
    IReadOnlyList<string> RemovedRemoteIds);
