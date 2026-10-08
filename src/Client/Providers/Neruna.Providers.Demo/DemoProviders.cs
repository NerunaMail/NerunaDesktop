using System.Globalization;
using System.Text;
using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;

namespace Neruna.Providers.Demo;

/// <summary>
/// Read-only sample data so the app can be shown and developed without a server.
/// Everything is generated relative to "now" so the inbox and the calendar always look current.
/// </summary>
public static class DemoData
{
    public const string ProviderId = "demo";

    public static Account CreateAccount() => new(
        Guid.Parse("4e2b8d3c-0000-4000-8000-00000000d3e0"),
        "Anna Muster",
        "anna.muster@example.com",
        [
            new ServiceConnection(Guid.Parse("4e2b8d3c-0000-4000-8000-00000000d3e1"), ServiceKind.Mail, ProviderId, new Dictionary<string, string>()),
            new ServiceConnection(Guid.Parse("4e2b8d3c-0000-4000-8000-00000000d3e2"), ServiceKind.Calendar, ProviderId, new Dictionary<string, string>()),
            new ServiceConnection(Guid.Parse("4e2b8d3c-0000-4000-8000-00000000d3e3"), ServiceKind.Contacts, ProviderId, new Dictionary<string, string>()),
        ]);

    internal static readonly (string Folder, string FromName, string FromAddress, string Subject, string Body, int MinutesAgo, bool Seen, bool Flagged, bool Attachment)[] Messages =
    [
        ("INBOX", "Marco Bernasconi", "marco@bernasconi.example", "Offerte Netzwerk-Erneuerung Q4", "Hallo Anna\n\nAnbei wie besprochen unsere Offerte für die Erneuerung der Switches und WLAN-Accesspoints. Die Installation könnten wir in KW 46 einplanen.\n\nFreundliche Grüsse\nMarco", 12, false, true, true),
        ("INBOX", "Lea Keller", "lea.keller@example.com", "Re: Teamausflug auf die Rigi", "Super, ich bin dabei! Soll ich die Tickets für die Zahnradbahn reservieren?\n\nLea", 47, false, false, false),
        ("INBOX", "SOGo Kalender", "noreply@example.com", "Einladung: Quartalsplanung (Do 14:00)", "Thomas Frei hat Sie zu «Quartalsplanung» eingeladen.\n\nWann: Donnerstag, 14:00–15:30\nWo: Sitzungszimmer Pilatus", 180, true, false, false),
        ("INBOX", "Thomas Frei", "thomas.frei@example.com", "Signaturen ab nächster Woche zentral", "Hallo zusammen\n\nAb Montag werden die E-Mail-Signaturen zentral über Neruna Cloud verteilt. Bitte prüft danach kurz, ob Telefonnummer und Funktion stimmen.\n\nDanke & Gruss\nThomas", 60 * 26, true, false, false),
        ("INBOX", "Swisscom", "rechnung@swisscom.example", "Ihre Rechnung Oktober 2026", "Guten Tag\n\nIhre Rechnung ist bereit. Betrag: CHF 129.00, zahlbar bis 31.10.2026.", 60 * 30, true, false, true),
        ("INBOX", "Nadia Rossi", "nadia.rossi@example.com", "Protokoll Kundenworkshop", "Hoi Anna\n\nHier das Protokoll vom Workshop. Offene Punkte sind gelb markiert.\n\nLiebe Grüsse\nNadia", 60 * 50, true, false, true),
        ("INBOX", "IT Support", "support@example.com", "Wartungsfenster Samstag 06:00–08:00", "Am Samstag werden die Mailserver aktualisiert. In dieser Zeit ist kein Versand möglich; Neruna puffert ausgehende Nachrichten lokal.", 60 * 24 * 6, true, false, false),
        ("Sent", "Anna Muster", "anna.muster@example.com", "Re: Offerte Netzwerk-Erneuerung Q4", "Danke Marco, ich schaue sie bis Freitag an.", 8, true, false, false),
        ("Drafts", "Anna Muster", "anna.muster@example.com", "Ferienvertretung Dezember", "Hallo Lea, könntest du vom 22.12. bis 5.1. …", 60 * 3, true, false, false),
    ];
}

public sealed class DemoMailProviderFactory : IProviderFactory<IMailProvider>
{
    public string ProviderId => DemoData.ProviderId;

    public string DisplayName => "Demo";

    public IMailProvider Create(ServiceConnection connection) => new DemoMailProvider(connection.Id);
}

internal sealed class DemoMailProvider(Guid connectionId) : IMailProvider
{
    private static readonly (string Id, string Name, FolderRole Role)[] Folders =
    [
        ("INBOX", "Posteingang", FolderRole.Inbox),
        ("Drafts", "Entwürfe", FolderRole.Drafts),
        ("Sent", "Gesendete Elemente", FolderRole.Sent),
        ("Archive", "Archiv", FolderRole.Archive),
        ("Trash", "Gelöschte Elemente", FolderRole.Trash),
        ("Projekte", "Projekte", FolderRole.None),
    ];

    public MailProviderCapabilities Capabilities => MailProviderCapabilities.Flags;

    public Task TestConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailFolder>>(Folders.Select(f => new MailFolder(connectionId, f.Id, f.Name, null, f.Role)).ToList());

    public Task<FolderSyncResult> SyncFolderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var messages = DemoData.Messages
            .Select((m, i) => (Index: i, Message: m))
            .Where(x => x.Message.Folder == folder.RemoteId)
            .Select(x => new MessageSummary(
                x.Index.ToString(CultureInfo.InvariantCulture),
                null,
                null,
                x.Message.Subject,
                new MailAddress(x.Message.FromName, x.Message.FromAddress),
                [new MailAddress("Anna Muster", "anna.muster@example.com")],
                now.AddMinutes(-x.Message.MinutesAgo),
                (x.Message.Seen ? MessageFlags.Seen : MessageFlags.None) | (x.Message.Flagged ? MessageFlags.Flagged : MessageFlags.None),
                2048,
                x.Message.Attachment,
                x.Message.Body.ReplaceLineEndings(" ")))
            .ToList();

        // A fresh sync state every time keeps the demo current (dates relative to now).
        return Task.FromResult(new FolderSyncResult(
            now.Ticks.ToString(CultureInfo.InvariantCulture),
            IsFullResync: true,
            messages,
            new Dictionary<string, MessageFlags>(),
            [],
            messages.Count,
            messages.Count(m => !m.Flags.HasFlag(MessageFlags.Seen))));
    }

    public Task<MimeMessage> GetMessageAsync(MailFolder folder, string remoteId, CancellationToken cancellationToken = default)
    {
        var m = DemoData.Messages[int.Parse(remoteId, CultureInfo.InvariantCulture)];
        var message = new MimeMessage { Subject = m.Subject, Date = DateTimeOffset.Now.AddMinutes(-m.MinutesAgo) };
        message.From.Add(new MailboxAddress(m.FromName, m.FromAddress));
        message.To.Add(new MailboxAddress("Anna Muster", "anna.muster@example.com"));
        message.Body = new TextPart("plain") { Text = m.Body };
        return Task.FromResult(message);
    }

    // Flags are kept in the local store only; the next demo sync restores the original state.
    public Task SetFlagsAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, MessageFlags flags, bool add, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task MoveAsync(MailFolder source, IReadOnlyCollection<string> remoteIds, MailFolder target, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The demo account is read-only.");

    public Task DeleteAsync(MailFolder folder, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The demo account is read-only.");

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The demo account cannot send mail.");

    public Task AppendAsync(MailFolder folder, MimeMessage message, MessageFlags flags, DateTimeOffset? receivedAt, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The demo account is read-only.");

    public Task<MailFolder> CreateFolderAsync(string name, FolderRole role, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The demo account is read-only.");

    // The demo has all its messages from the start; searching works on the stored ones (no ServerSearch).
    public Task<IReadOnlyList<MessageSummary>> FetchOlderAsync(MailFolder folder, IReadOnlyCollection<string> knownRemoteIds, int count, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MessageSummary>>([]);

    public Task<(IReadOnlyList<MessageSummary> Hits, bool IsTruncated)> SearchAsync(MailFolder folder, MailSearchQuery query, int limit, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The demo account cannot search on a server.");

    // Nothing ever arrives in the demo account.
    public Task WaitForChangesAsync(MailFolder folder, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class DemoCalendarProviderFactory : IProviderFactory<ICalendarProvider>
{
    public string ProviderId => DemoData.ProviderId;

    public string DisplayName => "Demo";

    public ICalendarProvider Create(ServiceConnection connection) => new DemoCalendarProvider(connection.Id);
}

internal sealed class DemoCalendarProvider(Guid connectionId) : ICalendarProvider
{
    public CalendarProviderCapabilities Capabilities => CalendarProviderCapabilities.None;

    public Task TestConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CalendarInfo>>(
        [
            new CalendarInfo(connectionId, "personal", "Kalender", "#0F6CBD", IsReadOnly: true),
            new CalendarInfo(connectionId, "team", "Team (geteilt)", "#C239B3", IsReadOnly: true),
        ]);

    public Task<CalendarSyncResult> SyncCalendarAsync(CalendarInfo calendar, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));

        (int Day, int Hour, int Minute, int Duration, string Title, string? Location, string? Rule)[] events = calendar.RemoteId == "personal"
            ?
            [
                (0, 9, 0, 15, "Stand-up", "Teams-Raum", "FREQ=WEEKLY;BYDAY=MO,WE,FR"),
                (1, 13, 30, 60, "Offerte Netzwerk prüfen", null, null),
                (3, 14, 0, 90, "Quartalsplanung", "Sitzungszimmer Pilatus", null),
                (4, 12, 0, 60, "Mittagessen mit Lea", "Restaurant Lindenhof", null),
            ]
            :
            [
                (2, 10, 0, 120, "Kundenworkshop Bernasconi AG", "Zürich", null),
                (4, 16, 0, 60, "Apéro Team", "Dachterrasse", null),
            ];

        var items = events.Select((e, i) =>
        {
            var start = monday.AddDays(e.Day).AddHours(e.Hour).AddMinutes(e.Minute);
            var uid = $"{calendar.RemoteId}-{i}@demo.neruna";
            var ics = new StringBuilder()
                .Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Neruna//Demo//DE\r\nBEGIN:VEVENT\r\n")
                .Append(CultureInfo.InvariantCulture, $"UID:{uid}\r\nDTSTAMP:20260101T000000Z\r\n")
                .Append(CultureInfo.InvariantCulture, $"DTSTART:{start:yyyyMMdd'T'HHmmss}\r\nDTEND:{start.AddMinutes(e.Duration):yyyyMMdd'T'HHmmss}\r\n")
                .Append(CultureInfo.InvariantCulture, $"SUMMARY:{e.Title}\r\n")
                .Append(e.Location is null ? string.Empty : $"LOCATION:{e.Location}\r\n")
                .Append(e.Rule is null ? string.Empty : $"RRULE:{e.Rule}\r\n")
                .Append("END:VEVENT\r\nEND:VCALENDAR\r\n")
                .ToString();
            return new CalendarObject(uid, null, ics);
        }).ToList();

        // The week moves with today, so always replace.
        return Task.FromResult(new CalendarSyncResult(monday.ToString("yyyyMMdd", CultureInfo.InvariantCulture), IsFullResync: true, items, []));
    }

    public Task<CalendarObject> SaveAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class DemoContactProviderFactory : IProviderFactory<IContactProvider>
{
    public string ProviderId => DemoData.ProviderId;

    public string DisplayName => "Demo";

    public IContactProvider Create(ServiceConnection connection) => new DemoContactProvider(connection.Id);
}

internal sealed class DemoContactProvider(Guid connectionId) : IContactProvider
{
    private static readonly (string Name, string Org, string Email, string Phone)[] People =
    [
        ("Marco Bernasconi", "Bernasconi Netzwerke AG", "marco@bernasconi.example", "+41 91 555 12 34"),
        ("Lea Keller", "Example AG", "lea.keller@example.com", "+41 44 555 20 01"),
        ("Thomas Frei", "Example AG", "thomas.frei@example.com", "+41 44 555 20 02"),
        ("Nadia Rossi", "Example AG", "nadia.rossi@example.com", "+41 44 555 20 07"),
        ("Jonas Weber", "Weber Treuhand GmbH", "j.weber@weber-treuhand.example", "+41 31 555 88 10"),
    ];

    // A generated placeholder portrait (96×96 JPEG) to show contact pictures in the demo.
    private const string NadiaPhoto = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCABgAGADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDqKKTNGa9m54fKLRSZozRcOUWikzRmi4cotFJmjNFw5RaKTNGaLhyi0UmaM0XDlEzRmkzRms7nRyi5ozSZqxp9v9rvYYBnDtg4ODjv+maUpqKbY40+Z2RpaFpBvWE04K2wP0Ln0Ht7/wCR1dtbQWy7beJIxgA7Rycep71KqqihUAVQMAAYAFLXzmIxU68rvbsfQ4fDQoR037jJoYplCzRpIoOcOoIzXL67ogt1NxZKfKA+dM5K+49v8/Tq6KmhiJ0JXi9OxVfDwrxtJa9zzXNGav69ai01OVEXbG/zoOOh+nQZyPwrPzX0cKinFSXU+dnScJOL6C5ozSZozV3J5RM0ZpM0ZrO50coua0vDrKus2xZgBlhye5UgVmZp0UjxSpJGcOhDKfQioqLng49yoe7JS7HpdFU9L1CLULfzIuGHDoTyp/w96uV83KLi+WW59BGSkroKKKgvbuKyt2mnbCjoO5PoPekk27IG0ldnK+LmU6ogVgSsQBAPQ5J/qKxM1NfXLXl3LO/Bc5x6DsPyqDNfR0YunTUX0PBrNTm5IXNGaTNGa1uZ8omaM0maM1nc35Rc0ZpM1m6xrFvpiASZeZgSsann6n0FON5OyE0krs2rK7msrhZoGww6jsR6H2ro7bxTEV/0q3dWAHMZBBPfg4x+teK3/iK/umYRyfZ4j0WPg9c/e65+mKyp55bhw88skrAYBdixx+NVPL41tZ7kxxjp6QPoKbxTbhR5NvK7Z6OQox9Rmud1HUJ9QmEk5HAwqrwq/SvHopZIZBJC7RuOjKcEfjWlZ69qNqRicyrnJWX5s8evX9aIZdGlrDcc8bKppLY9EzRmsbRtet9SfytphuMZ2Mchvoe9bGamScXZjjaSuhc0ZpM0ZpXHyiZozTc0juqIzOwVVGSScACs7nTymT4j1b+zrYJAy/apPug87R/ex/n9K4N2Z3ZnYszHJJOSTVjUbyS/vJJ5SfmPyqTnavYCqtepRp+zj5nlVanPLyCiiitTIKKKKACu18Law12htbtwZ0HyMTy4/wAR+v4E1xVSW80lvMksDlJEOQwrOrTVSNjSnUcJXPU80ZqvZXC3dpDOuMSKGwDnB7jPt0qbNeU3bRnqpJq6EzWd4hmaHRbtlwSV2c+jEA/zrQzWb4jjeXRLpUGSFDfgCCf0BpU2udX7mtWL5JW7Hn1FFFe0eCFFFFABRRRQAUUUUAdz4PmaTR9rYAikZBj04P8AU1t5rC8HRumklmGA8rMvuMAfzBrczXj12vaOx7lCLdONz//Z";

    public ContactProviderCapabilities Capabilities => ContactProviderCapabilities.None;

    public Task TestConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AddressBookInfo>>(
        [
            new AddressBookInfo(connectionId, "contacts", "Geschäftlich", IsReadOnly: true),
            new AddressBookInfo(connectionId, "private", "Privat", IsReadOnly: true),
        ]);

    public Task<AddressBookSyncResult> SyncAddressBookAsync(AddressBookInfo addressBook, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        if (addressBook.RemoteId == "private")
        {
            return Task.FromResult(new AddressBookSyncResult("1", IsFullResync: true,
            [
                new ContactObject("demo-p1", null, "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:demo-p1\r\nFN:Sandra Muster\r\nN:Muster;Sandra;;;\r\nEMAIL;TYPE=INTERNET,HOME:sandra.muster@example.net\r\nTEL;TYPE=CELL:+41 79 555 44 33\r\nEND:VCARD\r\n"),
                new ContactObject("demo-p2", null, "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:demo-p2\r\nFN:Peter Brunner\r\nN:Brunner;Peter;;;\r\nEMAIL;TYPE=INTERNET,HOME:peter.brunner@example.net\r\nEND:VCARD\r\n"),
            ], []));
        }

        var contacts = People.Select((p, i) => new ContactObject(
            $"demo-{i}",
            null,
            $"BEGIN:VCARD\r\nVERSION:4.0\r\nUID:demo-{i}\r\nFN:{p.Name}\r\nORG:{p.Org}\r\nEMAIL;TYPE=work:{p.Email}\r\n"
            + (i == 1 ? "EMAIL;TYPE=home:lea.keller@privat.example\r\n" : string.Empty)
            + (i == 3 ? "PHOTO;ENCODING=b;TYPE=JPEG:" + NadiaPhoto + "\r\n" : string.Empty)
            + $"TEL:{p.Phone}\r\nEND:VCARD\r\n")).ToList();

        // A distribution list: Lea with her private address, Marco with his default one, and an external address.
        contacts.Add(new ContactObject("demo-g1", null,
            "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:demo-g1\r\nFN:Projektteam Rigi\r\nN:Projektteam Rigi;;;;\r\nX-ADDRESSBOOKSERVER-KIND:group\r\n"
            + "X-ADDRESSBOOKSERVER-MEMBER;X-NERUNA-EMAIL=\"lea.keller@privat.example\":urn:uuid:demo-1\r\n"
            + "X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:demo-0\r\nX-ADDRESSBOOKSERVER-MEMBER:urn:uuid:demo-p1\r\n"
            + "X-ADDRESSBOOKSERVER-MEMBER:mailto:info@rigi.example\r\nEND:VCARD\r\n"));
        return Task.FromResult(new AddressBookSyncResult("1", IsFullResync: true, contacts, []));
    }

    public Task<ContactObject> SaveAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
