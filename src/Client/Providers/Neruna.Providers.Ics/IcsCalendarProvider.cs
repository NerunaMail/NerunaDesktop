using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

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
        var name = displayName ?? IcsFeed.CalendarName(content) ?? IcsProviderFactory.FallbackName(feedUrl);
        return [new CalendarInfo(connectionId, feedUrl.AbsoluteUri, name, Color: null, IsReadOnly: true)];
    }

    public async Task<CalendarSyncResult> SyncCalendarAsync(CalendarInfo calendar, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var content = await LoadAsync(cancellationToken);
        var hash = HashOf(content);

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

    /// <summary>
    /// The feed's content without DTSTAMP: many published calendars stamp every event with the time of the download,
    /// which would make an unchanged feed look new at every sync (and be stored all over again).
    /// </summary>
    internal static string HashOf(string content)
    {
        var lines = content.Split('\n').Where(l => !l.StartsWith("DTSTAMP", StringComparison.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private async Task<string> LoadAsync(CancellationToken cancellationToken)
    {
        if (_content is not null)
        {
            return _content;
        }

        if (feedUrl.IsFile)
        {
            // A file on this computer (or a network drive): read again at every sync, so changes to it show up.
            var path = feedUrl.LocalPath;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(F("Die Datei «{0}» wurde nicht gefunden.", path), path);
            }

            var text = await File.ReadAllTextAsync(path, cancellationToken);
            if (!text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(T("Die Datei ist kein iCalendar-Kalender (.ics)."));
            }

            return _content = text;
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

    public string DisplayName => T("Internet-Kalender (ICS)");

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

    public string Summary(IReadOnlyDictionary<string, string> settings) =>
        settings?.GetValueOrDefault(IcsCalendarProvider.UrlSetting) is { } url
            ? Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Display(uri) : url
            : string.Empty;

    /// <summary>Accepts webcal:// links as published by most calendar sites, and local .ics files (a path or file://).</summary>
    public static Uri NormalizeUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        url = url.Trim().Trim('"');
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url["webcal://".Length..];
        }

        // "C:\Kalender\ferien.ics", "/home/anna/ferien.ics", "\\server\share\ferien.ics", "~/ferien.ics"
        if (url.StartsWith("~/", StringComparison.Ordinal))
        {
            url = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), url[2..]);
        }

        if (!url.Contains("://", StringComparison.Ordinal) && Path.IsPathFullyQualified(url))
        {
            return new Uri(Path.GetFullPath(url));
        }

        var uri = new Uri(url, UriKind.Absolute);
        return uri.Scheme is "https" or "http" || uri.IsFile
            ? uri
            : throw new ArgumentException("Only http(s), webcal and file addresses are supported.", nameof(url));
    }

    /// <summary>The name when the feed has none: the host of a web address, the file name of a local file.</summary>
    public static string FallbackName(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsFile ? Path.GetFileNameWithoutExtension(url.LocalPath) : url.Host;
    }

    /// <summary>For the settings and lists: the path of a local file, else the address.</summary>
    public static string Display(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsFile ? url.LocalPath : url.AbsoluteUri;
    }
}
