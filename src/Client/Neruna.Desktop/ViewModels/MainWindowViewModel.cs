using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Discovery;
using Neruna.Core.Mail;

using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

internal enum Section
{
    Mail,
    Calendar,
    Contacts,
    Tasks,
    Chat,
    Settings,
}

/// <summary>Shell: navigation rail, sync, status bar and modal overlays (setup, editors).</summary>
internal sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);

    private readonly MailController _mail;
    private readonly CalendarController _calendar;
    private readonly ContactController _contacts;
    private readonly Neruna.Core.SyncCoordinator _sync;
    private readonly Neruna.Core.SyncActivity _syncActivity;
    private int _syncCalls;
    private readonly AccountSetupService _setup;
    private readonly AccountDiscovery _discovery;
    private readonly IAccountStore _accounts;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private readonly MailPushService _push;
    private readonly MailNotifier _notifier;
    private readonly ReminderScheduler _reminders;
    private readonly ISettingsStore _settings;
    private readonly Neruna.Core.Cloud.SettingsBackupService _backup;
    private readonly Neruna.Core.Cloud.CloudAccountSync _cloudAccounts;
    private readonly Neruna.Core.Auth.IBrowserLauncher _browser;
    private readonly Neruna.Core.Security.ClientCertificates _clientCertificates;
    private readonly Infrastructure.IFileService _files;
    private readonly Neruna.Core.Mail.QuickActionSuggestions _suggestions;
    private readonly Neruna.Core.Workspaces.WorkspaceService _workspacesToLoad;

    // Accounts whose password the user postponed ("Später") – asked again with the next start.
    private readonly HashSet<string> _postponedCloudAccounts = [];

    /// <summary>Remembered window placement and column widths; the views restore and update them.</summary>
    public UiLayout Layout { get; }

    public MainWindowViewModel(
        MailViewModel mailPage,
        CalendarViewModel calendarPage,
        ContactsViewModel contactsPage,
        TasksViewModel tasksPage,
        ChatViewModel chatPage,
        SettingsViewModel settingsPage,
        MailController mail,
        CalendarController calendar,
        ContactController contacts,
        Neruna.Core.SyncCoordinator sync,
        Neruna.Core.SyncActivity syncActivity,
        AccountSetupService setup,
        AccountDiscovery discovery,
        IAccountStore accounts,
        IHttpClientFactory http,
        UiLayout layout,
        MailPushService push,
        MailNotifier notifier,
        AutoRepliesViewModel autoReplies,
        ReminderScheduler reminders,
        ISettingsStore settings,
        Neruna.Core.Cloud.CloudSignatureSync cloudSignatures,
        Neruna.Core.Cloud.CloudTextTemplateSync cloudTemplates,
        Neruna.Core.Cloud.CloudMailTagSync cloudTags,
        Neruna.Core.Cloud.CloudCertificateSync cloudCertificates,
        Neruna.Core.Cloud.CloudPairingService cloudPairing,
        Neruna.Core.Cloud.SettingsBackupService backup,
        Neruna.Core.Cloud.CloudAccountSync cloudAccounts,
        Neruna.Core.Cloud.CloudController cloudController,
        Neruna.Core.Auth.IBrowserLauncher browser,
        Neruna.Core.Diagnostics.CrashReportService crashReports,
        Neruna.Core.Security.ClientCertificates clientCertificates,
        Infrastructure.IFileService files,
        Neruna.Core.Workspaces.WorkspaceService workspaces,
        Neruna.Core.Mail.QuickActionSuggestions suggestions,
        ILogger<MainWindowViewModel> logger)
    {
        _suggestions = suggestions;
        _workspacesToLoad = workspaces;
        settingsPage.Workspaces.DisplayChanged += (_, display) => WorkspaceDisplay = display;
        _clientCertificates = clientCertificates;
        _files = files;
        MailPage = mailPage;
        CalendarPage = calendarPage;
        ContactsPage = contactsPage;
        TasksPage = tasksPage;
        ChatPage = chatPage;
        SettingsPage = settingsPage;
        Layout = layout;
        _mail = mail;
        _calendar = calendar;
        _contacts = contacts;
        _sync = sync;
        _syncActivity = syncActivity;
        _setup = setup;
        _discovery = discovery;
        _accounts = accounts;
        _http = http;
        _push = push;
        _notifier = notifier;
        AutoReplies = autoReplies;
        _reminders = reminders;
        _settings = settings;
        _cloudSignatures = cloudSignatures;
        _cloudTemplates = cloudTemplates;
        _cloudTags = cloudTags;
        _cloudCertificates = cloudCertificates;
        _cloudPairing = cloudPairing;
        _backup = backup;
        _cloudAccounts = cloudAccounts;
        _browser = browser;
        _crashReports = crashReports;
        _logger = logger;

        CurrentPage = mailPage;
        mailPage.Preferences.NavigationItems[0].IsActive = true;
        settingsPage.Attach(cloudSignatures, cloudTemplates, cloudCertificates);

        // Caught on the UI thread: Neruna keeps running and offers the report now (after the dialog shown at the moment).
        CrashHandler.Caught += (_, _) => Dispatcher.UIThread.Post(async () => await OfferCrashReportsAsync());

        WireMailPage();
        WireCalendarAndTasks();
        WireContacts();
        WireChat();
        WireSettings();
        WireBackground();

        // Blocked in the portal: what the organisation brought is already gone – the pages show it, the user learns why.
        cloudController.Revoked += (_, connection) => Dispatcher.UIThread.Post(async () =>
        {
            var message = F("{0} hat dieses Gerät gesperrt oder entfernt. Konten, Zertifikate, Signaturen, Textvorlagen und der Chat der Organisation wurden von diesem Computer gelöscht.", connection.OrganizationName);
            StatusText = message;
            await CloudAccountsChangedAsync(sync: false);
            await SettingsPage.Cloud.ReloadAsync();
            await Views.ChoiceDialog.ShowInFrontAsync(T("Gerät gesperrt"), message, (T("OK"), true, true));
        });

        _timer = new DispatcherTimer { Interval = SyncInterval };
        _timer.Tick += async (_, _) => await SyncAsync();
    }

    // ---- How the pages talk to each other and to the shell -----------------------------------------------------------

    private void WireMailPage()
    {
        MailPage.StatusMessage += (_, message) => StatusText = message;
        MailPage.Agenda.OpenRequested += async (_, occurrence) =>
        {
            CurrentPage = CalendarPage;
            await CalendarPage.OpenOccurrenceAsync(occurrence);
        };
        MailPage.CalendarChanged += async (_, _) => await CalendarChangedAsync();
        MailPage.Tree.AccountsReordered += async (_, _) => await SettingsPage.Accounts.ReloadAsync();
        MailPage.PropertyChanged += async (_, e) =>
        {
            // Opening a signed message may have collected a new certificate.
            if (e.PropertyName == nameof(MailViewModel.ReadingPane) && MailPage.ReadingPane?.HasSecurity == true)
            {
                await SettingsPage.Certificates.ReloadAsync();
            }
        };

        MailPage.Tree.AutoReplyRequested += async (_, node) => await ShowAutoReplyAsync(node.Account, node.Connection);
        AutoReplies.Changed += (_, _) =>
        {
            MailPage.ShowAutoReplyBadges(AutoReplies.ActiveConnections);
            SettingsPage.Accounts.ShowAutoReplies(AutoReplies.ActiveConnections);
        };
        AutoReplies.EditRequested += async (_, row) => await ShowAutoReplyAsync(row.Account, row.Connection);

        // Right-click on an account → "Ordner abonnieren …".
        MailPage.Tree.FolderSubscriptionsRequested += async (_, node) =>
        {
            var dialog = new FolderSubscriptionsViewModel(_mail, node.Connection, node.Title);
            dialog.Finished += async (_, saved) =>
            {
                Overlay = null;
                if (saved)
                {
                    await MailPage.ReloadAsync();
                }
            };
            Overlay = dialog;
            await dialog.LoadAsync();
        };
    }

    // An event changed (editor, invitation answered): calendar, the agenda beside the mail and the reminders follow.
    private async Task CalendarChangedAsync()
    {
        await CalendarPage.ReloadAsync();
        await MailPage.Agenda.ReloadAsync();
        await _reminders.CheckAsync();
    }

    private void WireCalendarAndTasks()
    {
        CalendarPage.SubscribeRequested += (_, _) => ShowIcsSubscription();
        CalendarPage.ManageRequested += (_, _) => ShowCalendarSelection();
        CalendarPage.PrintRequested += (_, _) => ShowCalendarPrint();
        MailPage.QuickActionSuggested += (_, suggestion) => SuggestQuickAction(suggestion);
        CalendarPage.SpecialRequested += async (_, _) => await ShowSpecialCalendarAsync();
        CalendarPage.RemoveRequested += async (_, info) => await RemoveCalendarAccountAsync(info);
        CalendarPage.EditorRequested += (_, editor) => ShowEditor(editor, CalendarChangedAsync);
        CalendarPage.StatusMessage += (_, message) => StatusText = message;
        CalendarPage.TaskListsChanged += async (_, _) => await TasksPage.ReloadAsync();
        CalendarPage.TaskOpenRequested += async (_, occurrence) =>
        {
            CurrentPage = TasksPage;
            await TasksPage.OpenAsync(occurrence.Calendar, occurrence.ObjectRemoteId);
        };

        TasksPage.EditorRequested += (_, editor) => ShowEditor(editor, TasksPage.ReloadAsync);
        TasksPage.StatusMessage += (_, message) => StatusText = message;
        TasksPage.CalendarChanged += async (_, _) => await CalendarPage.ReloadAsync();
    }

    private void WireContacts()
    {
        ContactsPage.EditorRequested += (_, editor) => ShowEditor(editor, async () =>
        {
            await ContactsPage.ReloadAsync();
            MailPage.ContactsChanged(); // a sender just saved as a contact loses its "+"
        });
        MailPage.AddContactRequested += (_, sender) => ContactsPage.NewContactFrom(sender);
        MailPage.ManageTagsRequested += async (_, _) =>
        {
            Navigate(Section.Settings);
            await SettingsPage.ShowMailTagsAsync();
        };
        MailPage.ServerRulesRequested += async (_, account) =>
        {
            Navigate(Section.Settings);
            await SettingsPage.ShowServerRulesAsync(account.Id);
        };
        MailPage.Tree.ColorChanged += async (_, _) => await SettingsPage.Accounts.RefreshColorsAsync();
        ContactsPage.ManageRequested += (_, _) => ShowAddressBookSelection();
        ContactsPage.MailRequested += async (_, recipients) =>
        {
            CurrentPage = MailPage;
            await MailPage.ComposeToAsync(recipients);
        };
    }

    private void WireChat()
    {
        // Chat button hidden in Einstellungen → Design: no chat, offline, no status switch.
        ChatPage.IsEnabled = MailPage.Preferences.IsChatShown;
        MailPage.Preferences.NavigationChanged += (_, _) => ChatPage.IsEnabled = MailPage.Preferences.IsChatShown;

        // The unread count of the chat on its button in the rail.
        ChatPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.UnreadText) || e.PropertyName == nameof(ChatViewModel.HasUnread))
            {
                UpdateChatBadge();
            }
        };
        MailPage.Preferences.NavigationItems.CollectionChanged += (_, _) =>
        {
            UpdateChatBadge();
            OnCurrentPageChanged(CurrentPage);
        };
        ChatPage.CloudSetupRequested += (_, _) =>
        {
            CurrentPage = SettingsPage;
            SettingsPage.SelectedTab = SettingsViewModel.CloudTab;
        };
    }

    private void WireSettings()
    {
        var cloud = SettingsPage.Cloud;

        // Accounts of the organisation arrived (connecting, or the regular cloud refresh): show them and fetch their mail.
        cloud.AccountsArrived += async (_, _) => await CloudAccountsChangedAsync(sync: true);

        // Connected or disconnected: the chat follows at once, not only with the next tick.
        cloud.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(CloudViewModel.IsNotConnected))
            {
                await ChatPage.RefreshNowAsync();
            }
        };
        cloud.ChatChanged += async (_, _) => await ChatPage.RefreshNowAsync();

        // Restored from a backup: every page from the store again, then the servers of the restored accounts.
        SettingsPage.FileBackup.Restored += async (_, _) => await AfterRestoreAsync();
        cloud.Restored += async (_, _) => await AfterRestoreAsync();
        async Task AfterRestoreAsync()
        {
            ShowBackupHint = false;
            await MailPage.Preferences.LoadAsync();
            await SettingsPage.ReloadAsync();
            await RefreshPagesAsync();
            HasAccounts = (await _accounts.GetAccountsAsync()).Count > 0;
            await UpdatePushAsync();
            await SyncAsync();
        }

        cloud.BackedUp += (_, _) => ShowBackupHint = false;

        var accountsPage = SettingsPage.Accounts;
        accountsPage.AddAccountRequested += (_, _) => ShowAccountSetup();
        accountsPage.EditRequested += (_, account) => ShowAccountSetup(account);
        accountsPage.AutoReplyRequested += async (_, account) =>
        {
            if (account.ConnectionsOf(Neruna.Core.Accounts.ServiceKind.Mail).FirstOrDefault() is { } connection)
            {
                await ShowAutoReplyAsync(account, connection);
            }
        };
        accountsPage.SubscribeRequested += (_, _) => ShowIcsSubscription();
        accountsPage.PstRequested += (_, _) => ShowPstAccount();
        // "Serverregeln …" of an account: their own settings page, on this account.
        accountsPage.ServerRulesRequested += async (_, account) => await SettingsPage.ShowServerRulesAsync(account.Id);
        accountsPage.TransferRequested += async (_, dialog) =>
        {
            dialog.Finished += (_, _) => Overlay = null;
            Overlay = dialog;
            await dialog.StartAsync();
        };
        accountsPage.ColorsChanged += async (_, _) => await MailPage.Tree.UpdateColorsAsync();
        accountsPage.AccountsChanged += async (_, _) =>
        {
            await RefreshPagesAsync();
            await UpdatePushAsync();
            _ = RefreshAutoRepliesAsync();
        };

        SettingsPage.MailOptions.PushChanged += async (_, _) => await UpdatePushAsync();
        SettingsPage.MailOptions.TestNotificationRequested += (_, _) =>
        {
            if (_notifier.ShowTest() is { } error)
            {
                StatusText = T("Test-Benachrichtigung fehlgeschlagen: ") + error;
            }
        };
    }

    // Push and the full sync run in the background; the UI is updated on its thread.
    private void WireBackground()
    {
        _push.FolderSynced += (_, folder) => _ = Dispatcher.UIThread.InvokeAsync(() => MailPage.RefreshFolderAsync(folder.ConnectionId, folder.RemoteId));
        _push.NewMail += (_, e) => _ = Dispatcher.UIThread.InvokeAsync(() => _notifier.NotifyAsync(e, IsOpenInFront, OpenMessageAsync));

        // During a full sync: folders and each finished folder appear at once, not only when everything is done.
        _mail.FoldersSynced += (_, _) => _ = Dispatcher.UIThread.InvokeAsync(() => MailPage.RefreshAfterSyncAsync());
        _mail.FolderSynced += (_, folder) => _ = Dispatcher.UIThread.InvokeAsync(() => MailPage.RefreshFolderAsync(folder.ConnectionId, folder.RemoteId));
    }

    public MailViewModel MailPage { get; }

    /// <summary>The out-of-office replies behind the button at the top.</summary>
    public AutoRepliesViewModel AutoReplies { get; }

    public CalendarViewModel CalendarPage { get; }

    public ContactsViewModel ContactsPage { get; }

    public ChatViewModel ChatPage { get; }

    public TasksViewModel TasksPage { get; }

    public SettingsViewModel SettingsPage { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMail), nameof(IsCalendar), nameof(IsContacts), nameof(IsTasks), nameof(IsChat), nameof(IsSettings), nameof(SectionTitle))]
    public partial ViewModelBase CurrentPage { get; set; }

    private void UpdateChatBadge()
    {
        foreach (var item in MailPage.Preferences.NavigationItems)
        {
            item.Badge = item.Section == Section.Chat && ChatPage.HasUnread ? ChatPage.UnreadText : null;
        }
    }

    /// <summary>The chat is in front in an active window: it asks often and marks what is shown as read.</summary>
    public void UpdateChatActive() => ChatPage.IsActive = IsChat && Overlay is null && NotificationService.IsAppActive;

    public bool IsMail => CurrentPage == MailPage;

    public bool IsCalendar => CurrentPage == CalendarPage;

    public bool IsContacts => CurrentPage == ContactsPage;

    public bool IsChat => CurrentPage == ChatPage;

    public bool IsTasks => CurrentPage == TasksPage;

    public bool IsSettings => CurrentPage == SettingsPage;

    /// <summary>Shown in the header bar; the window title already says "Neruna".</summary>
    public string SectionTitle => IsCalendar ? T("Kalender") : IsContacts ? T("Kontakte") : IsTasks ? T("Aufgaben") : IsChat ? "Chat" : IsSettings ? T("Einstellungen") : T("E-Mails");

    /// <summary>Modal content shown above the shell, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlay), nameof(OverlayWidth))]
    public partial ViewModelBase? Overlay { get; set; }

    public bool HasOverlay => Overlay is not null;

    /// <summary>Some dialogs show two things side by side and need more room (group editor, the transfer's QR code and PIN).</summary>
    public double OverlayWidth => Overlay switch
    {
        GroupEditorViewModel { IsReadOnly: false } => 980,
        CalendarSelectionViewModel { HasSeveralSources: true } => 1040, // accounts beside the calendars, long names
        CalendarSelectionViewModel or AddressBookSelectionViewModel => 760,
        CalendarPrintViewModel => 1080, // options beside a page preview
        AccountTransferDialogViewModel => 900, // the QR code (up to ~520 px) with PIN and instructions beside it
        SyncStatusViewModel or BackupChangesViewModel => 820, // account, service and state in one line, errors below
        _ => 640,
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = T("Bereit");

    /// <summary>The last run had failures: the status bar shows them in red.</summary>
    [ObservableProperty]
    public partial bool HasSyncProblems { get; set; }

    /// <summary>Click on the status bar: what synchronises, since when, how the last runs ended.</summary>
    [RelayCommand]
    private void OpenSyncStatus()
    {
        var status = new SyncStatusViewModel(_syncActivity, SyncCommand);
        status.Finished += (_, _) => Overlay = null;
        Overlay = status;
    }

    [ObservableProperty]
    public partial bool HasAccounts { get; set; } = true;

    public async Task InitializeAsync()
    {
        await LoadLocalAsync();
        await StartAsync();
    }

    /// <summary>Everything from the local store – fast, done while the splash screen is visible.</summary>
    public async Task LoadLocalAsync()
    {
        // The workspace first: every page shows what it filters.
        await InitWorkspacesAsync(_workspacesToLoad);
        Editor.HtmlEditor.DefaultSpellLanguage = await _settings.GetAsync(SettingKeys.ComposeSpellLanguage) is { Length: > 0 } spellLanguage ? spellLanguage : null;
        // Neruna opens with the first button of the rail (E-Mail unless hidden or moved).
        Navigate(MailPage.Preferences.StartSection);
        await RefreshPagesAsync();
        await MailPage.Agenda.LoadStateAsync();
        await MailPage.LoadAiAsync();
        await MailPage.LoadTagsAsync();
        HasAccounts = (await _accounts.GetAccountsAsync()).Count > 0;
    }

    /// <summary>Once the window is visible: account setup for a first start, otherwise sync with the servers.</summary>
    /// <summary>mailto: links (new mail) and .eml files (message window) handed over by the system.</summary>
    public async Task OpenFromSystemAsync(IReadOnlyList<string> items)
    {
        foreach (var item in items)
        {
            if (SystemOpen.IsMailto(item))
            {
                Overlay = Overlay is CrashReportViewModel ? Overlay : null;
                CurrentPage = MailPage;
                await MailPage.ComposeAsync(Neruna.Core.Mail.MailtoLink.Parse(item));
            }
            else if (SystemOpen.IsEml(item))
            {
                await MailPage.OpenFileAsync(item);
            }
        }
    }

    public async Task StartAsync()
    {
        // Crashed last time: offer the report first (or send it / drop it, as set).
        await OfferCrashReportsAsync();
        // The chat needs no mail account, only the cloud connection.
        _ = ChatPage.StartAsync();
        if (!HasAccounts)
        {
            ShowAccountSetup();
            return;
        }

        await StartServicesAsync();
    }

    private bool _servicesStarted;

    // Periodic sync, updates, push and reminders – at start, or after the first account was set up.
    private async Task StartServicesAsync()
    {
        if (_servicesStarted)
        {
            await SyncAsync();
            return;
        }

        _servicesStarted = true;
        _timer.Start();
        SettingsPage.Updates.Start();

        // Out of office: what was known shows at once; the servers are asked alongside the first sync, not after it.
        await AutoReplies.LoadKnownAsync([.. MailPage.Accounts.Select(a => (a.Account, a.Connection))]);
        _ = RefreshAutoRepliesAsync();
        await SyncAsync();
        await UpdatePushAsync();
        _reminders.Start(OpenReminderAsync);
    }

    /// <summary>Starts or stops watching the inboxes (setting "Neue E-Mails sofort abrufen"; accounts changed).</summary>
    private async Task UpdatePushAsync()
    {
        try
        {
            if (await _settings.GetBoolAsync(SettingKeys.MailPush, fallback: true))
            {
                await _push.StartAsync();
            }
            else
            {
                await _push.StopAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Starting push failed");
        }
    }

    // New mail: no notification while the user looks at exactly that folder in Neruna.
    private bool IsOpenInFront(MailFolder folder) =>
        NotificationService.IsAppActive && CurrentPage == MailPage && Overlay is null
        && MailPage.CurrentFolder?.Folder is { } open && open.ConnectionId == folder.ConnectionId && open.RemoteId == folder.RemoteId;

    private async Task OpenMessageAsync(MailFolder folder, string remoteId)
    {
        NotificationService.ActivateMainWindow();
        Overlay = null;
        CurrentPage = MailPage;
        await MailPage.RevealAsync(folder.ConnectionId, folder.RemoteId, remoteId);
    }

    private Section? SectionOf(ViewModelBase page) =>
        page == MailPage ? Section.Mail : page == CalendarPage ? Section.Calendar : page == ContactsPage ? Section.Contacts
        : page == TasksPage ? Section.Tasks : page == ChatPage ? Section.Chat : page == SettingsPage ? Section.Settings : null;

    [RelayCommand]
    private void Navigate(Section section) => CurrentPage = section switch
    {
        Section.Calendar => CalendarPage,
        Section.Contacts => ContactsPage,
        Section.Tasks => TasksPage,
        Section.Chat => ChatPage,
        Section.Settings => SettingsPage,
        _ => MailPage,
    };

    private readonly Neruna.Core.Cloud.CloudSignatureSync _cloudSignatures;
    private readonly Neruna.Core.Cloud.CloudTextTemplateSync _cloudTemplates;
    private readonly Neruna.Core.Cloud.CloudMailTagSync _cloudTags;
    private readonly Neruna.Core.Cloud.CloudCertificateSync _cloudCertificates;
    private readonly Neruna.Core.Cloud.CloudPairingService _cloudPairing;
    private DateTime _cloudSyncedAt;

    // The organisation's central signatures: with the sync, at most every 10 minutes; offline or unreachable is no error.
    /// <returns>Accounts of the organisation were added, changed or removed (their mail still has to be fetched).</returns>
    private async Task<bool> SyncCloudAsync()
    {
        if (DateTime.UtcNow - _cloudSyncedAt < TimeSpan.FromMinutes(10))
        {
            return false;
        }

        var cloudAccountsChanged = false;

        try
        {
            await Task.Run(() => _cloudSignatures.SyncAsync());
            await Task.Run(() => _cloudTemplates.SyncAsync());
            await Task.Run(() => _cloudTags.SyncAsync());
            await Task.Run(() => _cloudCertificates.SyncAsync());
            SettingsPage.Cloud.UpdateCertificateStatus();
            if (await Task.Run(() => _cloudAccounts.SyncAsync()))
            {
                await CloudAccountsChangedAsync(sync: false);
                cloudAccountsChanged = true;
            }

            AskForCloudAccountPassword();

            // Phones paired from this desktop get what they still miss (re-encrypted here, zero knowledge).
            try
            {
                await Task.Run(() => _cloudPairing.DeliverAsync());
            }
            catch (Exception ex) when (ex is Neruna.Core.Cloud.CloudException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Paired phones not supplied");
            }

            _cloudSyncedAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is Neruna.Core.Cloud.CloudException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Cloud signatures not refreshed");
        }

        return cloudAccountsChanged;
    }

    private async Task CloudAccountsChangedAsync(bool sync)
    {
        await SettingsPage.Accounts.ReloadAsync();
        await RefreshPagesAsync();
        HasAccounts = (await _accounts.GetAccountsAsync()).Count > 0;
        await UpdatePushAsync();
        if (sync && CanSync())
        {
            await SyncAsync();
        }

        AskForCloudAccountPassword();
    }

    private readonly Neruna.Core.Diagnostics.CrashReportService _crashReports;
    private bool _crashOfferWaiting;

    /// <summary>Waiting crash reports: send or drop as set, else ask – never over another dialog (then afterwards).</summary>
    private async Task OfferCrashReportsAsync()
    {
        try
        {
            var mode = await _crashReports.GetModeAsync();
            var pending = await _crashReports.GetPendingAsync();
            if (pending.Count == 0 || Overlay is CrashReportViewModel)
            {
                return;
            }

            switch (mode)
            {
                case Neruna.Core.Diagnostics.CrashReportMode.Never:
                    _crashReports.Discard(pending);
                    return;
                case Neruna.Core.Diagnostics.CrashReportMode.Always:
                    await _crashReports.SendAsync(pending);
                    StatusText = T("Ein Fehler ist aufgetreten – der Bericht wurde an Neruna gesendet.");
                    return;
            }

            if (Overlay is not null)
            {
                _crashOfferWaiting = true;
                return;
            }

            var dialog = new CrashReportViewModel(pending, _crashReports);
            dialog.Finished += (_, _) => Overlay = null;
            Overlay = dialog;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Crash reports not offered");
        }
    }

    // An account of the organisation without password: ask for it (one at a time, not over another dialog).
    private void AskForCloudAccountPassword()
    {
        if (Overlay is not null || _cloudAccounts.Status.Pending.FirstOrDefault(p => !_postponedCloudAccounts.Contains(p.CloudId)) is not { } pending)
        {
            return;
        }

        var prompt = new CloudAccountPasswordViewModel(pending, _cloudAccounts);
        prompt.Finished += async (_, done) =>
        {
            Overlay = null;
            if (done)
            {
                await CloudAccountsChangedAsync(sync: true);
            }
            else
            {
                _postponedCloudAccounts.Add(pending.CloudId);
            }

            AskForCloudAccountPassword();
        };
        Overlay = prompt;
    }

    // Always clickable: while a run is under way the click asks for another one (the coordinator lets it share the next).
    [RelayCommand]
    private async Task SyncAsync()
    {
        // Timer, button, new accounts and the cloud all ask for syncs; the coordinator runs one at a time and lets
        // waiting requests share the next run. The status bar names the step, so a long one is not a riddle.
        _syncCalls++;
        IsSyncing = true;
        StatusText = T("Synchronisiere …");
        try
        {
            // Accounts removed meanwhile leave the status window.
            _syncActivity.Forget((await _accounts.GetAccountsAsync()).SelectMany(a => a.Connections).Select(c => c.Id));
            var reports = await StepAsync(T("Konten werden synchronisiert …"), "accounts", () => _sync.SyncAllAsync());
            await StepAsync(T("Ansicht wird aktualisiert …"), "pages", async () =>
            {
                await RefreshPagesAsync();
                await MailPage.Agenda.ReloadAsync();
                await _reminders.CheckAsync();
                return true;
            });
            if (await StepAsync(T("Neruna Cloud wird abgeglichen …"), "cloud", SyncCloudAsync))
            {
                // New accounts from the organisation: fetch their folders and mail right away.
                reports = [.. reports, .. await StepAsync(T("Neue Konten werden synchronisiert …"), "new accounts", () => _sync.SyncAllAsync())];
                await RefreshPagesAsync();
            }

            if (DateTime.UtcNow - _autoRepliesCheckedAt > TimeSpan.FromHours(1))
            {
                _ = RefreshAutoRepliesAsync();
            }

            if (DateTime.UtcNow - _backupCheckedAt > BackupCheckInterval)
            {
                _ = CheckBackupHintAsync();
            }

            var failures = reports.SelectMany(r => r.Failures).ToList();
            HasSyncProblems = failures.Count > 0;
            StatusText = failures.Count == 0
                ? F("Synchronisiert um {0:t}", DateTime.Now)
                : failures.Count == 1
                    ? F("Synchronisiert um {0:t} – 1 Verbindung fehlgeschlagen", DateTime.Now)
                    : F("Synchronisiert um {0:t} – {1} Verbindungen fehlgeschlagen", DateTime.Now, failures.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = T("Synchronisierung abgebrochen.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            HasSyncProblems = true;
            StatusText = T("Synchronisierung fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsSyncing = --_syncCalls > 0;
        }
    }

    private bool CanSync() => !IsSyncing;

    /// <summary>One step of a sync run: named in the status bar and logged with its duration.</summary>
    private async Task<T> StepAsync<T>(string status, string name, Func<Task<T>> step)
    {
        StatusText = status;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return await step();
        }
        finally
        {
            var took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (took > TimeSpan.FromSeconds(5))
            {
                _logger.LogInformation("Sync step {Step} took {Seconds:0.0} s", name, took.TotalSeconds);
            }
        }
    }

    /// <summary>
    /// Closing Neruna: open drafts with unsaved changes are saved or dropped as the user says.
    /// </summary>
    /// <param name="ask">Shows the question; gets the draft titles, returns the choice.</param>
    /// <returns>True if Neruna may close.</returns>
    public async Task<bool> PrepareCloseAsync(Func<IReadOnlyList<string>, Task<CloseChoice>> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        var drafts = MailPage.UnsavedDrafts;
        if (drafts.Count == 0)
        {
            return true;
        }

        switch (await ask(drafts.Select(d => d.Title).ToList()))
        {
            case CloseChoice.Save:
                StatusText = drafts.Count == 1 ? T("Entwurf wird gespeichert …") : T("Entwürfe werden gespeichert …");
                var saved = true;
                foreach (var draft in drafts)
                {
                    saved &= await draft.SaveBeforeExitAsync();
                }

                if (!saved)
                {
                    StatusText = T("Nicht alle Entwürfe konnten gespeichert werden – Neruna bleibt offen.");
                }

                return saved;

            case CloseChoice.Discard:
                foreach (var draft in drafts)
                {
                    draft.DiscardOnExit();
                }

                return true;

            default:
                return false;
        }
    }

    // "Element öffnen" in the reminder window: the event in the calendar's editor.
    private async Task OpenReminderAsync(Neruna.Core.Calendar.CalendarOccurrence occurrence)
    {
        NotificationService.ActivateMainWindow();
        CurrentPage = CalendarPage;
        await CalendarPage.OpenOccurrenceAsync(occurrence);
    }

    [RelayCommand]
    private void ShowAccountSetup(Neruna.Core.Accounts.Account? editing = null)
    {
        if (editing?.Connections.Any(c => c.ProviderId == Neruna.Core.Providers.ProviderIds.Pst) == true)
        {
            ShowPstAccount(editing);
            return;
        }

        var setup = new AccountSetupViewModel(_discovery, _setup, _http.CreateClient("dav"))
        {
            Browser = _browser,
            Certificates = _clientCertificates,
            Files = _files,
            Web = _http.CreateClient("neruna"),
            Settings = _settings,
            AskRedirect = (title, message) => Views.ChoiceDialog.ShowInFrontAsync(title, message, (T("Erlauben"), true, false), (T("Nicht verwenden"), false, true)),
        };
        if (editing is not null)
        {
            setup.LoadForEditing(editing);
        }
        else
        {
            setup.CloudRequested += (_, _) =>
            {
                Overlay = null;
                CurrentPage = SettingsPage;
                SettingsPage.SelectedTab = SettingsViewModel.CloudTab;
            };
        }

        setup.Finished += async (_, created) =>
        {
            Overlay = null;
            if (created && editing is not null)
            {
                // Changed names or servers: every page reloads, then a sync with the new settings.
                await SettingsPage.Accounts.ReloadAsync();
                await RefreshPagesAsync();
                await SyncAsync();
                return;
            }

            if (created)
            {
                HasAccounts = true;
                if (setup.CreatedAccount is { } account)
                {
                    await ShowNewAccountAsync(account);
                }

                await StartServicesAsync();
            }
        };
        Overlay = setup;
    }

    /// <summary>
    /// A new account shows up right after the sign-in (not only when every account is synced) and its mail is fetched
    /// first – otherwise the closed dialog looks like a cancelled one and invites a second try.
    /// </summary>
    private async Task ShowNewAccountAsync(Neruna.Core.Accounts.Account account)
    {
        var connections = account.ConnectionsOf(Neruna.Core.Accounts.ServiceKind.Mail).ToList();
        foreach (var connection in connections)
        {
            MailPage.Tree.MarkSettingUp(connection.Id, true);
        }

        IsSyncing = true;
        StatusText = F("Konto «{0}» wird eingerichtet …", account.Title);
        await SettingsPage.Accounts.ReloadAsync();
        await MailPage.ReloadAsync();
        if (connections.Count > 0)
        {
            CurrentPage = MailPage;
        }

        foreach (var connection in connections)
        {
            try
            {
                await Task.Run(() => _mail.SyncConnectionAsync(connection));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The full sync right after tries again and reports it in the status bar.
                _logger.LogWarning(ex, "First sync of the new account failed");
            }
            finally
            {
                MailPage.Tree.MarkSettingUp(connection.Id, false);
            }
        }

        _ = RefreshAutoRepliesAsync(); // the new account may support an out-of-office reply
    }

    // ---- Out of office ---------------------------------------------------------------------------------------------

    private async Task ShowAutoReplyAsync(Neruna.Core.Accounts.Account account, Neruna.Core.Accounts.ServiceConnection connection)
    {
        var dialog = new AutoReplyViewModel(_mail, _settings, account, connection);
        dialog.Finished += async (_, saved) =>
        {
            Overlay = null;
            await AutoReplies.ReloadTemplatesAsync(); // templates may have been added or deleted
            if (saved && dialog.Saved is { } reply)
            {
                // What was just stored counts at once – no second trip to the server (and no wait for other accounts).
                StatusText = T("Abwesenheitsnotiz gespeichert.");
                AutoReplies.Apply(account, connection, reply, dialog.Features, dialog.Message.Trim());
            }
        };
        Overlay = dialog;
        await dialog.LoadAsync();
    }

    private DateTime _autoRepliesCheckedAt;

    private async Task RefreshAutoRepliesAsync()
    {
        _autoRepliesCheckedAt = DateTime.UtcNow;
        await AutoReplies.RefreshAsync([.. MailPage.Accounts.Select(a => (a.Account, a.Connection))]);
    }

    // Birthdays, holidays or an ICS subscription: an account with just this calendar – asked, then removed.
    private async Task RemoveCalendarAccountAsync(Neruna.Core.Calendar.CalendarInfo info)
    {
        var account = (await _accounts.GetAccountsAsync()).FirstOrDefault(a => a.Connections.Any(c => c.Id == info.ConnectionId));
        if (account is null)
        {
            return;
        }

        var sure = await Views.ChoiceDialog.ShowInFrontAsync(T("Kalender entfernen"),
            F("«{0}» aus Neruna entfernen? Bei einem abonnierten Kalender bleibt er beim Anbieter bestehen und kann jederzeit wieder abonniert werden.", info.Name),
            (T("Entfernen"), true, false), (T("Abbrechen"), false, true));
        if (!sure)
        {
            return;
        }

        await _accounts.DeleteAccountAsync(account.Id);
        StatusText = F("«{0}» entfernt.", info.Name);
        await SettingsPage.Accounts.ReloadAsync();
        HasAccounts = (await _accounts.GetAccountsAsync()).Count > 0;
        await RefreshPagesAsync();
    }

    // An Outlook data file as an account (or its name and place changed).
    private void ShowPstAccount(Neruna.Core.Accounts.Account? editing = null)
    {
        var dialog = new PstAccountViewModel(_setup, _accounts, _mail, _files, editing);
        dialog.Finished += async (_, done) =>
        {
            Overlay = null;
            if (done)
            {
                HasAccounts = true;
                await SettingsPage.Accounts.ReloadAsync();
                await RefreshPagesAsync();
                await SyncAsync();
            }
        };
        Overlay = dialog;
    }

    private void ShowIcsSubscription()
    {
        var subscription = new IcsSubscriptionViewModel(_setup);
        subscription.Finished += async (_, created) =>
        {
            Overlay = null;
            if (created)
            {
                HasAccounts = true;
                await SyncAsync();
                CurrentPage = CalendarPage;
            }
        };
        Overlay = subscription;
    }

    private async Task ShowSpecialCalendarAsync()
    {
        var special = new SpecialCalendarViewModel(_setup, await _accounts.GetAccountsAsync());
        special.Finished += async (_, created) =>
        {
            Overlay = null;
            if (created)
            {
                HasAccounts = true;
                await SyncAsync();
                CurrentPage = CalendarPage;
            }
        };
        Overlay = special;
    }

    // "Schnellaktion erstellen?" – not over another dialog (then it is asked again after three more moves).
    private void SuggestQuickAction(Neruna.Core.Mail.QuickActionSuggestion suggestion)
    {
        if (Overlay is not null)
        {
            return;
        }

        var dialog = new QuickActionSuggestionViewModel(suggestion, _suggestions);
        dialog.Finished += (_, created) =>
        {
            Overlay = null;
            if (created is not null)
            {
                StatusText = F("Schnellaktion «{0}» erstellt.", created);
            }
        };
        Overlay = dialog;
    }

    private void ShowCalendarPrint()
    {
        var print = new CalendarPrintViewModel(_calendar, _files, CalendarPage.CalendarGroups.SelectMany(g => g.Items), CalendarViewModel.StartOfWeek(CalendarPage.WeekStart), CalendarPage.IsWorkWeek);
        print.Finished += (_, _) =>
        {
            Overlay = null;
            print.Release();
        };
        Overlay = print;
        _ = print.RefreshAsync();
    }

    private void ShowCalendarSelection()
    {
        var selection = new CalendarSelectionViewModel(_calendar, _settings);
        selection.Finished += async (_, changed) =>
        {
            Overlay = null;
            if (changed)
            {
                await CalendarPage.ReloadAsync();
                StatusText = F("Kalender aktualisiert um {0:t}", DateTime.Now);
            }
        };
        // Several accounts: the two-column layout needs more room once they are known.
        selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CalendarSelectionViewModel.HasSeveralSources))
            {
                OnPropertyChanged(nameof(OverlayWidth));
            }
        };
        Overlay = selection;
        _ = selection.RefreshAsync();
    }

    private void ShowAddressBookSelection()
    {
        var selection = new AddressBookSelectionViewModel(_contacts, _settings);
        selection.Finished += async (_, changed) =>
        {
            Overlay = null;
            if (changed)
            {
                await ContactsPage.ReloadAsync();
                StatusText = F("Adressbücher aktualisiert um {0:t}", DateTime.Now);
            }
        };
        Overlay = selection;
        _ = selection.RefreshAsync();
    }

    // Editors raise Finished(changed); the page reloads from the local store, which the controller already updated.
    private void ShowEditor(ViewModelBase editor, Func<Task> reload)
    {
        void OnFinished(object? sender, bool changed)
        {
            Overlay = null;
            if (changed)
            {
                _ = reload();
                StatusText = (sender as EventEditorViewModel)?.ResultMessage ?? F("Gespeichert um {0:t}", DateTime.Now);
            }
        }

        switch (editor)
        {
            case EventEditorViewModel eventEditor:
                eventEditor.Finished += OnFinished;
                break;
            case ContactEditorViewModel contactEditor:
                contactEditor.Finished += OnFinished;
                break;
            case GroupEditorViewModel groupEditor:
                groupEditor.Finished += OnFinished;
                break;
            case ContactDuplicatesViewModel duplicates:
                duplicates.Finished += OnFinished;
                break;
            case TaskEditorViewModel taskEditor:
                taskEditor.Finished += OnFinished;
                break;
        }

        Overlay = editor;
    }

    // Pages not on screen are only marked; they reload when opened (rebuilding them all froze the window after a sync).
    private readonly HashSet<ViewModelBase> _stalePages = [];

    private async Task RefreshPagesAsync()
    {
        _stalePages.UnionWith([MailPage, CalendarPage, ContactsPage, TasksPage, SettingsPage]);
        await RefreshIfStaleAsync(CurrentPage);
    }

    private async Task RefreshIfStaleAsync(ViewModelBase page)
    {
        if (!_stalePages.Remove(page))
        {
            return;
        }

        try
        {
            switch (page)
            {
                case MailViewModel mail:
                    await mail.RefreshAfterSyncAsync();
                    break;
                case CalendarViewModel calendar:
                    await calendar.ReloadAsync();
                    break;
                case ContactsViewModel contacts:
                    await contacts.ReloadAsync();
                    break;
                case TasksViewModel tasks:
                    await tasks.ReloadAsync();
                    break;
                case SettingsViewModel settings:
                    await settings.ReloadAsync();
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reloading {Page} failed", page.GetType().Name);
        }
    }

    partial void OnCurrentPageChanged(ViewModelBase value)
    {
        foreach (var item in MailPage.Preferences.NavigationItems)
        {
            item.IsActive = SectionOf(value) == item.Section;
        }

        UpdateChatActive();
        _ = RefreshIfStaleAsync(value);
    }

    partial void OnOverlayChanged(ViewModelBase? value)
    {
        UpdateChatActive();
        if (value is null && _crashOfferWaiting)
        {
            _crashOfferWaiting = false;
            Dispatcher.UIThread.Post(async () => await OfferCrashReportsAsync());
        }
    }

    /// <summary>"Einstellungen geändert – Neue Sicherung erstellen?" (only when this device has the vault set up).</summary>
    [ObservableProperty]
    public partial bool ShowBackupHint { get; set; }

    // Changed since the last backup? Asked at the first sync after the start, then every few hours – not after every
    // dialog, a reminder now and then is enough.
    private static readonly TimeSpan BackupCheckInterval = TimeSpan.FromHours(4);
    private DateTime _backupCheckedAt;

    private async Task CheckBackupHintAsync()
    {
        _backupCheckedAt = DateTime.UtcNow;
        try
        {
            ShowBackupHint = await _backup.ShouldSuggestBackupAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not compare settings with the last backup");
        }
    }

    /// <summary>Click on the hint: what changed since the last backup.</summary>
    [RelayCommand]
    private async Task ShowBackupChangesAsync()
    {
        IReadOnlyList<Neruna.Core.Cloud.BackupChange>? changes = null;
        try
        {
            changes = await _backup.GetChangesSinceBackupAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not list the changes since the last backup");
        }

        var dialog = new BackupChangesViewModel(changes, BackupNowCommand, BackupLaterCommand);
        dialog.Finished += (_, _) => Overlay = null;
        Overlay = dialog;
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        ShowBackupHint = false;
        CurrentPage = SettingsPage;
        SettingsPage.SelectedTab = SettingsViewModel.CloudTab;
        await SettingsPage.Cloud.StartBackupAsync();
    }

    [RelayCommand]
    private async Task BackupLaterAsync()
    {
        ShowBackupHint = false;
        await _backup.DismissSuggestionAsync();
    }
}

/// <summary>The answer to "Entwurf speichern?" when Neruna closes.</summary>
internal enum CloseChoice
{
    Cancel,
    Save,
    Discard,
}
