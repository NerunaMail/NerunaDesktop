using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;

namespace Neruna.Core.Contacts;

public sealed record ContactEntry(AddressBookInfo AddressBook, string RemoteId, ContactCard Card);

/// <summary>An address book offered by the server, for choosing which ones Neruna shows and synchronizes.</summary>
/// <param name="IsSelected">Currently shown.</param>
/// <param name="IsNew">Appeared since the user last chose.</param>
public sealed record AddressBookCandidate(AddressBookInfo AddressBook, bool IsSelected, bool IsNew);

/// <summary>A contacts connection (CardDAV …) with the account it belongs to.</summary>
public sealed record AddressBookSource(Account Account, ServiceConnection Connection);

/// <summary>A group member with its contact (if found) and the address that this group uses for it.</summary>
public sealed record ResolvedMember(GroupMember Member, ContactEntry? Contact, string? Address)
{
    public string DisplayName => Contact?.Card.DisplayName ?? Address ?? Member.ContactUid ?? "?";

    /// <summary>"Name &lt;address&gt;" for the To field, or null if the member has no address.</summary>
    public string? Recipient => Address is null ? null
        : Contact is null ? Address
        : $"\"{Contact.Card.DisplayName.Replace("\"", string.Empty, StringComparison.Ordinal)}\" <{Address}>";
}

/// <summary>
/// The UI's single entry point for contacts, independent of the backend (CardDAV, later LDAP, EWS, Neruna global contacts).
/// </summary>
public sealed class ContactController(
    IAccountStore accounts,
    IContactStore store,
    ProviderRegistry providers,
    ISettingsStore settings,
    ILogger<ContactController> logger)
{
    public Task<SyncReport> SyncAllAsync(CancellationToken cancellationToken = default) =>
        SyncReport.ForEachConnectionAsync(accounts, ServiceKind.Contacts, SyncConnectionAsync, logger, cancellationToken);

    public async Task SyncConnectionAsync(ServiceConnection connection, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateContacts(connection);

        var remote = await provider.GetAddressBooksAsync(cancellationToken);

        // Without an explicit choice every address book is shown (and new ones appear automatically).
        if (await GetIdsAsync(SelectedKey(connection.Id), cancellationToken) is { } selected)
        {
            remote = remote.Where(b => selected.Contains(b.RemoteId)).ToList();
        }

        foreach (var book in await store.MergeAddressBooksAsync(connection.Id, remote, cancellationToken))
        {
            var known = await store.GetContactVersionsAsync(connection.Id, book.RemoteId, cancellationToken);
            var result = await provider.SyncAddressBookAsync(book, known, cancellationToken);
            await store.ApplySyncResultAsync(book, result, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AddressBookSource>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
        (await accounts.GetAccountsAsync(cancellationToken))
            .SelectMany(a => a.ConnectionsOf(ServiceKind.Contacts).Select(c => new AddressBookSource(a, c)))
            .ToList();

    /// <summary>Asks the server again which address books exist (own, shared, global) and which of them are shown.</summary>
    public async Task<IReadOnlyList<AddressBookCandidate>> DiscoverAsync(ServiceConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var provider = providers.CreateContacts(connection);
        var remote = await provider.GetAddressBooksAsync(cancellationToken);
        var local = (await store.GetAddressBooksAsync(connection.Id, cancellationToken)).Select(b => b.RemoteId).ToHashSet(StringComparer.Ordinal);
        var known = await GetIdsAsync(KnownKey(connection.Id), cancellationToken);
        return remote
            .Select(b => new AddressBookCandidate(b, local.Contains(b.RemoteId), known is null ? !local.Contains(b.RemoteId) : !known.Contains(b.RemoteId)))
            .OrderBy(c => c.AddressBook.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Shows exactly these address books of the connection (others are removed locally, not on the server).</summary>
    /// <param name="offered">All address books the user saw; later additions on the server count as new.</param>
    public async Task SetSelectionAsync(ServiceConnection connection, IReadOnlyCollection<string> selected, IReadOnlyCollection<string> offered, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await settings.SetAsync(SelectedKey(connection.Id), JsonSerializer.Serialize(selected), cancellationToken);
        await settings.SetAsync(KnownKey(connection.Id), JsonSerializer.Serialize(offered), cancellationToken);
        await SyncConnectionAsync(connection, cancellationToken);
    }

    private async Task<HashSet<string>?> GetIdsAsync(string key, CancellationToken cancellationToken)
    {
        if (await settings.GetAsync(key, cancellationToken) is not { Length: > 0 } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?.ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SelectedKey(Guid connectionId) => $"addressbooks.selected.{connectionId:N}";

    private static string KnownKey(Guid connectionId) => $"addressbooks.known.{connectionId:N}";

    public Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(CancellationToken cancellationToken = default) =>
        store.GetAddressBooksAsync(null, cancellationToken);

    public async Task<ContactObject?> GetContactAsync(AddressBookInfo addressBook, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        return (await store.GetContactsAsync(addressBook.ConnectionId, addressBook.RemoteId, cancellationToken)).FirstOrDefault(c => c.RemoteId == remoteId);
    }

    /// <summary>Creates a contact in <paramref name="target"/> or updates/moves the one at <paramref name="existing"/>.</summary>
    /// <exception cref="RemoteConflictException">Someone changed the contact on the server; sync and retry.</exception>
    public async Task<ContactObject> SaveContactAsync(
        AddressBookInfo target,
        ContactDraft draft,
        (AddressBookInfo AddressBook, string RemoteId)? existing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return await SaveCardAsync(target, draft.ToVCard, existing, cancellationToken);
    }

    /// <summary>Creates a group in <paramref name="target"/> or updates/moves the one at <paramref name="existing"/>.</summary>
    /// <exception cref="RemoteConflictException">Someone changed the group on the server; sync and retry.</exception>
    public Task<ContactObject> SaveGroupAsync(
        AddressBookInfo target,
        GroupDraft draft,
        (AddressBookInfo AddressBook, string RemoteId)? existing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return SaveCardAsync(target, draft.ToVCard, existing, cancellationToken);
    }

    /// <summary>
    /// The members of a group with their contacts (looked up by UID in all address books) and the address this group
    /// uses: the one chosen for the member, otherwise the contact's first address.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedMember>> ResolveMembersAsync(ContactCard group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        var byUid = new Dictionary<string, ContactEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await SearchAsync(null, cancellationToken))
        {
            byUid.TryAdd(entry.Card.MemberUid, entry);
        }

        return group.Members.Select(member =>
        {
            if (member.ContactUid is null)
            {
                return new ResolvedMember(member, null, member.Email);
            }

            var contact = byUid.GetValueOrDefault(member.ContactUid);
            var chosen = member.Email is { } email && contact?.Card.EmailAddresses.Contains(email, StringComparer.OrdinalIgnoreCase) != false ? email : null;
            return new ResolvedMember(member, contact, chosen ?? contact?.Card.EmailAddresses.FirstOrDefault());
        }).ToList();
    }

    private async Task<ContactObject> SaveCardAsync(
        AddressBookInfo target,
        Func<string?, string> build,
        (AddressBookInfo AddressBook, string RemoteId)? existing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var current = existing is { } e ? await GetContactAsync(e.AddressBook, e.RemoteId, cancellationToken) : null;
        var data = build(current?.VCardData);
        var moving = existing is { } ex && (ex.AddressBook.ConnectionId != target.ConnectionId || ex.AddressBook.RemoteId != target.RemoteId);
        var toSave = moving || current is null ? new ContactObject(string.Empty, null, data) : current with { VCardData = data };

        ContactObject saved;
        await using (var provider = providers.CreateContacts(await ConnectionAsync(target.ConnectionId, cancellationToken)))
        {
            saved = await provider.SaveAsync(target, toSave, cancellationToken);
        }

        await store.UpsertContactAsync(target, saved, cancellationToken);

        if (moving && current is not null)
        {
            await DeleteContactAsync(existing!.Value.AddressBook, current.RemoteId, cancellationToken);
        }

        return saved;
    }

    /// <exception cref="RemoteConflictException">Someone changed the contact on the server; sync and retry.</exception>
    public async Task DeleteContactAsync(AddressBookInfo addressBook, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        var current = await GetContactAsync(addressBook, remoteId, cancellationToken);
        if (current is null)
        {
            return;
        }

        await using (var provider = providers.CreateContacts(await ConnectionAsync(addressBook.ConnectionId, cancellationToken)))
        {
            await provider.DeleteAsync(addressBook, current, cancellationToken);
        }

        await store.DeleteContactAsync(addressBook, remoteId, cancellationToken);
    }

    /// <summary>Contacts across all address books whose name, organization or email contains <paramref name="query"/>.</summary>
    public async Task<IReadOnlyList<ContactEntry>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        var result = new List<ContactEntry>();
        foreach (var book in await store.GetAddressBooksAsync(null, cancellationToken))
        {
            foreach (var contact in await store.GetContactsAsync(book.ConnectionId, book.RemoteId, cancellationToken))
            {
                var card = VCardReader.Read(contact.VCardData);
                if (Matches(card, query))
                {
                    result.Add(new ContactEntry(book, contact.RemoteId, card));
                }
            }
        }

        result.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Card.DisplayName, b.Card.DisplayName));
        return result;
    }

    /// <summary>
    /// Everything to send to: each address of each contact (one entry per address) and each group with an address for
    /// every member. For An/Cc autocompletion and the contact picker.
    /// </summary>
    public async Task<IReadOnlyList<RecipientEntry>> GetRecipientsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await SearchAsync(null, cancellationToken);
        var byUid = new Dictionary<string, ContactEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            byUid.TryAdd(entry.Card.MemberUid, entry);
        }

        var result = new List<RecipientEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, card) in entries)
        {
            if (card.IsGroup)
            {
                var members = card.Members.Select(member => member.ContactUid is null
                        ? member.Email is { } external ? new MailAddress(null, external) : null
                        : byUid.GetValueOrDefault(member.ContactUid) is { } contact
                            ? (member.Email is { } chosen && contact.Card.EmailAddresses.Contains(chosen, StringComparer.OrdinalIgnoreCase) ? chosen : contact.Card.EmailAddresses.FirstOrDefault()) is { } address
                                ? new MailAddress(contact.Card.DisplayName, address)
                                : null
                            : member.Email is { } email ? new MailAddress(null, email) : null)
                    .OfType<MailAddress>()
                    .DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (members.Count > 0)
                {
                    result.Add(new RecipientEntry(card.DisplayName, members, members.Count == 1 ? "Gruppe · 1 Mitglied" : $"Gruppe · {members.Count} Mitglieder", IsGroup: true));
                }

                continue;
            }

            foreach (var email in card.EmailAddresses)
            {
                // The same person in two address books is offered once.
                if (seen.Add(card.DisplayName + "\n" + email))
                {
                    result.Add(new RecipientEntry(card.DisplayName, [new MailAddress(card.DisplayName, email)], card.Organization, IsGroup: false));
                }
            }
        }

        return result;
    }

    private async Task<ServiceConnection> ConnectionAsync(Guid connectionId, CancellationToken cancellationToken) =>
        (await accounts.GetAccountsAsync(cancellationToken)).SelectMany(a => a.Connections).FirstOrDefault(c => c.Id == connectionId)
        ?? throw new InvalidOperationException($"Connection {connectionId} no longer exists.");

    private static bool Matches(ContactCard card, string? query) =>
        string.IsNullOrWhiteSpace(query)
        || card.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (card.Organization?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || card.EmailAddresses.Any(e => e.Contains(query, StringComparison.OrdinalIgnoreCase));
}
