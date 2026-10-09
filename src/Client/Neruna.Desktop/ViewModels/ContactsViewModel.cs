using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Contacts;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Contacts: address books on the left (shown/hidden, own color), contacts and groups in
/// the middle (colored by address book), details on the right.
/// </summary>
internal sealed partial class ContactsViewModel(ContactController contacts, ISettingsStore settings, IFileService files) : ViewModelBase
{
    private static readonly string[] Palette = ["#0F6CBD", "#C239B3", "#0B6A0B", "#CA5010", "#8764B8", "#038387"];

    private IReadOnlyList<ContactEntry> _all = [];
    private HashSet<string> _hidden = [];

    /// <summary>The shell shows contact and group editors as overlay and reloads when they report a change.</summary>
    public event EventHandler<ViewModelBase>? EditorRequested;

    /// <summary>"Adressbücher verwalten": the shell shows the selection dialog.</summary>
    public event EventHandler? ManageRequested;

    /// <summary>"E-Mail schreiben": recipients for a new message (one contact or all members of a group).</summary>
    public event EventHandler<string>? MailRequested;

    public static IReadOnlyList<string> ColorChoices => CalendarViewModel.ColorChoices;

    public ObservableCollection<AddressBookItem> AddressBooks { get; } = [];

    public ObservableCollection<ContactItem> Items { get; } = [];

    /// <summary>Members of the selected group, with the address used for each.</summary>
    public ObservableCollection<MemberItem> SelectedMembers { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(WriteMailCommand))]
    [NotifyPropertyChangedFor(nameof(IsContactSelected), nameof(IsGroupSelected))]
    public partial ContactItem? Selected { get; set; }

    public bool IsContactSelected => Selected is { IsGroup: false };

    public bool IsGroupSelected => Selected is { IsGroup: true };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewContactCommand), nameof(NewGroupCommand))]
    public partial bool HasAddressBooks { get; set; }

    public async Task ReloadAsync()
    {
        _hidden = await LoadHiddenAsync();
        var books = await contacts.GetAddressBooksAsync();
        _all = await Task.Run(() => contacts.SearchAsync(null));

        // With several accounts the account is shown below each address book: two "Domain Address Book" stay apart.
        var sources = await contacts.GetSourcesAsync();
        var owners = sources.ToDictionary(s => s.Connection.Id, s => s.Account.Title);
        var showOwner = owners.Values.Distinct().Count() > 1;

        AddressBooks.Clear();
        var index = 0;
        foreach (var book in books)
        {
            var color = await settings.GetAsync(ColorKey(book)) ?? Palette[index++ % Palette.Length];
            var item = new AddressBookItem(book, color, _all.Count(e => Same(e.AddressBook, book)))
            {
                IsVisible = !_hidden.Contains(Key(book)),
                Owner = showOwner ? owners.GetValueOrDefault(book.ConnectionId) : null,
            };
            item.PropertyChanged += async (_, e) =>
            {
                if (e.PropertyName == nameof(AddressBookItem.IsVisible))
                {
                    await SaveVisibilityAsync(item);
                }
            };
            AddressBooks.Add(item);
        }

        HasAddressBooks = AddressBooks.Count > 0;
        Filter();
    }

    /// <summary>Sets an address book's color (stored in Neruna only); null restores the default.</summary>
    public async Task SetColorAsync(AddressBookItem book, string? color)
    {
        ArgumentNullException.ThrowIfNull(book);
        await settings.SetAsync(ColorKey(book.Info), color);
        await ReloadAsync();
    }

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnSelectedChanged(ContactItem? value) => _ = LoadMembersAsync(value);

    [RelayCommand]
    private void Manage() => ManageRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(HasAddressBooks))]
    private void NewContact() =>
        EditorRequested?.Invoke(this, new ContactEditorViewModel(contacts, files, Writable(), ContactDraft.Empty, null));

    [RelayCommand(CanExecute = nameof(HasAddressBooks))]
    private void NewGroup() =>
        EditorRequested?.Invoke(this, new GroupEditorViewModel(contacts, Writable(), _all, new GroupDraft(string.Empty, []), null));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        var entry = Selected!.Entry;
        var stored = await contacts.GetContactAsync(entry.AddressBook, entry.RemoteId);
        if (stored is null)
        {
            return;
        }

        EditorRequested?.Invoke(this, entry.Card.IsGroup
            ? new GroupEditorViewModel(contacts, Writable(), _all, GroupDraft.FromVCard(stored.VCardData), (entry.AddressBook, entry.RemoteId))
            : new ContactEditorViewModel(contacts, files, Writable(), ContactDraft.FromVCard(stored.VCardData), (entry.AddressBook, entry.RemoteId)));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task WriteMailAsync()
    {
        var entry = Selected!.Entry;
        var recipients = entry.Card.IsGroup
            ? (await contacts.ResolveMembersAsync(entry.Card)).Select(m => m.Recipient).OfType<string>().ToList()
            : entry.Card.EmailAddresses.Take(1).Select(a => $"\"{entry.Card.DisplayName.Replace("\"", string.Empty, StringComparison.Ordinal)}\" <{a}>").ToList();
        if (recipients.Count > 0)
        {
            MailRequested?.Invoke(this, string.Join(", ", recipients));
        }
    }

    private bool HasSelection() => Selected is not null;

    private List<AddressBookInfo> Writable() => AddressBooks.Select(b => b.Info).Where(b => !b.IsReadOnly).ToList();

    private void Filter()
    {
        var selected = Selected?.Entry;
        var visible = AddressBooks.Where(b => b.IsVisible).ToList();
        var query = SearchText.Trim();

        Items.Clear();
        foreach (var entry in _all)
        {
            var book = visible.FirstOrDefault(b => Same(b.Info, entry.AddressBook));
            if (book is not null && Matches(entry.Card, query))
            {
                Items.Add(new ContactItem(entry, book.Color));
            }
        }

        Selected = Items.FirstOrDefault(i => selected is not null && i.Entry.RemoteId == selected.RemoteId && Same(i.Entry.AddressBook, selected.AddressBook))
                   ?? Items.FirstOrDefault();
    }

    private async Task LoadMembersAsync(ContactItem? item)
    {
        SelectedMembers.Clear();
        if (item is not { IsGroup: true })
        {
            return;
        }

        foreach (var member in await contacts.ResolveMembersAsync(item.Entry.Card))
        {
            if (Selected == item)
            {
                SelectedMembers.Add(new MemberItem(member));
            }
        }
    }

    private async Task SaveVisibilityAsync(AddressBookItem book)
    {
        if (book.IsVisible ? _hidden.Remove(Key(book.Info)) : _hidden.Add(Key(book.Info)))
        {
            await settings.SetAsync(SettingKeys.HiddenAddressBooks, JsonSerializer.Serialize(_hidden));
        }

        Filter();
    }

    private async Task<HashSet<string>> LoadHiddenAsync()
    {
        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(await settings.GetAsync(SettingKeys.HiddenAddressBooks) ?? "[]") ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Matches(ContactCard card, string query) =>
        query.Length == 0
        || card.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (card.Organization?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || card.EmailAddresses.Any(e => e.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static bool Same(AddressBookInfo a, AddressBookInfo b) => a.ConnectionId == b.ConnectionId && a.RemoteId == b.RemoteId;

    private static string Key(AddressBookInfo book) => $"{book.ConnectionId:N}.{book.RemoteId}";

    private static string ColorKey(AddressBookInfo book) => "contacts.color." + Key(book);
}

internal sealed partial class AddressBookItem(AddressBookInfo info, string color, int count) : ObservableObject
{
    public AddressBookInfo Info { get; } = info;

    public string Name => Info.Name;

    public string Color { get; } = color;

    public IBrush Brush { get; } = Avalonia.Media.Brush.Parse(color);

    public int Count { get; } = count;

    /// <summary>Account the address book belongs to; only set when there are several accounts.</summary>
    public string? Owner { get; init; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}

internal sealed class ContactItem(ContactEntry entry, string color)
{
    private Bitmap? _photo;
    private bool _photoLoaded;

    public ContactEntry Entry { get; } = entry;

    public bool IsGroup => Entry.Card.IsGroup;

    public string Name => Entry.Card.DisplayName;

    public string? Organization => Entry.Card.Organization;

    public string? Title => Entry.Card.Title;

    public string? Note => Entry.Card.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public string Subtitle => IsGroup
        ? (Entry.Card.Members.Count == 1 ? T("Gruppe · 1 Mitglied") : F("Gruppe · {0} Mitglieder", Entry.Card.Members.Count))
        : string.Join(" · ", new[] { Entry.Card.Title, Entry.Card.Organization }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string PrimaryEmail => IsGroup ? Subtitle : Entry.Card.Emails.FirstOrDefault()?.Value ?? string.Empty;

    public IReadOnlyList<LabeledValue> Emails => Entry.Card.Emails.Select(e => new LabeledValue(e.Value, Label(e.Kind))).ToList();

    public IReadOnlyList<LabeledValue> Phones => Entry.Card.Phones.Select(p => new LabeledValue(p.Value, Label(p.Kind))).ToList();

    public string AddressBook => Entry.AddressBook.Name;

    /// <summary>The address book's color: stripe in the list, avatar background without photo.</summary>
    public IBrush Brush { get; } = Avalonia.Media.Brush.Parse(color);

    public string Initials => InitialsOf(Name);

    public static string InitialsOf(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    public Bitmap? Photo
    {
        get
        {
            if (!_photoLoaded)
            {
                _photoLoaded = true;
                _photo = ContactPhotoBitmap.From(Entry.Card.Photo);
            }

            return _photo;
        }
    }

    public bool HasPhoto => Photo is not null;

    public bool ShowInitials => !HasPhoto && !IsGroup;

    private static string Label(string? kind) => FieldKind.Find(FieldKind.PhoneKinds, kind).Label;
}

/// <summary>A group member in the details pane.</summary>
internal sealed class MemberItem(ResolvedMember member)
{
    public string Name => member.DisplayName;

    public string Address => member.Address ?? T("(keine E-Mail-Adresse)");

    public bool IsExternal => member.Member.IsExternal;

    public bool IsMissing => !member.Member.IsExternal && member.Contact is null;

    public string Hint => IsExternal ? T("Adresse ohne Kontakt") : IsMissing ? T("Kontakt nicht gefunden") : string.Empty;
}

internal static class ContactPhotoBitmap
{
    public static Bitmap? From(ContactPhoto? photo)
    {
        if (photo is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(photo.Data);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}

internal sealed record LabeledValue(string Value, string Label);
