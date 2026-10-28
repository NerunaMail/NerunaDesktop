using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Contacts;

public sealed record ContactEntry(AddressBookInfo AddressBook, string RemoteId, ContactCard Card);

/// <summary>An address book offered by the server, for choosing which ones Neruna shows and synchronizes.</summary>
/// <param name="IsSelected">Currently shown.</param>
/// <param name="IsNew">Appeared since the user last chose.</param>
public sealed record AddressBookCandidate(AddressBookInfo AddressBook, bool IsSelected, bool IsNew);

/// <summary>A contacts connection (CardDAV …) with the account it belongs to.</summary>
/// <param name="Server">The server in one line (URL, user), as the provider describes it.</param>
public sealed record AddressBookSource(Account Account, ServiceConnection Connection, string Server = "");

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
    ILogger<ContactController> logger,
    SyncActivity? activity = null) : Security.IContactCertificates
{
    // The contacts' certificates: asked for with every signed or encrypted mail, read again only after contacts changed.
    private IReadOnlyList<(byte[] Der, IReadOnlyList<string> Emails)>? _certificates;
    private int _contactsVersion;

    private void ContactsChanged()
    {
        Interlocked.Increment(ref _contactsVersion);
        _certificates = null;
    }

    public Task<SyncReport> SyncAllAsync(CancellationToken cancellationToken = default) =>
        SyncReport.ForEachConnectionAsync(accounts, ServiceKind.Contacts, SyncConnectionAsync, logger, cancellationToken, activity);

    public async Task SyncConnectionAsync(ServiceConnection connection, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateContacts(connection);

        var remote = await provider.GetAddressBooksAsync(cancellationToken);

        // Without an explicit choice every address book of the user is shown (and new ones appear automatically) – those
        // of other people found through delegation only once chosen under "Adressbücher verwalten".
        remote = await GetIdsAsync(SelectedKey(connection.Id), cancellationToken) is { } selected
            ? [.. remote.Where(b => selected.Contains(b.RemoteId))]
            : [.. remote.Where(b => b.SharedBy is null)];

        var before = (await store.GetAddressBooksAsync(connection.Id, cancellationToken)).Select(b => b.RemoteId).ToHashSet(StringComparer.Ordinal);
        var books = await store.MergeAddressBooksAsync(connection.Id, remote, cancellationToken);
        if (!before.SetEquals(books.Select(b => b.RemoteId)))
        {
            ContactsChanged();
        }

        foreach (var book in books)
        {
            var known = await store.GetContactVersionsAsync(connection.Id, book.RemoteId, cancellationToken);
            var result = await provider.SyncAddressBookAsync(book, known, cancellationToken);
            await store.ApplySyncResultAsync(book, result, cancellationToken);
            if (result.IsFullResync || result.AddedOrChanged.Count > 0 || result.RemovedRemoteIds.Count > 0)
            {
                ContactsChanged();
            }
        }
    }

    public async Task<IReadOnlyList<AddressBookSource>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
        (await accounts.GetAccountsAsync(cancellationToken))
            .SelectMany(a => a.ConnectionsOf(ServiceKind.Contacts).Select(c => new AddressBookSource(a, c, providers.Summary(c))))
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

    private async Task<HashSet<string>?> GetIdsAsync(string key, CancellationToken cancellationToken) =>
        (await settings.GetListAsync(key, cancellationToken))?.ToHashSet(StringComparer.Ordinal);

    private static string SelectedKey(Guid connectionId) => $"addressbooks.selected.{connectionId:N}";

    private static string KnownKey(Guid connectionId) => $"addressbooks.known.{connectionId:N}";

    /// <summary>All address books, with the user's own display names where set (as for calendars).</summary>
    public async Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AddressBookInfo>();
        foreach (var book in await store.GetAddressBooksAsync(null, cancellationToken))
        {
            result.Add(await settings.GetAsync(DisplayNameKey(book), cancellationToken) is { Length: > 0 } own
                ? book with { Name = own, ServerName = book.Name }
                : book);
        }

        return result;
    }

    /// <summary>The user's own name for an address book (kept in Neruna); null or empty restores the server's.</summary>
    public Task SetDisplayNameAsync(AddressBookInfo addressBook, string? name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        var own = name?.Trim();
        return settings.SetAsync(DisplayNameKey(addressBook), string.IsNullOrEmpty(own) || own == (addressBook.ServerName ?? addressBook.Name) ? null : own, cancellationToken);
    }

    private static string DisplayNameKey(AddressBookInfo book) => $"addressbook.name.{book.ConnectionId:N}.{book.RemoteId}";

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
        ContactsChanged();

        if (moving && current is not null)
        {
            await DeleteContactAsync(existing!.Value.AddressBook, current.RemoteId, cancellationToken);
        }

        // An address that is a real contact now leaves "Verwendete Adressen".
        if (target.RemoteId != AddressCollector.AddressBookId)
        {
            await ForgetCollectedAsync(VCardReader.Read(data).EmailAddresses, cancellationToken);
        }

        return saved;
    }

    private async Task ForgetCollectedAsync(IReadOnlyCollection<string> addresses, CancellationToken cancellationToken)
    {
        if (addresses.Count == 0)
        {
            return;
        }

        foreach (var book in (await store.GetAddressBooksAsync(null, cancellationToken)).Where(b => b.RemoteId == AddressCollector.AddressBookId))
        {
            foreach (var contact in await store.GetContactsAsync(book.ConnectionId, book.RemoteId, cancellationToken))
            {
                if (VCardReader.Read(contact.VCardData).EmailAddresses.Any(a => addresses.Contains(a, StringComparer.OrdinalIgnoreCase)))
                {
                    await DeleteContactAsync(book, contact.RemoteId, cancellationToken);
                }
            }
        }
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
        ContactsChanged();
    }

    /// <summary>The S/MIME certificates kept with contacts (vCard KEY), with the contact's addresses.</summary>
    public async Task<IReadOnlyList<(byte[] Der, IReadOnlyList<string> Emails)>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (_certificates is { } cached)
        {
            return cached;
        }

        var version = Volatile.Read(ref _contactsVersion);
        var result = new List<(byte[], IReadOnlyList<string>)>();
        foreach (var book in await store.GetAddressBooksAsync(null, cancellationToken))
        {
            // Read whenever a signed mail is opened or recipients are checked for encryption: only cards with a KEY are parsed.
            foreach (var contact in (await store.GetContactsAsync(book.ConnectionId, book.RemoteId, cancellationToken))
                         .Where(c => c.VCardData.Contains("KEY", StringComparison.OrdinalIgnoreCase)))
            {
                var certificates = ContactCertificates.Read(contact.VCardData);
                if (certificates.Count > 0)
                {
                    var emails = VCardReader.Read(contact.VCardData).EmailAddresses;
                    result.AddRange(certificates.Select(c => (c, emails)));
                }
            }
        }

        // Kept unless contacts changed while reading (then the next call reads again).
        if (version == Volatile.Read(ref _contactsVersion))
        {
            _certificates = result;
        }

        return result;
    }

    /// <summary>
    /// Adds the certificate to every contact (in a writable address book) with one of <paramref name="emails"/>. A
    /// contact that cannot be saved right now is skipped; if none could take it, the certificate stays on this device.
    /// </summary>
    public async Task<bool> AddAsync(byte[] der, IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(der);
        ArgumentNullException.ThrowIfNull(emails);
        var stored = false;
        foreach (var entry in (await SearchAsync(null, cancellationToken))
                     .Where(e => !e.AddressBook.IsReadOnly && !e.Card.IsGroup && e.Card.EmailAddresses.Any(a => emails.Contains(a, StringComparer.OrdinalIgnoreCase))))
        {
            try
            {
                var current = await GetContactAsync(entry.AddressBook, entry.RemoteId, cancellationToken);
                if (current is not null && ContactCertificates.Read(current.VCardData).Any(c => c.AsSpan().SequenceEqual(der)))
                {
                    stored = true;
                    continue;
                }

                var saved = await SaveCardAsync(entry.AddressBook, v => ContactCertificates.Add(v ?? string.Empty, der), (entry.AddressBook, entry.RemoteId), cancellationToken);

                // Only if the server kept it (a provider that cannot store certificates drops the KEY).
                stored |= ContactCertificates.Read(saved.VCardData).Any(c => c.AsSpan().SequenceEqual(der));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Could not add a certificate to contact {Contact}", entry.RemoteId);
            }
        }

        return stored;
    }

    /// <summary>Takes the certificate out of every contact that has it.</summary>
    public async Task RemoveAsync(byte[] der, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(der);
        foreach (var book in (await store.GetAddressBooksAsync(null, cancellationToken)).Where(b => !b.IsReadOnly))
        {
            foreach (var contact in (await store.GetContactsAsync(book.ConnectionId, book.RemoteId, cancellationToken))
                         .Where(c => ContactCertificates.Read(c.VCardData).Any(k => k.AsSpan().SequenceEqual(der))))
            {
                await SaveCardAsync(book, v => ContactCertificates.Remove(v ?? string.Empty, der), (book, contact.RemoteId), cancellationToken);
            }
        }
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
                    result.Add(new RecipientEntry(card.DisplayName, members, members.Count == 1 ? T("Gruppe · 1 Mitglied") : F("Gruppe · {0} Mitglieder", members.Count), IsGroup: true));
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
