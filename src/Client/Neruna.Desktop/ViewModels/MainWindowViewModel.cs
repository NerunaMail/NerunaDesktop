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
        AccountSetupService setup,
        AccountDiscovery discovery,
        IAccountStore accounts,
        IHttpClientFactory http,
        UiLayout layout,
        MailPushService push,
        MailNotifier notifier,
        ReminderScheduler reminders,
        ISettingsStore settings,
        Neruna.Core.Cloud.CloudSignatureSync cloudSignatures,
        Neruna.Core.Cloud.CloudTextTemplateSync cloudTemplates,
        Neruna.Core.Cloud.CloudCertificateSync cloudCertificates,
        Neruna.Core.Cloud.SettingsBackupService backup,
        Neruna.Core.Cloud.CloudAccountSync cloudAccounts,
        Neruna.Core.Auth.IBrowserLauncher browser,
        Neruna.Core.Diagnostics.CrashReportService crashReports,
        ILogger<MainWindowViewModel> logger)
    {
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
        _setup = setup;
        _discovery = discovery;
        _accounts = accounts;
        _http = http;
        _push = push;
        _notifier = notifier;
        _reminders = reminders;
        _settings = settings;
        _cloudSignatures = cloudSignatures;
        _cloudTemplates = cloudTemplates;
        _cloudCertificates = cloudCertificates;
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
        MailPage.AutoRepliesChanged += (_, _) => SettingsPage.Accounts.ShowAutoReplies(MailPage.ActiveAutoReplies);

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
        CalendarPage.SpecialRequested += async (_, _) => await ShowSpecialCalendarAsync();
        CalendarPage.EditorRequested += (_, editor) => ShowEditor(editor, CalendarChangedAsync);
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
        ContactsPage.EditorRequested += (_, editor) => ShowEditor(editor, ContactsPage.ReloadAsync);
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
            if (e.PropertyName == nameof(CloudViewModel.Connection))
            {
                await ChatPage.RefreshNowAsync();
            }
        };

        // Restored from a backup: every page from the store again, then the servers of the restored accounts.
        cloud.Restored += async (_, _) =>
        {
            ShowBackupHint = false;
            await MailPage.Preferences.LoadAsync();
            await SettingsPage.ReloadAsync();
            await RefreshPagesAsync();
            HasAccounts = (await _accounts.GetAccountsAsync()).Count > 0;
            await UpdatePushAsync();
            await SyncAsync();
        };
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
        accountsPage.AccountsChanged += async (_, _) =>
        {
            await RefreshPagesAsync();
            await UpdatePushAsync();
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

    /// <summary>The group editor shows members and contacts side by side and needs more room.</summary>
    public double OverlayWidth => Overlay switch
    {
        GroupEditorViewModel { IsReadOnly: false } => 980,
        CalendarSelectionViewModel { HasSeveralSources: true } => 900,
        _ => 640,
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = T("Bereit");

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
        // Neruna opens with the first button of the rail (E-Mail unless hidden or moved).
        Navigate(MailPage.Preferences.StartSection);
        await RefreshPagesAsync();
        await MailPage.Agenda.LoadStateAsync();
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
    private readonly Neruna.Core.Cloud.CloudCertificateSync _cloudCertificates;
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
            await Task.Run(() => _cloudCertificates.SyncAsync());
            SettingsPage.Cloud.UpdateCertificateStatus();
            if (await Task.Run(() => _cloudAccounts.SyncAsync()))
            {
                await CloudAccountsChangedAsync(sync: false);
                cloudAccountsChanged = true;
            }

            AskForCloudAccountPassword();
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

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncAsync()
    {
        // Timer, button, new accounts and the cloud all ask for syncs; the coordinator runs one at a time and lets
        // waiting requests share the next run. "Synchronisiere …" stays until the last request is done.
        _syncCalls++;
        IsSyncing = true;
        StatusText = T("Synchronisiere …");
        try
        {
            var reports = await _sync.SyncAllAsync();
            await RefreshPagesAsync();
            await MailPage.Agenda.ReloadAsync();
            await _reminders.CheckAsync();
            if (await SyncCloudAsync())
            {
                // New accounts from the organisation: fetch their folders and mail right away.
                reports = [.. reports, .. await _sync.SyncAllAsync()];
                await RefreshPagesAsync();
            }

            if (DateTime.UtcNow - _autoRepliesCheckedAt > TimeSpan.FromHours(1))
            {
                _ = RefreshAutoRepliesAsync();
            }

            var failures = reports.SelectMany(r => r.Failures).ToList();
            StatusText = failures.Count == 0
                ? F("Synchronisiert um {0:t}", DateTime.Now)
                : F("Synchronisiert um {0:t} – {1} Verbindung(en) fehlgeschlagen: {2} (Details im Log)", DateTime.Now, failures.Count, failures[0].Error.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sync failed");
            StatusText = T("Synchronisierung fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsSyncing = --_syncCalls > 0;
        }
    }

    private bool CanSync() => !IsSyncing;

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
        var setup = new AccountSetupViewModel(_discovery, _setup, _http.CreateClient("dav")) { Browser = _browser };
        if (editing is not null)
        {
            setup.LoadForEditing(editing);
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
    }

    // ---- Out of office ---------------------------------------------------------------------------------------------

    private async Task ShowAutoReplyAsync(Neruna.Core.Accounts.Account account, Neruna.Core.Accounts.ServiceConnection connection)
    {
        var dialog = new AutoReplyViewModel(_mail, account, connection);
        dialog.Finished += (_, saved) =>
        {
            Overlay = null;
            if (saved && dialog.Saved is { } reply)
            {
                // What was just stored counts at once – no second trip to the server (and no wait for other accounts).
                StatusText = T("Abwesenheitsnotiz gespeichert.");
                MailPage.UpdateAutoReplies(new Dictionary<Guid, Neruna.Core.Mail.AutoReply?> { [connection.Id] = reply });
            }
        };
        Overlay = dialog;
        await dialog.LoadAsync();
    }

    private DateTime _autoRepliesCheckedAt;

    /// <summary>
    /// Asks every mail account's server whether it answers automatically (at start, then hourly) – all at once, so a
    /// server without ManageSieve does not hold up the others. Unknown answers keep what Neruna knew.
    /// </summary>
    private async Task RefreshAutoRepliesAsync()
    {
        _autoRepliesCheckedAt = DateTime.UtcNow;
        var nodes = MailPage.Accounts.ToList();
        var answers = await Task.WhenAll(nodes.Select(node => Task.Run(async () =>
        {
            try
            {
                return (node.Connection.Id, (Neruna.Core.Mail.AutoReply?)(await _mail.GetAutoReplyAsync(node.Connection)).Reply);
            }
            catch (Neruna.Core.Mail.AutoReplyUnavailableException ex)
            {
                // No ManageSieve, a missing permission: this account has none in Neruna.
                _logger.LogInformation("Out-of-office status of {Account} not available: {Reason}", node.Title, ex.Message);
                return (node.Connection.Id, (Neruna.Core.Mail.AutoReply?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Out-of-office status of {Account} could not be read", node.Title);
                return (node.Connection.Id, (Neruna.Core.Mail.AutoReply?)null);
            }
        })));
        MailPage.UpdateAutoReplies(answers.ToDictionary(a => a.Item1, a => a.Item2));
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

    private void ShowCalendarSelection()
    {
        var selection = new CalendarSelectionViewModel(_calendar);
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
        var selection = new AddressBookSelectionViewModel(_contacts);
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

        if (value is null && CurrentPage != SettingsPage)
        {
            _ = CheckBackupHintAsync();
        }
    }

    // Leaving the settings (or an account dialog opened from elsewhere): changed since the last backup? Then ask.
    partial void OnCurrentPageChanged(ViewModelBase oldValue, ViewModelBase newValue)
    {
        if (oldValue == SettingsPage && newValue != SettingsPage)
        {
            _ = CheckBackupHintAsync();
        }
    }

    /// <summary>"Einstellungen geändert – Neue Sicherung erstellen?" (only when this device has the vault set up).</summary>
    [ObservableProperty]
    public partial bool ShowBackupHint { get; set; }

    private async Task CheckBackupHintAsync()
    {
        try
        {
            ShowBackupHint = await _backup.ShouldSuggestBackupAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not compare settings with the last backup");
        }
    }

    [RelayCommand]
    private void BackupNow()
    {
        ShowBackupHint = false;
        CurrentPage = SettingsPage;
        SettingsPage.SelectedTab = SettingsViewModel.CloudTab;
        SettingsPage.Cloud.StartBackup();
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
