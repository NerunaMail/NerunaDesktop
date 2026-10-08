using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Neruna.Core;
using Neruna.Core.Contacts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Create/edit a contact group (distribution list). Left the members – for each one with several addresses the user
/// picks the one this list uses –, right the contacts not yet in the group, searchable, to add with a click.
/// Addresses without contact can be added too.
/// </summary>
internal sealed partial class GroupEditorViewModel : ViewModelBase
{
    private readonly ContactController _contacts;
    private readonly IReadOnlyList<ContactEntry> _allContacts;
    private readonly (AddressBookInfo AddressBook, string RemoteId)? _existing;

    public GroupEditorViewModel(
        ContactController contacts,
        IReadOnlyList<AddressBookInfo> writableAddressBooks,
        IReadOnlyList<ContactEntry> allContacts,
        GroupDraft draft,
        (AddressBookInfo AddressBook, string RemoteId)? existing)
    {
        _contacts = contacts;
        // Only contacts with an address can receive mail through a group; one entry per contact (UID).
        _allContacts = allContacts
            .Where(c => !c.Card.IsGroup && c.Card.EmailAddresses.Count > 0)
            .DistinctBy(c => c.Card.MemberUid, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.Card.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _existing = existing;

        IsReadOnly = existing?.AddressBook.IsReadOnly ?? false;
        AddressBooks = IsReadOnly ? [existing!.Value.AddressBook] : writableAddressBooks;
        SelectedAddressBook = existing is { } e
            ? AddressBooks.FirstOrDefault(b => b.ConnectionId == e.AddressBook.ConnectionId && b.RemoteId == e.AddressBook.RemoteId) ?? AddressBooks.FirstOrDefault()
            : AddressBooks.FirstOrDefault();
        Name = draft.Name;

        var byUid = new Dictionary<string, ContactEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var contact in _allContacts)
        {
            byUid.TryAdd(contact.Card.MemberUid, contact);
        }

        foreach (var member in draft.Members)
        {
            Members.Add(member.ContactUid is { } uid
                ? GroupMemberRow.ForContact(uid, byUid.GetValueOrDefault(uid), member.Email)
                : GroupMemberRow.ForAddress(member.Email ?? string.Empty));
        }

        Members.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MemberCountText));
            OnPropertyChanged(nameof(HasNoMembers));
            UpdateAvailable();
        };
        UpdateAvailable();
    }

    public event EventHandler<bool>? Finished;

    public IReadOnlyList<AddressBookInfo> AddressBooks { get; }

    public bool IsReadOnly { get; }

    public bool IsExisting => _existing is not null;

    public string Heading => IsReadOnly ? "Gruppe (schreibgeschützt)" : IsExisting ? "Gruppe bearbeiten" : "Neue Gruppe";

    public ObservableCollection<GroupMemberRow> Members { get; } = [];

    /// <summary>Contacts not yet in the group that match <see cref="SearchText"/>, to add with a click.</summary>
    public ObservableCollection<ContactChoice> Available { get; } = [];

    public bool HasNoMembers => Members.Count == 0;

    /// <summary>Read-only groups have no contact list on the right; the members use the full width.</summary>
    public int MembersColumnSpan => IsReadOnly ? 3 : 1;

    public string AvailableHint => SearchText.Trim().Length > 0
        ? "Kein passender Kontakt (oder bereits in der Gruppe)."
        : "Alle Kontakte mit E-Mail-Adresse sind bereits in der Gruppe.";

    public bool HasNoAvailable => Available.Count == 0;

    [ObservableProperty]
    public partial AddressBookInfo? SelectedAddressBook { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExternalAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool ConfirmDelete { get; set; }

    public string MemberCountText => Members.Count == 1 ? "1 Mitglied" : $"{Members.Count} Mitglieder";

    partial void OnSearchTextChanged(string value) => UpdateAvailable();

    // Contacts already in the group disappear from the right-hand list.
    private void UpdateAvailable()
    {
        var members = Members.Select(m => m.ContactUid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = SearchText.Trim();
        Available.Clear();
        foreach (var contact in _allContacts.Where(c => !members.Contains(c.Card.MemberUid) && Matches(c, query)))
        {
            Available.Add(new ContactChoice(contact));
        }

        OnPropertyChanged(nameof(HasNoAvailable));
        OnPropertyChanged(nameof(AvailableHint));
    }

    private static bool Matches(ContactEntry contact, string query) =>
        query.Length == 0
        || contact.Card.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || contact.Card.Organization?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true
        || contact.Card.EmailAddresses.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void AddContact(ContactChoice choice)
    {
        var contact = choice.Entry;
        if (Members.Any(m => string.Equals(m.ContactUid, contact.Card.MemberUid, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Error = null;
        Members.Add(GroupMemberRow.ForContact(contact.Card.MemberUid, contact, null));
    }

    [RelayCommand]
    private void AddExternal()
    {
        var address = ExternalAddress.Trim();
        if (!MailboxAddress.TryParse(address, out var mailbox) || !mailbox.Address.Contains('@', StringComparison.Ordinal))
        {
            Error = "Bitte eine gültige E-Mail-Adresse eingeben.";
            return;
        }

        Error = null;
        if (Members.Any(m => m.Addresses.Contains(mailbox.Address, StringComparer.OrdinalIgnoreCase)))
        {
            Error = $"{mailbox.Address} ist bereits in der Gruppe.";
            return;
        }

        Error = null;
        Members.Add(GroupMemberRow.ForAddress(mailbox.Address));
        ExternalAddress = string.Empty;
    }

    [RelayCommand]
    private void RemoveMember(GroupMemberRow row) => Members.Remove(row);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Error = null;
        if (SelectedAddressBook is null)
        {
            Error = "Kein beschreibbares Adressbuch vorhanden.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "Bitte einen Namen für die Gruppe eingeben.";
            return;
        }

        var draft = new GroupDraft(Name.Trim(), Members.Select(m => m.ToMember()).ToList());
        await RunAsync(() => _contacts.SaveGroupAsync(SelectedAddressBook, draft, _existing));
    }

    private bool CanSave() => !IsBusy && !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task DeleteAsync()
    {
        if (_existing is not { } existing)
        {
            return;
        }

        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            return;
        }

        await RunAsync(() => _contacts.DeleteContactAsync(existing.AddressBook, existing.RemoteId));
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);

    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation();
            Finished?.Invoke(this, true);
        }
        catch (RemoteConflictException)
        {
            Error = "Die Gruppe wurde inzwischen auf dem Server geändert. Bitte synchronisieren (F5) und erneut bearbeiten.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = "Speichern fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A contact in the right-hand list of the group editor.</summary>
internal sealed class ContactChoice(ContactEntry entry)
{
    public ContactEntry Entry { get; } = entry;

    public string Name => Entry.Card.DisplayName;

    public string Initials => ContactItem.InitialsOf(Name);

    /// <summary>All addresses, so the user sees which contacts offer a choice.</summary>
    public string Addresses => string.Join(" · ", Entry.Card.EmailAddresses);

    public string? Organization => Entry.Card.Organization;
}

/// <summary>One member in the group editor: a contact with a choice of its addresses, or a plain address.</summary>
internal sealed partial class GroupMemberRow : ObservableObject
{
    private readonly bool _unresolved;

    private GroupMemberRow(string? contactUid, string name, IReadOnlyList<string> addresses, string? selected, string? hint, bool unresolved = false)
    {
        _unresolved = unresolved;
        ContactUid = contactUid;
        Name = name;
        Addresses = addresses;
        SelectedAddress = selected;
        Hint = hint;
    }

    public string? ContactUid { get; }

    public string Name { get; }

    /// <summary>The contact's addresses; a choice is offered when there is more than one.</summary>
    public IReadOnlyList<string> Addresses { get; }

    public bool HasChoice => Addresses.Count > 1;

    // An address without contact already shows as the name.
    public bool HasSingleAddress => Addresses.Count <= 1 && ContactUid is not null;

    public string SingleAddress => Addresses.FirstOrDefault() ?? "(keine E-Mail-Adresse)";

    public string? Hint { get; }

    public bool HasHint => Hint is not null;

    [ObservableProperty]
    public partial string? SelectedAddress { get; set; }

    public static GroupMemberRow ForContact(string uid, ContactEntry? contact, string? chosen)
    {
        if (contact is null)
        {
            // Not (yet) in a local address book: keep the reference and its chosen address untouched.
            return new GroupMemberRow(uid, chosen ?? uid, chosen is null ? [] : [chosen], chosen, "Kontakt nicht gefunden", unresolved: true);
        }

        var addresses = contact.Card.EmailAddresses;
        var selected = addresses.FirstOrDefault(a => string.Equals(a, chosen, StringComparison.OrdinalIgnoreCase)) ?? addresses.FirstOrDefault();
        return new GroupMemberRow(uid, contact.Card.DisplayName, addresses, selected, addresses.Count == 0 ? "Kontakt hat keine E-Mail-Adresse" : null);
    }

    public static GroupMemberRow ForAddress(string address) => new(null, address, [address], address, "Adresse ohne Kontakt");

    // With only one address the reference follows the contact (no fixed address stored).
    public GroupMember ToMember() => ContactUid is null
        ? new GroupMember(null, SelectedAddress)
        : new GroupMember(ContactUid, HasChoice || _unresolved ? SelectedAddress : null);
}
