using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Providers;

namespace Neruna.Providers.Ics;

/// <summary>
/// Read-only subscription to an ICS/webcal feed (holidays, sports schedules, published calendars).
/// The whole feed is re-read on change; the sync state is the content hash.
/// </summary>
public sealed class IcsCalendarProvider(Guid connectionId, Uri feedUrl, string? displayName, HttpClient http) : ICalendarProvider
{
    public const string UrlSetting = "url";
    public const string NameSetting = "name";

    private string? _content;

    public CalendarProviderCapabilities Capabilities => CalendarProviderCapabilities.None;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) => await LoadAsync(cancellationToken);

    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var content = await LoadAsync(cancellationToken);
        var name = displayName ?? IcsFeed.CalendarName(content) ?? feedUrl.Host;
        return [new CalendarInfo(connectionId, feedUrl.AbsoluteUri, name, Color: null, IsReadOnly: true)];
    }

    public async Task<CalendarSyncResult> SyncCalendarAsync(CalendarInfo calendar, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var content = await LoadAsync(cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

        if (hash == calendar.SyncState)
        {
            return new CalendarSyncResult(hash, IsFullResync: false, [], []);
        }

        var items = IcsFeed.Split(content).Select(i => new CalendarObject(i.Uid, ETag: null, i.ICalendarData)).ToList();
        return new CalendarSyncResult(hash, IsFullResync: true, items, []);
    }

    public Task<CalendarObject> SaveAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ICS subscriptions are read-only.");

    public Task DeleteAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ICS subscriptions are read-only.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<string> LoadAsync(CancellationToken cancellationToken)
    {
        if (_content is not null)
        {
            return _content;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/calendar"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"Calendar feed returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!content.TrimStart().StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The URL does not return an iCalendar feed.");
        }

        return _content = content;
    }
}

public sealed class IcsProviderFactory(HttpClient http) : IProviderFactory<ICalendarProvider>
{
    public string ProviderId => ProviderIds.Ics;

    public string DisplayName => "Internet-Kalender (ICS)";

    public ICalendarProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.Settings.TryGetValue(IcsCalendarProvider.UrlSetting, out var url))
        {
            throw new InvalidOperationException("ICS connection has no URL.");
        }

        connection.Settings.TryGetValue(IcsCalendarProvider.NameSetting, out var name);
        return new IcsCalendarProvider(connection.Id, NormalizeUrl(url), name, http);
    }

    /// <summary>Accepts webcal:// links as published by most calendar sites.</summary>
    public static Uri NormalizeUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url["webcal://".Length..];
        }

        var uri = new Uri(url, UriKind.Absolute);
        return uri.Scheme is "https" or "http"
            ? uri
            : throw new ArgumentException("Only http(s) and webcal URLs are supported.", nameof(url));
    }
}
