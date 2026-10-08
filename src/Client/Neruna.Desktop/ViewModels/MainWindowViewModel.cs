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

namespace Neruna.Desktop.ViewModels;

internal enum Section
{
    Mail,
    Calendar,
    Contacts,
    Settings,
}

/// <summary>Shell: navigation rail, sync, status bar and modal overlays (setup, editors).</summary>
internal sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);

    private readonly MailController _mail;
    private readonly CalendarController _calendar;
    private readonly ContactController _contacts;
    private readonly AccountSetupService _setup;
    private readonly AccountDiscovery _discovery;
    private readonly IAccountStore _accounts;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private readonly MailPushService _push;
    private readonly NotificationService _notifications;
    private readonly ReminderScheduler _reminders;
    private readonly ISettingsStore _settings;

    /// <summary>Remembered window placement and column widths; the views restore and update them.</summary>
    public UiLayout Layout { get; }

    public MainWindowViewModel(
        MailViewModel mailPage,
        CalendarViewModel calendarPage,
        ContactsViewModel contactsPage,
        SettingsViewModel settingsPage,
        MailController mail,
        CalendarController calendar,
        ContactController contacts,
        AccountSetupService setup,
        AccountDiscovery discovery,
        IAccountStore accounts,
        IHttpClientFactory http,
        UiLayout layout,
        MailPushService push,
        NotificationService notifications,
        ReminderScheduler reminders,
        ISettingsStore settings,
        ILogger<MainWindowViewModel> logger)
    {
        Layout = layout;
        _push = push;
        _notifications = notifications;
        _reminders = reminders;
        _settings = settings;
        MailPage = mailPage;
        CalendarPage = calendarPage;
        ContactsPage = contactsPage;
        SettingsPage = settingsPage;
        var accountsPage = settingsPage.Accounts;
        _mail = mail;
        _calendar = calendar;
        _contacts = contacts;
        _setup = setup;
        _discovery = discovery;
        _accounts = accounts;
        _http = http;
        _logger = logger;
        CurrentPage = mailPage;

        mailPage.StatusMessage += (_, message) => StatusText = message;
        mailPage.CalendarChanged += async (_, _) =>
        {
            await CalendarPage.ReloadAsync();
            await _reminders.CheckAsync();
        };
        mailPage.AccountsReordered += async (_, _) => await SettingsPage.Accounts.ReloadAsync();
        mailPage.PropertyChanged += async (_, e) =>
        {
            // Opening a signed message may have collected a new certificate.
            if (e.PropertyName == nameof(MailViewModel.ReadingPane) && mailPage.ReadingPane?.HasSecurity == true)
            {
                await SettingsPage.Certificates.ReloadAsync();
            }
        };
        calendarPage.SubscribeRequested += (_, _) => ShowIcsSubscription();
        calendarPage.ManageRequested += (_, _) => ShowCalendarSelection();
        calendarPage.EditorRequested += (_, editor) => ShowEditor(editor, async () =>
        {
            await CalendarPage.ReloadAsync();
            await _reminders.CheckAsync();
        });
        contactsPage.EditorRequested += (_, editor) => ShowEditor(editor, ContactsPage.ReloadAsync);
        contactsPage.ManageRequested += (_, _) => ShowAddressBookSelection();
        contactsPage.MailRequested += async (_, recipients) =>
        {
            CurrentPage = MailPage;
            await MailPage.ComposeToAsync(recipients);
        };
        accountsPage.AddAccountRequested += (_, _) => ShowAccountSetup();
        accountsPage.SubscribeRequested += (_, _) => ShowIcsSubscription();
        accountsPage.AccountsChanged += async (_, _) =>
        {
            await RefreshPagesAsync();
            await UpdatePushAsync();
        };
        settingsPage.MailOptions.PushChanged += async (_, _) => await UpdatePushAsync();
        settingsPage.MailOptions.TestNotificationRequested += (_, _) =>
        {
            try
            {
                _notifications.Show(new NotificationViewModel(
                    "Neue E-Mail · Test", "Neruna", "So sehen Benachrichtigungen aus", "Ein Klick holt Neruna nach vorne.",
                    () =>
                    {
                        NotificationService.ActivateMainWindow();
                        return Task.CompletedTask;
                    }));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Showing the test notification failed");
                StatusText = "Test-Benachrichtigung fehlgeschlagen: " + ex.Message;
            }
        };

        // Push runs in the background; the UI is updated on its thread.
        push.FolderSynced += (_, folder) => _ = Dispatcher.UIThread.InvokeAsync(() => MailPage.RefreshFolderAsync(folder.ConnectionId, folder.RemoteId));
        push.NewMail += (_, e) => _ = Dispatcher.UIThread.InvokeAsync(() => NotifyAsync(e));

        _timer = new DispatcherTimer { Interval = SyncInterval };
        _timer.Tick += async (_, _) => await SyncAsync();
    }

    public MailViewModel MailPage { get; }

    public CalendarViewModel CalendarPage { get; }

    public ContactsViewModel ContactsPage { get; }

    public SettingsViewModel SettingsPage { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMail), nameof(IsCalendar), nameof(IsContacts), nameof(IsSettings), nameof(SectionTitle))]
    public partial ViewModelBase CurrentPage { get; set; }

    public bool IsMail => CurrentPage == MailPage;

    public bool IsCalendar => CurrentPage == CalendarPage;

    public bool IsContacts => CurrentPage == ContactsPage;

    public bool IsSettings => CurrentPage == SettingsPage;

    /// <summary>Shown in the header bar; the window title already says "Neruna".</summary>
    public string SectionTitle => IsCalendar ? "Kalender" : IsContacts ? "Kontakte" : IsSettings ? "Einstellungen" : "E-Mails";

    /// <summary>Modal content shown above the shell, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlay), nameof(OverlayWidth))]
    public partial ViewModelBase? Overlay { get; set; }

    public bool HasOverlay => Overlay is not null;

    /// <summary>The group editor shows members and contacts side by side and needs more room.</summary>
    public double OverlayWidth => Overlay is GroupEditorViewModel { IsReadOnly: false } ? 980 : 640;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Bereit";

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
        await RefreshPagesAsync();
        HasAccounts = (await _accounts.GetAccountsAsync()).Count > 0;
    }

    /// <summary>Once the window is visible: account setup for a first start, otherwise sync with the servers.</summary>
    public async Task StartAsync()
    {
        if (!HasAccounts)
        {
            ShowAccountSetup();
            return;
        }

        _timer.Start();
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

    // New mail: a notification – unless the user is looking at exactly that inbox in Neruna right now.
    private async Task NotifyAsync(NewMailEvent e)
    {
        try
        {
            await ShowNotificationsAsync(e);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Showing the notification failed");
        }
    }

    private async Task ShowNotificationsAsync(NewMailEvent e)
    {
        var account = e.Account.EmailAddress ?? e.Account.DisplayName;
        if (!await _settings.GetBoolAsync(SettingKeys.MailNotifications, fallback: true))
        {
            _logger.LogInformation("{Count} new mail(s) for {Account}: notifications are switched off", e.Messages.Count, account);
            return;
        }

        var lookingAtIt = NotificationService.IsAppActive && CurrentPage == MailPage && Overlay is null
                          && MailPage.CurrentFolder?.Folder is { } open && open.ConnectionId == e.Folder.ConnectionId && open.RemoteId == e.Folder.RemoteId;
        if (lookingAtIt)
        {
            _logger.LogInformation("{Count} new mail(s) for {Account}: no notification, the inbox is open in front", e.Messages.Count, account);
            return;
        }

        _logger.LogInformation("{Count} new mail(s) for {Account}: showing a notification", e.Messages.Count, account);

        if (e.Messages.Count > MaxNotifications)
        {
            var newest = e.Messages[0];
            _notifications.Show(new NotificationViewModel(
                $"{e.Messages.Count} neue E-Mails · {account}",
                string.Join(", ", e.Messages.Select(m => m.From?.DisplayText).Where(n => n is not null).Distinct().Take(3)),
                newest.Subject,
                null,
                () => OpenMessageAsync(e.Folder, newest.RemoteId)));
            return;
        }

        foreach (var message in e.Messages.Reverse())
        {
            _notifications.Show(new NotificationViewModel(
                "Neue E-Mail · " + account,
                message.From?.DisplayText ?? "(unbekannt)",
                string.IsNullOrWhiteSpace(message.Subject) ? "(kein Betreff)" : message.Subject,
                message.Preview,
                () => OpenMessageAsync(e.Folder, message.RemoteId)));
        }
    }

    private const int MaxNotifications = 3;

    private async Task OpenMessageAsync(MailFolder folder, string remoteId)
    {
        NotificationService.ActivateMainWindow();
        Overlay = null;
        CurrentPage = MailPage;
        await MailPage.RevealAsync(folder.ConnectionId, folder.RemoteId, remoteId);
    }

    [RelayCommand]
    private void Navigate(Section section) => CurrentPage = section switch
    {
        Section.Calendar => CalendarPage,
        Section.Contacts => ContactsPage,
        Section.Settings => SettingsPage,
        _ => MailPage,
    };

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncAsync()
    {
        IsSyncing = true;
        StatusText = "Synchronisiere …";
        try
        {
            // The three areas are independent; one failing never blocks the others.
            var reports = await Task.WhenAll(_mail.SyncAllAsync(), _calendar.SyncAllAsync(), _contacts.SyncAllAsync());
            await RefreshPagesAsync();
            await _reminders.CheckAsync();

            var failures = reports.SelectMany(r => r.Failures).ToList();
            StatusText = failures.Count == 0
                ? $"Synchronisiert um {DateTime.Now:HH:mm}"
                : $"Synchronisiert um {DateTime.Now:HH:mm} – {failures.Count} Verbindung(en) fehlgeschlagen: {failures[0].Error.Message} (Details im Log)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sync failed");
            StatusText = "Synchronisierung fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private bool CanSync() => !IsSyncing;

    // "Element öffnen" in the reminder window: the event in the calendar's editor.
    private async Task OpenReminderAsync(Neruna.Core.Calendar.CalendarOccurrence occurrence)
    {
        NotificationService.ActivateMainWindow();
        CurrentPage = CalendarPage;
        await CalendarPage.OpenOccurrenceAsync(occurrence);
    }

    [RelayCommand]
    private void ShowAccountSetup()
    {
        var setup = new AccountSetupViewModel(_discovery, _setup, _http.CreateClient("dav"));
        setup.Finished += async (_, created) =>
        {
            Overlay = null;
            if (created)
            {
                HasAccounts = true;
                _timer.Start();
                await SyncAsync();
            }
        };
        Overlay = setup;
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

    private void ShowCalendarSelection()
    {
        var selection = new CalendarSelectionViewModel(_calendar);
        selection.Finished += async (_, changed) =>
        {
            Overlay = null;
            if (changed)
            {
                await CalendarPage.ReloadAsync();
                StatusText = $"Kalender aktualisiert um {DateTime.Now:HH:mm}";
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
                StatusText = $"Adressbücher aktualisiert um {DateTime.Now:HH:mm}";
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
                StatusText = (sender as EventEditorViewModel)?.ResultMessage ?? $"Gespeichert um {DateTime.Now:HH:mm}";
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
        }

        Overlay = editor;
    }

    private async Task RefreshPagesAsync()
    {
        await MailPage.RefreshAfterSyncAsync();
        await CalendarPage.ReloadAsync();
        await ContactsPage.ReloadAsync();
        await SettingsPage.ReloadAsync();
    }
}
