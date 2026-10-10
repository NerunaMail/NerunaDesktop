using Avalonia.VisualTree;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Neruna.Core;
using Neruna.Desktop;
using Neruna.Desktop.Editor;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

// Usage: dotnet run --project tools/Neruna.Desktop.Snapshot -- <output-dir> [light|dark] [--live <mail-host> <dav-url>]
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/screenshots");
var theme = args.Length > 1 && args[1] == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
var mailFiles = Array.IndexOf(args, "--eml") is var e and >= 0 ? args[(e + 1)..] : null;
var certificates = Array.IndexOf(args, "--certificates") is var ce and >= 0 ? args[(ce + 1)..(ce + 4)] : null;
var calendars = Array.IndexOf(args, "--calendars") is var cl and >= 0 ? new Uri(args[cl + 1]) : null;
var cloudAccounts = Array.IndexOf(args, "--cloud-accounts") is var ca and >= 0 ? args[(ca + 1)..(ca + 4)] : null;
var backup = Array.IndexOf(args, "--backup") is var bk and >= 0 ? args[(bk + 1)..(bk + 6)] : null;
var firstStart = Array.IndexOf(args, "--first-start") is var fs and >= 0 ? args[fs + 1] : null;
var live = Array.IndexOf(args, "--live") is var i and >= 0 ? (Host: args[i + 1], Dav: new Uri(args[i + 2])) : ((string Host, Uri Dav)?)null;
Directory.CreateDirectory(output);
// Language of the pictures: NERUNA_SNAPSHOT_LANG=en|fr|it (German by default).
Neruna.Core.Localization.Texts.Use(Environment.GetEnvironmentVariable("NERUNA_SNAPSHOT_LANG") ?? "de");

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

Application.Current!.RequestedThemeVariant = theme;

// Headless rendering has no native WebView: compose uses its plain-text fallback.
HtmlEditor.ForcePlainText = true;

var done = new CancellationTokenSource();
var exitCode = 0;
Dispatcher.UIThread.Post(async () =>
{
    try
    {
        if (mailFiles is not null)
        {
            await Snapshots.RenderMailsAsync(output, mailFiles);
        }
        else if (calendars is { } calendarDav)
        {
            await LiveScenario.CalendarManagementAsync(output, calendarDav);
        }
        else if (cloudAccounts is [var accountsServer, var accountsCode, var accountsPin])
        {
            await LiveScenario.CloudAccountsAsync(output, accountsServer, accountsCode, accountsPin);
        }
        else if (backup is [var backupServer, var code1, var pin1, var code2, var pin2])
        {
            await LiveScenario.BackupAsync(output, backupServer, code1, pin1, code2, pin2);
        }
        else if (certificates is [var certServer, var certCode, var certPin])
        {
            await LiveScenario.CertificatesAsync(output, certServer, certCode, certPin);
        }
        else if (firstStart is { } host)
        {
            await LiveScenario.FirstStartAsync(output, host);
        }
        else if (live is { } l)
        {
            await LiveScenario.RunAsync(output, l.Host, l.Dav);
        }
        else
        {
            await Snapshots.RunAsync(output);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        exitCode = 1;
    }
    finally
    {
        await done.CancelAsync();
    }
});
Dispatcher.UIThread.MainLoop(done.Token);
return exitCode;

internal static class Snapshots
{
    public static async Task RunAsync(string output)
    {
        // Screenshots of the compose editor: formatted preview instead of the plain-text fallback (no WebView here).
        HtmlEditor.ScreenshotPreview = true;
        var dataDir = Path.Combine(Path.GetTempPath(), "neruna-snapshot-" + Guid.NewGuid().ToString("N"));
        await using var services = await AppServices.BuildAsync(new AppOptions(dataDir, Demo: true));
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 880 };
        window.Show();

        await vm.InitializeAsync();

        await CheckMarkAsReadAsync(vm, services.GetRequiredService<ISettingsStore>());
        await CheckLayoutAsync(vm, window, services.GetRequiredService<ISettingsStore>());

        // Favoriten on top of the folder tree: the inbox and a project folder.
        foreach (var folder in vm.MailPage.Accounts.SelectMany(a => a.AllFolders()).Where(f => f.Folder.Role == Neruna.Core.Mail.FolderRole.Inbox).Take(2)
                     .Concat(vm.MailPage.Accounts.SelectMany(a => a.AllFolders()).Where(f => f.Folder.Role == Neruna.Core.Mail.FolderRole.None).Take(1)))
        {
            await vm.MailPage.AddFavoriteCommand.ExecuteAsync(folder);
        }

        // Mail: open the first message in the inbox.
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First();
        await Task.Delay(300);
        await SaveAsync(window, output, "mail.png");

        // A freshly added account: in the tree at once, "Ordner werden geladen …" until its first sync is done.
        var newAccount = vm.MailPage.Accounts[0].Connection.Id;
        vm.MailPage.MarkSettingUp(newAccount, true);
        await Task.Delay(200);
        await SaveAsync(window, output, "mail-account-setting-up.png");
        vm.MailPage.MarkSettingUp(newAccount, false);
        vm.MailPage.Preferences.ToolbarLabels = false;
        await SaveAsync(window, output, "mail-icons-only.png");
        vm.MailPage.Preferences.ToolbarLabels = true;

        // Double-click: the message in its own window (through the mail page, then rendered on its own).
        await vm.MailPage.OpenInWindowAsync(vm.MailPage.SelectedMessage!);
        var messageWindow = new MessageWindow
        {
            DataContext = new MessageWindowViewModel(vm.MailPage.SelectedMessage!.Subject, vm.MailPage.Preferences, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.FromResult(false))
            {
                Pane = vm.MailPage.ReadingPane,
            },
            Width = 900,
            Height = 700,
        };
        messageWindow.Show();
        await SaveAsync(messageWindow, output, "mail-window.png");
        messageWindow.Close();

        // A signature for the demo account: new mails get it automatically.
        var signatures = services.GetRequiredService<Neruna.Core.Mail.SignatureService>();
        var signature = new Neruna.Core.Mail.Signature(Guid.NewGuid(), "Standard",
            "<div><b>Anna Muster</b></div><div style=\"color:#0F6CBD\">Projektleiterin · Muster Informatik AG</div><div>Tel. +41 44 123 45 67 · www.muster-informatik.example</div>", DateTimeOffset.Now);
        await signatures.SaveAsync(signature);
        await signatures.SaveAsync(signature with { Id = Guid.NewGuid(), Name = "Kurz", Html = "<div>Freundliche Grüsse<br>Anna</div>" });
        var demoAccount = (await services.GetRequiredService<Neruna.Core.IAccountStore>().GetAccountsAsync()).First();
        await signatures.SetAssignmentAsync(demoAccount.Id, new Neruna.Core.Mail.SignatureAssignment(signature.Id, null));

        // Own text templates (inserted at the caret, or by "kürzel::").
        var textTemplates = services.GetRequiredService<Neruna.Core.Mail.TextTemplateService>();
        await textTemplates.SaveAsync(new Neruna.Core.Mail.TextTemplate(Guid.NewGuid(), "Anrufnotiz",
            "<p><strong>Anrufnotiz</strong></p><table style=\"border-collapse: collapse; width: 100%\">"
            + "<tr><td style=\"border: 1px solid #c8c8c8; padding: 4px 8px; vertical-align: top\" width=\"120\">Name</td><td style=\"border: 1px solid #c8c8c8; padding: 4px 8px; vertical-align: top\">&nbsp;</td></tr><tr><td style=\"border: 1px solid #c8c8c8; padding: 4px 8px; vertical-align: top\">Nummer</td><td style=\"border: 1px solid #c8c8c8; padding: 4px 8px; vertical-align: top\">&nbsp;</td></tr><tr><td style=\"border: 1px solid #c8c8c8; padding: 4px 8px; vertical-align: top\">Grund</td><td style=\"border: 1px solid #c8c8c8; padding: 4px 8px; vertical-align: top\">&nbsp;</td></tr></table>",
            DateTimeOffset.Now, Shortcut: "tel"));
        await textTemplates.SaveAsync(new Neruna.Core.Mail.TextTemplate(Guid.NewGuid(), "Terminbestätigung", "<p>Gerne bestätige ich Ihnen den Termin.</p>", DateTimeOffset.Now, Shortcut: "termin"));

        // The editor shows a formatted preview here (no WebView headless): toolbar, text and signature as on Windows.
        await vm.MailPage.NewMailCommand.ExecuteAsync(null);
        await Task.Delay(300);
        var focused = window.FocusManager?.GetFocusedElement() as Control;
        Console.WriteLine($"New mail focus: {focused?.GetType().Name} in {focused?.FindAncestorOfType<Neruna.Desktop.Controls.RecipientBox>()?.Name ?? "-"}");
        vm.MailPage.Compose!.To = "Marco Bernasconi <marco@bernasconi.example>";
        vm.MailPage.Compose.Subject = "Offerte Netzwerk-Erneuerung Q4";
        await Task.Delay(300);
        await SaveAsync(window, output, "compose.png");
        vm.MailPage.Preferences.ToolbarLabels = false;
        await SaveAsync(window, output, "compose-icons-only.png");
        vm.MailPage.Preferences.ToolbarLabels = true;

        // An: typing suggests contacts and groups; the "An …" button opens the picker (several at once).
        var toBox = window.GetVisualDescendants().OfType<Neruna.Desktop.Controls.RecipientBox>().First();
        // Letter by letter (each one replaces the suggestions; the first versions crashed here on Windows).
        foreach (var typed in new[] { "l", "le", "l", "", "m", "ma", "marco", "x" })
        {
            await toBox.TypeAsync(typed);
            await Task.Delay(60);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        await toBox.TypeAsync("Marco Bernasconi <marco@bernasconi.example>, l");
        await Task.Delay(60);
        await toBox.TypeAsync("Marco Bernasconi <marco@bernasconi.example>, le");
        await Task.Delay(200);
        Console.WriteLine($"Suggestions open: {toBox.IsSuggesting}");
        await SaveAsync(window, output, "compose-autocomplete.png");
        await toBox.TypeAsync(string.Empty);

        var picking = RecipientPicker.ShowAsync(window, "An", vm.MailPage.Compose!.Recipients!);
        for (var i = 0; i < 20 && RecipientPicker.Open is null; i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        if (RecipientPicker.Open is { } picker)
        {
            picker.Width = 560;
            var pickList = picker.GetVisualDescendants().OfType<ListBox>().First();
            var entries = pickList.ItemsSource!.Cast<Neruna.Core.Contacts.RecipientEntry>().ToList();
            Console.WriteLine($"Picker: {entries.Count} entries: {string.Join(", ", entries.Select(e => e.Name))}");
            foreach (var entry in entries.Where((_, i) => i is 1 or 3))
            {
                pickList.SelectedItems!.Add(entry);
            }

            await SaveAsync(picker, output, "recipient-picker.png");
            var take = picker.GetVisualDescendants().OfType<Button>().First(b => b.IsDefault);
            take.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var picked = await picking;
            vm.MailPage.Compose!.AddRecipients(RecipientField.To, picked);
            Console.WriteLine($"Picked into An: {vm.MailPage.Compose.To}");
        }

        vm.MailPage.Compose = null;

        // Reply to the opened message: HTML quote with reply header (shown as text in the headless fallback).
        // Focus check with the editor's plain-text mode (the screenshot preview takes no focus).
        HtmlEditor.ScreenshotPreview = false;
        await vm.MailPage.ReplyCommand.ExecuteAsync(null);
        await Task.Delay(300);
        var replyFocus = window.FocusManager?.GetFocusedElement() as Control;
        Console.WriteLine($"Reply focus: {replyFocus?.GetType().Name} in editor: {replyFocus?.FindAncestorOfType<HtmlEditor>() is not null}");
        vm.MailPage.Compose = null;
        HtmlEditor.ScreenshotPreview = true;
        await vm.MailPage.ReplyCommand.ExecuteAsync(null);
        await Task.Delay(300);
        await SaveAsync(window, output, "reply.png");
        vm.MailPage.Compose = null;

        // The question when closing with a changed draft open.
        var question = ChoiceDialog.ShowAsync(window, "Neruna beenden",
            "Der Entwurf «Offerte Netzwerk-Erneuerung Q4» hat ungespeicherte Änderungen. Vor dem Beenden speichern?",
            ("Speichern", 1, true), ("Nicht speichern", 2, false), ("Abbrechen", 0, false));
        Dispatcher.UIThread.RunJobs();
        if (ChoiceDialog.Open is { } dialog)
        {
            await SaveAsync(dialog, output, "close-question.png");
            dialog.Close();
        }

        await question;

        // An invitation in a mail: the bar with Annehmen / Vorläufig / Ablehnen.
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First(m => m.Subject.StartsWith("Einladung:", StringComparison.Ordinal));
        for (var i = 0; i < 50 && vm.MailPage.ReadingPane?.Invitation?.Status is null; i++)
        {
            await Task.Delay(100);
        }

        await SaveAsync(window, output, "invitation.png");

        // Tagesansicht beside the mail: switched on in the header, remembered; its own choice of calendars.
        vm.MailPage.Agenda.IsOpen = true;
        await Task.Delay(300);
        await vm.MailPage.Agenda.ReloadAsync();
        Console.WriteLine($"Agenda: {string.Join(" | ", vm.MailPage.Agenda.Entries.Select(e => e switch { AgendaDayHeader h => h.Title, AgendaItem i => $"{i.Time} {i.Title}", _ => "?" }).Take(8))}");
        await SaveAsync(window, output, "mail-agenda.png");
        var teamChoice = vm.MailPage.Agenda.Calendars.FirstOrDefault(c => c.Info.RemoteId == "team");
        if (teamChoice is not null)
        {
            teamChoice.IsShown = false;
            await Task.Delay(300);
            Console.WriteLine($"Agenda without 'Team': {vm.MailPage.Agenda.Entries.OfType<AgendaItem>().Count()} appointments; remembered hidden: {await services.GetRequiredService<Neruna.Core.ISettingsStore>().GetAsync(Neruna.Core.SettingKeys.MailAgendaHidden)}");
            teamChoice.IsShown = true;
            await Task.Delay(300);
        }

        vm.MailPage.Agenda.IsOpen = false;
        await Task.Delay(100);
        Console.WriteLine($"Agenda remembered as open: {await services.GetRequiredService<Neruna.Core.ISettingsStore>().GetAsync(Neruna.Core.SettingKeys.MailAgendaOpen)}");

        // Advanced search across all folders of the account.
        await ClickAdvancedSearchAsync(window);
        Console.WriteLine($"Advanced search open after a click on the button: {vm.MailPage.IsAdvancedSearchOpen}");
        vm.MailPage.SearchBody = string.Empty;
        vm.MailPage.SearchSubject = "Offerte";
        vm.MailPage.SearchScopeChoice = vm.MailPage.SearchScopes.First(s => s.IsAccount);
        await vm.MailPage.RunSearchCommand.ExecuteAsync(null);
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().FirstOrDefault();
        await Task.Delay(400);
        await SaveAsync(window, output, "search.png");
        Console.WriteLine($"Demo search: {vm.MailPage.SearchInfo}");
        await vm.MailPage.CloseSearchCommand.ExecuteAsync(null);

        // A desktop notification (shown on its own; the website puts it onto the main window).
        var toast = new NotificationWindow
        {
            DataContext = new NotificationViewModel("Neue E-Mail · anna.muster@example.com", "Marco Bernasconi", "Offerte Netzwerk-Erneuerung Q4",
                "Hallo Anna, anbei wie besprochen unsere Offerte …", () => Task.CompletedTask),
        };
        toast.Show();
        await SaveAsync(toast, output, "notification-window.png");
        toast.Close();

        // The font dropdown opens in a popup that headless capture does not include, so render its items on their own.
        await SaveFontListAsync(output);
        await SaveSplashAsync(output);

        vm.NavigateCommand.Execute(Section.Calendar);
        await vm.CalendarPage.ReloadAsync();

        // An own display name for a calendar: in the list and in the event editor, the server keeps its name.
        var team = vm.CalendarPage.Calendars.First(c => c.Info.RemoteId == "team");
        await vm.CalendarPage.RenameAsync(team, "Team Bernasconi");
        var renamedItem = vm.CalendarPage.Calendars.First(c => c.Info.RemoteId == "team");
        Console.WriteLine($"Renamed calendar: list shows '{renamedItem.Name}', {renamedItem.ServerNameText}");
        await vm.CalendarPage.RenameAsync(renamedItem, null);
        Console.WriteLine($"Reset: list shows '{vm.CalendarPage.Calendars.First(c => c.Info.RemoteId == "team").Name}'");
        await SaveAsync(window, output, "calendar.png");
        vm.CalendarPage.NavigatorMonthCount = 2;
        await SaveAsync(window, output, "calendar-2-months.png");
        vm.CalendarPage.NavigatorMonthCount = 1;
        vm.CalendarPage.IsTimeGrid = true;
        await SaveAsync(window, output, "calendar-timegrid.png");

        // A meeting with attendees, their answers and a reminder.
        var calendarController = services.GetRequiredService<Neruna.Core.Calendar.CalendarController>();
        var personal = (await calendarController.GetCalendarsAsync()).First(c => c.RemoteId == "personal") with { IsReadOnly = false };
        var thursday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7)).AddDays(3).AddHours(14);
        var meeting = Neruna.Core.Calendar.EventDraft.New(thursday) with
        {
            Summary = "Quartalsplanung",
            Location = "Sitzungszimmer Pilatus",
            End = thursday.AddMinutes(90),
            Organizer = "anna.muster@example.com",
            Attendees =
            [
                new Neruna.Core.Calendar.EventAttendee("lea.keller@example.com", "Lea Keller", Neruna.Core.Calendar.Participation.Accepted),
                new Neruna.Core.Calendar.EventAttendee("marco@bernasconi.example", "Marco Bernasconi", Neruna.Core.Calendar.Participation.Tentative),
                new Neruna.Core.Calendar.EventAttendee("nadia.rossi@example.com", "Nadia Rossi"),
            ],
        };
        vm.Overlay = new EventEditorViewModel(calendarController, services.GetRequiredService<Neruna.Core.Calendar.InvitationService>(), [personal], meeting,
            (personal, "personal-2@demo.neruna"), _ => "anna.muster@example.com");
        await SaveAsync(window, output, "event-attendees.png");
        vm.Overlay = null;

        // The reminder window with the next events of the demo week.
        var upcoming = (await calendarController.GetOccurrencesAsync(await calendarController.GetCalendarsAsync(), DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddDays(4)))
            .Where(o => !o.IsAllDay).Take(3).ToList();
        var reminderModel = new ReminderWindowViewModel(_ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, TimeProvider.System);
        reminderModel.Show(upcoming.Select(o => new Neruna.Core.Calendar.Reminder(o.Uid + o.Start, o, o.Start.AddMinutes(-15))).ToList());
        var reminderWindow = new ReminderWindow { DataContext = reminderModel };
        reminderWindow.Show();
        await SaveAsync(reminderWindow, output, "reminder-window.png");
        reminderWindow.Close();
        vm.CalendarPage.IsWorkWeek = true;
        await Task.Delay(300);
        await SaveAsync(window, output, "calendar-workweek.png");
        vm.CalendarPage.IsWorkWeek = false;
        await Task.Delay(300);
        vm.CalendarPage.GridMinutes = 15;
        await SaveAsync(window, output, "calendar-timegrid-15.png");
        vm.CalendarPage.GridMinutes = 30;
        vm.CalendarPage.IsTimeGrid = false;

        // Special calendars: birthdays (reminder asked) and holidays of a canton, computed by Neruna.
        var specialAccounts = services.GetRequiredService<Neruna.Core.IAccountStore>();
        var special = new SpecialCalendarViewModel(services.GetRequiredService<Neruna.Core.Accounts.AccountSetupService>(), await specialAccounts.GetAccountsAsync());
        vm.Overlay = special;
        await SaveAsync(window, output, "special-calendar-birthdays.png");
        await special.AddCommand.ExecuteAsync(null);
        special = new SpecialCalendarViewModel(services.GetRequiredService<Neruna.Core.Accounts.AccountSetupService>(), await specialAccounts.GetAccountsAsync());
        special.SelectedCountry = special.Countries.First(c => c.Code == "CH");
        special.SelectedRegion = special.Regions.First(r => r.Code == "LU");
        vm.Overlay = special;
        await SaveAsync(window, output, "special-calendar-holidays.png");
        await special.AddCommand.ExecuteAsync(null);
        vm.Overlay = null;
        await calendarController.SyncAllAsync();
        await vm.CalendarPage.ReloadAsync();
        vm.CalendarPage.IsTimeGrid = true;
        vm.CalendarPage.WeekStart = new DateTime(2026, 10, 26);
        await vm.CalendarPage.ReloadAsync();
        await Task.Delay(500);
        await SaveAsync(window, output, "calendar-holidays.png");
        vm.CalendarPage.WeekStart = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        vm.CalendarPage.IsTimeGrid = false;

        // Tasks: own section with lists per account, grouped by due date; optionally also in the calendar.
        vm.NavigateCommand.Execute(Section.Tasks);
        await vm.TasksPage.ReloadAsync();
        await SaveAsync(window, output, "tasks.png");
        var mixedList = vm.TasksPage.ListGroups.SelectMany(g => g.Lists).First(l => l.Info.RemoteId == "personal");
        vm.TasksPage.ToggleSoloCommand.Execute(mixedList);
        await Task.Delay(300);
        await SaveAsync(window, output, "tasks-solo.png");
        vm.TasksPage.ToggleSoloCommand.Execute(mixedList);
        var firstTask = vm.TasksPage.Groups.First().Rows.First();
        firstTask.EditCommand.Execute(null);
        await Task.Delay(300);
        await SaveAsync(window, output, "tasks-editor.png");
        vm.Overlay = null;
        var demoTasks = vm.TasksPage.ListGroups.SelectMany(g => g.Lists).First(l => l.Info.RemoteId == "tasks");
        demoTasks.ShowTasks = true;
        await Task.Delay(300);
        vm.NavigateCommand.Execute(Section.Calendar);
        vm.CalendarPage.IsTimeGrid = true;
        await vm.CalendarPage.ReloadAsync();
        await Task.Delay(400);
        await SaveAsync(window, output, "calendar-tasks.png");

        // The colour/name menu (a popup in the app), on its own: the same control in the calendar and under "Aufgaben".
        var menuItem = vm.CalendarPage.Calendars.First(c => c.Info.RemoteId == "personal");
        var menu = new Window
        {
            Width = 260,
            Height = 440,
            Content = new Border { Padding = new Avalonia.Thickness(16), Child = new Neruna.Desktop.Views.CollectionAppearanceEditor { DataContext = menuItem } },
        };
        menu.Bind(Window.BackgroundProperty, menu.GetResourceObservable("PaneBackgroundBrush"));
        menu.Show();
        await SaveAsync(menu, output, "collection-menu.png");
        menu.Close();
        var teamCalendar = vm.CalendarPage.Calendars.First(c => c.Info.RemoteId == "team");
        await vm.CalendarPage.ToggleSoloCommand.ExecuteAsync(teamCalendar);
        await Task.Delay(300);
        await SaveAsync(window, output, "calendar-solo.png");
        await vm.CalendarPage.ToggleSoloCommand.ExecuteAsync(teamCalendar);
        demoTasks.ShowTasks = false;
        vm.CalendarPage.IsTimeGrid = false;

        vm.NavigateCommand.Execute(Section.Contacts);
        await SaveAsync(window, output, "contacts.png");
        vm.ContactsPage.Selected = vm.ContactsPage.Items.First(i => i.IsGroup);
        await Task.Delay(300);
        await SaveAsync(window, output, "contacts-group.png");
        await vm.ContactsPage.EditCommand.ExecuteAsync(null);
        await Task.Delay(200);
        await SaveAsync(window, output, "contacts-group-editor.png");
        vm.Overlay = null;

        // The demo address books are read-only; render the editor as for a writable one (members left, contacts right).
        var contactController = services.GetRequiredService<Neruna.Core.Contacts.ContactController>();
        var writable = (await contactController.GetAddressBooksAsync()).Select(b => b with { IsReadOnly = false }).ToList();
        var allContacts = await contactController.SearchAsync(null);
        var rigi = allContacts.First(c => c.Card.IsGroup);
        var editable = new GroupEditorViewModel(contactController, writable, allContacts, Neruna.Core.Contacts.GroupDraft.FromVCard((await contactController.GetContactAsync(rigi.AddressBook, rigi.RemoteId))!.VCardData),
            (rigi.AddressBook with { IsReadOnly = false }, rigi.RemoteId));
        vm.Overlay = editable;
        await SaveAsync(window, output, "contacts-group-editor-edit.png");
        editable.SearchText = "na";
        await SaveAsync(window, output, "contacts-group-editor-search.png");
        Console.WriteLine($"Group editor: {editable.Members.Count} members, offered: {string.Join(", ", editable.Available.Select(c => c.Name))}");
        vm.Overlay = null;

        vm.ContactsPage.ManageCommand.Execute(null);
        var books = (AddressBookSelectionViewModel)vm.Overlay!;
        for (var i = 0; i < 50 && books.IsBusy; i++)
        {
            await Task.Delay(100);
        }

        await SaveAsync(window, output, "contacts-addressbooks.png");
        Console.WriteLine("Address books offered: " + string.Join(", ", books.Sources.SelectMany(g => g.AddressBooks).Select(b => b.Name + (b.IsSelected ? " [x]" : " [ ]"))));
        vm.Overlay = null;

        vm.NavigateCommand.Execute(Section.Mail);
        vm.NavigateCommand.Execute(Section.Settings);
        await vm.SettingsPage.ReloadAsync();
        vm.SettingsPage.SelectedTab = 1;
        await SaveAsync(window, output, "settings-mail.png");

        // Konto bearbeiten with a label and an alias (sender address of the same mailbox).
        var editedAccount = (await services.GetRequiredService<Neruna.Core.IAccountStore>().GetAccountsAsync())[0];
        vm.SettingsPage.SelectedTab = 0;
        await vm.SettingsPage.Accounts.ReloadAsync();
        vm.SettingsPage.Accounts.EditCommand.Execute(vm.SettingsPage.Accounts.Items[0]);
        await Task.Delay(300);
        if (vm.Overlay is AccountSetupViewModel accountEdit)
        {
            accountEdit.Label = "Büro";
            // Demo accounts have no servers; for the picture what a typical one looks like.
            (accountEdit.ImapHost, accountEdit.SmtpHost, accountEdit.Username) = ("mail.example.com", "mail.example.com", editedAccount.EmailAddress!);
            accountEdit.CalDavUrl = "https://mail.example.com/SOGo/dav/";
            accountEdit.CardDavUrl = "https://mail.example.com/SOGo/dav/";
            accountEdit.Aliases.Add(new AliasRow { Email = "info@" + editedAccount.EmailAddress!.Split('@')[1], DisplayName = "Muster Informatik AG" });
            await SaveAsync(window, output, "account-edit.png");
            accountEdit.CancelCommand.Execute(null);
        }
        var notifications = services.GetRequiredService<NotificationService>();
        vm.SettingsPage.MailOptions.TestNotificationCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"Test notification: visible={notifications.Visible.Count}, at={notifications.Visible.LastOrDefault()?.Position}");
        foreach (var open in notifications.Visible.ToList())
        {
            open.Close();
        }
        vm.SettingsPage.SelectedTab = 3;
        await SaveAsync(window, output, "settings-signatures.png");
        vm.SettingsPage.SelectedTab = 4;
        await vm.SettingsPage.TextTemplates.ReloadAsync();
        await SaveAsync(window, output, "settings-text-templates.png");
        await CertificateTabsAsync(vm, window, services, output);
        vm.SettingsPage.SelectedTab = 6;
        await SaveAsync(window, output, "settings-design.png");
        var preferences = services.GetRequiredService<UiPreferences>();
        preferences.ShowAddAccountButton = false;
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        var addAccount = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == Neruna.Core.Localization.Texts.T("Konto hinzufügen"));
        Console.WriteLine($"'Konto hinzufügen' hidden: {!addAccount.IsVisible}, remembered: {await services.GetRequiredService<Neruna.Core.ISettingsStore>().GetAsync(Neruna.Core.SettingKeys.ShowAddAccountButton)}");
        await SaveAsync(window, output, "settings-design-no-add-account.png");
        preferences.ShowAddAccountButton = true;
        // Navigation rail: Chat moved up to second place, Kontakte hidden – the rail follows at once, the choice is kept.
        var chatItem = preferences.NavigationItems.First(n => n.Section == Section.Chat);
        preferences.MoveNavigationItem(chatItem, -2);
        preferences.NavigationItems.First(n => n.Section == Section.Contacts).IsVisible = false;
        await SaveAsync(window, output, "settings-design-navigation.png");
        Console.WriteLine($"Navigation saved: {await services.GetRequiredService<Neruna.Core.ISettingsStore>().GetAsync(Neruna.Core.SettingKeys.NavigationItems)}");
        foreach (var item in preferences.NavigationItems)
        {
            item.IsVisible = false; // the last one stays visible
        }

        Console.WriteLine($"Visible after hiding all: {string.Join(", ", preferences.NavigationItems.Where(n => n.IsVisible).Select(n => n.Label))}");
        await preferences.LoadAsync();
        Console.WriteLine($"Reloaded: {string.Join(", ", preferences.NavigationItems.Select(n => n.Label + (n.IsVisible ? string.Empty : " (aus)")))}");
        foreach (var item in preferences.NavigationItems)
        {
            item.IsVisible = true;
        }

        preferences.MoveNavigationItem(preferences.NavigationItems.First(n => n.Section == Section.Chat), 2);
        // Einstellungen → Cloud: not connected; connected when a code for a running server is given
        // (NERUNA_SNAPSHOT_CLOUD=<url> NERUNA_SNAPSHOT_CLOUD_CODE=… NERUNA_SNAPSHOT_CLOUD_PIN=…).
        vm.SettingsPage.SelectedTab = 7;
        vm.SettingsPage.Cloud.DeviceName = "Anna – Notebook";
        await SaveAsync(window, output, "settings-cloud.png");

        // Chat without a cloud connection: the note that it needs Neruna Cloud/Control.
        vm.NavigateCommand.Execute(Section.Chat);
        await Task.Delay(200);
        await SaveAsync(window, output, "chat-not-connected.png");
        vm.NavigateCommand.Execute(Section.Settings);
        if (Environment.GetEnvironmentVariable("NERUNA_SNAPSHOT_CLOUD") is { Length: > 0 } cloudUrl)
        {
            vm.SettingsPage.Cloud.Server = cloudUrl;
            vm.SettingsPage.Cloud.Code = Environment.GetEnvironmentVariable("NERUNA_SNAPSHOT_CLOUD_CODE") ?? string.Empty;
            vm.SettingsPage.Cloud.Pin = Environment.GetEnvironmentVariable("NERUNA_SNAPSHOT_CLOUD_PIN") ?? string.Empty;
            await vm.SettingsPage.Cloud.ConnectCommand.ExecuteAsync(null);
            Console.WriteLine($"Cloud: {vm.SettingsPage.Cloud.ConnectedText ?? vm.SettingsPage.Cloud.Error}");
            await SaveAsync(window, output, "settings-cloud-connected.png");

            // The organisation's signatures arrived with the connection.
            vm.SettingsPage.SelectedTab = 3;
            await vm.SettingsPage.Signatures.ReloadAsync();
            vm.SettingsPage.Signatures.Selected = vm.SettingsPage.Signatures.Signatures.FirstOrDefault(s => s.IsFromCloud);
            Console.WriteLine($"Cloud signatures: {string.Join(", ", vm.SettingsPage.Signatures.Signatures.Where(s => s.IsFromCloud).Select(s => s.Name))}");
            await SaveAsync(window, output, "settings-signatures-cloud.png");

            // Chat: rooms, people with their status, messages with formatting; the badge counts what is unread.
            await vm.ChatPage.RefreshNowAsync();
            await Task.Delay(300);
            Console.WriteLine($"Chat: {vm.ChatPage.Rooms.Count} room(s), {vm.ChatPage.Directs.Count} private, {vm.ChatPage.TotalUnread} unread");
            vm.NavigateCommand.Execute(Section.Mail);
            await SaveAsync(window, output, "chat-badge.png");
            vm.NavigateCommand.Execute(Section.Chat);
            await vm.ChatPage.RefreshNowAsync();
            await Task.Delay(400);
            await SaveAsync(window, output, "chat.png");
            // The status list in the header is a popup: its buttons must reach the command (and set the status).
            var presenceButton = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PresenceButton");
            presenceButton.Flyout!.ShowAt(presenceButton);
            await Task.Delay(300);
            var choices = TopLevel.GetTopLevel(window)!.GetVisualDescendants().OfType<Button>().ToList();
            var popupButtons = (presenceButton.Flyout as Flyout)?.Content is Control flyoutContent
                ? flyoutContent.GetVisualDescendants().OfType<Button>().ToList()
                : [];
            Console.WriteLine($"Presence choices: {popupButtons.Count}");
            // A real click (pointer down/up) on "Nicht stören", as a user does.
            var dnd = popupButtons.FirstOrDefault(b => b.DataContext is PresenceChoice { Key: Neruna.Core.Chat.Presence.DoNotDisturb });
            if (dnd?.TranslatePoint(new Point(dnd.Bounds.Width / 2, dnd.Bounds.Height / 2), window) is { } at)
            {
                window.MouseDown(at, Avalonia.Input.MouseButton.Left);
                window.MouseUp(at, Avalonia.Input.MouseButton.Left);
            }

            await Task.Delay(300);
            Console.WriteLine($"Presence after choosing: {vm.ChatPage.Presence}");
            presenceButton.Flyout.Hide();
            vm.ChatPage.SelectCommand.Execute(vm.ChatPage.Directs.FirstOrDefault());
            vm.ChatPage.Draft = "Ja, gerne **gleich** ☕";
            await Task.Delay(300);
            await SaveAsync(window, output, "chat-private.png");
            await vm.ChatPage.SendCommand.ExecuteAsync(null);
            await Task.Delay(300);
            await SaveAsync(window, output, "chat-sent.png");
            Appearance.Apply(ThemeMode.Dark, ColorScheme.Blue);
            vm.ChatPage.SelectCommand.Execute(vm.ChatPage.Rooms.FirstOrDefault());
            await Task.Delay(300);
            await SaveAsync(window, output, "chat-dark.png");
            Appearance.Apply(ThemeMode.Light, ColorScheme.Blue);
            await vm.ChatPage.SetPresenceCommand.ExecuteAsync(Neruna.Core.Chat.Presence.Available);
            // Chat button hidden in Einstellungen → Design: offline for the others, no status switch in the header.
            var chatNav = vm.MailPage.Preferences.NavigationItems.First(n => n.Section == Section.Chat);
            chatNav.IsVisible = false;
            await Task.Delay(500);
            Console.WriteLine($"Chat hidden: status switch shown={vm.ChatPage.ShowPresence}, presence kept={vm.ChatPage.Presence}");
            await SaveAsync(window, output, "chat-hidden.png");
            chatNav.IsVisible = true;
            await Task.Delay(500);
            Console.WriteLine($"Chat shown again: status switch shown={vm.ChatPage.ShowPresence}");
            vm.NavigateCommand.Execute(Section.Settings);
        }

        vm.SettingsPage.SelectedTab = 8;
        await SaveAsync(window, output, "settings-about.png");

        // Crash report offered after a crash: "Details anzeigen" shows exactly what is sent (cleaned).
        var crashes = services.GetRequiredService<Neruna.Core.Diagnostics.CrashReportService>();
        try
        {
            throw new InvalidOperationException("Sync failed for anna.muster@example.com on imap.example.com");
        }
        catch (InvalidOperationException ex)
        {
            crashes.Store.Save(Neruna.Core.Diagnostics.CrashReportBuilder.Capture(ex, fatal: true, "0.1.12", "2026-10-10 14:02:11 INF Sync anna.muster@example.com", DateTimeOffset.Now));
        }

        var crashDialog = new CrashReportViewModel(await crashes.GetPendingAsync(), crashes);
        vm.NavigateCommand.Execute(Section.Mail);
        vm.Overlay = crashDialog;
        await SaveAsync(window, output, "crash-report.png");
        crashDialog.ToggleDetailsCommand.Execute(null);
        await SaveAsync(window, output, "crash-report-details.png");
        crashDialog.DontSendCommand.Execute(null);
        vm.Overlay = null;
        vm.NavigateCommand.Execute(Section.Settings);

        // Color schemes and dark mode applied to the running app.
        vm.NavigateCommand.Execute(Section.Mail);
        Appearance.Apply(ThemeMode.Dark, ColorScheme.Red);
        await SaveAsync(window, output, "theme-dark-red.png");
        vm.NavigateCommand.Execute(Section.Calendar);
        Appearance.Apply(ThemeMode.Light, ColorScheme.Green);
        await SaveAsync(window, output, "theme-light-green.png");
        Appearance.Apply(ThemeMode.Light, ColorScheme.Blue);
        vm.NavigateCommand.Execute(Section.Settings);
        vm.SettingsPage.SelectedTab = 0;

        vm.NavigateCommand.Execute(Section.Mail);
        vm.ShowAccountSetupCommand.Execute(null);
        await SaveAsync(window, output, "account-setup-kind.png");
        ((AccountSetupViewModel)vm.Overlay!).ChooseImapCommand.Execute(null);
        await SaveAsync(window, output, "account-setup.png");

        window.Close();
        Directory.Delete(dataDir, recursive: true);
    }

    /// <summary>Renders .eml files through the real reading-pane pipeline (debugging display issues of single mails).</summary>
    public static async Task RenderMailsAsync(string output, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var message = await MimeKit.MimeMessage.LoadAsync(file);
            var content = Neruna.Core.Mail.MessageContent.From(message);
            var view = new TheArtOfDev.HtmlRenderer.Avalonia.HtmlPanel { BaseStylesheet = MessageBodyView.BaseStylesheet, Background = Brushes.White };
            // Embedded pictures (cid:) from the message itself, as in the reading pane.
            view.ImageLoad += (_, e) =>
            {
                var cid = Neruna.Core.Mail.MessageContent.ContentIdOf(e.Event.Src);
                if (cid is null || message.BodyParts.OfType<MimeKit.MimePart>().FirstOrDefault(p => p.ContentId == cid) is not { Content: { } data })
                {
                    return;
                }

                e.Event.Handled = true;
                using var stream = new MemoryStream();
                data.DecodeTo(stream);
                stream.Position = 0;
                e.Event.Callback(new Avalonia.Media.Imaging.Bitmap(stream));
            };
            view.Text = Neruna.Desktop.Infrastructure.MailPaper.Prepare(content.Html, darkTheme: false).Html;
            var window = new Window { Width = 720, Height = 1000, Content = view, Background = Brushes.White };
            window.Show();
            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var path = Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".png");
            window.CaptureRenderedFrame()?.Save(path, PngBitmapEncoderOptions.Default);
            Console.WriteLine(path);
            window.Close();
        }
    }

    // Behaviour check of the "Als gelesen markieren" setting against the demo's unread message.
    private static async Task CheckMarkAsReadAsync(MainWindowViewModel vm, ISettingsStore settings)
    {
        MailViewModel.ReadDelay = TimeSpan.FromMilliseconds(400);
        var page = vm.MailPage;
        var results = new List<string>();
        foreach (var mode in Enum.GetValues<MarkAsReadMode>())
        {
            await settings.SetAsync(SettingKeys.MarkAsRead, mode.ToString());
            var unread = page.Entries.OfType<MessageItemViewModel>().First(m => m.IsUnread);
            page.SelectedEntry = unread;
            await Task.Delay(150);
            var afterOpen = unread.IsUnread;
            await Task.Delay(600);
            var afterDelay = unread.IsUnread;
            if (mode == MarkAsReadMode.OnReply)
            {
                await page.ReplyCommand.ExecuteAsync(null);
                page.Compose = null;
            }

            results.Add($"{mode}: nach Öffnen {(afterOpen ? "ungelesen" : "gelesen")}, nach Wartezeit {(afterDelay ? "ungelesen" : "gelesen")}, Ende {(unread.IsUnread ? "ungelesen" : "gelesen")}");
            await page.ToggleReadCommand.ExecuteAsync(null);
            if (!unread.IsUnread)
            {
                await page.ToggleReadCommand.ExecuteAsync(null);
            }

            page.SelectedEntry = null;
        }

        await settings.SetAsync(SettingKeys.MarkAsRead, null);
        MailViewModel.ReadDelay = TimeSpan.FromSeconds(10);
        Console.WriteLine(string.Join(Environment.NewLine, results));
    }

    private static async Task SaveSplashAsync(string output)
    {
        var splash = new SplashWindow { Status = "Konten und Ordner werden geladen …" };
        splash.Show();
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var path = Path.Combine(output, "splash.png");
        splash.CaptureRenderedFrame()?.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine(path);
        splash.Close();
    }

    private static async Task SaveFontListAsync(string output)
    {
        var list = new StackPanel { Margin = new Thickness(8), Spacing = 2 };
        foreach (var font in FontCatalog.Fonts.Take(24))
        {
            list.Children.Add(font.IsFont
                ? new TextBlock { Text = font.Name, FontFamily = font.Family, FontSize = 15, Margin = new Thickness(6, 3) }
                : new Border { Height = 1, Background = Brushes.LightGray, Margin = new Thickness(0, 4) });
        }

        var window = new Window { Width = 260, Height = 760, Content = list, Background = Brushes.White };
        window.Show();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var path = Path.Combine(output, "font-list.png");
        window.CaptureRenderedFrame()?.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine(path);
        window.Close();
    }

    // Column widths and window size survive a restart: drag, save, load into a fresh layout, restore.
    // Einstellungen → Zertifikate: own certificates with details, contacts as a short list, authorities apart.
    private static async Task CertificateTabsAsync(MainWindowViewModel vm, Window window, IServiceProvider services, string output)
    {
        var manager = services.GetRequiredService<Neruna.Core.Security.CertificateManager>();
        await manager.ImportAsync(Neruna.Client.Tests.TestPki.Pkcs12(Neruna.Client.Tests.TestPki.User("Anna Muster", "anna@example.com"), "a"), "a");
        await manager.ImportAsync(Neruna.Client.Tests.TestPki.Ca.GetEncoded(), null);
        foreach (var (name, days) in new[] { ("Lea Keller", 400), ("Marco Bernasconi", 20), ("Beat Brunner", -30), ("Sara Rossi", 700), ("Jonas Weber", 90) })
        {
            var notAfter = DateTime.UtcNow.AddDays(days);
            var user = Neruna.Client.Tests.TestPki.User(name, name.Split(' ')[0].ToLowerInvariant() + "@example.com", notAfter.AddYears(-2), notAfter);
            await manager.ImportAsync(user.Certificate.GetEncoded(), null);
        }

        vm.SettingsPage.SelectedTab = 5;
        await vm.SettingsPage.Certificates.ReloadAsync();
        await SaveAsync(window, output, "settings-certificates.png");
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Last(t => t.Items.Count == 3);
        tabs.SelectedIndex = 1;
        await SaveAsync(window, output, "settings-certificates-contacts.png");
        tabs.SelectedIndex = 2;
        await SaveAsync(window, output, "settings-certificates-authorities.png");
        Console.WriteLine($"Certificates: {vm.SettingsPage.Certificates.OwnHeader}, {vm.SettingsPage.Certificates.ContactsHeader}, {vm.SettingsPage.Certificates.AuthoritiesHeader}");
    }

    private static async Task CheckLayoutAsync(MainWindowViewModel vm, MainWindow window, ISettingsStore settings)
    {
        var grid = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "Columns" && g.FindAncestorOfType<MailView>() is not null);
        grid.ColumnDefinitions[0].Width = new GridLength(312);
        grid.ColumnDefinitions[2].Width = new GridLength(455);
        Dispatcher.UIThread.RunJobs();
        vm.Layout.Flush();

        var fresh = new UiLayout(settings);
        await fresh.LoadAsync();
        var restoredGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,6,400,6,*") };
        fresh.TrackColumns(restoredGrid, "columns.mail", 0, 2);

        var compose = new Window { Width = 800, Height = 600, MinWidth = 400, MinHeight = 300 };
        fresh.TrackWindow(compose, "window.test", withPosition: false);
        compose.Show();
        compose.Width = 900;
        compose.Height = 640;
        Dispatcher.UIThread.RunJobs();
        compose.Close();
        var again = new UiLayout(settings);
        await again.LoadAsync();
        var reopened = new Window { Width = 800, Height = 600, MinWidth = 400, MinHeight = 300 };
        again.TrackWindow(reopened, "window.test", withPosition: false);

        Console.WriteLine($"Layout: mail columns restored={restoredGrid.ColumnDefinitions[0].Width.Value}/{restoredGrid.ColumnDefinitions[2].Width.Value}, " +
                          $"window restored={reopened.Width}x{reopened.Height}");
        grid.ColumnDefinitions[0].Width = new GridLength(240);
        grid.ColumnDefinitions[2].Width = new GridLength(400);
    }

    /// <summary>A real mouse click on the "Erweiterte Suche" button (not its binding: that hid a button that did nothing).</summary>
    public static async Task ClickAdvancedSearchAsync(Window window)
    {
        var button = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .First(t => ToolTip.GetTip(t) is string tip && tip == Neruna.Core.Localization.Texts.T("Erweiterte Suche: Absender, Betreff, Text, Ordner"));
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task SaveAsync(Window window, string output, string name)
    {
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        var path = Path.Combine(output, name);
        frame.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine(path);
    }
}
