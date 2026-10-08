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
var live = Array.IndexOf(args, "--live") is var i and >= 0 ? (Host: args[i + 1], Dav: new Uri(args[i + 2])) : ((string Host, Uri Dav)?)null;
Directory.CreateDirectory(output);
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("de-CH");

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
        var dataDir = Path.Combine(Path.GetTempPath(), "neruna-snapshot-" + Guid.NewGuid().ToString("N"));
        await using var services = await AppServices.BuildAsync(new AppOptions(dataDir, Demo: true));
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 880 };
        window.Show();

        await vm.InitializeAsync();

        await CheckMarkAsReadAsync(vm, services.GetRequiredService<ISettingsStore>());
        await CheckLayoutAsync(vm, window, services.GetRequiredService<ISettingsStore>());

        // Mail: open the first message in the inbox.
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First();
        await Task.Delay(300);
        await SaveAsync(window, output, "mail.png");
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

        await vm.MailPage.NewMailCommand.ExecuteAsync(null);
        await SaveAsync(window, output, "compose-fallback.png");

        // How the toolbar looks with a real WebView (the editor area itself stays empty headless).
        vm.MailPage.Compose!.Formatting.IsRichText = true;
        await SaveAsync(window, output, "compose.png");
        vm.MailPage.Compose = null;

        // Reply to the opened message: HTML quote with reply header (shown as text in the headless fallback).
        await vm.MailPage.ReplyCommand.ExecuteAsync(null);
        await SaveAsync(window, output, "reply.png");
        vm.MailPage.Compose = null;

        // The font dropdown opens in a popup that headless capture does not include, so render its items on their own.
        await SaveFontListAsync(output);
        await SaveSplashAsync(output);

        vm.NavigateCommand.Execute(Section.Calendar);
        await SaveAsync(window, output, "calendar.png");
        vm.CalendarPage.NavigatorMonthCount = 2;
        await SaveAsync(window, output, "calendar-2-months.png");
        vm.CalendarPage.NavigatorMonthCount = 1;
        vm.CalendarPage.IsTimeGrid = true;
        await SaveAsync(window, output, "calendar-timegrid.png");
        vm.CalendarPage.IsWorkWeek = true;
        await Task.Delay(300);
        await SaveAsync(window, output, "calendar-workweek.png");
        vm.CalendarPage.IsWorkWeek = false;
        await Task.Delay(300);
        vm.CalendarPage.GridMinutes = 15;
        await SaveAsync(window, output, "calendar-timegrid-15.png");
        vm.CalendarPage.GridMinutes = 30;
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
        vm.SettingsPage.SelectedTab = 5;
        await SaveAsync(window, output, "settings-design.png");
        vm.SettingsPage.SelectedTab = 6;
        await SaveAsync(window, output, "settings-about.png");

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
            var view = new TheArtOfDev.HtmlRenderer.Avalonia.HtmlPanel { BaseStylesheet = MessageBodyView.BaseStylesheet, Text = content.Html, Background = Brushes.White };
            var window = new Window { Width = 720, Height = 520, Content = view, Background = Brushes.White };
            window.Show();
            await Task.Delay(100);
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
