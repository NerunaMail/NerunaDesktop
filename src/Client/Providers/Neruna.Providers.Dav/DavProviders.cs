using System.Text;
using Microsoft.Extensions.Logging;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Dav;

/// <summary>Settings of a CalDAV or CardDAV connection.</summary>
public sealed record DavSettings(Uri Url, string Username)
{
    public const string UrlKey = "url";
    public const string UsernameKey = "username";

    public IReadOnlyDictionary<string, string> ToDictionary() =>
        new Dictionary<string, string> { [UrlKey] = Url.AbsoluteUri, [UsernameKey] = Username };

    public static DavSettings FromDictionary(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.TryGetValue(UrlKey, out var url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("DAV connection has no valid URL.");
        }

        return new DavSettings(uri, settings.GetValueOrDefault(UsernameKey) ?? string.Empty);
    }

    internal static DavSettings? FromDiscovery(DavServerSettings? server, string emailAddress) =>
        server is null ? null : new DavSettings(server.ServerUrl, DiscoveryPlaceholders.Expand(server.UsernameTemplate, emailAddress));
}

/// <summary>Shared plumbing: lazily creates the WebDAV client once the password is read from the credential store.</summary>
internal abstract class DavProviderBase(
    Guid connectionId,
    DavSettings settings,
    DavFlavor flavor,
    HttpClient http,
    ICredentialStore credentials,
    ILogger logger) : IAsyncDisposable
{
    private DavCollectionService? _service;

    protected Guid ConnectionId => connectionId;

    protected DavSettings Settings => settings;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
        _ = await (await ServiceAsync(cancellationToken)).DiscoverAsync(cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected async Task<DavCollectionService> ServiceAsync(CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            var password = await credentials.GetSecretAsync(connectionId, cancellationToken) ?? string.Empty;
            _service = new DavCollectionService(new DavClient(http, settings.Username, password, logger), settings.Url, flavor, logger);
        }

        return _service;
    }

    /// <summary>The UID property of iCalendar or vCard data; used to name new resources like other clients do.</summary>
    protected static string UidOf(string data)
    {
        var current = new StringBuilder();
        foreach (var raw in data.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && line[0] is ' ' or '\t')
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }

            if (TryUid(current.ToString()) is { } uid)
            {
                return uid;
            }

            current.Clear().Append(line);
        }

        return TryUid(current.ToString()) ?? Guid.NewGuid().ToString("D");
    }

    private static string? TryUid(string line) =>
        line.StartsWith("UID", StringComparison.OrdinalIgnoreCase) && line.Length > 4 && line[3] is ':' or ';'
            ? line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim()
            : null;
}

internal sealed class CalDavCalendarProvider : DavProviderBase, ICalendarProvider, ICalendarUrlLookup, ICalendarScheduling
{
    public string? DiscoveryDetails { get; private set; }

    public async Task<CalendarInfo?> GetCalendarAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var collection = await (await ServiceAsync(cancellationToken)).GetCollectionAsync(url.IsAbsoluteUri ? url : new Uri(Settings.Url, url), cancellationToken);
        return collection is { Content: not CalendarContent.None }
            ? new CalendarInfo(ConnectionId, collection.Url.AbsoluteUri, collection.Name, collection.Color, collection.IsReadOnly, Content: collection.Content)
            : null;
    }

    internal CalDavCalendarProvider(Guid connectionId, DavSettings settings, HttpClient http, ICredentialStore credentials, ILogger logger)
        : base(connectionId, settings, DavFlavor.CalDav, http, credentials, logger)
    {
    }

    public CalendarProviderCapabilities Capabilities => CalendarProviderCapabilities.Write;

    public async Task<bool> SchedulesItselfAsync(CalendarInfo calendar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return await (await ServiceAsync(cancellationToken)).SchedulesItselfAsync(new Uri(calendar.RemoteId), cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var service = await ServiceAsync(cancellationToken);
        var collections = await service.DiscoverAsync(cancellationToken);
        DiscoveryDetails = service.Home is { } home
            ? F("Kalender-Ordner {0} · angemeldet als {1}", home.AbsoluteUri, Settings.Username)
            : F("Nur die eingetragene Adresse {0} (kein Kalender-Ordner gefunden) · angemeldet als {1}", Settings.Url.AbsoluteUri, Settings.Username);
        return collections
            .Where(c => c.Content != CalendarContent.None)
            .Select(c => new CalendarInfo(ConnectionId, c.Url.AbsoluteUri, c.Name, c.Color, c.IsReadOnly, Content: c.Content))
            .ToList();
    }

    public async Task<CalendarSyncResult> SyncCalendarAsync(CalendarInfo calendar, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var result = await (await ServiceAsync(cancellationToken)).SyncAsync(new Uri(calendar.RemoteId), calendar.SyncState, knownVersions, cancellationToken);
        return new CalendarSyncResult(result.State, false, result.Changed.Select(i => new CalendarObject(i.RemoteId, i.ETag, i.Data)).ToList(), result.Removed);
    }

    public async Task<CalendarObject> SaveAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(item);
        var saved = await (await ServiceAsync(cancellationToken)).PutAsync(new Uri(calendar.RemoteId), item.RemoteId, item.ETag, item.ICalendarData, UidOf(item.ICalendarData), cancellationToken);
        return new CalendarObject(saved.RemoteId, saved.ETag, saved.Data);
    }

    public async Task DeleteAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(item);
        await (await ServiceAsync(cancellationToken)).DeleteAsync(new Uri(calendar.RemoteId), item.RemoteId, item.ETag, cancellationToken);
    }
}

internal sealed class CardDavContactProvider : DavProviderBase, IContactProvider
{
    internal CardDavContactProvider(Guid connectionId, DavSettings settings, HttpClient http, ICredentialStore credentials, ILogger logger)
        : base(connectionId, settings, DavFlavor.CardDav, http, credentials, logger)
    {
    }

    public ContactProviderCapabilities Capabilities => ContactProviderCapabilities.Write;

    public async Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(CancellationToken cancellationToken = default)
    {
        var collections = await (await ServiceAsync(cancellationToken)).DiscoverAsync(cancellationToken);
        return collections.Select(c => new AddressBookInfo(ConnectionId, c.Url.AbsoluteUri, c.Name, c.IsReadOnly)).ToList();
    }

    public async Task<AddressBookSyncResult> SyncAddressBookAsync(AddressBookInfo addressBook, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        var result = await (await ServiceAsync(cancellationToken)).SyncAsync(new Uri(addressBook.RemoteId), addressBook.SyncState, knownVersions, cancellationToken);
        return new AddressBookSyncResult(result.State, false, result.Changed.Select(i => new ContactObject(i.RemoteId, i.ETag, i.Data)).ToList(), result.Removed);
    }

    public async Task<ContactObject> SaveAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        ArgumentNullException.ThrowIfNull(contact);
        var saved = await (await ServiceAsync(cancellationToken)).PutAsync(new Uri(addressBook.RemoteId), contact.RemoteId, contact.ETag, contact.VCardData, UidOf(contact.VCardData), cancellationToken);
        return new ContactObject(saved.RemoteId, saved.ETag, saved.Data);
    }

    public async Task DeleteAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressBook);
        ArgumentNullException.ThrowIfNull(contact);
        await (await ServiceAsync(cancellationToken)).DeleteAsync(new Uri(addressBook.RemoteId), contact.RemoteId, contact.ETag, cancellationToken);
    }
}

/// <param name="http">Must not follow redirects automatically (see <see cref="DavClient"/>).</param>
public sealed class CalDavProviderFactory(HttpClient http, ICredentialStore credentials, ILoggerFactory loggerFactory) : IProviderFactory<ICalendarProvider>
{
    public string ProviderId => ProviderIds.CalDav;

    public string DisplayName => "CalDAV";

    public ICalendarProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new CalDavCalendarProvider(connection.Id, DavSettings.FromDictionary(connection.Settings), http, credentials, loggerFactory.CreateLogger<CalDavCalendarProvider>());
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) =>
        DavSettings.FromDiscovery(config?.CalDav, emailAddress)?.ToDictionary();

    public MailProviderConfig? DescribeSettings(IReadOnlyDictionary<string, string> settings)
    {
        var dav = DavSettings.FromDictionary(settings);
        return new MailProviderConfig(string.Empty, null, [], [], [new DavServerSettings(ServerProtocol.CalDav, dav.Url, dav.Username)]);
    }

    public string Summary(IReadOnlyDictionary<string, string> settings)
    {
        var dav = DavSettings.FromDictionary(settings);
        return $"{dav.Url} · {dav.Username}";
    }
}

/// <param name="http">Must not follow redirects automatically (see <see cref="DavClient"/>).</param>
public sealed class CardDavProviderFactory(HttpClient http, ICredentialStore credentials, ILoggerFactory loggerFactory) : IProviderFactory<IContactProvider>
{
    public string ProviderId => ProviderIds.CardDav;

    public string DisplayName => "CardDAV";

    public IContactProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new CardDavContactProvider(connection.Id, DavSettings.FromDictionary(connection.Settings), http, credentials, loggerFactory.CreateLogger<CardDavContactProvider>());
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) =>
        DavSettings.FromDiscovery(config?.CardDav, emailAddress)?.ToDictionary();

    public MailProviderConfig? DescribeSettings(IReadOnlyDictionary<string, string> settings)
    {
        var dav = DavSettings.FromDictionary(settings);
        return new MailProviderConfig(string.Empty, null, [], [], [new DavServerSettings(ServerProtocol.CardDav, dav.Url, dav.Username)]);
    }

    public string Summary(IReadOnlyDictionary<string, string> settings)
    {
        var dav = DavSettings.FromDictionary(settings);
        return $"{dav.Url} · {dav.Username}";
    }
}
