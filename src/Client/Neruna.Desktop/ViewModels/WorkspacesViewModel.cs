using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Workspaces;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Workspaces: own views ("Business", "Privat") with their accounts, folders, calendars and address
/// books, a colour, notifications and chat; and whether they are switched at the top of the rail or by tabs.
/// </summary>
internal sealed partial class WorkspacesViewModel(
    WorkspaceService workspaces,
    IAccountStore accounts,
    MailController mail,
    CalendarController calendar,
    ContactController contacts) : ViewModelBase
{
    public ObservableCollection<Workspace> Workspaces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(EditCommand))]
    public partial Workspace? Selected { get; set; }

    /// <summary>The workspace being edited (null: none open).</summary>
    [ObservableProperty]
    public partial WorkspaceEditor? Editor { get; set; }

    [ObservableProperty]
    public partial bool ShowAsTabs { get; set; }

    public bool ShowAsRail
    {
        get => !ShowAsTabs;
        set => ShowAsTabs = !value;
    }

    /// <summary>Raised when the display changes; the main window follows at once.</summary>
    public event EventHandler<WorkspaceDisplay>? DisplayChanged;

    private bool _loading;

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            await workspaces.LoadAsync();
            ShowAsTabs = await workspaces.GetDisplayAsync() == WorkspaceDisplay.Tabs;
            OnPropertyChanged(nameof(ShowAsRail));
            var selected = Selected?.Id;
            Workspaces.Clear();
            foreach (var workspace in workspaces.All.Where(w => !w.IsFull))
            {
                Workspaces.Add(workspace);
            }

            Selected = Workspaces.FirstOrDefault(w => w.Id == selected) ?? Workspaces.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnShowAsTabsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAsRail));
        if (!_loading)
        {
            var display = value ? WorkspaceDisplay.Tabs : WorkspaceDisplay.Rail;
            _ = workspaces.SetDisplayAsync(display);
            DisplayChanged?.Invoke(this, display);
        }
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        var all = await accounts.GetAccountsAsync();
        var color = WorkspaceColors.Palette[Workspaces.Count % WorkspaceColors.Palette.Count];
        await OpenAsync(new Workspace(Guid.NewGuid(), Workspaces.Count == 0 ? T("Business") : F("Workspace {0}", Workspaces.Count + 1), color,
            [.. all.Select(a => a.Id)], [], [], []), isNew: true);
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EditAsync() => OpenAsync(Selected!, isNew: false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        await workspaces.DeleteAsync(Selected!.Id);
        Editor = null;
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveUpAsync()
    {
        await workspaces.MoveAsync(Selected!.Id, -1);
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveDownAsync()
    {
        await workspaces.MoveAsync(Selected!.Id, 1);
        await ReloadAsync();
    }

    partial void OnSelectedChanged(Workspace? value)
    {
        if (!_loading && value is not null && Editor is not null && Editor.Id != value.Id)
        {
            _ = OpenAsync(value, isNew: false);
        }
    }

    private async Task OpenAsync(Workspace workspace, bool isNew)
    {
        var all = await accounts.GetAccountsAsync();
        var calendars = await calendar.GetCalendarsAsync();
        var books = await contacts.GetAddressBooksAsync();
        var items = new List<WorkspaceAccountItem>();
        foreach (var account in all)
        {
            var folders = new List<WorkspaceFolderItem>();
            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail))
            {
                folders.AddRange(WorkspaceFolderItem.Tree(account.Id, await mail.GetFoldersAsync(connection.Id), workspace.HiddenFolders));
            }

            var own = account.Connections.Select(c => c.Id).ToHashSet();
            items.Add(new WorkspaceAccountItem(account, workspace.ShowsAccount(account.Id) && (isNew || workspace.Accounts.Contains(account.Id)),
                folders,
                [.. calendars.Where(c => own.Contains(c.ConnectionId)).Select(c =>
                    new WorkspaceToggleItem(CalendarController.CalendarKey(c), c.HasEvents ? c.Name : c.Name + " " + T("(Aufgaben)"), !workspace.HiddenCalendars.Contains(CalendarController.CalendarKey(c))))],
                [.. books.Where(b => own.Contains(b.ConnectionId)).Select(b =>
                    new WorkspaceToggleItem(Workspace.AddressBookKey(b.ConnectionId, b.RemoteId), b.Name, !workspace.HiddenAddressBooks.Contains(Workspace.AddressBookKey(b.ConnectionId, b.RemoteId))))]));
        }

        var editor = new WorkspaceEditor(workspace, isNew, items);
        editor.Finished += async (_, saved) =>
        {
            if (saved is not null)
            {
                await workspaces.SaveAsync(saved);
                Selected = saved;
            }

            Editor = null;
            await ReloadAsync();
        };
        Editor = editor;
    }
}

/// <summary>The workspace being edited.</summary>
internal sealed partial class WorkspaceEditor : ObservableObject
{
    private readonly Workspace _original;

    public WorkspaceEditor(Workspace original, bool isNew, IReadOnlyList<WorkspaceAccountItem> accounts)
    {
        _original = original;
        IsNew = isNew;
        Name = original.Name;
        Color = original.Color;
        NotifyOnlyHere = original.NotifyOnlyHere;
        HideChat = original.HideChat;
        Accounts = accounts;
    }

    public event EventHandler<Workspace?>? Finished;

    public Guid Id => _original.Id;

    public bool IsNew { get; }

    public string Title => IsNew ? T("Neuer Workspace") : T("Workspace bearbeiten");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBrush))]
    public partial string Color { get; set; }

    public Avalonia.Media.IBrush ColorBrush => WorkspaceColors.Brush(Color);

    public static IReadOnlyList<string> Palette => WorkspaceColors.Palette;

    [ObservableProperty]
    public partial bool NotifyOnlyHere { get; set; }

    [ObservableProperty]
    public partial bool HideChat { get; set; }

    public IReadOnlyList<WorkspaceAccountItem> Accounts { get; }

    [RelayCommand]
    private void PickColor(string? color)
    {
        if (color is not null)
        {
            Color = color;
        }
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => Finished?.Invoke(this, _original with
    {
        Name = Name.Trim(),
        Color = Color,
        NotifyOnlyHere = NotifyOnlyHere,
        HideChat = HideChat,
        Accounts = [.. Accounts.Where(a => a.IsIncluded).Select(a => a.Account.Id)],
        // Each folder's own choice is kept, also below a hidden one: showing the parent again brings them back as they were.
        HiddenFolders = [.. Accounts.SelectMany(a => a.Folders).Where(f => !f.IsSystem && !f.IsShown).Select(f => f.Key)],
        HiddenCalendars = [.. Accounts.SelectMany(a => a.Calendars).Where(c => !c.IsShown).Select(c => c.Key)],
        HiddenAddressBooks = [.. Accounts.SelectMany(a => a.AddressBooks).Where(b => !b.IsShown).Select(b => b.Key)],
    });

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, null);
}

/// <summary>An account in the editor: included or not, with its folders, calendars and address books.</summary>
internal sealed partial class WorkspaceAccountItem : ObservableObject
{
    public WorkspaceAccountItem(Account account, bool isIncluded, IReadOnlyList<WorkspaceFolderItem> folders,
        IReadOnlyList<WorkspaceToggleItem> calendars, IReadOnlyList<WorkspaceToggleItem> addressBooks)
    {
        Account = account;
        IsIncluded = isIncluded;
        Folders = folders;
        Calendars = calendars;
        AddressBooks = addressBooks;
    }

    public Account Account { get; }

    public string Title => Account.Title;

    [ObservableProperty]
    public partial bool IsIncluded { get; set; }

    /// <summary>All folders, parents before their children (shown indented).</summary>
    public IReadOnlyList<WorkspaceFolderItem> Folders { get; }

    public IReadOnlyList<WorkspaceToggleItem> Calendars { get; }

    public IReadOnlyList<WorkspaceToggleItem> AddressBooks { get; }

    public bool HasFolders => Folders.Count > 0;

    public bool HasCalendars => Calendars.Count > 0;

    public bool HasAddressBooks => AddressBooks.Count > 0;
}

/// <summary>
/// A mail folder in the editor. System folders are always ticked; a subfolder can only be chosen while the folder it
/// is in shows – one cannot see a subfolder without its parent.
/// </summary>
internal sealed partial class WorkspaceFolderItem : ObservableObject
{
    private WorkspaceFolderItem(Guid accountId, MailFolder folder, WorkspaceFolderItem? parent, int depth, bool isShown)
    {
        Folder = folder;
        Parent = parent;
        Key = Workspace.FolderKey(accountId, folder.RemoteId);
        Indent = new Avalonia.Thickness(depth * 18, 0, 0, 0);
        IsShown = isShown;
        parent?.Children.Add(this);
    }

    public MailFolder Folder { get; }

    public WorkspaceFolderItem? Parent { get; }

    public List<WorkspaceFolderItem> Children { get; } = [];

    public string Key { get; }

    public string Name => Folder.Name;

    public Avalonia.Thickness Indent { get; }

    public bool IsSystem => Workspace.IsSystemFolder(Folder);

    /// <summary>The user's own choice for this folder (kept while its parent is hidden).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked))]
    public partial bool IsShown { get; set; }

    /// <summary>Whether everything above it shows (system folders count as shown).</summary>
    public bool ParentShows => Parent is null || Parent.IsSystem || (Parent.IsShown && Parent.ParentShows);

    /// <summary>The tick: system folders always, others only with their parent shown.</summary>
    public bool IsChecked
    {
        get => IsSystem || (IsShown && ParentShows);
        set
        {
            if (CanChange)
            {
                IsShown = value;
            }
        }
    }

    public bool CanChange => !IsSystem && ParentShows;

    public string? Hint => IsSystem ? T("Systemordner – immer sichtbar") : !ParentShows ? T("Erst den übergeordneten Ordner einblenden") : null;

    partial void OnIsShownChanged(bool value) => Refresh(this);

    // Everything below follows: ticks and whether they can be changed.
    private static void Refresh(WorkspaceFolderItem item)
    {
        foreach (var child in item.Children)
        {
            child.OnPropertyChanged(nameof(IsChecked));
            child.OnPropertyChanged(nameof(CanChange));
            child.OnPropertyChanged(nameof(Hint));
            Refresh(child);
        }
    }

    /// <summary>The folders of an account, parents first, with their depth.</summary>
    public static IEnumerable<WorkspaceFolderItem> Tree(Guid accountId, IReadOnlyList<MailFolder> folders, IReadOnlyList<string> hidden)
    {
        var byParent = folders.ToLookup(f => f.ParentRemoteId is { } p && folders.Any(x => x.RemoteId == p) ? p : null);
        var result = new List<WorkspaceFolderItem>();
        void Add(string? parentId, WorkspaceFolderItem? parent, int depth)
        {
            foreach (var folder in byParent[parentId].OrderBy(f => f.Role == FolderRole.None ? 1 : 0).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new WorkspaceFolderItem(accountId, folder, parent, depth, !hidden.Contains(Workspace.FolderKey(accountId, folder.RemoteId)));
                result.Add(item);
                Add(folder.RemoteId, item, depth + 1);
            }
        }

        Add(null, null, 0);
        return result;
    }
}

/// <summary>A calendar, task list or address book in the editor.</summary>
internal sealed partial class WorkspaceToggleItem(string key, string name, bool isShown) : ObservableObject
{
    public string Key { get; } = key;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsShown { get; set; } = isShown;
}
