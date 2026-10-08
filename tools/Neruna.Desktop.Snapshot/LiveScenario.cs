using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Utils;
using Neruna.Contracts.Discovery;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Security;
using Neruna.Core.Contacts;
using Neruna.Desktop;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

/// <summary>
/// End-to-end run against real servers (GreenMail for IMAP/SMTP, Radicale for CalDAV/CardDAV), see README "Testlabor".
/// Sets up the account through the normal setup path, syncs, opens the editors and renders each state.
/// </summary>
internal static class LiveScenario
{
    private static readonly string CertDir = Path.GetFullPath(Path.Combine("tools", "testlab", "certs"));

    public static async Task RunAsync(string output, string mailHost, Uri davUrl)
    {
        var user = "anna@example.com";
        await SeedDavAsync(davUrl, user);
        await EnsureFolderAsync(mailHost, "Projekte");
        await SeedMailAsync(mailHost);
        await SeedRichMailAsync(mailHost);

        var dataDir = Path.Combine(Path.GetTempPath(), "neruna-live-" + Guid.NewGuid().ToString("N"));
        await using var services = await AppServices.BuildAsync(new AppOptions(dataDir, Demo: false));

        var setup = services.GetRequiredService<AccountSetupService>();
        var config = new MailProviderConfig(
            "example.com",
            null,
            [new MailServerSettings(ServerProtocol.Imap, mailHost, 3143, SocketSecurity.None, AuthScheme.PasswordCleartext, user)],
            [new MailServerSettings(ServerProtocol.Smtp, mailHost, 3025, SocketSecurity.None, AuthScheme.PasswordCleartext, user)],
            [new DavServerSettings(ServerProtocol.CalDav, davUrl, user), new DavServerSettings(ServerProtocol.CardDav, davUrl, user)]);
        var account = setup.BuildAccount("Anna Muster", user, config);
        Console.WriteLine($"Connections: {string.Join(", ", account.Connections.Select(c => c.ProviderId))}");
        await setup.CreateAsync(account, "geheim");

        // A second mail account (Lea) to test moving messages between accounts.
        var leaConfig = new MailProviderConfig(
            "example.com",
            null,
            [new MailServerSettings(ServerProtocol.Imap, mailHost, 3143, SocketSecurity.None, AuthScheme.PasswordCleartext, "lea@example.com")],
            [new MailServerSettings(ServerProtocol.Smtp, mailHost, 3025, SocketSecurity.None, AuthScheme.PasswordCleartext, "lea@example.com")],
            []);
        await setup.CreateAsync(setup.BuildAccount("Lea Keller", "lea@example.com", leaConfig), "geheim");

        // Anna's S/MIME setup: own certificate, an expired old one, the lab CA and Lea's public certificate.
        var certificates = services.GetRequiredService<CertificatesViewModel>();
        await certificates.ImportDataAsync("anna.p12", await File.ReadAllBytesAsync(Path.Combine(CertDir, "anna.p12")), "geheim");
        await certificates.ImportDataAsync("expired.p12", await File.ReadAllBytesAsync(Path.Combine(CertDir, "expired.p12")), "geheim");
        await certificates.ImportDataAsync("testlab-ca.crt", await File.ReadAllBytesAsync(Path.Combine(CertDir, "testlab-ca.crt")), null);
        await certificates.ImportDataAsync("lea.crt", await File.ReadAllBytesAsync(Path.Combine(CertDir, "lea.crt")), null);

        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 880 };
        window.Show();
        await vm.InitializeAsync();
        Console.WriteLine("Status: " + vm.StatusText);

        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First(m => m.HasAttachments);
        await WaitAsync(() => vm.MailPage.ReadingPane?.Message is not null && vm.MailPage.SelectedMessage?.IsUnread == false);
        Console.WriteLine($"Opened message marked read: {vm.MailPage.SelectedMessage?.IsUnread == false}, inbox unread: {vm.MailPage.CurrentFolder?.UnreadCount}");
        await Snapshots.SaveAsync(window, output, "live-mail.png");

        await OpenAsync(vm, "Newsletter mit Logo");
        Console.WriteLine($"Newsletter: blocked remote={vm.MailPage.ReadingPane?.HasBlockedRemoteContent}, attachments={vm.MailPage.ReadingPane?.Attachments.Count}");
        await Snapshots.SaveAsync(window, output, "live-html.png");

        // Drag & drop: the newsletter from the list onto the folder "Projekte" (events as the UI raises them).
        {
            var dragged = vm.MailPage.SelectedMessage!;
            var target = window.GetVisualDescendants().OfType<TreeViewItem>().First(t => t.DataContext is MailFolderNode { Folder.Name: "Projekte" });
            var inbox = window.GetVisualDescendants().OfType<TreeViewItem>().First(t => t.DataContext is MailFolderNode { Folder.Role: Neruna.Core.Mail.FolderRole.Inbox });
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(MailView.MessageFormat, new[] { dragged }));
            DragEventArgs Raise(RoutedEvent<DragEventArgs> routed, TreeViewItem item)
            {
                var label = item.GetVisualDescendants().OfType<TextBlock>().First();
                var args = new DragEventArgs(routed, data, label, new Point(4, 4), KeyModifiers.None);
                label.RaiseEvent(args);
                return args;
            }

            var overInbox = Raise(DragDrop.DragOverEvent, inbox);
            Console.WriteLine($"Drag over own folder: effect={overInbox.DragEffects}");
            var over = Raise(DragDrop.DragOverEvent, target);
            Console.WriteLine($"Drag over 'Projekte': effect={over.DragEffects}, highlighted={target.Classes.Contains("drop-target")}");
            await Snapshots.SaveAsync(window, output, "live-drag.png");
            Raise(DragDrop.DropEvent, target);
            await WaitAsync(() => vm.MailPage.Entries.OfType<MessageItemViewModel>().All(m => m.Subject != dragged.Subject || m != dragged));
            await Task.Delay(500);
            using var imap = new MailKit.Net.Imap.ImapClient();
            await imap.ConnectAsync(mailHost, 3143, SecureSocketOptions.None);
            await imap.AuthenticateAsync("anna@example.com", "geheim");
            var projects = await imap.GetFolderAsync("Projekte");
            await projects.OpenAsync(MailKit.FolderAccess.ReadOnly);
            var onServer = await projects.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(dragged.Subject));
            Console.WriteLine($"Dropped: still in list={vm.MailPage.Entries.Contains(dragged)}, in 'Projekte' on server={onServer.Count > 0}, folder unread={((MailFolderNode)target.DataContext!).UnreadCount}, status={vm.StatusText}");
            await imap.DisconnectAsync(true);
        }

        // Multi-selection (as Ctrl-click does) and drag & drop into another account: Anna's two mails into Lea's inbox.
        {
            var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "MessageList");
            var picks = PickSubjects
                .Select(subject => vm.MailPage.Entries.OfType<MessageItemViewModel>().First(m => m.Subject == subject))
                .ToArray();
            list.SelectedItems!.Clear();
            foreach (var pick in picks)
            {
                list.SelectedItems.Add(pick);
            }

            Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"Selection: {vm.MailPage.SelectionText}, view model has {vm.MailPage.SelectedMessages.Count}");
            await Snapshots.SaveAsync(window, output, "live-multiselect.png");

            var leaInbox = window.GetVisualDescendants().OfType<TreeViewItem>()
                .First(t => t.DataContext is MailFolderNode { Folder.Role: Neruna.Core.Mail.FolderRole.Inbox } node && node.Account.Account.EmailAddress == "lea@example.com");
            var label = leaInbox.GetVisualDescendants().OfType<TextBlock>().First();
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(MailView.MessageFormat, picks));
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, label, new Point(4, 4), KeyModifiers.None);
            label.RaiseEvent(over);
            Console.WriteLine($"Drag 2 mails over Lea's inbox: effect={over.DragEffects}");
            label.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, label, new Point(4, 4), KeyModifiers.None));
            await WaitAsync(() => vm.StatusText?.Contains("verschoben", StringComparison.Ordinal) == true && !vm.StatusText.Contains('…', StringComparison.Ordinal));

            using var imap = new MailKit.Net.Imap.ImapClient();
            await imap.ConnectAsync(mailHost, 3143, SecureSocketOptions.None);
            await imap.AuthenticateAsync("lea@example.com", "geheim");
            await imap.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);
            var found = 0;
            foreach (var pick in picks)
            {
                found += (await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(pick.Subject))).Count > 0 ? 1 : 0;
            }

            Console.WriteLine($"Cross-account: {found}/2 in Lea's inbox on server, left in Anna's list: {picks.Count(vm.MailPage.Entries.Contains)}, status={vm.StatusText}");
            await imap.DisconnectAsync(true);
        }

        await OpenAsync(vm, "Vertrag zur Unterschrift");
        Console.WriteLine("Inline PDF attachments: " + string.Join(", ", vm.MailPage.ReadingPane!.Attachments.Select(a => a.FileName)));
        await Snapshots.SaveAsync(window, output, "live-inline-pdf.png");

        await OpenAsync(vm, "Vertraulich: Konditionen 2027");
        Console.WriteLine($"S/MIME: {vm.MailPage.ReadingPane?.SecurityLevel} – {vm.MailPage.ReadingPane?.SecurityText}");
        await Snapshots.SaveAsync(window, output, "live-smime.png");

        await OpenAsync(vm, "Signierte Rückmeldung");
        Console.WriteLine($"Signed only: {vm.MailPage.ReadingPane?.SecurityLevel} – {vm.MailPage.ReadingPane?.SecurityText}");

        await vm.MailPage.ReplyCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.MailPage.Compose?.CanSign == true);
        Console.WriteLine($"Compose: can sign={vm.MailPage.Compose?.CanSign}");
        await WaitAsync(() => vm.MailPage.Compose?.Encrypt == true);
        Console.WriteLine($"Automatic: sign={vm.MailPage.Compose!.Sign}, encrypt={vm.MailPage.Compose.Encrypt} ({vm.MailPage.Compose.EncryptHint})");
        var to = vm.MailPage.Compose.To;
        vm.MailPage.Compose.To = to + ", unbekannt@example.org";
        await WaitAsync(() => vm.MailPage.Compose?.Encrypt == false);
        Console.WriteLine($"Unknown recipient added: encrypt={vm.MailPage.Compose.Encrypt} ({vm.MailPage.Compose.EncryptHint})");
        vm.MailPage.Compose.To = to;
        await WaitAsync(() => vm.MailPage.Compose?.Encrypt == true);
        await Snapshots.SaveAsync(window, output, "live-compose-smime.png");

        // Send a signed + encrypted reply to Lea through the real SMTP server and read the copy in "Sent"... via Lea's view: just send.
        var compose = vm.MailPage.Compose!;
        compose.Sign = true;
        compose.Encrypt = true;
        await compose.SendCommand.ExecuteAsync(null);
        Console.WriteLine("Signed+encrypted reply sent: " + (compose.Error ?? "ok"));
        vm.MailPage.Compose = null;

        await vm.MailPage.ReplyAllCommand.ExecuteAsync(null);
        await Snapshots.SaveAsync(window, output, "live-reply.png");
        vm.MailPage.Compose = null;

        await CheckDraftsAsync(vm, window, output, mailHost, user);
        await CheckPushAsync(vm, window, output, mailHost, user, services.GetRequiredService<NotificationService>());
        await CheckInvitationAsync(vm, window, output, mailHost, user, services);
        await CheckSearchAsync(vm, window, output, mailHost, user);

        vm.NavigateCommand.Execute(Section.Calendar);
        await Snapshots.SaveAsync(window, output, "live-calendar.png");

        // "Kalender verwalten": choose once, then a calendar subscribed later on the server is offered as new.
        await RemoveServerCalendarAsync(davUrl, user, "abo-ferien/");
        await ChooseCalendarsAsync(vm, _ => true);
        await AddServerCalendarAsync(davUrl, user, "abo-ferien/", "Abo: Ferienplan Lea");
        vm.CalendarPage.ManageCommand.Execute(null);
        var selection = (CalendarSelectionViewModel)vm.Overlay!;
        await WaitAsync(() => !selection.IsBusy);
        Console.WriteLine("Calendar selection: " + string.Join(", ", selection.Sources.SelectMany(s => s.Calendars).Select(c => $"{c.Name}{(c.IsSelected ? " [x]" : " [ ]")}{(c.IsNew ? " neu" : "")}")));
        await Snapshots.SaveAsync(window, output, "live-calendar-selection.png");
        foreach (var item in selection.Sources.SelectMany(s => s.Calendars).Where(c => c.IsNew))
        {
            item.IsSelected = true;
        }

        await selection.ApplyCommand.ExecuteAsync(null);
        Console.WriteLine("Calendars after choosing: " + string.Join(", ", vm.CalendarPage.Calendars.Select(c => c.Info.Name)));

        var editorShown = new TaskCompletionSource();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Overlay) && vm.Overlay is EventEditorViewModel)
            {
                editorShown.TrySetResult();
            }
        };
        var firstEvent = vm.CalendarPage.Days.SelectMany(d => d.Events).First();
        await vm.CalendarPage.EditEventCommand.ExecuteAsync(firstEvent);
        await editorShown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Snapshots.SaveAsync(window, output, "live-event-editor.png");

        // Save a change through the UI's view model and verify it landed on the server.
        var editor = (EventEditorViewModel)vm.Overlay!;
        editor.Title += " (geändert)";
        await editor.SaveCommand.ExecuteAsync(null);
        Console.WriteLine("Event saved: " + (editor.Error ?? "ok"));

        vm.NavigateCommand.Execute(Section.Contacts);
        await Snapshots.SaveAsync(window, output, "live-contacts.png");
        await vm.ContactsPage.EditCommand.ExecuteAsync(null);
        await Snapshots.SaveAsync(window, output, "live-contact-editor.png");
        ((ContactEditorViewModel)vm.Overlay!).CancelCommand.Execute(null);

        // Contact picture: any image becomes a 256×256 JPEG in the vCard.
        var picturePath = Path.Combine(Path.GetTempPath(), "neruna-live-portrait.png");
        using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(640, 480)))
        {
            surface.Canvas.Clear(new SkiaSharp.SKColor(90, 140, 200));
            surface.Canvas.DrawCircle(320, 200, 110, new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(240, 205, 175) });
            using var png = surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(picturePath, png.ToArray());
        }

        var avatar = AvatarImage.FromFile(picturePath)!;
        var contacts = services.GetRequiredService<ContactController>();
        var leaEntry = (await contacts.SearchAsync("Lea")).First(e => !e.Card.IsGroup);
        var leaStored = await contacts.GetContactAsync(leaEntry.AddressBook, leaEntry.RemoteId);
        await contacts.SaveContactAsync(leaEntry.AddressBook,
            ContactDraft.FromVCard(leaStored!.VCardData) with { Photo = new ContactPhoto(avatar, "image/jpeg"), ReplacePhoto = true },
            (leaEntry.AddressBook, leaEntry.RemoteId));
        Console.WriteLine($"Contact photo saved: {avatar.Length} bytes JPEG, read back: {(await contacts.SearchAsync("Lea")).First(e => !e.Card.IsGroup).Card.Photo?.Data.Length} bytes");

        // Distribution list through the editor: two contacts and an external address.
        await vm.ContactsPage.ReloadAsync();
        vm.ContactsPage.NewGroupCommand.Execute(null);
        var groupEditor = (GroupEditorViewModel)vm.Overlay!;
        groupEditor.Name = "Live-Verteiler";
        groupEditor.SearchText = "Lea";
        groupEditor.AddContactCommand.Execute(groupEditor.Available.First());
        Console.WriteLine($"After adding Lea: same contact still offered={groupEditor.Available.Any(c => groupEditor.Members.Any(m => m.ContactUid == c.Entry.Card.MemberUid))}");
        groupEditor.SearchText = "Marco";
        groupEditor.AddContactCommand.Execute(groupEditor.Available.First());
        groupEditor.SearchText = string.Empty;
        groupEditor.ExternalAddress = "gast@example.org";
        groupEditor.AddExternalCommand.Execute(null);
        await Snapshots.SaveAsync(window, output, "live-group-editor.png");
        await groupEditor.SaveCommand.ExecuteAsync(null);
        Console.WriteLine("Group saved: " + (groupEditor.Error ?? "ok"));
        await vm.ContactsPage.ReloadAsync();
        vm.ContactsPage.Selected = vm.ContactsPage.Items.First(i => i.Name == "Live-Verteiler");
        await Task.Delay(300);
        await Snapshots.SaveAsync(window, output, "live-group.png");
        await vm.ContactsPage.WriteMailCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.MailPage.Compose is not null);
        Console.WriteLine("Mail to group: " + vm.MailPage.Compose?.To);
        vm.MailPage.Compose = null;
        var created = await contacts.SearchAsync("Live-Verteiler");
        foreach (var group in created)
        {
            await contacts.DeleteContactAsync(group.AddressBook, group.RemoteId);
        }

        vm.NavigateCommand.Execute(Section.Contacts);

        vm.NavigateCommand.Execute(Section.Settings);
        await Snapshots.SaveAsync(window, output, "live-accounts.png");
        await vm.SettingsPage.ReloadAsync();
        vm.SettingsPage.SelectedTab = 4;
        Console.WriteLine($"Certificates: own={vm.SettingsPage.Certificates.Own.Count}, contacts={vm.SettingsPage.Certificates.Contacts.Count} ({string.Join(", ", vm.SettingsPage.Certificates.Contacts.Select(c => c.Emails))}), CAs={vm.SettingsPage.Certificates.Authorities.Count}");
        await Snapshots.SaveAsync(window, output, "live-certificates.png");
        vm.SettingsPage.SelectedTab = 0;

        vm.ShowAccountSetupCommand.Execute(null);
        await Snapshots.SaveAsync(window, output, "live-account-setup.png");

        window.Close();
        Directory.Delete(dataDir, recursive: true);
    }

    private static async Task OpenAsync(MainWindowViewModel vm, string subject)
    {
        vm.NavigateCommand.Execute(Section.Mail);
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First(m => m.Subject == subject);
        await WaitAsync(() => vm.MailPage.ReadingPane?.Message is not null);
        await Task.Delay(300);
    }

    // Loading older mail (page size 5 instead of 500), quick search on the server (a word only in the text), and the
    // advanced search across all folders by sender – then flagging a hit that lies in another folder.
    private static async Task CheckSearchAsync(MainWindowViewModel vm, MainWindow window, string output, string mailHost, string user)
    {
        vm.NavigateCommand.Execute(Section.Mail);
        var inbox = vm.MailPage.Accounts.SelectMany(a => a.AllFolders())
            .First(f => f.Folder.Role == Neruna.Core.Mail.FolderRole.Inbox && f.Account.Account.EmailAddress == user);
        MailViewModel.PageSize = 5;
        vm.MailPage.SelectedTreeItem = vm.MailPage.Accounts.SelectMany(a => a.AllFolders()).First(f => f != inbox);
        await WaitAsync(() => vm.MailPage.CurrentFolder != inbox);
        vm.MailPage.SelectedTreeItem = inbox;
        await WaitAsync(() => vm.MailPage.CurrentFolder == inbox && vm.MailPage.LoadedText is not null);
        Console.WriteLine($"Paging: {vm.MailPage.LoadedText}, more={vm.MailPage.HasMore}");
        await vm.MailPage.LoadMoreCommand.ExecuteAsync(null);
        Console.WriteLine($"After 'Weitere laden': {vm.MailPage.LoadedText}, shown={vm.MailPage.Entries.OfType<MessageItemViewModel>().Count()}");
        MailViewModel.PageSize = 500;

        // A word that is only in the text of a message (not subject or preview of the loaded ones).
        var word = "Frage zum Vertrag";
        vm.MailPage.SearchText = word;
        Console.WriteLine($"Quick search (loaded only): {vm.MailPage.Entries.OfType<MessageItemViewModel>().Count()} hits, server hint={vm.MailPage.ShowServerSearchHint}");
        await vm.MailPage.SearchServerCommand.ExecuteAsync(null);
        Console.WriteLine($"Server search '{word}': {vm.MailPage.SearchInfo} | {string.Join(", ", vm.MailPage.Entries.OfType<MessageItemViewModel>().Select(m => m.Subject).Take(3))}");

        // Advanced: from Marco, all folders of Anna's account.
        vm.MailPage.ToggleAdvancedSearchCommand.Execute(null);
        vm.MailPage.SearchBody = string.Empty;
        vm.MailPage.SearchFrom = "marco@example.com";
        vm.MailPage.SearchScopeChoice = vm.MailPage.SearchScopes.First(s => s.IsAccount && s.Account.Account.EmailAddress == user);
        await vm.MailPage.RunSearchCommand.ExecuteAsync(null);
        var hits = vm.MailPage.Entries.OfType<MessageItemViewModel>().ToList();
        Console.WriteLine($"Advanced search: {vm.MailPage.ListTitle} – {vm.MailPage.SearchInfo}; folders: {string.Join(", ", hits.Select(h => h.FolderText).Distinct())}");
        await Snapshots.SaveAsync(window, output, "live-search.png");

        // An action on a hit from another folder than the open one goes to that folder.
        if (hits.FirstOrDefault(h => h.Folder != inbox) is { } elsewhere)
        {
            vm.MailPage.SelectedEntry = elsewhere;
            await WaitAsync(() => vm.MailPage.ReadingPane?.Message is not null);
            var wasFlagged = elsewhere.IsFlagged;
            await vm.MailPage.ToggleFlagCommand.ExecuteAsync(null);
            using var imap = new MailKit.Net.Imap.ImapClient();
            await imap.ConnectAsync(mailHost, 3143, SecureSocketOptions.None);
            await imap.AuthenticateAsync(user, "geheim");
            var folder = await imap.GetFolderAsync(elsewhere.Folder!.Folder.RemoteId);
            await folder.OpenAsync(MailKit.FolderAccess.ReadOnly);
            var summary = (await folder.FetchAsync(new List<MailKit.UniqueId> { new(uint.Parse(elsewhere.Summary.RemoteId, System.Globalization.CultureInfo.InvariantCulture)) }, new MailKit.FetchRequest(MailKit.MessageSummaryItems.Flags))).Single();
            Console.WriteLine($"Flag on hit in '{elsewhere.FolderText}': {wasFlagged} → {summary.Flags?.HasFlag(MailKit.MessageFlags.Flagged)} on the server");
            await imap.DisconnectAsync(true);
        }

        await vm.MailPage.CloseSearchCommand.ExecuteAsync(null);
        Console.WriteLine($"Search closed: back in {vm.MailPage.ListTitle}, {vm.MailPage.Entries.OfType<MessageItemViewModel>().Count()} messages");
    }

    // Invitations: Marco invites Anna by mail; the bar shows it, "Annehmen" puts it into Anna's calendar (Radicale)
    // and sends Marco the answer. Then the event editor with reminder and attendees, and the reminder window.
    private static async Task CheckInvitationAsync(MainWindowViewModel vm, MainWindow window, string output, string mailHost, string user, IServiceProvider services)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var start = DateTime.Today.AddDays(2).AddHours(14);
        var draft = Neruna.Core.Calendar.EventDraft.New(start) with
        {
            Summary = "Offertbesprechung " + tag,
            Location = "Sitzungszimmer Rigi",
            Attendees = [new Neruna.Core.Calendar.EventAttendee(user, "Anna Muster")],
            Organizer = "marco@example.com",
        };
        var data = Neruna.Core.Calendar.ITip.Request(draft.ToICalendar(null));
        var invite = Neruna.Core.Calendar.ITip.Mail(new MimeKit.MailboxAddress("Marco Bernasconi", "marco@example.com"), [new MimeKit.MailboxAddress("Anna", user)],
            "Einladung: Offertbesprechung " + tag, "Marco lädt Sie ein.", data, "REQUEST");
        using (var smtp = new MailKit.Net.Smtp.SmtpClient())
        {
            await smtp.ConnectAsync(mailHost, 3025, SecureSocketOptions.None);
            await smtp.SendAsync(invite);
            await smtp.DisconnectAsync(true);
        }

        var inbox = vm.MailPage.Accounts.SelectMany(a => a.AllFolders())
            .First(f => f.Folder.Role == Neruna.Core.Mail.FolderRole.Inbox && f.Account.Account.EmailAddress == user);
        vm.MailPage.SelectedTreeItem = inbox;
        await WaitAsync(() => vm.MailPage.CurrentFolder == inbox && vm.MailPage.Entries.OfType<MessageItemViewModel>().Any(m => m.Subject.EndsWith(tag, StringComparison.Ordinal)));
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First(m => m.Subject.EndsWith(tag, StringComparison.Ordinal));
        await WaitAsync(() => vm.MailPage.ReadingPane?.Invitation?.CanRespond == true);
        var banner = vm.MailPage.ReadingPane?.Invitation;
        Console.WriteLine($"Invitation bar: {banner?.Heading} | {banner?.When} | {banner?.Status} | conflicts: {banner?.Conflicts ?? "-"}");
        await Snapshots.SaveAsync(window, output, "live-invitation.png");

        Console.WriteLine($"Calendar choice: {banner?.SelectedCalendar?.Name} of [{string.Join(", ", banner?.Calendars.Select(c => c.Name) ?? [])}]");
        await services.GetRequiredService<Neruna.Core.ISettingsStore>().SetDefaultReminderAsync(30);
        await banner!.AcceptCommand.ExecuteAsync(null);
        Console.WriteLine($"Accepted: {banner.Status} {banner.Error} | status bar: {vm.StatusText}");
        var calendar = services.GetRequiredService<Neruna.Core.Calendar.CalendarController>();
        var stored = await calendar.FindByUidAsync(Neruna.Core.Calendar.EventDraft.UidOf(data)!);
        Console.WriteLine($"In Anna's calendar: {stored?.Calendar.Name} – {(stored?.Item.ICalendarData.Contains("PARTSTAT=ACCEPTED", StringComparison.Ordinal) == true ? "zugesagt" : "?")}, " +
                          $"reminder {Neruna.Core.Calendar.EventDraft.FromICalendar(stored!.Value.Item.ICalendarData).ReminderMinutes} min");

        using var imap = new MailKit.Net.Imap.ImapClient();
        await imap.ConnectAsync(mailHost, 3143, SecureSocketOptions.None);
        await imap.AuthenticateAsync("marco@example.com", "geheim");
        await imap.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);
        var replies = await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains("Zugesagt: Offertbesprechung " + tag));
        Console.WriteLine($"Answer in Marco's inbox: {replies.Count}");
        await imap.DisconnectAsync(true);

        // The event editor of the accepted meeting (attendee view) and of a new event (reminder, attendees).
        vm.NavigateCommand.Execute(Section.Calendar);
        await vm.CalendarPage.ReloadAsync();
        var occurrence = (await calendar.GetOccurrencesAsync([stored!.Value.Calendar], new DateTimeOffset(start.Date), new DateTimeOffset(start.Date.AddDays(1))))
            .First(o => o.Summary.EndsWith(tag, StringComparison.Ordinal));
        await vm.CalendarPage.OpenOccurrenceAsync(occurrence);
        await WaitAsync(() => vm.Overlay is EventEditorViewModel);
        var editor = (EventEditorViewModel)vm.Overlay!;
        Console.WriteLine($"Editor as attendee: organizer={editor.Organizer}, can edit attendees={editor.CanEditAttendees}, statuses={string.Join(", ", editor.AttendeeStatuses.Select(a => a.Text + " " + a.State))}");
        await Snapshots.SaveAsync(window, output, "live-event-attendee.png");
        vm.Overlay = null;

        // A due reminder: an event in 10 minutes with the default reminder (30 min) is due at once.
        var soon = DateTime.Now.AddMinutes(10);
        var newDraft = Neruna.Core.Calendar.EventDraft.New(new DateTime(soon.Year, soon.Month, soon.Day, soon.Hour, soon.Minute, 0), await services.GetRequiredService<Neruna.Core.ISettingsStore>().GetDefaultReminderAsync())
            with { Summary = "Telefon mit Marco " + tag, Location = "Büro" };
        await calendar.SaveEventAsync(stored.Value.Calendar, newDraft);
        var reminders = services.GetRequiredService<ReminderScheduler>();
        await reminders.CheckAsync();
        Console.WriteLine($"Reminder window open: {reminders.Window is not null}");
        if (reminders.Window is { DataContext: ReminderWindowViewModel model } reminderWindow)
        {
            Console.WriteLine("Reminders: " + string.Join(" | ", model.Items.Select(i => $"{i.Title} ({i.When}, {i.Due})")) + $"; snooze choices: {string.Join(", ", model.SnoozeOptions.Select(o => o.Label).Take(3))} …");
            await Snapshots.SaveAsync(reminderWindow, output, "live-reminders.png");
            model.Selected = model.Items.First(i => i.Title.EndsWith(tag, StringComparison.Ordinal));
            model.SnoozeChoice = model.SnoozeOptions.First(o => o.Label == "Bei Beginn");
            await model.SnoozeCommand.ExecuteAsync(null);
            await reminders.CheckAsync();
            Console.WriteLine($"After snoozing until start: this one gone={model.Items.All(i => !i.Title.EndsWith(tag, StringComparison.Ordinal))}, {model.Items.Count} older left");
            if (model.Items.Count > 0)
            {
                await model.DismissAllCommand.ExecuteAsync(null);
            }
        }
    }

    // Push: a mail sent from outside appears without any manual sync, a notification shows it, a reply being written
    // stays open, and clicking the notification opens the message.
    private static async Task CheckPushAsync(MainWindowViewModel vm, MainWindow window, string output, string mailHost, string user, NotificationService notifications)
    {
        var inbox = vm.MailPage.Accounts.SelectMany(a => a.AllFolders())
            .First(f => f.Folder.Role == Neruna.Core.Mail.FolderRole.Inbox && f.Account.Account.EmailAddress == user);
        vm.MailPage.SelectedTreeItem = inbox;
        await WaitAsync(() => vm.MailPage.CurrentFolder == inbox && vm.MailPage.Entries.OfType<MessageItemViewModel>().Any(m => m.Subject == "Vertrag zur Unterschrift"));
        await OpenAsync(vm, "Vertrag zur Unterschrift");
        await vm.MailPage.ReplyCommand.ExecuteAsync(null);
        var reply = vm.MailPage.Compose!;
        reply.Subject += " (in Arbeit)";

        var subject = "Push " + Guid.NewGuid().ToString("N")[..6];
        var message = new MimeKit.MimeMessage { Subject = subject, Body = new MimeKit.TextPart("plain") { Text = "Grüezi Anna, kurze Frage zum Vertrag." } };
        message.From.Add(new MimeKit.MailboxAddress("Marco Bernasconi", "marco@example.com"));
        message.To.Add(new MimeKit.MailboxAddress("Anna", user));
        var started = DateTime.Now;
        using (var smtp = new MailKit.Net.Smtp.SmtpClient())
        {
            await smtp.ConnectAsync(mailHost, 3025, SecureSocketOptions.None);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);
        }

        for (var i = 0; i < 150 && !vm.MailPage.Entries.OfType<MessageItemViewModel>().Any(m => m.Subject == subject); i++)
        {
            await Task.Delay(100);
        }

        var seconds = (DateTime.Now - started).TotalSeconds;
        await WaitAsync(() => notifications.Visible.Count > 0);
        Console.WriteLine($"Push: in list after {seconds:0.0}s={vm.MailPage.Entries.OfType<MessageItemViewModel>().Any(m => m.Subject == subject)}, " +
                          $"reply still open={vm.MailPage.Compose == reply} ({reply.Subject}), notifications={notifications.Visible.Count}");

        Console.WriteLine("All notifications: " + string.Join(" | ", notifications.Visible.Select(w => ((NotificationViewModel)w.DataContext!).Subject)));
        if (notifications.Visible.LastOrDefault() is { DataContext: NotificationViewModel toast } toastWindow)
        {
            Console.WriteLine($"Notification: {toast.Title} | {toast.Sender} | {toast.Subject} | {toast.Preview}");
            await Snapshots.SaveAsync(toastWindow, output, "live-notification.png");
            await reply.SaveOnLeaveAsync();
            await toast.Open();
            await WaitAsync(() => vm.MailPage.SelectedMessage?.Subject == subject && vm.MailPage.ReadingPane?.Message is not null);
            Console.WriteLine($"Notification clicked: opened={vm.MailPage.SelectedMessage?.Subject == subject}");
            foreach (var open in notifications.Visible.ToList())
            {
                open.Close();
            }
        }
    }

    // Drafts: save (GreenMail has no "Drafts" folder – it is created), change and leave by opening another message
    // (saved again, still one copy), continue it from "Entwürfe", send – then the draft is gone.
    private static async Task CheckDraftsAsync(MainWindowViewModel vm, MainWindow window, string output, string mailHost, string user)
    {
        var subject = "Entwurf " + Guid.NewGuid().ToString("N")[..6];
        await vm.MailPage.NewMailCommand.ExecuteAsync(null);
        var compose = vm.MailPage.Compose!;
        compose.To = "lea@example.com";
        compose.Subject = subject;
        await compose.SaveDraftCommand.ExecuteAsync(null);
        Console.WriteLine($"Draft: {compose.DraftStatus}, dirty={compose.IsDirty}");

        compose.Subject = subject + " überarbeitet";
        await OpenAsync(vm, "Vertrag zur Unterschrift");
        await WaitAsync(() => vm.StatusText?.StartsWith("Entwurf gespeichert", StringComparison.Ordinal) == true);
        Console.WriteLine($"Left the draft: compose open={vm.MailPage.Compose is not null}, status={vm.StatusText}, " +
                          $"on server: {string.Join(" | ", await DraftSubjectsAsync(mailHost, user, subject))}");

        var drafts = vm.MailPage.Accounts.SelectMany(a => a.AllFolders())
            .First(f => f.Folder.Role == Neruna.Core.Mail.FolderRole.Drafts && f.Account.Account.EmailAddress == user);
        vm.MailPage.SelectedTreeItem = drafts;
        await WaitAsync(() => vm.MailPage.CurrentFolder == drafts && vm.MailPage.Entries.OfType<MessageItemViewModel>().Any(m => m.Subject.StartsWith(subject, StringComparison.Ordinal)));
        vm.MailPage.SelectedEntry = vm.MailPage.Entries.OfType<MessageItemViewModel>().First(m => m.Subject.StartsWith(subject, StringComparison.Ordinal));
        await WaitAsync(() => vm.MailPage.EditDraftCommand.CanExecute(null));
        await vm.MailPage.EditDraftCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.MailPage.Compose is not null);
        var reopened = vm.MailPage.Compose!;
        Console.WriteLine($"Reopened draft: to={reopened.To}, subject={reopened.Subject}, folder name={drafts.Name}");
        await Snapshots.SaveAsync(window, output, "live-draft.png");

        reopened.Sign = false;
        reopened.Encrypt = false;
        await reopened.SendCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.MailPage.Compose is null);
        await Task.Delay(500);
        Console.WriteLine($"Draft sent: {reopened.Error ?? "ok"}, left on server: {(await DraftSubjectsAsync(mailHost, user, subject)).Count}, " +
                          $"in list: {vm.MailPage.Entries.OfType<MessageItemViewModel>().Count(m => m.Subject.StartsWith(subject, StringComparison.Ordinal))}");
    }

    private static async Task<List<string>> DraftSubjectsAsync(string host, string user, string subject)
    {
        using var imap = new MailKit.Net.Imap.ImapClient();
        await imap.ConnectAsync(host, 3143, SecureSocketOptions.None);
        await imap.AuthenticateAsync(user, "geheim");
        var folder = await imap.GetFolderAsync("Drafts");
        await folder.OpenAsync(MailKit.FolderAccess.ReadOnly);
        var found = new List<string>();
        foreach (var uid in await folder.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(subject)))
        {
            var message = await folder.GetMessageAsync(uid);
            found.Add(message.Subject ?? string.Empty);
        }

        await imap.DisconnectAsync(true);
        return found;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }

    private static readonly string[] PickSubjects = ["Offerte Netzwerk-Erneuerung Q4", "Newsletter Oktober"];

    private static async Task EnsureFolderAsync(string host, string name)
    {
        using var imap = new MailKit.Net.Imap.ImapClient();
        await imap.ConnectAsync(host, 3143, SecureSocketOptions.None);
        await imap.AuthenticateAsync("anna@example.com", "geheim");
        var root = imap.GetFolder(imap.PersonalNamespaces[0]);
        if ((await root.GetSubfoldersAsync()).All(f => f.Name != name))
        {
            await root.CreateAsync(name, true);
        }

        await imap.DisconnectAsync(true);
    }

    private static async Task SeedMailAsync(string host)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(host, 3025, SecureSocketOptions.None);
        await smtp.AuthenticateAsync("marco@example.com", "geheim");

        var offer = new MimeMessage { Subject = "Offerte Netzwerk-Erneuerung Q4" };
        offer.From.Add(new MailboxAddress("Marco Bernasconi", "marco@example.com"));
        offer.To.Add(new MailboxAddress("Anna Muster", "anna@example.com"));
        offer.Cc.Add(new MailboxAddress("Lea Keller", "lea@example.com"));
        var body = new BodyBuilder
        {
            TextBody = "Hallo Anna\n\nAnbei wie besprochen unsere Offerte. Installation in KW 46 möglich.\n\nFreundliche Grüsse\nMarco",
        };
        body.Attachments.Add("Offerte-Q4.pdf", Encoding.UTF8.GetBytes("%PDF-1.4 demo"), new ContentType("application", "pdf"));
        offer.Body = body.ToMessageBody();
        await smtp.SendAsync(offer);

        var html = new MimeMessage { Subject = "Newsletter Oktober" };
        html.From.Add(new MailboxAddress("Example AG", "marco@example.com"));
        html.To.Add(new MailboxAddress("Anna Muster", "anna@example.com"));
        html.Body = new TextPart("html") { Text = "<h1>Neuigkeiten</h1><p>Unser <b>Herbstanlass</b> findet am 24. Oktober statt.</p><ul><li>Apéro</li><li>Vorträge</li></ul>" };
        await smtp.SendAsync(html);

        await smtp.DisconnectAsync(true);
    }

    /// <summary>HTML with an embedded logo and a tracking pixel, an inline PDF, and S/MIME mails from Marco and Lea.</summary>
    private static async Task SeedRichMailAsync(string host)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(host, 3025, SecureSocketOptions.None);
        await smtp.AuthenticateAsync("marco@example.com", "geheim");

        var newsletter = NewMessage("Example AG", "marco@example.com", "Newsletter mit Logo");
        var body = new BodyBuilder();
        var logo = body.LinkedResources.Add("logo.png", RenderLogo(), new ContentType("image", "png"));
        logo.ContentId = MimeUtils.GenerateMessageId();
        body.HtmlBody = $"""
            <table width="100%" cellpadding="0" cellspacing="0" style="font-family:Arial">
              <tr><td style="background:#0F6CBD;padding:16px"><img src="cid:{logo.ContentId}" alt="Example AG" width="220"></td></tr>
              <tr><td style="padding:16px">
                <h2 style="color:#0F6CBD">Herbstanlass am 24. Oktober</h2>
                <p>Liebe Kundinnen und Kunden</p>
                <p>Wir laden Sie herzlich zu unserem <b>Herbstanlass</b> ein. Programm:</p>
                <ul><li>17:00 Apéro</li><li>18:00 Vorträge zu IT-Sicherheit</li><li>19:30 Abendessen</li></ul>
                <p><a href="https://example.com/anmeldung">Jetzt anmelden</a></p>
                <img src="https://tracker.example/open.gif?id=4711" width="1" height="1">
              </td></tr>
              <tr><td style="padding:16px;color:#777;font-size:11px">Example AG · Bahnhofstrasse 1 · 8001 Zürich</td></tr>
            </table>
            """;
        body.TextBody = "Herbstanlass am 24. Oktober";
        newsletter.Body = body.ToMessageBody();
        await smtp.SendAsync(newsletter);

        var contract = NewMessage("Marco Bernasconi", "marco@example.com", "Vertrag zur Unterschrift");
        var pdf = new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream("%PDF-1.4 Vertrag"u8.ToArray())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = "Vertrag-2027.pdf" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        contract.Body = new Multipart("mixed") { new TextPart("plain") { Text = "Hallo Anna\n\nAnbei der Vertrag (als Inline-PDF, wie Apple Mail es sendet).\n\nMarco" }, pdf };
        await smtp.SendAsync(contract);

        // Marco signs with his certificate and encrypts for Anna.
        using (var marcoContext = new TemporarySecureMimeContext())
        {
            await using (var p12 = File.OpenRead(Path.Combine(CertDir, "marco.p12")))
            {
                marcoContext.Import(p12, "geheim");
            }

            marcoContext.Import(new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(await File.ReadAllBytesAsync(Path.Combine(CertDir, "anna.crt"))));
            var secret = NewMessage("Marco Bernasconi", "marco@example.com", "Vertraulich: Konditionen 2027");
            // Like Thunderbird: format=flowed, long lines soft-wrapped with a trailing space.
            var flowed = new TextPart("plain") { Text = "Hallo Anna\r\n\r\nDie Konditionen für 2027: 12 % Rabatt auf alle Switches, sofern die \r\nBestellung bis Ende November eingeht.\r\nBitte vertraulich behandeln.\r\n\r\nFreundliche Grüsse\r\nMarco\r\n" };
            flowed.ContentType.Format = "flowed";
            secret.Body = flowed;
            secret.SignAndEncrypt(marcoContext);
            await smtp.SendAsync(secret);
        }

        await smtp.DisconnectAsync(true);

        // Lea sends a clear-signed message (her certificate is already in Anna's store).
        using var lea = new SmtpClient();
        await lea.ConnectAsync(host, 3025, SecureSocketOptions.None);
        await lea.AuthenticateAsync("lea@example.com", "geheim");
        using (var leaContext = new TemporarySecureMimeContext())
        {
            await using (var p12 = File.OpenRead(Path.Combine(CertDir, "lea.p12")))
            {
                leaContext.Import(p12, "geheim");
            }

            var signed = NewMessage("Lea Keller", "lea@example.com", "Signierte Rückmeldung");
            signed.Body = new TextPart("plain") { Text = "Hallo Anna\n\nDie Tickets für die Rigi sind reserviert.\n\nLea" };
            signed.Sign(leaContext);
            await lea.SendAsync(signed);
        }

        await lea.DisconnectAsync(true);
    }

    private static MimeMessage NewMessage(string fromName, string fromAddress, string subject)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(new MailboxAddress("Anna Muster", "anna@example.com"));
        return message;
    }

    // A small logo rendered with Avalonia itself, so the embedded image is real PNG data.
    private static byte[] RenderLogo()
    {
        var logo = new Border
        {
            Width = 220,
            Height = 48,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock { Text = "EXAMPLE AG", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#0F6CBD"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        logo.Measure(new Size(220, 48));
        logo.Arrange(new Rect(0, 0, 220, 48));
        using var bitmap = new RenderTargetBitmap(new PixelSize(220, 48));
        bitmap.Render(logo);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static async Task ChooseCalendarsAsync(MainWindowViewModel vm, Func<CalendarChoiceItem, bool> select)
    {
        vm.CalendarPage.ManageCommand.Execute(null);
        var selection = (CalendarSelectionViewModel)vm.Overlay!;
        await WaitAsync(() => !selection.IsBusy);
        foreach (var item in selection.Sources.SelectMany(s => s.Calendars))
        {
            item.IsSelected = select(item);
        }

        await selection.ApplyCommand.ExecuteAsync(null);
    }

    private static async Task RemoveServerCalendarAsync(Uri davUrl, string user, string path)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":geheim")));
        await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, new Uri(new Uri(davUrl, "/" + Uri.EscapeDataString(user) + "/"), path)));
    }

    private static async Task AddServerCalendarAsync(Uri davUrl, string user, string path, string name)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":geheim")));
        var url = new Uri(new Uri(davUrl, "/" + Uri.EscapeDataString(user) + "/"), path);
        await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url));
        await MkcolAsync(http, url, "<C:calendar/>", name, "#13A10EFF");
    }

    private static async Task SeedDavAsync(Uri davUrl, string user)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":geheim")));
        var home = new Uri(davUrl, "/" + Uri.EscapeDataString(user) + "/");

        await MkcolAsync(http, new Uri(home, "kalender/"), "<C:calendar/>", "Kalender", "#0F6CBDFF");
        await MkcolAsync(http, new Uri(home, "team/"), "<C:calendar/>", "Team", "#C239B3FF");
        await MkcolAsync(http, new Uri(home, "kontakte/"), "<A:addressbook/>", "Kontakte", null);

        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        await PutEventAsync(http, new Uri(home, "kalender/live-standup.ics"), "live-standup", monday.AddHours(9), 15, "Stand-up", "FREQ=WEEKLY;BYDAY=MO,WE,FR");
        await PutEventAsync(http, new Uri(home, "kalender/live-planung.ics"), "live-planung", monday.AddDays(3).AddHours(14), 90, "Quartalsplanung", null);
        await PutEventAsync(http, new Uri(home, "team/live-workshop.ics"), "live-workshop", monday.AddDays(2).AddHours(10), 120, "Kundenworkshop", null);

        await PutAsync(http, new Uri(home, "kontakte/live-marco.vcf"), "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:live-marco\r\nFN:Marco Bernasconi\r\nN:Bernasconi;Marco;;;\r\nORG:Bernasconi Netzwerke AG\r\nTITLE:Verkaufsleiter\r\nEMAIL;TYPE=INTERNET,WORK:marco@example.com\r\nTEL;TYPE=CELL:+41 79 555 12 34\r\nADR;TYPE=WORK:;;Via Cantonale 1;Lugano;;6900;Schweiz\r\nEND:VCARD\r\n", "text/vcard");
        await PutAsync(http, new Uri(home, "kontakte/live-lea.vcf"), "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:urn:uuid:live-lea\r\nFN:Lea Keller\r\nN:Keller;Lea;;;\r\nEMAIL;TYPE=work:lea@example.com\r\nEND:VCARD\r\n", "text/vcard");
    }

    private static Task PutEventAsync(HttpClient http, Uri url, string uid, DateTime start, int minutes, string summary, string? rule) =>
        PutAsync(http, url, $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260101T000000Z\r\n"
                            + $"DTSTART:{start.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}\r\nDTEND:{start.AddMinutes(minutes).ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}\r\n"
                            + $"SUMMARY:{summary}\r\n{(rule is null ? string.Empty : "RRULE:" + rule + "\r\n")}END:VEVENT\r\nEND:VCALENDAR\r\n", "text/calendar");

    private static async Task PutAsync(HttpClient http, Uri url, string content, string mediaType)
    {
        var response = await http.PutAsync(url, new StringContent(content, Encoding.UTF8, mediaType));
        response.EnsureSuccessStatusCode();
    }

    private static async Task MkcolAsync(HttpClient http, Uri url, string type, string name, string? color)
    {
        var colorXml = color is null ? string.Empty : $"<I:calendar-color>{color}</I:calendar-color>";
        var body = $"""<?xml version="1.0"?><D:mkcol xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:A="urn:ietf:params:xml:ns:carddav" xmlns:I="http://apple.com/ns/ical/"><D:set><D:prop><D:resourcetype><D:collection/>{type}</D:resourcetype><D:displayname>{name}</D:displayname>{colorXml}</D:prop></D:set></D:mkcol>""";
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), url) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
        {
            throw new InvalidOperationException($"MKCOL {url}: {(int)response.StatusCode}");
        }
    }
}
