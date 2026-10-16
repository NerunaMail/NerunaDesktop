using System.Text.Json;
using Neruna.Core.Calendar;
using Neruna.Core.Mail;

namespace Neruna.Core.Workspaces;

/// <summary>
/// A view of Neruna for one part of life or work ("Business", "Privat"): which accounts it shows, which of their folders,
/// calendars and address books, its colour, and whether notifications and the chat follow it. Only a view – everything
/// keeps synchronising, so switching is immediate. <see cref="Full"/> (everything) always exists.
/// </summary>
/// <param name="Accounts">The accounts shown (mail, calendars, contacts of each).</param>
/// <param name="HiddenFolders">Folders left out ("{account id:N}|{folder id}"); new folders show, system folders always do.</param>
/// <param name="HiddenCalendars">Calendars and task lists left out (<see cref="CalendarController.CalendarKey"/>).</param>
/// <param name="HiddenAddressBooks">Address books left out (connection id:N|remote id).</param>
/// <param name="NotifyOnlyHere">New mail and reminders only for the accounts of this workspace while it is open.</param>
/// <param name="HideChat">The chat of the organisation is not shown here (e.g. "Privat").</param>
public sealed record Workspace(
    Guid Id,
    string Name,
    string Color,
    IReadOnlyList<Guid> Accounts,
    IReadOnlyList<string> HiddenFolders,
    IReadOnlyList<string> HiddenCalendars,
    IReadOnlyList<string> HiddenAddressBooks,
    bool NotifyOnlyHere = true,
    bool HideChat = false)
{
    /// <summary>Everything, always there: the view Neruna had before workspaces.</summary>
    public static Workspace Full { get; } = new(Guid.Empty, string.Empty, string.Empty, [], [], [], [], false, false);

    public bool IsFull => Id == Guid.Empty;

    /// <summary>Inbox, sent, drafts, trash, junk and archive are always shown, they cannot be hidden.</summary>
    public static bool IsSystemFolder(MailFolder folder) => folder?.Role is not (null or FolderRole.None);

    public static string FolderKey(Guid accountId, string folderRemoteId) => $"{accountId:N}|{folderRemoteId}";

    public static string AddressBookKey(Guid connectionId, string remoteId) => $"{connectionId:N}|{remoteId}";

    public bool ShowsAccount(Guid accountId) => IsFull || Accounts.Contains(accountId);

    /// <summary>A folder and everything below it can be hidden; system folders always show.</summary>
    public bool ShowsFolder(Guid accountId, MailFolder folder, IReadOnlyDictionary<string, MailFolder> byId)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(byId);
        if (IsFull || IsSystemFolder(folder))
        {
            return IsFull || ShowsAccount(accountId);
        }

        if (!ShowsAccount(accountId))
        {
            return false;
        }

        for (MailFolder? current = folder; current is not null; current = current.ParentRemoteId is { } parent ? byId.GetValueOrDefault(parent) : null)
        {
            if (!IsSystemFolder(current) && HiddenFolders.Contains(FolderKey(accountId, current.RemoteId)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The folders of an account this workspace shows.</summary>
    public IReadOnlyList<MailFolder> FoldersOf(Guid accountId, IReadOnlyList<MailFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (IsFull)
        {
            return folders;
        }

        var byId = folders.ToDictionary(f => f.RemoteId, StringComparer.Ordinal);
        return [.. folders.Where(f => ShowsFolder(accountId, f, byId))];
    }

    /// <param name="accountId">The account the calendar belongs to; null when unknown (then only the hidden list counts).</param>
    public bool ShowsCalendar(CalendarInfo calendar, Guid? accountId) =>
        IsFull || ((accountId is null || ShowsAccount(accountId.Value)) && !HiddenCalendars.Contains(CalendarController.CalendarKey(calendar)));

    public bool ShowsAddressBook(Guid connectionId, string remoteId, Guid? accountId) =>
        IsFull || ((accountId is null || ShowsAccount(accountId.Value)) && !HiddenAddressBooks.Contains(AddressBookKey(connectionId, remoteId)));
}

/// <summary>Where the workspaces are chosen: an icon at the top of the navigation rail, or tabs below the header.</summary>
public enum WorkspaceDisplay
{
    Rail,
    Tabs,
}

/// <summary>
/// The workspaces and the one in use (Einstellungen → Workspaces; switched at the top left or by the tabs). Stored with
/// the settings, so the personal backup takes them along.
/// </summary>
public sealed class WorkspaceService(ISettingsStore settings, IAccountStore? accounts = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private List<Workspace>? _custom;

    /// <summary>Raised after switching or after the workspaces changed; the views reload what they show.</summary>
    public event EventHandler? Changed;

    public Workspace Current { get; private set; } = Workspace.Full;

    /// <summary>The full view first, then the user's own in their order.</summary>
    public IReadOnlyList<Workspace> All => [Workspace.Full, .. _custom ?? []];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _custom = Parse(await settings.GetAsync(SettingKeys.Workspaces, cancellationToken));
        var current = Guid.TryParse(await settings.GetAsync(SettingKeys.WorkspaceCurrent, cancellationToken), out var id) ? id : Guid.Empty;
        Current = _custom.FirstOrDefault(w => w.Id == current) ?? Workspace.Full;
    }

    public async Task<WorkspaceDisplay> GetDisplayAsync(CancellationToken cancellationToken = default) =>
        await settings.GetAsync(SettingKeys.WorkspaceDisplay, cancellationToken) == "tabs" ? WorkspaceDisplay.Tabs : WorkspaceDisplay.Rail;

    public Task SetDisplayAsync(WorkspaceDisplay display, CancellationToken cancellationToken = default) =>
        settings.SetAsync(SettingKeys.WorkspaceDisplay, display == WorkspaceDisplay.Tabs ? "tabs" : null, cancellationToken);

    public async Task SwitchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var target = All.FirstOrDefault(w => w.Id == id) ?? Workspace.Full;
        if (target == Current)
        {
            return;
        }

        Current = target;
        await settings.SetAsync(SettingKeys.WorkspaceCurrent, target.IsFull ? null : target.Id.ToString(), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds or replaces a workspace (the full view cannot be changed).</summary>
    public async Task SaveAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.IsFull)
        {
            throw new ArgumentException("The full view cannot be changed.", nameof(workspace));
        }

        var list = _custom ??= Parse(await settings.GetAsync(SettingKeys.Workspaces, cancellationToken));
        var index = list.FindIndex(w => w.Id == workspace.Id);
        if (index >= 0)
        {
            list[index] = workspace;
        }
        else
        {
            list.Add(workspace);
        }

        await StoreAsync(cancellationToken);
        if (Current.Id == workspace.Id)
        {
            Current = workspace;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var list = _custom ??= Parse(await settings.GetAsync(SettingKeys.Workspaces, cancellationToken));
        list.RemoveAll(w => w.Id == id);
        await StoreAsync(cancellationToken);
        if (Current.Id == id)
        {
            await SwitchAsync(Guid.Empty, cancellationToken);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task MoveAsync(Guid id, int delta, CancellationToken cancellationToken = default)
    {
        var list = _custom ??= Parse(await settings.GetAsync(SettingKeys.Workspaces, cancellationToken));
        var index = list.FindIndex(w => w.Id == id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return;
        }

        (list[index], list[target]) = (list[target], list[index]);
        await StoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The account a calendar or address book connection belongs to.</summary>
    public async Task<Guid?> AccountOfAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        accounts is null ? null : (await accounts.GetAccountsAsync(cancellationToken)).FirstOrDefault(a => a.Connections.Any(c => c.Id == connectionId))?.Id;

    /// <summary>Whether the open workspace shows this calendar.</summary>
    public async Task<bool> ShowsCalendarAsync(CalendarInfo calendar, CancellationToken cancellationToken = default) =>
        Current.IsFull || Current.ShowsCalendar(calendar, await AccountOfAsync(calendar.ConnectionId, cancellationToken));

    /// <summary>New mail of this account is announced: always, unless the open workspace keeps notifications to itself.</summary>
    public bool Notifies(Guid accountId) => !Current.NotifyOnlyHere || Current.ShowsAccount(accountId);

    /// <summary>A reminder of this calendar shows: always, unless the open workspace keeps them to itself.</summary>
    public async Task<bool> NotifiesAsync(CalendarInfo calendar, CancellationToken cancellationToken = default) =>
        !Current.NotifyOnlyHere || await ShowsCalendarAsync(calendar, cancellationToken);

    private Task StoreAsync(CancellationToken cancellationToken) =>
        settings.SetAsync(SettingKeys.Workspaces, _custom is { Count: > 0 } list ? JsonSerializer.Serialize(list, Json) : null, cancellationToken);

    private static List<Workspace> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Workspace>>(json, Json)?.Where(w => w.Id != Guid.Empty && w.Name.Length > 0).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
