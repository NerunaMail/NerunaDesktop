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
using Neruna.Desktop.Snapshot;

// Usage: dotnet run --project tools/Neruna.Desktop.Snapshot -- <output-dir> [light|dark] [--live <mail-host> <dav-url>]
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/screenshots");
var theme = args.Length > 1 && args[1] == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
var mailFiles = Array.IndexOf(args, "--eml") is var e and >= 0 ? args[(e + 1)..] : null;
var certificates = Array.IndexOf(args, "--certificates") is var ce and >= 0 ? args[(ce + 1)..(ce + 4)] : null;
var calendars = Array.IndexOf(args, "--calendars") is var cl and >= 0 ? new Uri(args[cl + 1]) : null;
var cloudAccounts = Array.IndexOf(args, "--cloud-accounts") is var ca and >= 0 ? args[(ca + 1)..(ca + 4)] : null;
var backup = Array.IndexOf(args, "--backup") is var bk and >= 0 ? args[(bk + 1)..(bk + 6)] : null;
var twoOrganisations = Array.IndexOf(args, "--two-organisations") is var to and >= 0 ? args[(to + 1)..(to + 6)] : null;
var rulesHost = Array.IndexOf(args, "--rules") is var rh and >= 0 ? args[rh + 1] : null;
var pst = Array.IndexOf(args, "--pst") is var ps and >= 0 ? args[ps + 1] : null;
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
MessageBodyView.UseRenderer = true;

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
        else if (rulesHost is not null)
        {
            await LiveScenario.ServerRulesAsync(output, rulesHost);
        }
        else if (pst is not null)
        {
            await LiveScenario.PstAsync(output, pst);
        }
        else if (twoOrganisations is [var orgServer, var orgCode1, var orgPin1, var orgCode2, var orgPin2])
        {
            await LiveScenario.TwoOrganisationsAsync(output, orgServer, orgCode1, orgPin1, orgCode2, orgPin2);
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
            await vm.MailPage.Tree.AddFavoriteCommand.ExecuteAsync(folder);
        }

        // Mail: open the first message in the inbox.
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First();
        await Task.Delay(300);
        await SaveAsync(window, output, "mail.png");

        // A sender with a contact picture: the picture instead of the initials.
        if (vm.MailPage.Entries.OfType<MessageItemViewModel>().FirstOrDefault(m => m.Summary.From?.Address == "nadia.rossi@example.com") is { } nadia)
        {
            vm.MailPage.SelectedEntry = nadia;
            await Task.Delay(500);
            await SaveAsync(window, output, "mail-sender-photo.png");
        }

        // An account on its color in the folder list (Einstellungen → Konten → ⋯ → Farbe in der Ordnerliste).
        var colorSettings = services.GetRequiredService<ISettingsStore>();
        foreach (var node in vm.MailPage.Accounts)
        {
            await Neruna.Desktop.ViewModels.AccountColors.SetAsync(colorSettings, node.Account.Id, "#0B6A0B");
        }

        await vm.MailPage.Tree.UpdateColorsAsync();
        // A second account below: the line between the accounts.
        var accountStore = services.GetRequiredService<Neruna.Core.IAccountStore>();
        var second = new Neruna.Core.Accounts.Account(Guid.NewGuid(), "Zweites Konto", "zweit@example.com",
            [new Neruna.Core.Accounts.ServiceConnection(Guid.NewGuid(), Neruna.Core.Accounts.ServiceKind.Mail, "demo", new Dictionary<string, string>())]);
        await accountStore.SaveAccountAsync(second);
        await vm.MailPage.ReloadAsync();
        await Task.Delay(300);
        await SaveAsync(window, output, "mail-account-color.png");
        await accountStore.DeleteAccountAsync(second.Id);
        await vm.MailPage.ReloadAsync();
        foreach (var node in vm.MailPage.Accounts)
        {
            await Neruna.Desktop.ViewModels.AccountColors.SetAsync(colorSettings, node.Account.Id, null);
        }

        await vm.MailPage.Tree.UpdateColorsAsync();
        Console.WriteLine($"Sender of the open mail can be saved as a contact: {vm.MailPage.ReadingPane?.CanAddSender}");

        // A sender who is no contact: "+" beside the name saves them as one.
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First(m => m.Sender == "Swisscom");
        await Task.Delay(800);
        Console.WriteLine($"Swisscom can be saved as a contact: {vm.MailPage.ReadingPane?.CanAddSender}");
        await SaveAsync(window, output, "mail-add-sender.png");
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First();
        await Task.Delay(300);

        // The list as a table (one line per mail, in columns), sorted by sender; back to cards afterwards.
        vm.MailPage.ListLayout.IsTable = true;
        vm.MailPage.ListLayout.SetWidths(170, 115, 60);
        await Task.Delay(300);
        await SaveAsync(window, output, "mail-table.png");
        vm.MailPage.SortByCommand.Execute(Neruna.Desktop.ViewModels.MailSort.From);
        await Task.Delay(300);
        await SaveAsync(window, output, "mail-table-by-sender.png");
        Console.WriteLine($"Table: sort {vm.MailPage.ListLayout.Sort} desc={vm.MailPage.ListLayout.Descending}, first {vm.MailPage.Entries.OfType<MessageItemViewModel>().First().Sender}");
        vm.MailPage.SortByCommand.Execute(Neruna.Desktop.ViewModels.MailSort.Date);

        // "Gesendete Elemente": the list shows whom a mail went to ("An: …"), the column head says "An".
        var inbox = vm.MailPage.CurrentFolder;
        vm.MailPage.Tree.SelectedTreeItem = vm.MailPage.Accounts.SelectMany(a => a.AllFolders()).First(f => f.Folder.Role == Neruna.Core.Mail.FolderRole.Sent);
        await Task.Delay(600);
        await SaveAsync(window, output, "mail-table-sent.png");
        Console.WriteLine($"Sent: column {vm.MailPage.FromColumnTitle}, first {vm.MailPage.Entries.OfType<MessageItemViewModel>().FirstOrDefault()?.Sender}");
        vm.MailPage.ListLayout.IsTable = false;
        await Task.Delay(300);
        await SaveAsync(window, output, "mail-sent.png");
        vm.MailPage.ListLayout.IsTable = true;
        vm.MailPage.Tree.SelectedTreeItem = inbox;
        await Task.Delay(600);

        // The reading pane below the list (tall screens), then none (a double-click opens the mail).
        vm.MailPage.PlacePaneCommand.Execute(Neruna.Desktop.ViewModels.ReadingPanePosition.Below);
        await Task.Delay(400);
        await SaveAsync(window, output, "mail-pane-below.png");
        vm.MailPage.PlacePaneCommand.Execute(Neruna.Desktop.ViewModels.ReadingPanePosition.None);
        await Task.Delay(300);
        await SaveAsync(window, output, "mail-pane-none.png");
        vm.MailPage.PlacePaneCommand.Execute(Neruna.Desktop.ViewModels.ReadingPanePosition.Right);
        vm.MailPage.ListLayout.IsTable = false;
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First();
        await Task.Delay(600);

        // A PDF attachment in the reading pane instead of the message; a second click shows the message again.
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First(m => m.HasAttachments);
        for (var i = 0; i < 50 && vm.MailPage.ReadingPane?.Message is null; i++)
        {
            await Task.Delay(100);
        }

        var pdfAttachment = vm.MailPage.ReadingPane!.Attachments.First(a => a.CanPreview);
        await pdfAttachment.ActivateCommand.ExecuteAsync(null);
        for (var i = 0; i < 50 && (vm.MailPage.ReadingPane.Preview?.IsBusy != false); i++)
        {
            await Task.Delay(100);
        }

        await SaveAsync(window, output, "mail-attachment-preview.png");

        // An attached mail (.eml): shown as a message in the reading pane.
        var openMessage = vm.MailPage.SelectedEntry;
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First(m => m.Summary.Subject == "Protokoll Kundenworkshop");
        for (var i = 0; i < 50 && vm.MailPage.ReadingPane?.Message?.Subject != "Protokoll Kundenworkshop"; i++)
        {
            await Task.Delay(100);
        }

        await vm.MailPage.ReadingPane!.Attachments.First(a => a.PreviewKind == Neruna.Core.Mail.AttachmentPreviewKind.Message).ActivateCommand.ExecuteAsync(null);
        await Task.Delay(800);
        Console.WriteLine($"Attached mail: {vm.MailPage.ReadingPane.Preview?.Message?.Subject} problem={vm.MailPage.ReadingPane.Preview?.Problem}");
        await SaveAsync(window, output, "mail-attached-message.png");
        vm.MailPage.SelectedEntry = openMessage;
        for (var i = 0; i < 50 && vm.MailPage.ReadingPane?.Attachments.Any(a => a.CanPreview) != true; i++)
        {
            await Task.Delay(100);
        }

        pdfAttachment = vm.MailPage.ReadingPane!.Attachments.First(a => a.CanPreview);
        await pdfAttachment.ActivateCommand.ExecuteAsync(null);
        for (var i = 0; i < 50 && (vm.MailPage.ReadingPane.Preview?.IsBusy != false); i++)
        {
            await Task.Delay(100);
        }

        Console.WriteLine($"Attachment preview: {vm.MailPage.ReadingPane.Preview?.Pages.Count} page(s), {vm.MailPage.ReadingPane.Preview?.PageInfo}, problem={vm.MailPage.ReadingPane.Preview?.Problem}");
        // Stamping the PDF: placed (shown on the page at once), then kept – with the person's name, not the login.
        await services.GetRequiredService<Neruna.Core.Mail.QuickActionService>().SetStampNameAsync("Anna Muster");
        var stampPreview = vm.MailPage.ReadingPane.Preview!;
        Console.WriteLine($"Can stamp: {stampPreview.CanStamp}, stamps: {string.Join(", ", stampPreview.Stamps.Select(s => s.Text))}");
        stampPreview.StartStampCommand.Execute(stampPreview.Stamps[0]);
        await Task.Delay(200);

        // Before the click: the stamp itself follows the mouse.
        if (Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Image>()
                .FirstOrDefault(i => i.DataContext is PreviewPage) is { } pageImage
            && pageImage.TranslatePoint(new Point(pageImage.Bounds.Width * 0.6, pageImage.Bounds.Height * 0.3), window) is { } ghostAt)
        {
            window.MouseMove(ghostAt, Avalonia.Input.RawInputModifiers.None);
            await Task.Delay(200);
            await SaveAsync(window, output, "mail-pdf-stamp-ghost.png");
        }

        await stampPreview.PlaceAsync(stampPreview.Pages[0], 0.72, 0.16);
        await Task.Delay(200);
        await SaveAsync(window, output, "mail-pdf-stamp.png");
        stampPreview.ApplyStampCommand.Execute(null);
        await Task.Delay(200);
        await SaveAsync(window, output, "mail-pdf-stamped.png");
        Console.WriteLine($"Stamped: {stampPreview.IsStamped}, stamping: {stampPreview.IsStamping}");

        // Own text: clicked first, then typed – shown where it begins.
        stampPreview.StartTextCommand.Execute(null);
        await stampPreview.PlaceAsync(stampPreview.Pages[0], 0.08, 0.36);
        stampPreview.TextContent = "Bitte bis Freitag bestätigen\nVisum: AM";
        stampPreview.TextColor = "#C42B1C";
        await Task.Delay(800);
        await SaveAsync(window, output, "mail-pdf-text.png");
        stampPreview.ApplyStampCommand.Execute(null);
        Console.WriteLine($"Text added: {stampPreview.IsStamped}, adding: {stampPreview.IsAddingText}");

        // A quick action for mails with a PDF: its button in the coloured bar.
        var quick = services.GetRequiredService<Neruna.Core.Mail.QuickActionService>();
        var firstStamp = (await quick.GetStampsAsync())[0];
        await quick.SaveActionsAsync(
        [
            new Neruna.Core.Mail.QuickAction(Guid.NewGuid(), "Offerte genehmigen", "#107C10",
                new Neruna.Core.Mail.QuickActionCondition { Attachment = Neruna.Core.Mail.QuickActionAttachmentKind.Pdf },
                [
                    new Neruna.Core.Mail.QuickActionStep(Neruna.Core.Mail.QuickActionStepKind.StampPdf) { StampId = firstStamp.Id },
                    new Neruna.Core.Mail.QuickActionStep(Neruna.Core.Mail.QuickActionStepKind.SaveAttachment) { Folder = @"\\fileserver\Offerten\Genehmigt", FileName = "{Datum}_{Absender}_{Dateiname}" },
                    new Neruna.Core.Mail.QuickActionStep(Neruna.Core.Mail.QuickActionStepKind.Reply),
                    new Neruna.Core.Mail.QuickActionStep(Neruna.Core.Mail.QuickActionStepKind.MoveToFolder) { FolderName = "Projekte" },
                ]),
            new Neruna.Core.Mail.QuickAction(Guid.NewGuid(), "An Buchhaltung", "#CA5010",
                new Neruna.Core.Mail.QuickActionCondition { Attachment = Neruna.Core.Mail.QuickActionAttachmentKind.Pdf },
                [new Neruna.Core.Mail.QuickActionStep(Neruna.Core.Mail.QuickActionStepKind.NewMail) { To = "buchhaltung@example.com", Subject = "Rechnung {Absender}" }]),
        ]);
        await pdfAttachment.ActivateCommand.ExecuteAsync(null);
        await Task.Delay(300);
        await SaveAsync(window, output, "mail-quick-actions.png");
        Console.WriteLine($"Quick action buttons: {string.Join(", ", vm.MailPage.QuickActionButtons.Select(b => b.Label))}");
        vm.NavigateCommand.Execute(Section.Settings);
        vm.SettingsPage.SelectedTab = 11;
        await vm.SettingsPage.QuickActions.ReloadAsync();
        await Task.Delay(300);
        await SaveAsync(window, output, "settings-quick-actions.png");

        // Schlagwörter: one from the organisation, the chips in the list, the right-click submenu and the settings page.
        var tagService = services.GetRequiredService<Neruna.Core.Mail.MailTagService>();
        await tagService.SetCloudAsync("snapshot", [new Neruna.Core.Mail.MailTag("offerte", "Offerte", "#e8590c", "snapshot")]);
        await vm.MailPage.LoadTagsAsync();
        vm.NavigateCommand.Execute(Section.Mail);
        await Task.Delay(500);
        await SaveAsync(window, output, "mail-tags.png");
        var tagged = window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is MessageItemViewModel m && m.HasTags);
        if (tagged?.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.ContextMenu is not null) is { } tagRow)
        {
            vm.MailPage.SelectedEntry = (MessageListEntry)tagged.DataContext!;
            tagRow.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
            await Task.Delay(500);
            var tagsItem = tagRow.ContextMenu!.Items.OfType<MenuItem>().First(i => i.Tag as string == "tags");
            tagsItem.Open();
            await Task.Delay(500);
            Dispatcher.UIThread.RunJobs();
            await SaveAsync(window, output, "mail-tags-menu.png");
            Console.WriteLine($"Tag menu: {string.Join(" | ", tagsItem.Items.OfType<MenuItem>().Select(m => $"{m.Header}{(m.IsEnabled ? "" : " (off)")}"))}");
            tagRow.ContextMenu.Close();
        }

        Console.WriteLine($"Tagged messages: {vm.MailPage.Entries.OfType<MessageItemViewModel>().Count(m => m.HasTags)}");
        await vm.SettingsPage.ShowMailTagsAsync();
        vm.NavigateCommand.Execute(Section.Settings);
        await Task.Delay(300);
        await SaveAsync(window, output, "settings-mail-tags.png");
        vm.SettingsPage.SelectedTab = 11;
        await Task.Delay(300);
        await vm.SettingsPage.QuickActions.EditCommand.ExecuteAsync(vm.SettingsPage.QuickActions.Actions[0]);
        await Task.Delay(300);
        await SaveAsync(window, output, "settings-quick-action-edit.png");
        // The move step's folder list: accounts as headings, folders indented below.
        var editorScroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.DataContext is QuickActionsViewModel);
        editorScroll.Offset = new Vector(0, 10000);
        await Task.Delay(300);
        var folderBox = window.GetVisualDescendants().OfType<ComboBox>().Last(c => c.Classes.Contains("folders"));
        Console.WriteLine($"Move folder chosen: {(folderBox.SelectedItem as FolderChoice)?.Path}, choices: {string.Join(" | ", ((IEnumerable<FolderChoice>)folderBox.ItemsSource!).Select(f => new string(' ', f.Depth) + f.Name + (f.IsAccount ? " (Konto)" : "")))}");
        folderBox.IsDropDownOpen = true;
        await Task.Delay(400);
        await SaveAsync(window, output, "settings-quick-action-folders.png");
        folderBox.IsDropDownOpen = false;
        // "Jedes Mal fragen": the editor without the folder list, and the picker that appears when the action runs.
        var moveStep = vm.SettingsPage.QuickActions.Editing!.Steps.Last(s => s.ShowsMoveFolder);
        moveStep.AskFolder = true;
        await Task.Delay(200);
        await SaveAsync(window, output, "settings-quick-action-ask.png");
        var folderPicker = new FolderPickerViewModel(vm.MailPage.Accounts, vm.MailPage.Accounts[0], null);
        var pickerWindow = new Window { Width = 440, Height = 560, Content = new FolderPickerView { DataContext = folderPicker, Margin = new Thickness(20, 16) } };
        pickerWindow.Bind(Window.BackgroundProperty, pickerWindow.GetResourceObservable("PaneBackgroundBrush"));
        pickerWindow.Show();
        await SaveAsync(pickerWindow, output, "quick-action-folder-picker.png");
        folderPicker.Filter = "ar";
        await SaveAsync(pickerWindow, output, "quick-action-folder-picker-search.png");
        Console.WriteLine($"Picker focus on search: {pickerWindow.FocusManager?.GetFocusedElement() is TextBox}, matches: {string.Join(" | ", folderPicker.Folders.Select(f => f.Name))}");
        pickerWindow.Close();

        // Einstellungen → Profile with two profiles (the demo itself has none).
        var profileRoot = Path.Combine(Path.GetTempPath(), "neruna-snapshot-profiles-" + Guid.NewGuid().ToString("N"));
        var profileList = Neruna.Storage.ProfileList.Load(profileRoot);
        profileList.Add("Business");
        var profiles = new ProfilesViewModel(new AppOptions(profileRoot, false, Root: profileRoot, Profile: profileList.All[0]),
            new Neruna.Storage.Credentials.LocalCredentialStore(profileRoot), Microsoft.Extensions.Logging.Abstractions.NullLogger<ProfilesViewModel>.Instance);
        profiles.Reload();
        profiles.Profiles[1].ConfirmDelete = true;
        var profilesWindow = new Window { Width = 980, Height = 560, Content = new ProfilesView { DataContext = profiles, Margin = new Thickness(24, 16) } };
        profilesWindow.Bind(Window.BackgroundProperty, profilesWindow.GetResourceObservable("PaneBackgroundBrush"));
        profilesWindow.Show();
        await SaveAsync(profilesWindow, output, "settings-profiles.png");
        profilesWindow.Close();
        var chooser = new ProfileChooserWindow(Neruna.Storage.ProfileList.Load(profileRoot));
        chooser.Show();
        await SaveAsync(chooser, output, "profile-chooser.png");
        chooser.Close();
        Directory.Delete(profileRoot, recursive: true);
        vm.SettingsPage.QuickActions.Editing!.CancelCommand.Execute(null);
        vm.NavigateCommand.Execute(Section.Mail);
        await Task.Delay(200);
        Console.WriteLine($"Preview closed again: {vm.MailPage.ReadingPane.Preview is null && vm.MailPage.ReadingPane.ShowsBody}");
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First();
        await Task.Delay(300);

        // The list is rebuilt (as after deleting with the Delete key): the focus stays in the list, so the next Delete works.
        var messageList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "MessageList");
        (messageList.ContainerFromItem(messageList.SelectedItem!) as ListBoxItem)?.Focus();
        vm.MailPage.ShowUnreadOnly = true;
        vm.MailPage.ShowUnreadOnly = false;
        await Task.Delay(300);
        var focusedAfter = window.FocusManager?.GetFocusedElement() as Visual;
        Console.WriteLine($"Focus after rebuilding the list: {focusedAfter?.GetType().Name}, in list={focusedAfter?.FindAncestorOfType<ListBox>(includeSelf: true) == messageList}");

        // Only unread mail: the closed envelope beside the search, highlighted while it filters.
        vm.MailPage.ShowUnreadOnly = true;
        await Task.Delay(200);
        await SaveAsync(window, output, "mail-unread-only.png");
        Console.WriteLine($"Unread only: {vm.MailPage.Entries.OfType<MessageItemViewModel>().Count()} shown, all unread or open: {vm.MailPage.Entries.OfType<MessageItemViewModel>().All(m => m.IsUnread || m == vm.MailPage.SelectedMessage)}");
        vm.MailPage.ShowUnreadOnly = false;

        // Out of office: the dialog, then the hint above the list while it is on.
        vm.MailPage.Tree.AutoReplyCommand.Execute(vm.MailPage.Accounts[0]);
        for (var i = 0; i < 50 && (vm.Overlay as AutoReplyViewModel)?.IsBusy != false; i++)
        {
            await Task.Delay(100);
        }

        if (vm.Overlay is AutoReplyViewModel autoReply)
        {
            autoReply.IsEnabled = true;
            autoReply.IsScheduled = true;
            autoReply.FirstDay = DateTime.Today;
            autoReply.LastDay = DateTime.Today.AddDays(11);
            autoReply.Message = "Guten Tag\n\nIch bin vom {{start}} bis und mit {{end}} in den Ferien und lese meine E-Mails nicht.\n\nFreundliche Grüsse\nAnna Muster";
            autoReply.TemplateName = "Ferien";
            await autoReply.SaveTemplateCommand.ExecuteAsync(null);
            await SaveAsync(window, output, "auto-reply.png");
            await autoReply.SaveCommand.ExecuteAsync(null);
            Console.WriteLine($"Out of office sent: {autoReply.Saved?.Message.Split('\n')[2]} | templates: {string.Join(", ", vm.AutoReplies.Templates.Select(t => t.Name))}, in use: {vm.AutoReplies.Accounts.FirstOrDefault()?.SelectedTemplate?.Name}");
            Console.WriteLine($"Out of office on: header '{vm.AutoReplies.HeaderText}', list: {string.Join(" | ", vm.AutoReplies.Accounts.Select(e => e.Title + " " + e.Status))}");
            // "Weitere Optionen …" in the list at the top opens the full dialog (a real click on the opened list).
            var autoReplyButton = window.FindControl<Button>("AutoReplyButton")!;
            var flyout = (Flyout)autoReplyButton.Flyout!;
            flyout.ShowAt(autoReplyButton);
            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();
            var more = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants((Control)flyout.Content!).OfType<Button>().First(b => Equals(b.Content, Neruna.Core.Localization.Texts.T("Weitere Optionen …")));
            more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 30 && vm.Overlay is not AutoReplyViewModel; i++)
            {
                await Task.Delay(100);
                Dispatcher.UIThread.RunJobs();
            }

            Console.WriteLine($"Weitere Optionen opens the dialog: {vm.Overlay is AutoReplyViewModel}");
            vm.Overlay = null;

            var row0 = vm.AutoReplies.Accounts[0];
            row0.ShowChecked(DateTimeOffset.Now.AddHours(-2), reachable: false);
            Console.WriteLine($"Out of office unreachable: '{row0.Status}', can change={row0.CanChange}, still listed={vm.AutoReplies.IsAvailable}");
            row0.ShowChecked(DateTimeOffset.Now, reachable: true);
            await SaveAsync(window, output, "auto-reply-notice.png");
            vm.NavigateCommand.Execute(Section.Settings);
            vm.SettingsPage.SelectedTab = 0;
            await Task.Delay(300);
            await SaveAsync(window, output, "auto-reply-accounts.png");
            vm.NavigateCommand.Execute(Section.Mail);

            // Switched off again from the list at the top: the hint goes at once.
            vm.MailPage.Tree.AutoReplyCommand.Execute(vm.MailPage.Accounts[0]);
            for (var i = 0; i < 50 && (vm.Overlay as AutoReplyViewModel)?.IsBusy != false; i++)
            {
                await Task.Delay(100);
            }

            var off = (AutoReplyViewModel)vm.Overlay!;
            off.IsEnabled = false;
            await off.SaveCommand.ExecuteAsync(null);
            Console.WriteLine($"Out of office off: header '{vm.AutoReplies.HeaderText}' active={vm.AutoReplies.AnyActive}, settings button '{vm.SettingsPage.Accounts.Items[0].AutoReplyText}', tree badge={vm.MailPage.Accounts[0].HasAutoReply}");

            // A series template ("Freitags") switched on, then back to "Ferien": the period comes back – in the dialog and in the list.
            async Task<AutoReplyViewModel> OpenAutoReplyAsync()
            {
                vm.MailPage.Tree.AutoReplyCommand.Execute(vm.MailPage.Accounts[0]);
                await Task.Delay(100);
                for (var i = 0; i < 50 && (vm.Overlay as AutoReplyViewModel)?.IsBusy != false; i++)
                {
                    await Task.Delay(100);
                }

                return (AutoReplyViewModel)vm.Overlay!;
            }

            var series = await OpenAutoReplyAsync();
            series.IsEnabled = true;
            series.IsSeries = true;
            series.Weekdays.Single(d => d.Day == DayOfWeek.Friday).IsChecked = true;
            series.Message = "Freitags bin ich nicht im Büro.";
            series.TemplateName = "Freitags";
            await series.SaveTemplateCommand.ExecuteAsync(null);
            await series.SaveCommand.ExecuteAsync(null);
            var back = await OpenAutoReplyAsync();
            Console.WriteLine($"Series dialog: series={back.IsSeries} period shown={back.ShowsPeriod} template={back.SelectedTemplate?.Name}");
            back.SelectedTemplate = back.Templates.First(t => t.Name == "Ferien");
            await Task.Delay(200);
            Console.WriteLine($"Dialog back to Ferien: series={back.IsSeries} period shown={back.ShowsPeriod} can schedule={back.CanSchedule}");
            await SaveAsync(window, output, "auto-reply-series-back.png");
            vm.Overlay = null;
            var row = vm.AutoReplies.Accounts[0];
            Console.WriteLine($"List with series: series={row.IsSeries} period shown={row.ShowsPeriod}");
            row.SelectedTemplate = row.Templates.First(t => t.Name == "Ferien");
            await Task.Delay(500);
            Console.WriteLine($"List back to Ferien: series={row.IsSeries} period shown={row.ShowsPeriod} error={row.Error}");
            row.IsScheduled = true;
            await Task.Delay(500);
            Console.WriteLine($"List with a period: '{row.Status}', series={row.IsSeries} error={row.Error}");
        }

        // A freshly added account: in the tree at once, "Ordner werden geladen …" until its first sync is done.
        var newAccount = vm.MailPage.Accounts[0].Connection.Id;
        vm.MailPage.Tree.MarkSettingUp(newAccount, true);
        await Task.Delay(200);
        await SaveAsync(window, output, "mail-account-setting-up.png");
        vm.MailPage.Tree.MarkSettingUp(newAccount, false);
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
                QuickActions = [.. vm.MailPage.QuickActionButtons],
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

        // "Aufs Handy übertragen": QR code with the PIN and the instructions beside it.
        var transferDialog = new AccountTransferDialogViewModel(services.GetRequiredService<Neruna.Core.Cloud.AccountTransferService>(), demoAccount);
        transferDialog.ShowCode(new Neruna.Core.Cloud.AccountTransferCode(new string('A', 900), "482913"));
        vm.Overlay = transferDialog;
        await SaveAsync(window, output, "account-transfer.png");
        vm.Overlay = null;

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

        // A PDF for a recipient who gets PDFs only with a password: the bar, and the lock on the attachment.
        var pdfSecurity = services.GetRequiredService<Neruna.Core.Mail.PdfProtectionService>();
        await pdfSecurity.SaveRuleAsync(new Neruna.Core.Mail.PdfProtectionRule(Guid.NewGuid(), "@bernasconi.example", false), "Rigi-2026-Pilatus");
        await pdfSecurity.SaveRuleAsync(new Neruna.Core.Mail.PdfProtectionRule(Guid.NewGuid(), "treuhand@muster-treuhand.example", true), "Kontrolle-Q4");
        var pdfFile = Path.Combine(Path.GetTempPath(), "Offerte Netzwerk Q4.pdf");
        using (var blank = new PdfSharp.Pdf.PdfDocument())
        {
            blank.AddPage();
            using var page = new MemoryStream();
            blank.Save(page);
            await File.WriteAllBytesAsync(pdfFile, Neruna.Core.Mail.PdfPages.Stamp(page.ToArray(), new Neruna.Core.Mail.PdfStampMark(0, 0.5, 0.2, "Offerte", ["Q4 2026"], "#0F6CBD")));
        }

        await vm.MailPage.Compose.AttachFilesAsync([pdfFile]);
        await Task.Delay(500);
        await SaveAsync(window, output, "compose-pdf-protect.png");
        vm.MailPage.Compose.ProtectWithOtherPasswordCommand.Execute(null);
        await Task.Delay(200);
        await SaveAsync(window, output, "compose-pdf-password.png");
        await vm.MailPage.Compose.ConfirmPdfPasswordCommand.ExecuteAsync(null);
        await Task.Delay(500);
        await SaveAsync(window, output, "compose-pdf-protected.png");
        vm.MailPage.Compose.RemoveAttachmentCommand.Execute(vm.MailPage.Compose.Attachments[0]);
        File.Delete(pdfFile);

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

        // A 4K bitmap as logo: recognised as too large, shrunk to a PNG of 300 pixels.
        var bmp = FourKBitmap();
        var bmpInfo = Neruna.Desktop.Editor.InsertedImages.Inspect(bmp)!;
        var shrunk = Neruna.Desktop.Editor.InsertedImages.ShrinkToPng(bmp, Neruna.Desktop.Editor.InsertedImages.SuggestedWidth);
        var shrunkInfo = Neruna.Desktop.Editor.InsertedImages.Inspect(shrunk)!;
        Console.WriteLine($"4K BMP: {bmpInfo.Width}x{bmpInfo.Height} {bmpInfo.MimeType} {bmpInfo.Bytes / 1024} KB large={bmpInfo.IsLarge} keep={bmpInfo.CanKeep} → {shrunkInfo.Width}x{shrunkInfo.Height} {shrunkInfo.MimeType} {shrunkInfo.Bytes} bytes");

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
        Console.WriteLine($"Advanced search open after a click on the button: {vm.MailPage.Search.IsAdvancedSearchOpen}");
        vm.MailPage.Search.SearchBody = string.Empty;
        vm.MailPage.Search.SearchSubject = "Offerte";
        vm.MailPage.Search.SearchScopeChoice = vm.MailPage.Search.SearchScopes.First(s => s.IsAccount);
        await vm.MailPage.Search.RunSearchCommand.ExecuteAsync(null);
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().FirstOrDefault();
        await Task.Delay(400);
        await SaveAsync(window, output, "search.png");
        Console.WriteLine($"Demo search: {vm.MailPage.Search.SearchInfo}");
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

        // A mail's picture enlarged in its own window (Windows: "Bild vergrössern" in the reading pane's menu).
        using (var logo = new MemoryStream(FourKBitmap()))
        {
            var viewer = new ImageViewerWindow(new Avalonia.Media.Imaging.Bitmap(logo)) { Width = 900, Height = 600 };
            viewer.Show();
            await SaveAsync(viewer, output, "image-viewer.png");
            viewer.Close();
        }

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
        var categorized = vm.CalendarPage.Days.SelectMany(d => d.Events).First(e => e.CategoryBrushes.Count > 2);
        Console.WriteLine($"Category tooltip: {categorized.Tooltip.Replace('\n', '|')}");

        // A calendar moved within its account under "Kalender verwalten"; then the right-click menu on an event.
        var firstGroup = vm.CalendarPage.CalendarGroups.First(g => g.Items.Count > 1);
        var before = string.Join(", ", firstGroup.Items.Select(i => i.Name));
        vm.CalendarPage.ManageCommand.Execute(null);
        var calendarSelection = (CalendarSelectionViewModel)vm.Overlay!;
        for (var i = 0; i < 50 && (calendarSelection.IsBusy || calendarSelection.Sources.Count == 0); i++)
        {
            await Task.Delay(100);
        }

        var moved = calendarSelection.Sources.First(g => g.Calendars.Count(c => c.IsSelected) > 1);
        // The last calendar to the top: in "Meine Kalender" it comes first.
        var last = moved.Calendars.Last();
        while (last.CanMoveUp)
        {
            last.MoveUpCommand.Execute(null);
        }

        if (moved.Calendars.FirstOrDefault(c => c.CanBeViewOnly && c != last) is { } viewOnly)
        {
            viewOnly.IsViewOnly = true;
        }

        await Task.Delay(300);
        await SaveAsync(window, output, "calendar-manage.png");
        await calendarSelection.ApplyCommand.ExecuteAsync(null);
        await vm.CalendarPage.ReloadAsync();
        Console.WriteLine($"Calendar order: {before} -> {string.Join(", ", vm.CalendarPage.CalendarGroups.First(g => g.AccountId == firstGroup.AccountId).Items.Select(i => i.Name))}");
        var eventButton = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is TimedEventItem && b.IsEffectivelyVisible);
        if (eventButton is not null)
        {
            eventButton.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
            await Task.Delay(500);
            Dispatcher.UIThread.RunJobs();
            await SaveAsync(window, output, "calendar-event-menu.png");
            var shown = TopLevel.GetTopLevel(eventButton)!.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault()
                        ?? Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(eventButton)?.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault();
            var occurrence = ((TimedEventItem)eventButton.DataContext!).Item.Occurrence;
            Console.WriteLine($"Event menu for '{occurrence.Summary}': {await vm.CalendarPage.EventMenuAsync(occurrence)}, open={shown is not null}: {string.Join(" | ", (shown?.Items.OfType<MenuItem>() ?? []).Select(m => $"{m.Header}{(m.IsEnabled ? "" : " (off)")}"))}");
        }

        vm.CalendarPage.IsPlanning = true;
        vm.CalendarPage.PlanningPeopleText = "Lea Keller <lea.keller@example.com>, Marco Bernasconi <marco@bernasconi.example.com>, extern@other.org";
        await Task.Delay(1500);
        await SaveAsync(window, output, "calendar-planning.png");
        vm.CalendarPage.PlanningPeopleText = string.Empty;
        vm.CalendarPage.IsPlanning = false;

        // "Termine suchen": the hits instead of the week, coming ones first.
        vm.CalendarPage.SearchText = "e";
        for (var i = 0; i < 50 && vm.CalendarPage.SearchStatus is null or "Suche …"; i++)
        {
            await Task.Delay(100);
        }

        await SaveAsync(window, output, "calendar-search.png");
        Console.WriteLine($"Calendar search: {vm.CalendarPage.SearchStatus}");
        vm.CalendarPage.SearchText = string.Empty;

        // "Drucken": the print dialog with the week, the month and the list as pages.
        vm.CalendarPage.PrintCommand.Execute(null);
        if (vm.Overlay is CalendarPrintViewModel print)
        {
            await print.RefreshAsync();
            await SaveAsync(window, output, "calendar-print.png");
            print.Layout = CalendarPrintViewModel.Layouts[2];
            await print.RefreshAsync();
            await SaveAsync(window, output, "calendar-print-month.png");
            print.Layout = CalendarPrintViewModel.Layouts[3];
            await print.RefreshAsync();
            await SaveAsync(window, output, "calendar-print-list.png");
            print.CancelCommand.Execute(null);
        }

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
        // Categories chosen in the editor, the list open.
        var categoryEditor = new EventEditorViewModel(calendarController, services.GetRequiredService<Neruna.Core.Calendar.InvitationService>(), [personal],
            meeting with { Categories = ["Besprechung", "Projekt"] }, (personal, "personal-2@demo.neruna"), _ => "anna.muster@example.com")
        {
            KnownCategories = await services.GetRequiredService<Neruna.Core.Calendar.CalendarCategoryService>().GetAllAsync(),
        };
        vm.Overlay = categoryEditor;
        await SaveAsync(window, output, "event-categories.png");
        if (window.GetVisualDescendants().OfType<DropDownButton>().FirstOrDefault(b => b.IsEffectivelyVisible && b.Flyout is Flyout) is { } categoryButton)
        {
            categoryButton.Flyout!.ShowAt(categoryButton);
            await SaveAsync(window, output, "event-categories-open.png");
            categoryButton.Flyout.Hide();
        }

        vm.Overlay = null;

        // Online meeting: a Jitsi server set up (Einstellungen → Besprechungen), the meeting added in the editor.
        var meetings = services.GetRequiredService<Neruna.Core.Meetings.MeetingController>();
        await services.GetRequiredService<Neruna.Core.ISettingsStore>().SetAsync(Neruna.Core.Meetings.MeetingController.ServicesKey, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new Neruna.Core.Meetings.MeetingService(Guid.NewGuid(), Neruna.Core.Meetings.MeetingKinds.Jitsi, "Jitsi Meet (meet.example.org)",
                new Dictionary<string, string> { [Neruna.Core.Meetings.JitsiMeetingProvider.ServerKey] = "https://meet.example.org" }),
        }));
        var online = new EventEditorViewModel(calendarController, services.GetRequiredService<Neruna.Core.Calendar.InvitationService>(), [personal], meeting with { Location = null },
            null, _ => "anna.muster@example.com") { Meetings = meetings };
        vm.Overlay = online;
        await Task.Delay(300);
        await SaveAsync(window, output, "event-meeting-choice.png");
        await online.AddMeetingCommand.ExecuteAsync(null);
        await SaveAsync(window, output, "event-meeting.png");
        vm.Overlay = null;

        // The same with a connected Zoom account (also for the Zoom Marketplace listing). The meeting itself would
        // need Zoom's API: it is put in as Zoom would return it.
        var zoom = new Neruna.Core.Meetings.MeetingService(Guid.NewGuid(), Neruna.Core.Meetings.MeetingKinds.Zoom, "Zoom (anna.muster@example.com)",
            new Dictionary<string, string> { [Neruna.Providers.Zoom.ZoomMeetingProvider.UserKey] = "anna.muster@example.com" });
        await services.GetRequiredService<Neruna.Core.ISettingsStore>().SetAsync(Neruna.Core.Meetings.MeetingController.ServicesKey, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            zoom,
            new Neruna.Core.Meetings.MeetingService(Guid.NewGuid(), Neruna.Core.Meetings.MeetingKinds.Jitsi, "Jitsi Meet (meet.example.org)",
                new Dictionary<string, string> { [Neruna.Core.Meetings.JitsiMeetingProvider.ServerKey] = "https://meet.example.org" }),
        }));
        var zoomEvent = new EventEditorViewModel(calendarController, services.GetRequiredService<Neruna.Core.Calendar.InvitationService>(), [personal], meeting with { Location = null },
            null, _ => "anna.muster@example.com") { Meetings = meetings };
        vm.Overlay = zoomEvent;
        await Task.Delay(300);
        await SaveAsync(window, output, "event-zoom-choice.png");
        var zoomMeeting = new Neruna.Core.Meetings.OnlineMeeting(Neruna.Core.Meetings.MeetingKinds.Zoom, "https://us06web.zoom.us/j/81234567890?pwd=Qk9tU1hYcFpLb3dNZ0x0dz09",
            Neruna.Core.Localization.Texts.T("Besprechungs-ID:") + " 812 3456 7890\n" + Neruna.Core.Localization.Texts.T("Kenncode:") + " 482913");
        zoomEvent.Description = zoomMeeting.AddTo(zoomEvent.Description);
        zoomEvent.Location = Neruna.Core.Localization.Texts.F("{0}-Besprechung", zoomMeeting.Name);
        zoomEvent.Meeting = zoomMeeting;
        await SaveAsync(window, output, "event-zoom.png");
        vm.Overlay = null;

        // Status bar → status window: per account and service how the last run went.
        await vm.SyncCommand.ExecuteAsync(null);
        vm.OpenSyncStatusCommand.Execute(null);
        await SaveAsync(window, output, "sync-status.png");
        vm.Overlay = null;

        // "Einstellungen geändert" → what changed since the last backup.
        vm.Overlay = new BackupChangesViewModel(
        [
            new Neruna.Core.Cloud.BackupChange("Einstellungen", "calendar.visibleTo", Neruna.Core.Cloud.BackupChangeKind.Changed),
            new Neruna.Core.Cloud.BackupChange("Einstellungen", "calendar.color.a2ba0035b58c4d93a66b58ab22268061.https://mail.example.com/SOGo/dav/anna/Calendar/personal/", Neruna.Core.Cloud.BackupChangeKind.Added),
            new Neruna.Core.Cloud.BackupChange("Einstellungen", "mail.offline.1cde456b642840d0bb57181f34bd3f64", Neruna.Core.Cloud.BackupChangeKind.Added),
            new Neruna.Core.Cloud.BackupChange("Einstellungen", "mail.offline.673973c04b4c4b32a2837adeff3430a0", Neruna.Core.Cloud.BackupChangeKind.Added),
            new Neruna.Core.Cloud.BackupChange("Einstellungen", "privacy.blockTrackers", Neruna.Core.Cloud.BackupChangeKind.Added),
            new Neruna.Core.Cloud.BackupChange("Einstellungen", "mail.remoteContentAllowed", Neruna.Core.Cloud.BackupChangeKind.Changed),
            new Neruna.Core.Cloud.BackupChange("Einstellungen", "mail.quickActions", Neruna.Core.Cloud.BackupChangeKind.Changed),
            new Neruna.Core.Cloud.BackupChange("Signaturen", "Privat", Neruna.Core.Cloud.BackupChangeKind.Added),
            new Neruna.Core.Cloud.BackupChange("Konten", "anna.muster@example.com", Neruna.Core.Cloud.BackupChangeKind.Changed),
        ], vm.BackupNowCommand, vm.BackupLaterCommand);
        await SaveAsync(window, output, "backup-changes.png");
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
        var menuItem = vm.CalendarPage.Calendars.FirstOrDefault(c => c.CanRemove) ?? vm.CalendarPage.Calendars.First(c => c.Info.RemoteId == "personal");
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

        // A contact with an S/MIME certificate (made here: name and address in it, valid for two years).
        var person = allContacts.First(c => !c.Card.IsGroup && c.Card.EmailAddresses.Count > 0);
        var personData = (await contactController.GetContactAsync(person.AddressBook, person.RemoteId))!.VCardData;
        using (var key = System.Security.Cryptography.RSA.Create(2048))
        {
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={person.Card.DisplayName}, E={person.Card.EmailAddresses[0]}", key,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            names.AddEmailAddress(person.Card.EmailAddresses[0]);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature | System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment, false));
            using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now.AddYears(2));
            var withCertificate = Neruna.Core.Contacts.ContactCertificates.Add(personData, made.RawData);
            vm.Overlay = new ContactEditorViewModel(contactController, services.GetRequiredService<Neruna.Desktop.Infrastructure.IFileService>(), writable,
                Neruna.Core.Contacts.ContactDraft.FromVCard(withCertificate) with { Certificates = Neruna.Core.Contacts.ContactCertificates.Read(withCertificate) },
                (person.AddressBook with { IsReadOnly = false }, person.RemoteId));
            await SaveAsync(window, output, "contacts-editor-certificate.png");
            vm.Overlay = null;
        }

        vm.ContactsPage.ManageCommand.Execute(null);
        var books = (AddressBookSelectionViewModel)vm.Overlay!;
        for (var i = 0; i < 50 && books.IsBusy; i++)
        {
            await Task.Delay(100);
        }

        await SaveAsync(window, output, "contacts-addressbooks.png");
        Console.WriteLine("Address books offered: " + string.Join(", ", books.Sources.SelectMany(g => g.AddressBooks).Select(b => b.Name + (b.IsSelected ? " [x]" : " [ ]"))));
        // The order in the list: one address book moved down, then applied.
        var booksBefore = string.Join(", ", vm.ContactsPage.AddressBooks.Select(b => b.Name));
        if (books.Sources.FirstOrDefault(g => g.AddressBooks.Count > 1) is { } bookGroup)
        {
            bookGroup.AddressBooks[0].MoveDownCommand.Execute(null);
            await books.ApplyCommand.ExecuteAsync(null);
            await vm.ContactsPage.ReloadAsync();
        }

        vm.Overlay = null;
        await Task.Delay(300);
        await SaveAsync(window, output, "contacts-manage-button.png");
        Console.WriteLine($"Address book order: {booksBefore} -> {string.Join(", ", vm.ContactsPage.AddressBooks.Select(b => b.Name))}");

        vm.NavigateCommand.Execute(Section.Mail);
        vm.NavigateCommand.Execute(Section.Settings);
        await vm.SettingsPage.ReloadAsync();
        vm.SettingsPage.SelectedTab = 1;
        await SaveAsync(window, output, "settings-mail.png");
        vm.SettingsPage.SelectedTab = 2;
        await SaveAsync(window, output, "settings-calendar.png");
        if (window.GetVisualDescendants().OfType<CalendarCategoriesView>().FirstOrDefault(v => v.IsEffectivelyVisible) is { } categoriesView)
        {
            categoriesView.BringIntoView();
            await SaveAsync(window, output, "settings-calendar-categories.png");
        }
        // Workspaces: "Business" without one folder, the rail in its colour, the menu, the tabs and the settings page.
        var workspaceService = services.GetRequiredService<Neruna.Core.Workspaces.WorkspaceService>();
        var workspaceAccount = (await services.GetRequiredService<Neruna.Core.IAccountStore>().GetAccountsAsync())[0];
        var workspaceFolders = await services.GetRequiredService<Neruna.Core.Mail.MailController>().GetFoldersAsync(workspaceAccount.ConnectionsOf(Neruna.Core.Accounts.ServiceKind.Mail).First().Id);
        var hiddenFolder = workspaceFolders.First(f => f.Role == Neruna.Core.Mail.FolderRole.None);
        var business = new Neruna.Core.Workspaces.Workspace(Guid.NewGuid(), "Business", "#0B6A0B", [workspaceAccount.Id],
            [Neruna.Core.Workspaces.Workspace.FolderKey(workspaceAccount.Id, hiddenFolder.RemoteId)], [], [], HideChat: true);
        await workspaceService.SaveAsync(business);
        await workspaceService.SaveAsync(new Neruna.Core.Workspaces.Workspace(Guid.NewGuid(), "Privat", "#C239B3", [], [], [], []));
        await workspaceService.SwitchAsync(business.Id);
        await Task.Delay(800);
        vm.NavigateCommand.Execute(Section.Mail);
        await SaveAsync(window, output, "workspace-business.png");
        await vm.SettingsPage.Workspaces.ReloadAsync();
        await vm.SettingsPage.Workspaces.EditCommand.ExecuteAsync(null);
        vm.NavigateCommand.Execute(Section.Settings);
        vm.SettingsPage.SelectedTab = 15;
        await SaveAsync(window, output, "settings-workspaces.png");
        vm.SettingsPage.Workspaces.ShowAsTabs = true;
        vm.NavigateCommand.Execute(Section.Mail);
        // Another profile no window has open: offered behind the workspaces.
        vm.ClosedProfiles.Add(new ProfileChoice(new Neruna.Storage.Profile(Guid.NewGuid(), "Verein", "verein")));
        vm.HasClosedProfiles = true;
        await SaveAsync(window, output, "workspace-tabs.png");
        vm.ClosedProfiles.Clear();
        vm.HasClosedProfiles = false;
        vm.SettingsPage.Workspaces.ShowAsTabs = false;
        await workspaceService.SwitchAsync(Guid.Empty);
        await Task.Delay(500);
        vm.NavigateCommand.Execute(Section.Settings);

        // News: the devblog, from the website's own feed next door (no request to neruna.org).
        var feedFile = Path.Combine(AppContext.BaseDirectory, "../../../../../../website/de/blog/feed.xml");
        if (File.Exists(feedFile))
        {
            vm.SettingsPage.News.Show(Neruna.Core.News.DevblogFeed.Parse(await File.ReadAllTextAsync(feedFile)));
            vm.SettingsPage.SelectedTab = SettingsViewModel.NewsTab;
            await SaveAsync(window, output, "settings-news.png");
        }

        // Einstellungen → PDF: stamps and the recipients with a password.
        await vm.SettingsPage.Pdf.LoadAsync();
        vm.SettingsPage.SelectedTab = SettingsViewModel.PdfTab;
        await SaveAsync(window, output, "settings-pdf.png");

        // Einstellungen → Besprechungen.
        await vm.SettingsPage.Meetings.LoadAsync();
        vm.SettingsPage.SelectedTab = SettingsViewModel.MeetingsTab;
        await SaveAsync(window, output, "settings-meetings.png");

        // Einstellungen → E-Mail → Schreiben.
        await vm.SettingsPage.MailOptions.ReloadAsync();
        vm.SettingsPage.SelectedTab = 17;
        await SaveAsync(window, output, "settings-compose.png");

        // Datenschutz with a few trusted senders, and with many (the list scrolls in its own box).
        var privacy = services.GetRequiredService<Neruna.Desktop.Infrastructure.PrivacyService>();
        var privacyOptions = await privacy.GetAsync();
        await privacy.SetAsync(privacyOptions with { TrustedSenders = ["@bernasconi.example", "newsletter@sbb.example", "lea.keller@example.com"] });
        // A few weeks of protection, for the statistics.
        var statistics = services.GetRequiredService<Neruna.Core.Privacy.PrivacyStatistics>();
        var forged = new MimeKit.MimeMessage { Subject = "Ihr Konto", Body = new MimeKit.TextPart("plain") { Text = "Bitte bestätigen" } };
        forged.From.Add(new MimeKit.MailboxAddress("Raiffeisen", "raiffeisen4@ferienwohnung.example"));
        var dangerous = Neruna.Core.Privacy.MessageAnalyzer.Analyze(forged);
        var normal = new MimeKit.MimeMessage { Subject = "Protokoll", Date = DateTimeOffset.Now, Body = new MimeKit.TextPart("plain") { Text = "Hallo Anna" } };
        normal.From.Add(new MimeKit.MailboxAddress("Lea Keller", "lea@example.com"));
        normal.To.Add(new MimeKit.MailboxAddress("Anna Muster", "anna.muster@example.com"));
        var harmless = Neruna.Core.Privacy.MessageAnalyzer.Analyze(normal);
        for (var i = 0; i < 214; i++)
        {
            await statistics.RecordMailAsync($"<{i}@snapshot>", i % 40 == 0 ? dangerous : harmless, trackers: i % 3, imagesBlocked: i % 4 == 0);
        }

        await statistics.RecordLinkAsync(warned: true, cleaned: true);
        await vm.SettingsPage.Privacy.ReloadAsync();
        vm.SettingsPage.SelectedTab = 14;
        await SaveAsync(window, output, "settings-privacy.png");
        // Further down: the phishing protection with the list of known phishing sites.
        if (window.GetVisualDescendants().OfType<PrivacyOptionsView>().FirstOrDefault(v => v.IsEffectivelyVisible)?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } privacyScroll)
        {
            privacyScroll.Offset = new Vector(0, 100000);
            await Task.Delay(200);
            await SaveAsync(window, output, "settings-privacy-phishing.png");
            privacyScroll.Offset = default;
        }

        await privacy.SetAsync(privacyOptions with { TrustedSenders = [.. Enumerable.Range(1, 100).Select(i => $"absender{i:000}@example.com")] });
        await vm.SettingsPage.Privacy.ReloadAsync();
        await Task.Delay(200);
        if (window.GetVisualDescendants().OfType<PrivacyOptionsView>().FirstOrDefault(v => v.IsEffectivelyVisible)?.FindDescendantOfType<ScrollViewer>() is { } scroller)
        {
            scroller.Offset = new Vector(0, 260);
        }

        await SaveAsync(window, output, "settings-privacy-many.png");
        await privacy.SetAsync(privacyOptions);
        await vm.SettingsPage.Privacy.ReloadAsync();

        // Konto bearbeiten with a label and an alias (sender address of the same mailbox).
        var editedAccount = (await services.GetRequiredService<Neruna.Core.IAccountStore>().GetAccountsAsync())[0];
        vm.SettingsPage.SelectedTab = 0;
        await vm.SettingsPage.Accounts.ReloadAsync();
        await SaveAsync(window, output, "settings-accounts.png");
        // A color chosen for the account the way a user does it: the round swatch opens the picker, a click on a color.
        var swatch = window.GetVisualDescendants().OfType<Button>().First(b => b.Flyout is Flyout && b.DataContext == vm.SettingsPage.Accounts.Items[0]);
        swatch.Flyout!.ShowAt(swatch);
        await Task.Delay(300);
        var colorButton = TopLevel.GetTopLevel(window)!.GetVisualDescendants().OfType<Button>()
            .Concat(Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants().OfType<Button>() ?? [])
            .Concat(swatch.Flyout is Flyout { } fly && fly.Content is Control content ? content.GetVisualDescendants().OfType<Button>() : [])
            .FirstOrDefault(b => b.DataContext is Neruna.Desktop.ViewModels.AccountColorChoice { HasColor: true, Color: "#8CBD18" });
        colorButton?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(500);
        Console.WriteLine($"Account color by click: button found {colorButton is not null}, has color {vm.SettingsPage.Accounts.Items[0].HasColor}");
        await Task.Delay(300);
        await SaveAsync(window, output, "settings-accounts-color.png");
        Console.WriteLine($"Account color choices: {vm.SettingsPage.Accounts.Items[0].ColorChoices.Count}");
        vm.SettingsPage.Accounts.Items[0].ColorChoices[0].ChooseCommand.Execute(null);

        // Einstellungen → Serverregeln: accounts with their number of rules, the chosen one's rules beside them.
        await vm.SettingsPage.ShowServerRulesAsync(vm.SettingsPage.Accounts.Items[0].Account.Id);
        await Task.Delay(1500);
        await SaveAsync(window, output, "settings-server-rules.png");
        vm.SettingsPage.SelectedTab = 0;
        // "⋯" of the first account, with "Offline verfügbar" opened.
        if (window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Flyout is MenuFlyout && b.Content is TextBlock { Text: "⋯" }) is { Flyout: MenuFlyout accountMenu } moreButton)
        {
            accountMenu.ShowAt(moreButton);
            await Task.Delay(300);
            if (accountMenu.Items.OfType<MenuItem>().FirstOrDefault(m => m.ItemsSource is not null) is { } offline)
            {
                offline.Open();
                await Task.Delay(300);
            }

            await SaveAsync(window, output, "settings-accounts-menu.png");
            accountMenu.Hide();
        }

        vm.SettingsPage.SelectedTab = 9;
        await SaveAsync(window, output, "settings-about-diagnosis.png");
        vm.SettingsPage.SelectedTab = 10;
        await SaveAsync(window, output, "settings-file-backup.png");
        vm.SettingsPage.SelectedTab = 0;
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

        // Einstellungen → KI: not connected (sign-in with the subscription or an API key), then connected (demo AI).
        vm.SettingsPage.SelectedTab = 8;
        vm.SettingsPage.Ai.SelectedProvider = vm.SettingsPage.Ai.Providers.First(p => p.Id == "openai");
        await Task.Delay(200);
        await SaveAsync(window, output, "settings-ai.png");
        var aiController = services.GetRequiredService<Neruna.Core.Ai.AiController>();
        await aiController.SignInAsync("demo");
        await Task.Delay(300);
        await SaveAsync(window, output, "settings-ai-connected.png");

        // Mail: "Zusammenfassen" with the connected AI, above the message.
        vm.NavigateCommand.Execute(Section.Mail);
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First();
        await Task.Delay(400);
        // First the question (the whole mail goes to the AI), then "OK".
        var summarizing = vm.MailPage.SummarizeCommand.ExecuteAsync(null);
        await Task.Delay(300);
        await SaveAsync(window, output, "mail-ai-summary-confirm.png");
        vm.MailPage.ReadingPane?.ConfirmSummaryCommand.Execute(null);
        await summarizing;
        await Task.Delay(200);
        Console.WriteLine($"AI summary: {vm.MailPage.ReadingPane?.Summary?.Text.Length} characters, error={vm.MailPage.ReadingPane?.Summary?.Error}");
        await SaveAsync(window, output, "mail-ai-summary.png");
        // "Sicherheit prüfen": the check of the open mail.
        vm.MailPage.ReadingPane?.Summary = null;
        vm.MailPage.ReadingPane?.ToggleAnalysisCommand.Execute(null);
        await Task.Delay(200);
        await SaveAsync(window, output, "mail-security-check.png");
        // "Mit KI prüfen": first the warning what goes to the AI.
        if (vm.MailPage.ReadingPane is { } checkedPane)
        {
            checkedPane.AiProviderName = "OpenAI";
            checkedPane.AskAiCheckCommand.Execute(null);
            await Task.Delay(200);
            await SaveAsync(window, output, "mail-ai-check.png");
            checkedPane.CancelAiCheckCommand.Execute(null);
        }

        vm.MailPage.ReadingPane?.ToggleAnalysisCommand.Execute(null);
        // "⋮" next to the subject → every header of the mail.
        vm.MailPage.ReadingPane?.ToggleHeadersCommand.Execute(null);
        await Task.Delay(200);
        await SaveAsync(window, output, "mail-headers.png");
        vm.MailPage.ReadingPane?.ToggleHeadersCommand.Execute(null);
        // The third mail of a sender moved by hand into the same folder: "Schnellaktion erstellen?"
        vm.Overlay = new QuickActionSuggestionViewModel(new Neruna.Core.Mail.QuickActionSuggestion(Guid.NewGuid(), "news@velo-shop.example", "Velo-Shop", "INBOX/Newsletter", "Newsletter"),
            services.GetRequiredService<Neruna.Core.Mail.QuickActionSuggestions>());
        await Task.Delay(200);
        await SaveAsync(window, output, "mail-quickaction-suggestion.png");
        vm.Overlay = null;
        // Antworten with AI: the bar above the text (instructions, "Schreiben", rewriting).
        await vm.MailPage.ReplyCommand.ExecuteAsync(null);
        await Task.Delay(1500);
        if (vm.MailPage.Compose?.Ai is { } composeAi)
        {
            composeAi.IsOpen = true;
            composeAi.Instruction = "zusagen, Umstellung am 7./8. November vorschlagen";
            await Task.Delay(300);
            await SaveAsync(window, output, "compose-ai.png");
            // Before the whole mail goes to the AI: "OK" or "Nicht senden".
            composeAi.ConfirmText = "Es ist kein Text markiert. Neruna sendet deshalb Ihren ganzen Text an OpenAI.";
            await Task.Delay(200);
            await SaveAsync(window, output, "compose-ai-confirm.png");
            composeAi.ConfirmText = null;
            // Writing into the text needs the real editor (a WebView), which the snapshot tool does not have; see tests/editor.
            await vm.MailPage.Compose.DiscardCommand.ExecuteAsync(null);
            await Task.Delay(300);
        }

        await aiController.DisconnectAsync();
        vm.NavigateCommand.Execute(Section.Settings);

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
            vm.SettingsPage.SelectedTab = 7; // Neruna Cloud (the AI shots before left Einstellungen → KI open)
            await Task.Delay(300);
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
        // Google (only in a build with the client secret): the sign-in step with the note on the test phase.
        ((AccountSetupViewModel)vm.Overlay!).Step = "google";
        await SaveAsync(window, output, "account-setup-google.png");
        vm.ShowAccountSetupCommand.Execute(null);
        ((AccountSetupViewModel)vm.Overlay!).ChooseImapCommand.Execute(null);
        await SaveAsync(window, output, "account-setup.png");
        vm.ShowAccountSetupCommand.Execute(null);
        var exchange = (AccountSetupViewModel)vm.Overlay!;
        await exchange.ChooseExchangeCommand.ExecuteAsync(null);
        exchange.DisplayName = "Anna Muster";
        exchange.Email = "anna.muster@muster-ag.ch";
        exchange.ExchangeServer = "mail.muster-ag.ch";
        exchange.Username = "MUSTER\\anna";
        exchange.ExchangeAuthentication = AccountSetupViewModel.AuthenticationChoices[1];
        await SaveAsync(window, output, "account-setup-exchange.png");
        vm.ShowAccountSetupCommand.Execute(null);
        var proton = (AccountSetupViewModel)vm.Overlay!;
        await proton.ChooseProtonCommand.ExecuteAsync(null);
        proton.DisplayName = "Anna Muster";
        proton.Email = "anna.muster@proton.me";
        await SaveAsync(window, output, "account-setup-proton.png");
        vm.ShowAccountSetupCommand.Execute(null);
        var dav = (AccountSetupViewModel)vm.Overlay!;
        dav.ChooseDavCommand.Execute(null);
        dav.DavServer = "dav.example.com";
        dav.Username = "anna";
        dav.Email = "anna.muster@example.com";
        await SaveAsync(window, output, "account-setup-dav.png");

        window.Close();
        Directory.Delete(dataDir, recursive: true);
    }

    private static string DataUri(string type, MimeKit.IMimeContent content)
    {
        using var stream = new MemoryStream();
        content.DecodeTo(stream);
        return "data:" + type + ";base64," + Convert.ToBase64String(stream.ToArray());
    }

    /// <summary>Renders .eml files through the real reading-pane pipeline (debugging display issues of single mails).</summary>
    public static async Task RenderMailsAsync(string output, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var message = await MimeKit.MimeMessage.LoadAsync(file);
            var content = Neruna.Core.Mail.MessageContent.From(message, forRenderer: true);
            var browser = Neruna.Core.Mail.MessageContent.From(message);
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
            // The page the reading pane gives the web view (open it in a browser to see the mail as on Windows/macOS).
            var page = Neruna.Desktop.Infrastructure.MailPaper.ForBrowser(browser.Html, darkTheme: false, allowRemote: false, id =>
                message.BodyParts.OfType<MimeKit.MimePart>().FirstOrDefault(p => p.ContentId == id) is { Content: { } part } picture
                    ? DataUri(picture.ContentType.MimeType, part)
                    : null).Html;
            await File.WriteAllTextAsync(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".browser.html"), page);
            await File.WriteAllTextAsync(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".shell.html"), Neruna.Desktop.Infrastructure.MailPaper.BrowserShell(false, false, 7));
            await File.WriteAllTextAsync(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".browser-dark.html"),
                Neruna.Desktop.Infrastructure.MailPaper.ForBrowser(browser.Html, darkTheme: true, allowRemote: false, _ => null).Html);
            // What the renderer got, next to the picture (to see why a mail looks wrong).
            await File.WriteAllTextAsync(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".html"), view.Text);
            // And as PDF ("Speichern unter … PDF", attachments as pages), to check the print layout.
            await File.WriteAllBytesAsync(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".pdf"), MailExport.ToPdf(message, attachments: true));
            var window = new Window { Width = 720, Height = 1000, Content = view, Background = Brushes.White };
            window.Show();
            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var path = Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".png");
            if (window.CaptureRenderedFrame() is { } shot)
            {
                ScreenshotArchive.Save(shot, output, Path.GetFileName(path));
            }

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
        if (splash.CaptureRenderedFrame() is { } shot)
        {
            ScreenshotArchive.Save(shot, output, "splash.png");
        }

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
        if (window.CaptureRenderedFrame() is { } shot)
        {
            ScreenshotArchive.Save(shot, output, "font-list.png");
        }

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
        ScreenshotArchive.Save(frame, output, name);
        Console.WriteLine(path);
    }

    // A 3840 × 2160 24-bit BMP with a colour gradient (Skia reads BMP but does not write it).
    private static byte[] FourKBitmap()
    {
        const int width = 3840, height = 2160, row = width * 3;
        var data = new byte[54 + row * height];
        void Int(int at, int value) => BitConverter.GetBytes(value).CopyTo(data, at);
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        Int(2, data.Length);
        Int(10, 54);
        Int(14, 40);
        Int(18, width);
        Int(22, height);
        data[26] = 1;
        data[28] = 24;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = 54 + y * row + x * 3;
                data[at] = (byte)(x * 255 / width);
                data[at + 1] = (byte)(y * 255 / height);
                data[at + 2] = 160;
            }
        }

        return data;
    }
}
