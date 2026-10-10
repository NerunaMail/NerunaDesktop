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
using static Neruna.Core.Localization.Texts;

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
    RecipientDirectory recipients,
    TextTemplateService textTemplates,
    AgendaViewModel agenda,
    ILogger<MailViewModel> logger) : ViewModelBase, IDisposable, IMailSearchHost
{
    /// <summary>Drafts with changes not yet stored – in the reading pane or in their own windows.</summary>
    public IReadOnlyList<ComposeViewModel> UnsavedDrafts =>
        [.. new[] { Compose }.Concat(windows.OpenComposes).OfType<ComposeViewModel>().Distinct().Where(c => !c.IsFinished && c.IsDirty)];

    /// <summary>"Tagesansicht": the next appointments beside the mail.</summary>
    public AgendaViewModel Agenda => agenda;

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

    private MailFolderTreeViewModel? _tree;

    /// <summary>The folder tree: accounts, folders, "Favoriten"; choosing a folder opens it here.</summary>
    public MailFolderTreeViewModel Tree
    {
        get
        {
            if (_tree is null)
            {
                _tree = new MailFolderTreeViewModel(accounts, mail, settings);
                _tree.FolderSelected += (_, folder) =>
                {
                    if (folder != CurrentFolder)
                    {
                        var select = _revealMessage;
                        _revealMessage = null;
                        _ = OpenFolderAsync(folder, select);
                    }
                };
            }

            return _tree;
        }
    }

    public ObservableCollection<MailAccountNode> Accounts => Tree.Accounts;

    /// <summary>Flat list of date-group headers and messages, rendered by one ListBox.</summary>
    public ObservableCollection<MessageListEntry> Entries { get; } = [];

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

    private MailAccountNode? CurrentAccount => SelectedMessage?.Folder?.Account ?? CurrentFolder?.Account ?? Accounts.FirstOrDefault();

    // The folder a message is in: its own (search results), otherwise the open folder.
    private MailFolderNode? FolderOf(MessageItemViewModel message) => message.Folder ?? CurrentFolder;

    public void Dispose() => _readingCts?.Dispose();

    public async Task ReloadAsync()
    {
        var selectedFolder = CurrentFolder?.Folder;
        // At the start: the folder open last time.
        var lastFolder = selectedFolder is null ? await settings.GetAsync(SettingKeys.LastMailFolder) : null;
        var selectedMessage = SelectedMessage?.Summary.RemoteId;
        await Tree.LoadAsync();
        ShowAutoReplyBadges(_autoReplyBadges); // the tree was built anew
        var allFolders = Accounts.SelectMany(a => a.AllFolders()).ToList();
        var folderToSelect = allFolders.FirstOrDefault(f => selectedFolder is not null && f.Folder.ConnectionId == selectedFolder.ConnectionId && f.Folder.RemoteId == selectedFolder.RemoteId)
                             ?? allFolders.FirstOrDefault(f => lastFolder is not null && FolderKey(f.Folder) == lastFolder)
                             ?? allFolders.FirstOrDefault(f => f.Folder.Role == FolderRole.Inbox);

        if (folderToSelect is null)
        {
            CurrentFolder = null;
            _allMessages = [];
            RebuildEntries();
            ReadingPane = null;
            return;
        }

        // The new tree node is the folder already open: selecting it must not load (and sync) it a second time.
        CurrentFolder = folderToSelect;
        Tree.SelectedTreeItem = folderToSelect;
        await LoadFolderAsync(folderToSelect, selectedMessage);
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

    /// <summary>The list shows only unread messages (the closed envelope beside the search).</summary>
    [ObservableProperty]
    public partial bool ShowUnreadOnly { get; set; }

    partial void OnShowUnreadOnlyChanged(bool value)
    {
        // Filtering keeps the open message open (no reload of the reading pane).
        var open = SelectedEntry;
        _refreshing = true;
        try
        {
            RebuildEntries();
            SelectedEntry = open is not null && Entries.Contains(open) ? open : null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ---- Folder menu: all read, empty ---------------------------------------------------------------------------

    /// <summary>"Alle als gelesen markieren": on the server, also mail not loaded yet.</summary>
    [RelayCommand]
    private async Task MarkFolderReadAsync(MailFolderNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            await Task.Run(() => mail.MarkAllReadAsync(folder.Account.Connection, folder.Folder));
            folder.UnreadCount = 0;
            if (CurrentFolder == folder)
            {
                foreach (var message in _allMessages)
                {
                    message.IsUnread = false;
                }

                await RefreshCurrentFolderAsync();
            }

            StatusMessage?.Invoke(this, F("«{0}»: alle als gelesen markiert.", folder.Name));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(T("Als gelesen markieren fehlgeschlagen"), ex);
        }
    }

    /// <summary>"Leeren" (trash, junk): everything deleted for good, after a question.</summary>
    [RelayCommand]
    private async Task EmptyFolderAsync(MailFolderNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var sure = await Views.ChoiceDialog.ShowInFrontAsync(F("«{0}» leeren", folder.Name),
            F("Alle Nachrichten in «{0}» endgültig löschen – auch die, die Neruna noch nicht geladen hat? Das lässt sich nicht rückgängig machen.", folder.Name),
            (T("Leeren"), true, false), (T("Abbrechen"), false, true));
        if (!sure)
        {
            return;
        }

        try
        {
            await Task.Run(() => mail.EmptyFolderAsync(folder.Account.Connection, folder.Folder));
            folder.UnreadCount = 0;
            if (CurrentFolder == folder)
            {
                await LoadFolderAsync(folder, null);
            }

            StatusMessage?.Invoke(this, F("«{0}» geleert.", folder.Name));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(F("«{0}» leeren fehlgeschlagen", folder.Name), ex);
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
    private Task ReplyAsync() => RespondAsync(MessageComposer.Reply(ReadingPane!.Message!, OwnAddresses(CurrentAccount!.Account), replyAll: false), ComposeKind.Reply);

    [RelayCommand(CanExecute = nameof(CanRespond))]
    private Task ReplyAllAsync() => RespondAsync(MessageComposer.Reply(ReadingPane!.Message!, OwnAddresses(CurrentAccount!.Account), replyAll: true), ComposeKind.Reply);

    [RelayCommand(CanExecute = nameof(CanRespond))]
    private Task ForwardAsync() => RespondAsync(MessageComposer.Forward(ReadingPane!.Message!), ComposeKind.Forward);

    private async Task RespondAsync(ComposeDraft draft, ComposeKind kind)
    {
        var message = SelectedMessage;
        var folder = message is null ? null : FolderOf(message);
        await StartComposeAsync(draft, kind, _opened?.Security.WasEncrypted == true,
            message is null || folder is null ? null : () => MarkRespondedAsync(message, folder, kind));

        // Replying or forwarding always means the message was read (except when the user wants to mark manually).
        if (message is { IsUnread: true } && await GetMarkAsReadModeAsync() != MarkAsReadMode.Never)
        {
            await SetReadAsync(message, read: true);
        }
    }

    private async Task<MarkAsReadMode> GetMarkAsReadModeAsync() =>
        Enum.TryParse<MarkAsReadMode>(await settings.GetAsync(SettingKeys.MarkAsRead), out var mode) ? mode : MarkAsReadMode.OnSelect;

    /// <summary>A new mail prefilled from outside (a mailto: link from the system).</summary>
    public Task ComposeAsync(ComposeDraft draft) => StartComposeAsync(draft, ComposeKind.New);

    /// <summary>
    /// An .eml file opened from the system: shown in a message window. Replies go out from the current account; the
    /// file itself is not changed or deleted.
    /// </summary>
    public async Task OpenFileAsync(string path)
    {
        try
        {
            var opened = await secureMime.OpenAsync(await MimeKit.MimeMessage.LoadAsync(path));
            var message = opened.Readable;
            var summary = new MessageSummary(path, message.MessageId, message.InReplyTo, message.Subject ?? string.Empty, null, [],
                message.Date == DateTimeOffset.MinValue ? File.GetLastWriteTime(path) : message.Date, MessageFlags.Seen, new FileInfo(path).Length, false, null);

            Task RespondAsync(Func<Account, ComposeDraft> draft, ComposeKind kind) =>
                CurrentAccount is { } account ? StartComposeAsync(draft(account.Account), kind) : Task.CompletedTask;

            var window = new MessageWindowViewModel(
                summary.Subject,
                preferences,
                _ => RespondAsync(a => MessageComposer.Reply(message, OwnAddresses(a), replyAll: false), ComposeKind.Reply),
                _ => RespondAsync(a => MessageComposer.Reply(message, OwnAddresses(a), replyAll: true), ComposeKind.Reply),
                _ => RespondAsync(_ => MessageComposer.Forward(message), ComposeKind.Forward),
                _ => Task.FromResult(false))
            {
                CanDelete = false,
            };
            void Show(bool allowRemoteContent) =>
                window.Pane = ReadingPaneViewModel.ForMessage(summary, opened, allowRemoteContent, files, _imageClient, () => Show(allowRemoteContent: true));
            Show(allowRemoteContent: false);
            windows.ShowMessage(window);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(T("Nachricht konnte nicht geöffnet werden"), ex);
        }
    }

    /// <summary>Starts a new mail to the given address, e.g. from a mailto: link in a message.</summary>
    public Task ComposeToAsync(string address) => StartComposeAsync(ComposeDraft.Empty with { To = address }, ComposeKind.New);

    private bool CanRespond() => ReadingPane?.Message is not null && CurrentAccount is not null;

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
    public string? SelectionText => SelectedMessages.Count > 1 ? F("{0} ausgewählt", SelectedMessages.Count) : null;

    // What actions work on: every selected message, at least the open one.
    private IReadOnlyList<MessageItemViewModel> ActionTargets =>
        SelectedMessages.Count > 0 ? SelectedMessages : SelectedMessage is { } message ? [message] : [];

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync() => await RunOnMessagesAsync(ActionTargets, T("Löschen"), (connection, folder, ids) => mail.DeleteAsync(connection, folder, ids), removesMessages: true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ArchiveAsync() => await RunOnMessagesAsync(ActionTargets, T("Archivieren"), (connection, folder, ids) => mail.ArchiveAsync(connection, folder, ids), removesMessages: true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleFlagAsync()
    {
        // The first selected message decides whether all get flagged or unflagged.
        var targets = ActionTargets;
        var flag = !targets[0].IsFlagged;
        await RunOnMessagesAsync(targets, T("Kennzeichnen"), async (connection, folder, ids) =>
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
            Report(T("Gelesen-Status konnte nicht gesetzt werden"), ex);
        }
    }

    // Sent: the original gets "beantwortet" or "weitergeleitet" – on the server, so other programs see it too.
    private async Task MarkRespondedAsync(MessageItemViewModel message, MailFolderNode folder, ComposeKind kind)
    {
        var flag = kind == ComposeKind.Forward ? MessageFlags.Forwarded : MessageFlags.Answered;
        try
        {
            await mail.SetFlagsAsync(folder.Account.Connection, folder.Folder, [message.Summary.RemoteId], flag, add: true);
            if (flag == MessageFlags.Forwarded)
            {
                message.IsForwarded = true;
            }
            else
            {
                message.IsAnswered = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(flag == MessageFlags.Forwarded ? T("Markierung «weitergeleitet» konnte nicht gesetzt werden") : T("Markierung «beantwortet» konnte nicht gesetzt werden"), ex);
        }
    }

    private static string[] OwnAddresses(Account account) => [.. account.Identities.Select(i => i.Email)];

    private async Task StartComposeAsync(ComposeDraft draft, ComposeKind kind, bool encrypt = false, Func<Task>? afterSent = null)
    {
        if (CurrentAccount is not { Account.EmailAddress: not null } account)
        {
            StatusMessage?.Invoke(this, T("Kein E-Mail-Konto vorhanden."));
            return;
        }

        try
        {
            var inWindow = await settings.GetBoolAsync(SettingKeys.ComposeInWindow);
            var compose = CreateCompose(account.Account, account.Connection, draft, kind, encrypt, inWindow);
            compose.AfterSent = afterSent;
            await compose.InitializeAsync();
            await ShowComposeAsync(compose);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(T("Verfassen konnte nicht geöffnet werden"), ex);
        }
    }

    private ComposeViewModel CreateCompose(Account account, ServiceConnection connection, ComposeDraft draft, ComposeKind kind, bool encrypt, bool inWindow)
    {
        var compose = new ComposeViewModel(mail, account, connection, draft, files, secureMime, settings, signatures, kind, encrypt, inWindow)
        {
            Recipients = recipients,
            Preferences = preferences,
            TextTemplates = textTemplates,
        };
        compose.Closed += async (_, sent) =>
        {
            if (Compose == compose)
            {
                Compose = null;
            }

            if (sent)
            {
                StatusMessage?.Invoke(this, T("Nachricht gesendet."));
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
            window.AfterSent = compose.AfterSent;
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
                _ => RespondAsync(MessageComposer.Reply(opened.Readable, OwnAddresses(account.Account), replyAll: false), ComposeKind.Reply),
                _ => RespondAsync(MessageComposer.Reply(opened.Readable, OwnAddresses(account.Account), replyAll: true), ComposeKind.Reply),
                _ => RespondAsync(MessageComposer.Forward(opened.Readable), ComposeKind.Forward),
                _ => DeleteFromWindowAsync(message, folder));
            void Show(bool allowRemoteContent) =>
                window.Pane = ReadingPaneViewModel.ForMessage(message.Summary, opened, allowRemoteContent, files, _imageClient, () =>
                {
                    _ = RememberRemoteContentAsync(message.Summary);
                    Show(allowRemoteContent: true);
                });
            Show(await IsRemoteContentAllowedAsync(message.Summary));
            windows.ShowMessage(window);

            if (message.IsUnread && await GetMarkAsReadModeAsync() != MarkAsReadMode.Never)
            {
                await SetReadAsync(message, read: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(T("Nachricht konnte nicht geöffnet werden"), ex);
        }
    }

    // From a message window, replies always get their own window as well.
    private async Task StartComposeInWindowAsync(MailAccountNode account, ComposeDraft draft, ComposeKind kind, bool encrypt, MessageItemViewModel message, MailFolderNode folder)
    {
        try
        {
            var compose = CreateCompose(account.Account, account.Connection, draft, kind, encrypt, inWindow: true);
            compose.AfterSent = () => MarkRespondedAsync(message, folder, kind);
            await compose.InitializeAsync();
            await ShowComposeAsync(compose);
            if (message.IsUnread && CurrentFolder == folder && await GetMarkAsReadModeAsync() != MarkAsReadMode.Never)
            {
                await SetReadAsync(message, read: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(T("Antworten fehlgeschlagen"), ex);
        }
    }

    private async Task<bool> DeleteFromWindowAsync(MessageItemViewModel message, MailFolderNode folder)
    {
        // Still in the list: delete like from the toolbar (the list and counters follow).
        if (_allMessages.Contains(message))
        {
            return await RunOnMessagesAsync([message], T("Löschen"), (connection, f, ids) => mail.DeleteAsync(connection, f, ids), removesMessages: true);
        }

        try
        {
            await mail.DeleteAsync(folder.Account.Connection, folder.Folder, [message.Summary.RemoteId]);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(T("Löschen fehlgeschlagen"), ex);
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
            Report(T("Entwurf konnte nicht geöffnet werden"), ex);
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
        var what = messages.Count == 1 ? T("Nachricht") : F("{0} Nachrichten", messages.Count);
        if (!sameAccount)
        {
            StatusMessage?.Invoke(this, F("{0} werden ins Konto {1} verschoben …", what, target.Account.Title));
        }

        Func<ServiceConnection, MailFolder, IReadOnlyCollection<string>, Task> operation = sameAccount
            ? (connection, folder, ids) => mail.MoveAsync(connection, folder, ids, target.Folder)
            : (connection, folder, ids) => connection.Id == target.Account.Connection.Id
                ? mail.MoveAsync(connection, folder, ids, target.Folder)
                : mail.MoveToAccountAsync(connection, folder, messages.Where(m => ids.Contains(m.Summary.RemoteId) && FolderOf(m)!.Folder == folder).Select(m => m.Summary).ToList(), target.Account.Connection, target.Folder);

        if (await RunOnMessagesAsync(messages, T("Verschieben"), operation, removesMessages: true))
        {
            target.UnreadCount += unread;
            StatusMessage?.Invoke(this, sameAccount
                ? F("{0} nach «{1}» verschoben.", what, target.Name)
                : F("{0} nach «{1}» ({2}) verschoben.", what, target.Name, target.Account.Title));
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
            await Task.Run(() => mail.SyncFolderAsync(folder.Account.Connection, folder.Folder));
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
        if (!await Tree.UpdateCountsAsync())
        {
            await ReloadAsync();
            return;
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
            Tree.SelectedTreeItem = node;
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

        var take = Math.Max(PageSize, _allMessages.Count);
        var summaries = await Task.Run(() => mail.GetMessagesAsync(folder.Folder, take: take));
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

    private static string FolderKey(MailFolder folder) => $"{folder.ConnectionId:N}|{folder.RemoteId}";

    private async Task LoadFolderAsync(MailFolderNode folder, string? messageToSelect)
    {
        EndSearch();
        if (CurrentFolder != folder)
        {
            _ = settings.SetAsync(SettingKeys.LastMailFolder, FolderKey(folder.Folder));
        }

        CurrentFolder = folder;
        var summaries = await Task.Run(() => mail.GetMessagesAsync(folder.Folder, take: PageSize));
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
        _storedCount = await Task.Run(() => mail.CountStoredAsync(folder.Folder));
        var total = (await mail.GetFoldersAsync(folder.Folder.ConnectionId)).FirstOrDefault(f => f.RemoteId == folder.Folder.RemoteId)?.TotalCount ?? 0;
        total = Math.Max(total, _storedCount);
        HasMore = !IsSearchMode && (_allMessages.Count < _storedCount || _storedCount < total);
        LoadedText = IsSearchMode || total == 0 ? null : F("{0:N0} von {1:N0} Nachrichten", _allMessages.Count, total);
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
                await Task.Run(() => mail.LoadOlderAsync(folder.Account.Connection, folder.Folder, PageSize));
            }

            var skip = _allMessages.Count;
            var more = await Task.Run(() => mail.GetMessagesAsync(folder.Folder, skip, PageSize));
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
            Report(T("Ältere Nachrichten konnten nicht geladen werden"), ex);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private bool CanLoadMore() => HasMore && !IsLoadingMore;

    // ---- Out of office: "Abwesenheitsnotiz aktiv" under the accounts that answer automatically ------------------------

    private IReadOnlySet<Guid> _autoReplyBadges = new HashSet<Guid>();

    public void ShowAutoReplyBadges(IReadOnlySet<Guid> activeConnections)
    {
        ArgumentNullException.ThrowIfNull(activeConnections);
        _autoReplyBadges = activeConnections;
        foreach (var node in Accounts)
        {
            node.HasAutoReply = activeConnections.Contains(node.Connection.Id);
        }
    }

    // ---- Search (on the server: MailSearchViewModel; here the list shows its hits) ---------------------------------

    private MailSearchViewModel? _search;

    public MailSearchViewModel Search => _search ??= new MailSearchViewModel(mail, this);

    /// <summary>The list shows search results (from the server), not a folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListTitle), nameof(ShowServerSearchHint))]
    public partial bool IsSearchMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListTitle))]
    public partial string? SearchTitle { get; set; }

    public string ListTitle => IsSearchMode ? SearchTitle ?? T("Suchergebnisse") : CurrentFolder?.Name ?? string.Empty;

    /// <summary>Under a quick search: the loaded messages were filtered; offer the whole folder on the server.</summary>
    public bool ShowServerSearchHint => !IsSearchMode && SearchText.Trim().Length > 0 && CurrentFolder is not null;

    IReadOnlyList<MailAccountNode> IMailSearchHost.SearchAccounts => Accounts;

    void IMailSearchHost.ShowSearchResults(IReadOnlyList<MessageItemViewModel> hits, string title, string? keepTerm)
    {
        _refreshing = true;
        try
        {
            IsSearchMode = true;
            SearchTitle = title;
            SearchText = keepTerm ?? string.Empty;
            _allMessages = [.. hits];
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
    }

    void IMailSearchHost.ReportSearchFailure(Exception error) => Report(T("Suche fehlgeschlagen"), error);

    void IMailSearchHost.ShowStatus(string message) => StatusMessage?.Invoke(this, message);

    /// <summary>Back from the results to the folder.</summary>
    [RelayCommand]
    private async Task CloseSearchAsync()
    {
        IsSearchMode = false;
        SearchTitle = null;
        if (Search.Close())
        {
            SearchText = string.Empty;
        }

        if (CurrentFolder is { } folder)
        {
            await LoadFolderAsync(folder, null);
        }
    }

    private void EndSearch()
    {
        IsSearchMode = false;
        SearchTitle = null;
        Search.SearchInfo = null;
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
            ShowOpened(message.Summary, allowRemoteContent: await IsRemoteContentAllowedAsync(message.Summary));

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
            ReadingPane = ReadingPaneViewModel.Info(T("Die Nachricht konnte nicht geladen werden: ") + ex.Message);
        }
    }

    // "Bilder laden" is remembered per message (by its Message-ID): opened again, the pictures come at once.
    private const int RememberedRemoteContentLimit = 2000;
    private List<string>? _remoteContentAllowed;

    private static string RemoteContentKey(MessageSummary summary) => summary.MessageId ?? summary.Subject + "|" + summary.Date.ToUnixTimeSeconds();

    private async Task<List<string>> RemoteContentAllowedAsync()
    {
        if (_remoteContentAllowed is null)
        {
            _remoteContentAllowed = await settings.GetListAsync(SettingKeys.RemoteContentAllowed) ?? [];
        }

        return _remoteContentAllowed;
    }

    private async Task<bool> IsRemoteContentAllowedAsync(MessageSummary summary) =>
        (await RemoteContentAllowedAsync()).Contains(RemoteContentKey(summary));

    private async Task RememberRemoteContentAsync(MessageSummary summary)
    {
        var allowed = await RemoteContentAllowedAsync();
        var key = RemoteContentKey(summary);
        if (allowed.Contains(key))
        {
            return;
        }

        allowed.Add(key);
        if (allowed.Count > RememberedRemoteContentLimit)
        {
            allowed.RemoveRange(0, allowed.Count - RememberedRemoteContentLimit);
        }

        await settings.SetListAsync(SettingKeys.RemoteContentAllowed, allowed);
    }

    private void ShowOpened(MessageSummary summary, bool allowRemoteContent)
    {
        if (_opened is not { } opened)
        {
            return;
        }

        ReadingPane = ReadingPaneViewModel.ForMessage(summary, opened, allowRemoteContent, files, _imageClient, () =>
        {
            _ = RememberRemoteContentAsync(summary);
            ShowOpened(summary, allowRemoteContent: true);
        });
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
        var banner = new InvitationBannerViewModel(invitation, account, invitations, async done =>
        {
            CalendarChanged?.Invoke(this, EventArgs.Empty);
            if (done.Length > 0)
            {
                await TidyAnsweredInvitationAsync(summary, done);
            }
        });
        _invitation = (summary.RemoteId, banner);
        pane.Invitation = banner;

        try
        {
            await banner.LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Checking the invitation {Uid} failed", invitation.Uid);
        }
    }

    // Once answered, the invitation mail goes to the trash (Einstellungen → Kalender) and the list moves on.
    private async Task TidyAnsweredInvitationAsync(MessageSummary summary, string done)
    {
        var message = _allMessages.FirstOrDefault(m => ReferenceEquals(m.Summary, summary));

        // Only into a trash folder: without one, deleting would be for good.
        if (message is null || FolderOf(message) is not { } folder || folder.Folder.Role == FolderRole.Trash
            || !await settings.GetBoolAsync(SettingKeys.DeleteAnsweredInvitations, fallback: true)
            || await mail.FindFolderAsync(folder.Folder.ConnectionId, FolderRole.Trash) is null)
        {
            StatusMessage?.Invoke(this, done);
            return;
        }

        if (await RunOnMessagesAsync([message], T("Einladung in den Papierkorb verschieben"), (connection, folder, ids) => mail.DeleteAsync(connection, folder, ids), removesMessages: true))
        {
            StatusMessage?.Invoke(this, done + T(" Die Einladung liegt im Papierkorb."));
        }
    }

    private void RebuildEntries()
    {
        var open = SelectedMessage; // before clearing: the list's selection goes with its items
        Entries.Clear();
        var query = SearchText.Trim();
        if (IsSearchMode && query == Search.ServerTerm)
        {
            query = string.Empty;
        }

        var messages = query.Length == 0 ? _allMessages : _allMessages.Where(m => m.Matches(query)).ToList();
        if (ShowUnreadOnly)
        {
            // The message being read stays, even though opening it marked it read.
            messages = [.. messages.Where(m => m.IsUnread || m == open)];
        }

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

    public static string TitleOf(Account account) => account.Title;

    public ObservableCollection<MailFolderNode> Folders { get; }

    /// <summary>Remembered across restarts (see <see cref="MailViewModel"/>).</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public string ExpansionKey => "account:" + Connection.Id.ToString("N");

    /// <summary>Just added, first sync still running.</summary>
    [ObservableProperty]
    public partial bool IsSettingUp { get; set; }

    /// <summary>The out-of-office reply is on: "Abwesend" next to the account.</summary>
    [ObservableProperty]
    public partial bool HasAutoReply { get; set; }

    public IEnumerable<MailFolderNode> AllFolders() => Folders.SelectMany(f => f.SelfAndDescendants());
}

/// <summary>"Favoriten" at the top of the folder tree: folders of any account the user picked (e.g. all inboxes).</summary>
internal sealed partial class FavoritesNode : ObservableObject
{
    public string Title { get; } = T("Favoriten");

    public ObservableCollection<FavoriteFolderNode> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}

/// <summary>A folder shown under "Favoriten": name and unread count of the real folder, plus its account.</summary>
internal sealed partial class FavoriteFolderNode : ObservableObject
{
    public FavoriteFolderNode(MailFolderNode target)
    {
        Target = target;
        target.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MailFolderNode.UnreadCount))
            {
                OnPropertyChanged(nameof(UnreadText));
                OnPropertyChanged(nameof(HasUnread));
            }
        };
    }

    public MailFolderNode Target { get; }

    public MailFolder Folder => Target.Folder;

    public string Name => Target.Name;

    public string AccountTitle => Target.Account.Title;

    public bool HasUnread => Target.HasUnread;

    public string UnreadText => Target.UnreadText;

    /// <summary>For the tree's expansion binding (a favourite has no children).</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

internal sealed partial class MailFolderNode(MailFolder folder) : ObservableObject
{
    /// <summary>Trash and junk can be emptied.</summary>
    public bool CanEmpty => Folder.Role is FolderRole.Trash or FolderRole.Junk;

    /// <summary>Also listed under "Favoriten" (the context menu offers adding or removing).</summary>
    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    /// <summary>Set by <see cref="MailAccountNode"/> when the tree is attached.</summary>
    public MailAccountNode Account { get; set; } = null!;

    public MailFolder Folder { get; } = folder;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public string ExpansionKey => Folder.ConnectionId.ToString("N") + "|" + Folder.RemoteId;

    /// <summary>Special folders get familiar German names, whatever the server calls them (INBOX, Sent, Trash …).</summary>
    public string Name => DisplayName(Folder);

    public static string DisplayName(MailFolder folder) => folder.Role switch
    {
        FolderRole.Inbox => T("Posteingang"),
        FolderRole.Drafts => T("Entwürfe"),
        FolderRole.Sent => T("Gesendete Elemente"),
        FolderRole.Trash => T("Gelöschte Elemente"),
        FolderRole.Junk => T("Junk-E-Mail"),
        FolderRole.Archive => T("Archiv"),
        _ => folder.Name,
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

    public string Subject => string.IsNullOrWhiteSpace(Summary.Subject) ? T("(kein Betreff)") : Summary.Subject;

    public string Preview => MailPreview.Clean(Summary.Preview) is { Length: > 0 } preview
        ? preview
        : IsEncrypted ? T("Verschlüsselte Nachricht – Inhalt wird beim Öffnen entschlüsselt") : string.Empty;

    public bool HasAttachments => Summary.HasAttachments;

    public bool IsSigned => Summary.Security.HasFlag(MessageSecurity.Signed);

    public bool IsEncrypted => Summary.Security.HasFlag(MessageSecurity.Encrypted);

    public string TimeText => DateGroup.ShortTime(Summary.Date);

    [ObservableProperty]
    public partial bool IsUnread { get; set; } = !summary.Flags.HasFlag(MessageFlags.Seen);

    [ObservableProperty]
    public partial bool IsFlagged { get; set; } = summary.Flags.HasFlag(MessageFlags.Flagged);

    /// <summary>Answered (IMAP \Answered) – also when answered with another mail program.</summary>
    [ObservableProperty]
    public partial bool IsAnswered { get; set; } = summary.Flags.HasFlag(MessageFlags.Answered);

    /// <summary>Forwarded (IMAP keyword $Forwarded).</summary>
    [ObservableProperty]
    public partial bool IsForwarded { get; set; } = summary.Flags.HasFlag(MessageFlags.Forwarded);

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
            return T("Heute");
        }

        if (day == today.AddDays(-1))
        {
            return T("Gestern");
        }

        if (day >= startOfWeek)
        {
            return T("Diese Woche");
        }

        return day >= startOfWeek.AddDays(-7) ? T("Letzte Woche") : T("Älter");
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
            : local.ToString("d", CultureInfo.CurrentCulture);
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
        Folder is null ? F("allen Ordnern von {0}", Account.Title)
        : withSubfolders && Folder.Children.Count > 0 ? F("{0} und Unterordnern", Folder.Name)
        : Folder.Name;

    /// <summary>Per account: "Alle Ordner", then its folders indented by depth.</summary>
    public static IReadOnlyList<SearchScope> For(IEnumerable<MailAccountNode> accounts)
    {
        var result = new List<SearchScope>();
        foreach (var account in accounts)
        {
            result.Add(new SearchScope(F("Alle Ordner – {0}", account.Title), account, null));
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
