using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MimeKit;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using Neruna.Desktop.Infrastructure;

namespace Neruna.Desktop.ViewModels;

/// <summary>Classic three panes: folder tree, message list grouped by date, reading pane (or inline compose).</summary>
internal sealed partial class MailViewModel(
    MailController mail,
    IAccountStore accounts,
    IFileService files,
    SecureMimeService secureMime,
    ISettingsStore settings,
    SignatureService signatures,
    IWindowService windows,
    IHttpClientFactory httpClients,
    InvitationService invitations,
    UiPreferences preferences,
    ILogger<MailViewModel> logger) : ViewModelBase, IDisposable
{
    /// <summary>Display preferences (toolbar with or without text).</summary>
    public UiPreferences Preferences => preferences;

    /// <summary>The message actions above the reading pane: a message is selected and no draft is open there.</summary>
    public bool ShowMessageActions => HasSelection && Compose is null;

    // The invitation bar of the open message; kept when the pane is rebuilt (remote images allowed).
    private (string RemoteId, InvitationBannerViewModel Banner)? _invitation;

    /// <summary>An answer or cancellation changed the calendar (the calendar page and reminders reload).</summary>
    public event EventHandler? CalendarChanged;

    private readonly HttpClient _imageClient = httpClients.CreateClient("neruna");
    private List<MessageItemViewModel> _allMessages = [];
    private CancellationTokenSource? _readingCts;
    private OpenedMessage? _opened;

    /// <summary>How long a message must stay open before it counts as read in <see cref="MarkAsReadMode.AfterDelay"/>.</summary>
    public static TimeSpan ReadDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Raised with a short user-facing message (errors, confirmations) for the status bar.</summary>
    public event EventHandler<string>? StatusMessage;

    /// <summary>The user moved an account in the folder tree; other lists (settings) follow.</summary>
    public event EventHandler? AccountsReordered;

    public ObservableCollection<MailAccountNode> Accounts { get; } = [];

    /// <summary>Flat list of date-group headers and messages, rendered by one ListBox.</summary>
    public ObservableCollection<MessageListEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial object? SelectedTreeItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMessage), nameof(HasSelection), nameof(ShowMessageActions))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(ArchiveCommand), nameof(ToggleFlagCommand), nameof(ToggleReadCommand), nameof(EditDraftCommand))]
    public partial MessageListEntry? SelectedEntry { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDraftsFolder))]
    [NotifyCanExecuteChangedFor(nameof(EditDraftCommand))]
    public partial MailFolderNode? CurrentFolder { get; set; }

    /// <summary>In "Entwürfe" a message opens for editing ("Bearbeiten", double-click).</summary>
    public bool IsDraftsFolder => (SelectedMessage?.Folder ?? CurrentFolder)?.Folder.Role == FolderRole.Drafts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightPane))]
    [NotifyCanExecuteChangedFor(nameof(ReplyCommand), nameof(ReplyAllCommand), nameof(ForwardCommand))]
    public partial ReadingPaneViewModel? ReadingPane { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightPane), nameof(ShowMessageActions))]
    public partial ComposeViewModel? Compose { get; set; }

    /// <summary>What the right-hand pane shows: an open draft wins over the selected message.</summary>
    public ViewModelBase? RightPane => (ViewModelBase?)Compose ?? ReadingPane;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public MessageItemViewModel? SelectedMessage => SelectedEntry as MessageItemViewModel;

    public bool HasSelection => SelectedMessage is not null;

    public bool HasMessages => Entries.Count > 0;

    public bool HasMailAccounts => Accounts.Count > 0;

    private MailAccountNode? CurrentAccount => SelectedMessage?.Folder?.Account ?? CurrentFolder?.Account ?? Accounts.FirstOrDefault();

    // The folder a message is in: its own (search results), otherwise the open folder.
    private MailFolderNode? FolderOf(MessageItemViewModel message) => message.Folder ?? CurrentFolder;

    public void Dispose() => _readingCts?.Dispose();

    // Folder tree: what the user collapsed stays collapsed after a restart (new folders start expanded).
    private HashSet<string>? _collapsed;

    private async Task<HashSet<string>> LoadCollapsedAsync()
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(await settings.GetAsync(SettingKeys.CollapsedFolders) ?? "[]") ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private void TrackExpansion(ObservableObject node, string key)
    {
        var collapsed = _collapsed!;
        switch (node)
        {
            case MailAccountNode account:
                account.IsExpanded = !collapsed.Contains(key);
                break;
            case MailFolderNode folder:
                folder.IsExpanded = !collapsed.Contains(key);
                break;
        }

        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MailFolderNode.IsExpanded))
            {
                return;
            }

            var expanded = node is MailAccountNode a ? a.IsExpanded : ((MailFolderNode)node).IsExpanded;
            if (expanded ? collapsed.Remove(key) : collapsed.Add(key))
            {
                _ = settings.SetAsync(SettingKeys.CollapsedFolders, System.Text.Json.JsonSerializer.Serialize(collapsed));
            }
        };
    }

    public async Task ReloadAsync()
    {
        var selectedFolder = CurrentFolder?.Folder;
        var selectedMessage = SelectedMessage?.Summary.RemoteId;

        Accounts.Clear();
        _collapsed ??= await LoadCollapsedAsync();
        foreach (var account in await accounts.GetAccountsAsync())
        {
            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail))
            {
                var folders = await mail.GetFoldersAsync(connection.Id);
                var node = new MailAccountNode(account, connection, BuildTree(folders));
                TrackExpansion(node, node.ExpansionKey);
                foreach (var folder in node.AllFolders())
                {
                    TrackExpansion(folder, folder.ExpansionKey);
                }

                Accounts.Add(node);
            }
        }

        OnPropertyChanged(nameof(HasMailAccounts));
        var allFolders = Accounts.SelectMany(a => a.AllFolders()).ToList();
        var folderToSelect = allFolders.FirstOrDefault(f => selectedFolder is not null && f.Folder.ConnectionId == selectedFolder.ConnectionId && f.Folder.RemoteId == selectedFolder.RemoteId)
                             ?? allFolders.FirstOrDefault(f => f.Folder.Role == FolderRole.Inbox);

        if (folderToSelect is null)
        {
            CurrentFolder = null;
            _allMessages = [];
            RebuildEntries();
            ReadingPane = null;
            return;
        }

        SelectedTreeItem = folderToSelect;
        await LoadFolderAsync(folderToSelect, selectedMessage);
    }

    partial void OnSelectedTreeItemChanged(object? value)
    {
        if (value is MailFolderNode folder && folder != CurrentFolder)
        {
            var select = _revealMessage;
            _revealMessage = null;
            _ = OpenFolderAsync(folder, select);
        }
    }

    // Set while the list is rebuilt in place: the selection is restored, not newly chosen by the user.
    private bool _refreshing;

    // A message to select once the folder being opened has loaded (notification click).
    private string? _revealMessage;

    partial void OnSelectedEntryChanged(MessageListEntry? value)
    {
        if (!_refreshing && value is MessageItemViewModel message)
        {
            _ = LeaveComposeThenOpenAsync(message);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildEntries();
        OnPropertyChanged(nameof(ShowServerSearchHint));
    }

    partial void OnCurrentFolderChanged(MailFolderNode? value) => OnPropertyChanged(nameof(ListTitle));

    [RelayCommand]
    private Task NewMailAsync() => StartComposeAsync(ComposeDraft.Empty, ComposeKind.New);

    // Replies to encrypted mail are encrypted again by default.
    [RelayCommand(CanExecute = nameof(CanRespond))]
    private Task ReplyAsync() => RespondAsync(MessageComposer.Reply(ReadingPane!.Message!, CurrentAccount!.Account.EmailAddress ?? string.Empty, replyAll: false), ComposeKind.Reply);

    [RelayCommand(CanExecute = nameof(CanRespond))]
    private Task ReplyAllAsync() => RespondAsync(MessageComposer.Reply(ReadingPane!.Message!, CurrentAccount!.Account.EmailAddress ?? string.Empty, replyAll: true), ComposeKind.Reply);

    [RelayCommand(CanExecute = nameof(CanRespond))]
    private Task ForwardAsync() => RespondAsync(MessageComposer.Forward(ReadingPane!.Message!), ComposeKind.Forward);

    private async Task RespondAsync(ComposeDraft draft, ComposeKind kind)
    {
        var message = SelectedMessage;
        await StartComposeAsync(draft, kind, _opened?.Security.WasEncrypted == true);

        // Replying or forwarding always means the message was read (except when the user wants to mark manually).
        if (message is { IsUnread: true } && await GetMarkAsReadModeAsync() != MarkAsReadMode.Never)
        {
            await SetReadAsync(message, read: true);
        }
    }

    private async Task<MarkAsReadMode> GetMarkAsReadModeAsync() =>
        Enum.TryParse<MarkAsReadMode>(await settings.GetAsync(SettingKeys.MarkAsRead), out var mode) ? mode : MarkAsReadMode.OnSelect;

    /// <summary>Starts a new mail to the given address, e.g. from a mailto: link in a message.</summary>
    public Task ComposeToAsync(string address) => StartComposeAsync(ComposeDraft.Empty with { To = address }, ComposeKind.New);

    private bool CanRespond() => ReadingPane?.Message is not null && CurrentAccount is not null;

    [RelayCommand]
    private Task MoveAccountUpAsync(MailAccountNode node) => MoveAccountAsync(node, -1);

    [RelayCommand]
    private Task MoveAccountDownAsync(MailAccountNode node) => MoveAccountAsync(node, +1);

    // Same order as in Einstellungen → Konten; the tree is rearranged in place (expanded folders stay open).
    private async Task MoveAccountAsync(MailAccountNode node, int delta)
    {
        var index = Accounts.IndexOf(node);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Accounts.Count)
        {
            return;
        }

        Accounts.Move(index, target);
        await accounts.SetOrderAsync(Accounts.Select(a => a.Account.Id).Distinct().ToList());
        AccountsReordered?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<MessageItemViewModel> _selectedMessages = [];

    /// <summary>Messages selected in the list (Ctrl/Shift-click), set by the view. Actions apply to all of them.</summary>
    public IReadOnlyList<MessageItemViewModel> SelectedMessages
    {
        get => _selectedMessages;
        set
        {
            _selectedMessages = value;
            OnPropertyChanged(nameof(SelectionText));
        }
    }

    /// <summary>"3 ausgewählt" above the list when more than one message is selected.</summary>
    public string? SelectionText => SelectedMessages.Count > 1 ? $"{SelectedMessages.Count} ausgewählt" : null;

    // What actions work on: every selected message, at least the open one.
    private IReadOnlyList<MessageItemViewModel> ActionTargets =>
        SelectedMessages.Count > 0 ? SelectedMessages : SelectedMessage is { } message ? [message] : [];

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync() => await RunOnMessagesAsync(ActionTargets, "Löschen", (connection, folder, ids) => mail.DeleteAsync(connection, folder, ids), removesMessages: true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ArchiveAsync() => await RunOnMessagesAsync(ActionTargets, "Archivieren", (connection, folder, ids) => mail.ArchiveAsync(connection, folder, ids), removesMessages: true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleFlagAsync()
    {
        // The first selected message decides whether all get flagged or unflagged.
        var targets = ActionTargets;
        var flag = !targets[0].IsFlagged;
        await RunOnMessagesAsync(targets, "Kennzeichnen", async (connection, folder, ids) =>
        {
            await mail.SetFlagsAsync(connection, folder, ids, MessageFlags.Flagged, flag);
            foreach (var message in targets)
            {
                message.IsFlagged = flag;
            }
        }, removesMessages: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ToggleReadAsync()
    {
        var targets = ActionTargets;
        return SetReadAsync(targets, targets[0].IsUnread);
    }

    private Task SetReadAsync(MessageItemViewModel message, bool read) => SetReadAsync([message], read);

    private async Task SetReadAsync(IReadOnlyList<MessageItemViewModel> messages, bool read)
    {
        var changing = messages.Where(m => m.IsUnread == read).ToList();
        try
        {
            foreach (var group in changing.GroupBy(FolderOf))
            {
                if (group.Key is not { } folder)
                {
                    continue;
                }

                await mail.SetFlagsAsync(folder.Account.Connection, folder.Folder, group.Select(m => m.Summary.RemoteId).ToList(), MessageFlags.Seen, read);
                foreach (var message in group)
                {
                    message.IsUnread = !read;
                }

                folder.UnreadCount += read ? -group.Count() : group.Count();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report("Gelesen-Status konnte nicht gesetzt werden", ex);
        }
    }

    private async Task StartComposeAsync(ComposeDraft draft, ComposeKind kind, bool encrypt = false)
    {
        if (CurrentAccount is not { Account.EmailAddress: not null } account)
        {
            StatusMessage?.Invoke(this, "Kein E-Mail-Konto vorhanden.");
            return;
        }

        try
        {
            var inWindow = await settings.GetBoolAsync(SettingKeys.ComposeInWindow);
            var compose = CreateCompose(account.Account, account.Connection, draft, kind, encrypt, inWindow);
            await compose.InitializeAsync();
            await ShowComposeAsync(compose);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report("Verfassen konnte nicht geöffnet werden", ex);
        }
    }

    private ComposeViewModel CreateCompose(Account account, ServiceConnection connection, ComposeDraft draft, ComposeKind kind, bool encrypt, bool inWindow)
    {
        var compose = new ComposeViewModel(mail, account, connection, draft, files, secureMime, settings, signatures, kind, encrypt, inWindow);
        compose.Closed += async (_, sent) =>
        {
            if (Compose == compose)
            {
                Compose = null;
            }

            if (sent)
            {
                StatusMessage?.Invoke(this, "Nachricht gesendet.");
            }

            await RefreshDraftsAsync(compose.Connection.Id, folderMayBeNew: false);
        };
        compose.Notice += (_, text) => StatusMessage?.Invoke(this, text);
        compose.DraftChanged += async (_, _) => await RefreshDraftsAsync(compose.Connection.Id, folderMayBeNew: true);
        compose.PopOutRequested += async (_, _) =>
        {
            // The draft moves over as HTML (with its saved copy); the inline editor is discarded afterwards.
            var dirty = compose.IsDirty;
            var (current, sign, encryptCurrent) = await compose.CaptureAsync();
            var window = CreateCompose(compose.Account, compose.Connection, current, compose.Kind, encryptCurrent, inWindow: true);
            if (dirty)
            {
                window.MarkChanged();
            }

            await window.InitializeAsync(sign, encryptCurrent);
            if (Compose == compose)
            {
                Compose = null;
            }

            await ShowComposeAsync(window);
        };
        return compose;
    }

    private async Task ShowComposeAsync(ComposeViewModel compose)
    {
        if (compose.IsWindow)
        {
            windows.ShowCompose(compose);
            return;
        }

        // Only one inline draft: the one open so far is kept in "Entwürfe".
        if (Compose is { } previous && previous != compose)
        {
            await previous.SaveOnLeaveAsync();
        }

        Compose = compose;
    }

    private async Task LeaveComposeThenOpenAsync(MessageItemViewModel message)
    {
        if (Compose is { } compose)
        {
            await compose.SaveOnLeaveAsync();
            if (Compose == compose)
            {
                Compose = null;
            }
        }

        if (SelectedEntry == message)
        {
            await OpenMessageAsync(message);
        }
    }

    /// <summary>
    /// Opens a message in its own window (double-click). It is loaded independently of the reading pane, so the window
    /// stays when the user moves on in the list or to another folder.
    /// </summary>
    public async Task OpenInWindowAsync(MessageItemViewModel message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (FolderOf(message) is not { } folder)
        {
            return;
        }

        try
        {
            var opened = await secureMime.OpenAsync(await mail.GetMessageAsync(folder.Account.Connection, folder.Folder, message.Summary.RemoteId));
            var account = folder.Account;

            Task RespondAsync(ComposeDraft draft, ComposeKind kind) =>
                StartComposeInWindowAsync(account, draft, kind, opened.Security.WasEncrypted, message, folder);

            var window = new MessageWindowViewModel(
                message.Summary.Subject,
                preferences,
                _ => RespondAsync(MessageComposer.Reply(opened.Readable, account.Account.EmailAddress ?? string.Empty, replyAll: false), ComposeKind.Reply),
                _ => RespondAsync(MessageComposer.Reply(opened.Readable, account.Account.EmailAddress ?? string.Empty, replyAll: true), ComposeKind.Reply),
                _ => RespondAsync(MessageComposer.Forward(opened.Readable), ComposeKind.Forward),
                _ => DeleteFromWindowAsync(message, folder));
            void Show(bool allowRemoteContent) =>
                window.Pane = ReadingPaneViewModel.ForMessage(message.Summary, opened, allowRemoteContent, files, _imageClient, () => Show(allowRemoteContent: true));
            Show(allowRemoteContent: false);
            windows.ShowMessage(window);

            if (message.IsUnread && await GetMarkAsReadModeAsync() != MarkAsReadMode.Never)
            {
                await SetReadAsync(message, read: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report("Nachricht konnte nicht geöffnet werden", ex);
        }
    }

    // From a message window, replies always get their own window as well.
    private async Task StartComposeInWindowAsync(MailAccountNode account, ComposeDraft draft, ComposeKind kind, bool encrypt, MessageItemViewModel message, MailFolderNode folder)
    {
        try
        {
            var compose = CreateCompose(account.Account, account.Connection, draft, kind, encrypt, inWindow: true);
            await compose.InitializeAsync();
            await ShowComposeAsync(compose);
            if (message.IsUnread && CurrentFolder == folder && await GetMarkAsReadModeAsync() != MarkAsReadMode.Never)
            {
                await SetReadAsync(message, read: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report("Antworten fehlgeschlagen", ex);
        }
    }

    private async Task<bool> DeleteFromWindowAsync(MessageItemViewModel message, MailFolderNode folder)
    {
        // Still in the list: delete like from the toolbar (the list and counters follow).
        if (_allMessages.Contains(message))
        {
            return await RunOnMessagesAsync([message], "Löschen", (connection, f, ids) => mail.DeleteAsync(connection, f, ids), removesMessages: true);
        }

        try
        {
            await mail.DeleteAsync(folder.Account.Connection, folder.Folder, [message.Summary.RemoteId]);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report("Löschen fehlgeschlagen", ex);
            return false;
        }
    }

    /// <summary>Continues a saved draft ("Entwürfe": "Bearbeiten" or double-click).</summary>
    [RelayCommand(CanExecute = nameof(CanEditDraft))]
    private async Task EditDraftAsync()
    {
        if (SelectedMessage is not { } message || FolderOf(message) is not { } folder)
        {
            return;
        }

        try
        {
            var mime = await mail.GetMessageAsync(folder.Account.Connection, folder.Folder, message.Summary.RemoteId);
            var draft = MessageComposer.FromDraft(mime, message.Summary.RemoteId);
            if (Compose is { IsFinished: false } open && open.MessageId == draft.MessageId)
            {
                return;
            }

            var inWindow = await settings.GetBoolAsync(SettingKeys.ComposeInWindow);
            var compose = CreateCompose(folder.Account.Account, folder.Account.Connection, draft, ComposeKind.New, encrypt: false, inWindow);
            await compose.InitializeAsync();
            await ShowComposeAsync(compose);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report("Entwurf konnte nicht geöffnet werden", ex);
        }
    }

    private bool CanEditDraft() => IsDraftsFolder && HasSelection;

    // The open "Entwürfe" list follows saves, and a folder just created for the first draft appears in the tree –
    // but not while a draft is open inline (reloading would close it).
    private async Task RefreshDraftsAsync(Guid connectionId, bool folderMayBeNew)
    {
        if (Compose is not null)
        {
            return;
        }

        if (folderMayBeNew && !Accounts.SelectMany(a => a.AllFolders()).Any(f => f.Folder.ConnectionId == connectionId && f.Folder.Role == FolderRole.Drafts))
        {
            await ReloadAsync();
        }
        else if (IsDraftsFolder && CurrentFolder is { } folder && folder.Folder.ConnectionId == connectionId)
        {
            await LoadFolderAsync(folder, SelectedMessage?.Summary.RemoteId);
        }
    }

    /// <summary>Drag &amp; drop target check: any other folder, also of another account.</summary>
    public bool CanMoveTo(MailFolderNode target) => IsSearchMode || (CurrentFolder is { } folder && target != folder);

    /// <summary>
    /// Moves messages (dragged from the list) into <paramref name="target"/>. Within an account the server moves them
    /// (IMAP MOVE); into another account each message is copied there and only then removed here.
    /// </summary>
    public async Task MoveToFolderAsync(IReadOnlyList<MessageItemViewModel> messages, MailFolderNode target)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(target);
        // Messages already in the target stay where they are (a search may mix folders).
        messages = messages.Where(m => FolderOf(m) is { } from && from != target).ToList();
        if (!CanMoveTo(target) || messages.Count == 0)
        {
            return;
        }

        var sameAccount = messages.All(m => FolderOf(m)!.Account.Connection.Id == target.Account.Connection.Id);
        var unread = messages.Count(m => m.IsUnread);
        var what = messages.Count == 1 ? "Nachricht" : $"{messages.Count} Nachrichten";
        if (!sameAccount)
        {
            StatusMessage?.Invoke(this, $"{what} werden ins Konto {target.Account.Title} verschoben …");
        }

        Func<ServiceConnection, MailFolder, IReadOnlyCollection<string>, Task> operation = sameAccount
            ? (connection, folder, ids) => mail.MoveAsync(connection, folder, ids, target.Folder)
            : (connection, folder, ids) => connection.Id == target.Account.Connection.Id
                ? mail.MoveAsync(connection, folder, ids, target.Folder)
                : mail.MoveToAccountAsync(connection, folder, messages.Where(m => ids.Contains(m.Summary.RemoteId) && FolderOf(m)!.Folder == folder).Select(m => m.Summary).ToList(), target.Account.Connection, target.Folder);

        if (await RunOnMessagesAsync(messages, "Verschieben", operation, removesMessages: true))
        {
            target.UnreadCount += unread;
            StatusMessage?.Invoke(this, sameAccount
                ? $"{what} nach «{target.Name}» verschoben."
                : $"{what} nach «{target.Name}» ({target.Account.Title}) verschoben.");
        }
    }

    /// <returns>True if the operation succeeded.</returns>
    private async Task<bool> RunOnMessagesAsync(IReadOnlyList<MessageItemViewModel> messages, string action, Func<ServiceConnection, MailFolder, IReadOnlyCollection<string>, Task> operation, bool removesMessages)
    {
        if (messages.Count == 0 || messages.Any(m => FolderOf(m) is null))
        {
            return false;
        }

        try
        {
            var index = messages.Select(m => Entries.IndexOf(m)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
            foreach (var group in messages.GroupBy(m => FolderOf(m)!))
            {
                await operation(group.Key.Account.Connection, group.Key.Folder, group.Select(m => m.Summary.RemoteId).ToList());
                if (removesMessages)
                {
                    group.Key.UnreadCount -= group.Count(m => m.IsUnread);
                }
            }

            if (!removesMessages)
            {
                return true;
            }

            var wasSelected = SelectedMessage is { } open && messages.Contains(open);
            var removed = messages.ToHashSet();
            _allMessages = _allMessages.Where(m => !removed.Contains(m)).ToList();
            SelectedMessages = [];
            RebuildEntries();

            // Continue with the next message (only if the open one was removed).
            if (wasSelected)
            {
                ReadingPane = null;
                SelectedEntry = Entries.Skip(Math.Max(0, index)).OfType<MessageItemViewModel>().FirstOrDefault()
                                ?? Entries.OfType<MessageItemViewModel>().LastOrDefault();
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report($"{action} fehlgeschlagen", ex);
            if (removesMessages && !IsSearchMode && CurrentFolder is { } folder)
            {
                // Part of a multi-message move may have succeeded: show what is really there now.
                await LoadFolderAsync(folder, SelectedMessage?.Summary.RemoteId);
            }

            return false;
        }
    }

    private async Task OpenFolderAsync(MailFolderNode folder, string? messageToSelect = null)
    {
        await LoadFolderAsync(folder, messageToSelect);

        // Opening a folder refreshes it from the server in the background.
        try
        {
            await mail.SyncFolderAsync(folder.Account.Connection, folder.Folder);
            if (CurrentFolder == folder)
            {
                var updated = (await mail.GetFoldersAsync(folder.Folder.ConnectionId)).FirstOrDefault(f => f.RemoteId == folder.Folder.RemoteId);
                folder.UnreadCount = updated?.UnreadCount ?? folder.UnreadCount;
                await RefreshCurrentFolderAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Folder sync failed for {Folder}", folder.Folder.RemoteId);
        }
    }

    /// <summary>
    /// After a sync (periodic or push): updates unread counts and the open list in place. Messages already shown keep
    /// their entry, so the selection, the reading pane and a draft being written stay as they are. Rebuilds the tree
    /// only when accounts or folders changed.
    /// </summary>
    public async Task RefreshAfterSyncAsync()
    {
        var structure = new List<(MailAccountNode? Node, IReadOnlyList<MailFolder> Folders)>();
        var index = 0;
        foreach (var account in await accounts.GetAccountsAsync())
        {
            foreach (var connection in account.ConnectionsOf(ServiceKind.Mail))
            {
                var node = index < Accounts.Count && Accounts[index].Connection.Id == connection.Id && Accounts[index].Title == MailAccountNode.TitleOf(account) ? Accounts[index] : null;
                structure.Add((node, await mail.GetFoldersAsync(connection.Id)));
                index++;
            }
        }

        var unchanged = structure.Count == Accounts.Count && structure.All(s => s.Node is { } node
            && node.AllFolders().Select(f => f.Folder.RemoteId).Order(StringComparer.Ordinal)
                .SequenceEqual(s.Folders.Select(f => f.RemoteId).Order(StringComparer.Ordinal)));
        if (!unchanged)
        {
            await ReloadAsync();
            return;
        }

        foreach (var (node, folders) in structure)
        {
            var byId = folders.ToDictionary(f => f.RemoteId, StringComparer.Ordinal);
            foreach (var folder in node!.AllFolders())
            {
                folder.UnreadCount = byId[folder.Folder.RemoteId].UnreadCount;
            }
        }

        await RefreshCurrentFolderAsync();
    }

    /// <summary>A push sync of one folder: counts of that folder, and the list if it is open.</summary>
    public async Task RefreshFolderAsync(Guid connectionId, string folderRemoteId)
    {
        var node = Accounts.SelectMany(a => a.AllFolders()).FirstOrDefault(f => f.Folder.ConnectionId == connectionId && f.Folder.RemoteId == folderRemoteId);
        if (node is null)
        {
            return;
        }

        var updated = (await mail.GetFoldersAsync(connectionId)).FirstOrDefault(f => f.RemoteId == folderRemoteId);
        node.UnreadCount = updated?.UnreadCount ?? node.UnreadCount;
        if (CurrentFolder == node)
        {
            await RefreshCurrentFolderAsync();
        }
    }

    /// <summary>Shows a message (notification click): opens its folder and selects it.</summary>
    public async Task RevealAsync(Guid connectionId, string folderRemoteId, string remoteId)
    {
        var node = Accounts.SelectMany(a => a.AllFolders()).FirstOrDefault(f => f.Folder.ConnectionId == connectionId && f.Folder.RemoteId == folderRemoteId);
        if (node is null)
        {
            return;
        }

        if (CurrentFolder != node)
        {
            _revealMessage = remoteId;
            SelectedTreeItem = node;
            return;
        }

        await RefreshCurrentFolderAsync();
        SelectedEntry = _allMessages.FirstOrDefault(m => m.Summary.RemoteId == remoteId) ?? SelectedEntry;
    }

    private async Task RefreshCurrentFolderAsync()
    {
        if (CurrentFolder is not { } folder)
        {
            return;
        }

        // A search result list stays until the search is closed.
        if (IsSearchMode)
        {
            return;
        }

        var summaries = await mail.GetMessagesAsync(folder.Folder, take: Math.Max(PageSize, _allMessages.Count));
        if (CurrentFolder != folder || IsSearchMode)
        {
            return;
        }

        await UpdateCountsAsync(folder);

        var existing = _allMessages.ToDictionary(m => m.Summary.RemoteId, StringComparer.Ordinal);
        var items = new List<MessageItemViewModel>(summaries.Count);
        foreach (var summary in summaries)
        {
            if (existing.TryGetValue(summary.RemoteId, out var item))
            {
                item.IsUnread = !summary.Flags.HasFlag(MessageFlags.Seen);
                item.IsFlagged = summary.Flags.HasFlag(MessageFlags.Flagged);
                items.Add(item);
            }
            else
            {
                items.Add(new MessageItemViewModel(summary, folder));
            }
        }

        if (items.SequenceEqual(_allMessages))
        {
            return;
        }

        var selected = SelectedEntry as MessageItemViewModel;
        _refreshing = true;
        try
        {
            _allMessages = items;
            RebuildEntries();
            SelectedEntry = selected is not null && items.Contains(selected) ? selected : null;
        }
        finally
        {
            _refreshing = false;
        }

        // The open message was deleted or moved elsewhere.
        if (selected is not null && SelectedEntry is null && Compose is null)
        {
            ReadingPane = null;
        }
    }

    private async Task LoadFolderAsync(MailFolderNode folder, string? messageToSelect)
    {
        EndSearch();
        CurrentFolder = folder;
        var summaries = await mail.GetMessagesAsync(folder.Folder, take: PageSize);
        _allMessages = summaries.Select(s => new MessageItemViewModel(s, folder)).ToList();
        RebuildEntries();
        await UpdateCountsAsync(folder);

        SelectedEntry = messageToSelect is null ? null : _allMessages.FirstOrDefault(m => m.Summary.RemoteId == messageToSelect);
        if (SelectedEntry is null && Compose is null)
        {
            ReadingPane = null;
        }
    }

    // ---- Loading older mail -------------------------------------------------------------------------------------

    /// <summary>The list shows this many messages at a time; scrolling down loads the next ones (settable for tests).</summary>
    internal static int PageSize { get; set; } = 500;

    private int _storedCount;

    /// <summary>"1500 von 8200 Nachrichten" below the list.</summary>
    [ObservableProperty]
    public partial string? LoadedText { get; set; }

    /// <summary>More messages than shown: in the local store or still on the server.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoadingMore { get; set; }

    private async Task UpdateCountsAsync(MailFolderNode folder)
    {
        _storedCount = await mail.CountStoredAsync(folder.Folder);
        var total = (await mail.GetFoldersAsync(folder.Folder.ConnectionId)).FirstOrDefault(f => f.RemoteId == folder.Folder.RemoteId)?.TotalCount ?? 0;
        total = Math.Max(total, _storedCount);
        HasMore = !IsSearchMode && (_allMessages.Count < _storedCount || _storedCount < total);
        LoadedText = IsSearchMode || total == 0 ? null : $"{_allMessages.Count:N0} von {total:N0} Nachrichten";
    }

    /// <summary>The next messages: from the local store first, then older ones from the server.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (CurrentFolder is not { } folder || IsSearchMode)
        {
            return;
        }

        IsLoadingMore = true;
        try
        {
            if (_allMessages.Count >= _storedCount)
            {
                await mail.LoadOlderAsync(folder.Account.Connection, folder.Folder, PageSize);
            }

            var more = await mail.GetMessagesAsync(folder.Folder, _allMessages.Count, PageSize);
            if (CurrentFolder != folder || IsSearchMode)
            {
                return;
            }

            var known = _allMessages.Select(m => m.Summary.RemoteId).ToHashSet(StringComparer.Ordinal);
            var selected = SelectedEntry;
            _refreshing = true;
            try
            {
                _allMessages = [.. _allMessages, .. more.Where(m => known.Add(m.RemoteId)).Select(m => new MessageItemViewModel(m, folder))];
                RebuildEntries();
                SelectedEntry = selected;
            }
            finally
            {
                _refreshing = false;
            }

            await UpdateCountsAsync(folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report("Ältere Nachrichten konnten nicht geladen werden", ex);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private bool CanLoadMore() => HasMore && !IsLoadingMore;

    // ---- Search -----------------------------------------------------------------------------------------------

    /// <summary>The list shows search results (from the server), not a folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListTitle), nameof(ShowServerSearchHint))]
    public partial bool IsSearchMode { get; set; }

    /// <summary>"Erweiterte Suche" is open above the list.</summary>
    [ObservableProperty]
    public partial bool IsAdvancedSearchOpen { get; set; }

    [ObservableProperty]
    public partial string SearchFrom { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchSubject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchBody { get; set; } = string.Empty;

    /// <summary>Where to search: a folder, or all folders of an account.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SearchScope> SearchScopes { get; set; } = [];

    [ObservableProperty]
    public partial SearchScope? SearchScopeChoice { get; set; }

    [ObservableProperty]
    public partial bool SearchSubfolders { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSearchCommand), nameof(SearchServerCommand))]
    public partial bool IsSearching { get; set; }

    /// <summary>"23 Treffer in Posteingang und Unterordnern" above the results.</summary>
    [ObservableProperty]
    public partial string? SearchInfo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListTitle))]
    public partial string? SearchTitle { get; set; }

    public string ListTitle => IsSearchMode ? SearchTitle ?? "Suchergebnisse" : CurrentFolder?.Name ?? string.Empty;

    /// <summary>Under a quick search: the loaded messages were filtered; offer the whole folder on the server.</summary>
    public bool ShowServerSearchHint => !IsSearchMode && SearchText.Trim().Length > 0 && CurrentFolder is not null;

    [RelayCommand]
    private void ToggleAdvancedSearch()
    {
        IsAdvancedSearchOpen = !IsAdvancedSearchOpen;
        if (IsAdvancedSearchOpen)
        {
            SearchScopes = SearchScope.For(Accounts);
            SearchScopeChoice = SearchScopes.FirstOrDefault(s => s.Folder is { } f && f == CurrentFolder) ?? SearchScopes.FirstOrDefault();
            if (SearchBody.Length == 0 && SearchFrom.Length == 0 && SearchSubject.Length == 0)
            {
                SearchBody = SearchText.Trim();
            }
        }
    }

    /// <summary>"Suchen" in the advanced search: sender, subject and text in the chosen folders, on the server.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task RunSearchAsync()
    {
        if (SearchScopeChoice is not { } scope)
        {
            return Task.CompletedTask;
        }

        var query = new MailSearchQuery(Clean(SearchFrom), Clean(SearchSubject), Clean(SearchBody));
        return SearchAsync(query, scope.Folders(SearchSubfolders), scope.Describe(SearchSubfolders));
    }

    /// <summary>The quick search term in the whole open folder on the server (also the text of messages).</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchServerAsync() =>
        CurrentFolder is { } folder && SearchText.Trim() is { Length: > 0 } term
            ? SearchAsync(new MailSearchQuery(Anywhere: term), [folder], folder.Name)
            : Task.CompletedTask;

    private bool CanSearch() => !IsSearching;

    private static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task SearchAsync(MailSearchQuery query, IReadOnlyList<MailFolderNode> folders, string where)
    {
        if (query.IsEmpty)
        {
            StatusMessage?.Invoke(this, "Bitte mindestens einen Suchbegriff eingeben.");
            return;
        }

        IsSearching = true;
        SearchInfo = "Suche läuft …";
        try
        {
            var nodes = folders.ToDictionary(f => (f.Folder.ConnectionId, f.Folder.RemoteId));
            var result = await mail.SearchAsync(folders.Select(f => f.Folder).ToList(), query);
            var several = folders.Select(f => f.Folder.ConnectionId).Distinct().Count() > 1;
            var items = result.Hits.Select(hit =>
            {
                var node = nodes[(hit.Folder.ConnectionId, hit.Folder.RemoteId)];
                return new MessageItemViewModel(hit.Message, node)
                {
                    FolderText = folders.Count > 1 ? (several ? $"{node.Name} · {node.Account.Title}" : node.Name) : null,
                };
            }).ToList();

            _refreshing = true;
            try
            {
                IsSearchMode = true;
                SearchTitle = $"Suche in {where}";
                SearchText = string.Empty;
                _allMessages = items;
                SelectedMessages = [];
                RebuildEntries();
                SelectedEntry = null;
            }
            finally
            {
                _refreshing = false;
            }

            if (Compose is null)
            {
                ReadingPane = null;
            }

            HasMore = false;
            LoadedText = null;
            SearchInfo = (items.Count == 1 ? "1 Treffer" : $"{items.Count} Treffer")
                         + (result.IsTruncated ? " – nur die neuesten pro Ordner, bitte genauer suchen" : string.Empty)
                         + (result.FailedFolders.Count > 0 ? $" – {result.FailedFolders.Count} Ordner konnten nicht durchsucht werden" : string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report("Suche fehlgeschlagen", ex);
            SearchInfo = "Suche fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Back from the results to the folder.</summary>
    [RelayCommand]
    private async Task CloseSearchAsync()
    {
        EndSearch();
        IsAdvancedSearchOpen = false;
        if (CurrentFolder is { } folder)
        {
            await LoadFolderAsync(folder, null);
        }
    }

    private void EndSearch()
    {
        IsSearchMode = false;
        SearchTitle = null;
        SearchInfo = null;
    }

    private async Task OpenMessageAsync(MessageItemViewModel message)
    {
        if (FolderOf(message) is not { } folder)
        {
            return;
        }

        if (_readingCts is not null)
        {
            await _readingCts.CancelAsync();
            _readingCts.Dispose();
        }

        _readingCts = new CancellationTokenSource();
        var token = _readingCts.Token;
        _opened = null;
        ReadingPane = ReadingPaneViewModel.Loading(message.Summary);

        try
        {
            var mime = await mail.GetMessageAsync(folder.Account.Connection, folder.Folder, message.Summary.RemoteId, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _opened = await secureMime.OpenAsync(mime, token);
            ShowOpened(message.Summary, allowRemoteContent: false);

            if (message.IsUnread)
            {
                switch (await GetMarkAsReadModeAsync())
                {
                    case MarkAsReadMode.OnSelect:
                        await SetReadAsync(message, read: true);
                        break;
                    case MarkAsReadMode.AfterDelay:
                        // Selecting another message cancels the token, so only a message really read gets marked.
                        await Task.Delay(ReadDelay, token);
                        if (SelectedMessage == message)
                        {
                            await SetReadAsync(message, read: true);
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Loading message {RemoteId} failed", message.Summary.RemoteId);
            ReadingPane = ReadingPaneViewModel.Info("Die Nachricht konnte nicht geladen werden: " + ex.Message);
        }
    }

    private void ShowOpened(MessageSummary summary, bool allowRemoteContent)
    {
        if (_opened is not { } opened)
        {
            return;
        }

        ReadingPane = ReadingPaneViewModel.ForMessage(summary, opened, allowRemoteContent, files, _imageClient, () => ShowOpened(summary, allowRemoteContent: true));
        if (_invitation is { } shown && shown.RemoteId == summary.RemoteId)
        {
            ReadingPane.Invitation = shown.Banner;
        }
        else if ((SelectedMessage is { } open ? FolderOf(open) : CurrentFolder) is { } folder && ITip.FromMessage(opened.Readable) is { } invitation)
        {
            _ = ShowInvitationAsync(summary, invitation, folder.Account.Account, ReadingPane);
        }
    }

    private async Task ShowInvitationAsync(MessageSummary summary, Invitation invitation, Account account, ReadingPaneViewModel pane)
    {
        InvitationBannerViewModel? banner = null;
        banner = new InvitationBannerViewModel(
            invitation,
            async answer =>
            {
                await invitations.RespondAsync(invitation, answer, account, banner!.IsInCalendar ? null : banner.SelectedCalendar);
                StatusMessage?.Invoke(this, answer switch
                {
                    Participation.Accepted => "Zugesagt – der Termin steht im Kalender.",
                    Participation.Tentative => "Mit Vorbehalt zugesagt – der Termin steht im Kalender.",
                    _ => "Abgesagt.",
                });
                CalendarChanged?.Invoke(this, EventArgs.Empty);
                await UpdateInvitationAsync(banner!, account);
            },
            async () =>
            {
                await invitations.ApplyCancelAsync(invitation);
                StatusMessage?.Invoke(this, "Der abgesagte Termin wurde aus dem Kalender entfernt.");
                CalendarChanged?.Invoke(this, EventArgs.Empty);
                await UpdateInvitationAsync(banner!, account);
            });
        _invitation = (summary.RemoteId, banner);
        pane.Invitation = banner;

        try
        {
            // An answer to my own invitation is recorded right away, like in office calendars.
            if (invitation.Method == InvitationMethod.Reply && await invitations.ApplyReplyAsync(invitation))
            {
                CalendarChanged?.Invoke(this, EventArgs.Empty);
            }

            await UpdateInvitationAsync(banner, account);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Checking the invitation {Uid} failed", invitation.Uid);
            banner.Error = "Der Kalender konnte nicht geprüft werden: " + ex.Message;
        }
    }

    // Status text, collisions and which buttons make sense – from the calendar as it is now.
    private async Task UpdateInvitationAsync(InvitationBannerViewModel banner, Account account)
    {
        var invitation = banner.Invitation;
        var state = await invitations.GetStateAsync(invitation, account);
        var mine = InvitationService.IsMine(invitation, account.EmailAddress) && invitation.Method != InvitationMethod.Reply;
        banner.IsInCalendar = state.Item is not null;
        if (banner.Calendars.Count == 0)
        {
            banner.Calendars = await invitations.TargetCalendarsAsync(account);
            banner.SelectedCalendar = banner.Calendars.FirstOrDefault();
        }
        switch (invitation.Method)
        {
            case InvitationMethod.Reply:
                var answer = invitation.Attendees.FirstOrDefault();
                var who = answer?.Name ?? answer?.Email ?? "Jemand";
                banner.Status = answer?.Status switch
                {
                    Participation.Accepted => $"{who} hat zugesagt.",
                    Participation.Tentative => $"{who} hat mit Vorbehalt zugesagt.",
                    Participation.Declined => $"{who} hat abgesagt.",
                    _ => $"{who} hat geantwortet.",
                } + (state.Item is null ? " Der Termin ist nicht (mehr) in Ihrem Kalender." : " Die Antwort ist in Ihrem Termin eingetragen.");
                banner.CanRespond = false;
                break;

            case InvitationMethod.Cancel:
                banner.Status = state.Item is null ? "Der Termin ist nicht (mehr) in Ihrem Kalender." : "Der Termin steht noch in Ihrem Kalender.";
                banner.CanRemove = state.Item is not null && state.Calendar is { IsReadOnly: false };
                break;

            default:
                banner.Status = mine ? "Sie sind der Organisator dieses Termins."
                    : state.IsOutdated ? "Diese Einladung ist veraltet – im Kalender steht bereits eine neuere Fassung."
                    : state.MyAnswer switch
                    {
                        Participation.Accepted when state.Item is not null => "Sie haben zugesagt.",
                        Participation.Tentative when state.Item is not null => "Sie haben mit Vorbehalt zugesagt.",
                        Participation.Declined => "Sie haben abgesagt.",
                        _ when state.Item is not null => "Steht in Ihrem Kalender – noch nicht beantwortet.",
                        _ => "Noch nicht beantwortet.",
                    };
                banner.CanRespond = !mine && !state.IsOutdated;
                break;
        }

        banner.Conflicts = invitation.Method == InvitationMethod.Request && state.Conflicts.Count > 0
            ? "Überschneidet sich mit: " + string.Join(", ", state.Conflicts.Take(3).Select(c => $"{c.Summary} ({c.Start:HH:mm}–{c.End:HH:mm})"))
            : null;
    }

    private void RebuildEntries()
    {
        Entries.Clear();
        var query = SearchText.Trim();
        var messages = query.Length == 0 ? _allMessages : _allMessages.Where(m => m.Matches(query)).ToList();

        foreach (var group in messages.GroupBy(m => DateGroup.Of(m.Summary.Date)))
        {
            Entries.Add(new MessageGroupHeader(group.Key));
            foreach (var message in group)
            {
                Entries.Add(message);
            }
        }

        OnPropertyChanged(nameof(HasMessages));
    }

    private void Report(string what, Exception ex)
    {
        logger.LogWarning(ex, "{Action}", what);
        StatusMessage?.Invoke(this, $"{what}: {ex.Message}");
    }

    private static List<MailFolderNode> BuildTree(IReadOnlyList<MailFolder> folders)
    {
        var nodes = folders.ToDictionary(f => f.RemoteId, f => new MailFolderNode(f), StringComparer.Ordinal);
        var roots = new List<MailFolderNode>();
        foreach (var node in folders.Select(f => nodes[f.RemoteId]))
        {
            if (node.Folder.ParentRemoteId is { } parent && nodes.TryGetValue(parent, out var parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        return roots;
    }
}

internal sealed partial class MailAccountNode : ObservableObject
{
    public MailAccountNode(Account account, ServiceConnection connection, List<MailFolderNode> folders)
    {
        Account = account;
        Connection = connection;
        Folders = new ObservableCollection<MailFolderNode>(folders);
        foreach (var folder in AllFolders())
        {
            folder.Account = this;
        }
    }

    public Account Account { get; }

    public ServiceConnection Connection { get; }

    public string Title => TitleOf(Account);

    public static string TitleOf(Account account) => account.EmailAddress ?? account.DisplayName;

    public ObservableCollection<MailFolderNode> Folders { get; }

    /// <summary>Remembered across restarts (see <see cref="MailViewModel"/>).</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public string ExpansionKey => "account:" + Connection.Id.ToString("N");

    public IEnumerable<MailFolderNode> AllFolders() => Folders.SelectMany(f => f.SelfAndDescendants());
}

internal sealed partial class MailFolderNode(MailFolder folder) : ObservableObject
{
    /// <summary>Set by <see cref="MailAccountNode"/> when the tree is attached.</summary>
    public MailAccountNode Account { get; set; } = null!;

    public MailFolder Folder { get; } = folder;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public string ExpansionKey => Folder.ConnectionId.ToString("N") + "|" + Folder.RemoteId;

    /// <summary>Special folders get familiar German names, whatever the server calls them (INBOX, Sent, Trash …).</summary>
    public string Name => Folder.Role switch
    {
        FolderRole.Inbox => "Posteingang",
        FolderRole.Drafts => "Entwürfe",
        FolderRole.Sent => "Gesendete Elemente",
        FolderRole.Trash => "Gelöschte Elemente",
        FolderRole.Junk => "Junk-E-Mail",
        FolderRole.Archive => "Archiv",
        _ => Folder.Name,
    };

    public ObservableCollection<MailFolderNode> Children { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadText), nameof(HasUnread))]
    public partial int UnreadCount { get; set; } = folder.UnreadCount;

    public bool HasUnread => UnreadCount > 0 && Folder.Role is not (FolderRole.Sent or FolderRole.Drafts);

    /// <summary>" (3)" after the folder name, empty when nothing is unread.</summary>
    public string UnreadText => HasUnread ? $" ({UnreadCount.ToString(CultureInfo.CurrentCulture)})" : string.Empty;

    public IEnumerable<MailFolderNode> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);
}

internal abstract class MessageListEntry : ObservableObject
{
    public abstract bool IsSelectable { get; }
}

internal sealed class MessageGroupHeader(string title) : MessageListEntry
{
    public string Title { get; } = title;

    public override bool IsSelectable => false;
}

internal sealed partial class MessageItemViewModel(MessageSummary summary, MailFolderNode? folder = null) : MessageListEntry
{
    public MessageSummary Summary { get; } = summary;

    /// <summary>The folder the message is in – for search results from several folders, each its own.</summary>
    public MailFolderNode? Folder { get; } = folder;

    /// <summary>Shown in search results: "Posteingang · anna@example.com".</summary>
    public string? FolderText { get; init; }

    public bool ShowFolder => FolderText is not null;

    public override bool IsSelectable => true;

    public string Sender => Summary.From?.DisplayText ?? "(unbekannt)";

    public string Subject => string.IsNullOrWhiteSpace(Summary.Subject) ? "(kein Betreff)" : Summary.Subject;

    public string Preview => Summary.Preview?.Trim() is { Length: > 0 } preview
        ? preview
        : IsEncrypted ? "Verschlüsselte Nachricht – Inhalt wird beim Öffnen entschlüsselt" : string.Empty;

    public bool HasAttachments => Summary.HasAttachments;

    public bool IsSigned => Summary.Security.HasFlag(MessageSecurity.Signed);

    public bool IsEncrypted => Summary.Security.HasFlag(MessageSecurity.Encrypted);

    public string TimeText => DateGroup.ShortTime(Summary.Date);

    [ObservableProperty]
    public partial bool IsUnread { get; set; } = !summary.Flags.HasFlag(MessageFlags.Seen);

    [ObservableProperty]
    public partial bool IsFlagged { get; set; } = summary.Flags.HasFlag(MessageFlags.Flagged);

    public bool Matches(string query) =>
        Subject.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Sender.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (Summary.From?.Address.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || Preview.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}

internal static class DateGroup
{
    public static string Of(DateTimeOffset date)
    {
        var day = date.LocalDateTime.Date;
        var today = DateTime.Today;
        var startOfWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        if (day >= today)
        {
            return "Heute";
        }

        if (day == today.AddDays(-1))
        {
            return "Gestern";
        }

        if (day >= startOfWeek)
        {
            return "Diese Woche";
        }

        return day >= startOfWeek.AddDays(-7) ? "Letzte Woche" : "Älter";
    }

    public static string ShortTime(DateTimeOffset date)
    {
        var local = date.LocalDateTime;
        if (local.Date == DateTime.Today)
        {
            return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        return local.Date > DateTime.Today.AddDays(-7)
            ? local.ToString("ddd HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
    }
}

/// <summary>Where the advanced search looks: one folder (with or without its subfolders) or all folders of an account.</summary>
internal sealed record SearchScope(string Label, MailAccountNode Account, MailFolderNode? Folder)
{
    public bool IsAccount => Folder is null;

    public IReadOnlyList<MailFolderNode> Folders(bool withSubfolders) =>
        Folder is null ? Account.AllFolders().ToList()
        : withSubfolders ? [Folder, .. Descendants(Folder)]
        : [Folder];

    public string Describe(bool withSubfolders) =>
        Folder is null ? $"allen Ordnern von {Account.Title}"
        : withSubfolders && Folder.Children.Count > 0 ? $"{Folder.Name} und Unterordnern"
        : Folder.Name;

    /// <summary>Per account: "Alle Ordner", then its folders indented by depth.</summary>
    public static IReadOnlyList<SearchScope> For(IEnumerable<MailAccountNode> accounts)
    {
        var result = new List<SearchScope>();
        foreach (var account in accounts)
        {
            result.Add(new SearchScope($"Alle Ordner – {account.Title}", account, null));
            void Add(IEnumerable<MailFolderNode> folders, int depth)
            {
                foreach (var folder in folders)
                {
                    result.Add(new SearchScope(new string(' ', 4 * depth) + folder.Name, account, folder));
                    Add(folder.Children, depth + 1);
                }
            }

            Add(account.Folders, 1);
        }

        return result;
    }

    private static IEnumerable<MailFolderNode> Descendants(MailFolderNode folder) =>
        folder.Children.SelectMany(c => (IEnumerable<MailFolderNode>)[c, .. Descendants(c)]);
}
