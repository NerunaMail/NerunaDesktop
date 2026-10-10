using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using static Neruna.Core.Localization.Texts;

using Neruna.Core.Calendar;

namespace Neruna.Providers.Dav;

/// <summary>What differs between CalDAV and CardDAV; everything else is shared.</summary>
internal sealed record DavFlavor(
    string WellKnown,
    XName HomeSet,
    XName CollectionType,
    XName Multiget,
    XName Data,
    string ContentType,
    string Extension)
{
    public static DavFlavor CalDav { get; } = new(
        "caldav",
        DavNames.CalDav + "calendar-home-set",
        DavNames.CalDav + "calendar",
        DavNames.CalDav + "calendar-multiget",
        DavNames.CalDav + "calendar-data",
        "text/calendar; charset=utf-8",
        ".ics");

    public static DavFlavor CardDav { get; } = new(
        "carddav",
        DavNames.CardDav + "addressbook-home-set",
        DavNames.CardDav + "addressbook",
        DavNames.CardDav + "addressbook-multiget",
        DavNames.CardDav + "address-data",
        "text/vcard; charset=utf-8",
        ".vcf");
}

internal sealed record DavCollection(Uri Url, string Name, string? Color, bool IsReadOnly, CalendarContent Content)
{
    public bool SupportsEvents => Content.HasFlag(CalendarContent.Events);
}

internal sealed record DavItem(string RemoteId, string? ETag, string Data);

internal sealed record DavSyncResult(string State, IReadOnlyList<DavItem> Changed, IReadOnlyList<string> Removed);

/// <summary>
/// Discovery, sync and writes for one DAV account. Remote ids are the resources' URL paths.
/// Sync prefers WebDAV sync-collection (RFC 6578) and falls back to CTag + ETag comparison for older servers.
/// </summary>
internal sealed class DavCollectionService(DavClient client, Uri startUrl, DavFlavor flavor, ILogger logger)
{
    /// <summary>
    /// True if the server sends invitations and replies itself when events with attendees are saved (RFC 6638 implicit
    /// scheduling, e.g. SOGo, Nextcloud) – then the client must not send them by e-mail as well.
    /// </summary>
    public async Task<bool> SchedulesItselfAsync(Uri calendar, CancellationToken cancellationToken)
    {
        var response = await client.SendAsync(HttpMethod.Options, calendar, null, null, null, null, cancellationToken);
        return response.Dav?.Split(',').Any(c => c.Trim().Equals("calendar-auto-schedule", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private const int MultigetBatchSize = 50;

    private static readonly XName ResourceType = DavNames.Dav + "resourcetype";
    private static readonly XName DisplayName = DavNames.Dav + "displayname";
    private static readonly XName GetETag = DavNames.Dav + "getetag";
    private static readonly XName SyncToken = DavNames.Dav + "sync-token";
    private static readonly XName CTag = DavNames.CalendarServer + "getctag";
    private static readonly XName CurrentUserPrincipal = DavNames.Dav + "current-user-principal";
    private static readonly XName Privileges = DavNames.Dav + "current-user-privilege-set";
    private static readonly XName CalendarColor = DavNames.Apple + "calendar-color";
    private static readonly XName SupportedComponents = DavNames.CalDav + "supported-calendar-component-set";

    /// <summary>
    /// Finds the user's collections from whatever URL was configured: server root, /.well-known/…, principal,
    /// home set or a single collection.
    /// </summary>
    /// <summary>The collection home that the last discovery listed (null: only the configured collection).</summary>
    public Uri? Home { get; private set; }

    /// <summary>The single collection at <paramref name="url"/>, or null if it is not one of our type.</summary>
    public async Task<DavCollection?> GetCollectionAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            return (await ListAsync(url, depth: 0, cancellationToken)).FirstOrDefault();
        }
        catch (DavException ex)
        {
            logger.LogDebug("DAV: {Url} is no usable collection ({Status})", url, (int)ex.Status);
            return null;
        }
    }

    public async Task<IReadOnlyList<DavCollection>> DiscoverAsync(CancellationToken cancellationToken)
    {
        Home = null;
        foreach (var candidate in Candidates())
        {
            Multistatus self;
            try
            {
                self = await client.PropfindAsync(candidate, 0, [ResourceType, CurrentUserPrincipal, flavor.HomeSet, DisplayName], cancellationToken);
            }
            catch (DavException ex)
            {
                logger.LogDebug("DAV discovery: {Url} not usable ({Status})", candidate, (int)ex.Status);
                continue;
            }

            var resource = self.Resources.FirstOrDefault();
            if (resource is null)
            {
                continue;
            }

            // The URL is a single calendar / address book (Thunderbird wants e.g. SOGo's …/Calendar/personal/). The
            // user's home still lists everything else – shared and subscribed calendars included – so look there first.
            if (resource.HasResourceType(flavor.CollectionType))
            {
                var configured = await ListAsync(resource.Href, depth: 0, cancellationToken);
                if (await FindHomeAsync(resource, cancellationToken) is { } userHome)
                {
                    try
                    {
                        var all = await ListAsync(userHome, depth: 1, cancellationToken);
                        Home = userHome;
                        return all.Any(c => c.Url == configured.FirstOrDefault()?.Url) ? all : [.. configured, .. all];
                    }
                    catch (DavException ex)
                    {
                        logger.LogDebug("DAV discovery: home {Url} not listable ({Status}); using the configured collection only", userHome, (int)ex.Status);
                    }
                }

                return configured;
            }

            var home = HrefOf(resource, flavor.HomeSet);
            if (home is null && HrefOf(resource, CurrentUserPrincipal) is { } principal)
            {
                var principalProps = await client.PropfindAsync(principal, 0, [flavor.HomeSet], cancellationToken);
                home = principalProps.Resources.Select(r => HrefOf(r, flavor.HomeSet)).FirstOrDefault(h => h is not null);
            }

            // Some servers are configured with the home URL directly and expose no principal.
            var collections = await ListAsync(home ?? resource.Href, depth: 1, cancellationToken);
            Home = home ?? resource.Href;
            if (collections.Count > 0 || home is not null)
            {
                return collections;
            }
        }

        throw new DavException(HttpStatusCode.NotFound, F("Unter {0} wurde kein {1}-Dienst gefunden.", startUrl, flavor.WellKnown.ToUpperInvariant()));
    }

    public async Task<DavSyncResult> SyncAsync(Uri collection, string? state, IReadOnlyDictionary<string, string?> known, CancellationToken cancellationToken)
    {
        if (state is null || state.StartsWith("sync:", StringComparison.Ordinal))
        {
            try
            {
                return await SyncCollectionAsync(collection, state?[5..], known, cancellationToken);
            }
            catch (DavException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.BadRequest
                                              or HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed or HttpStatusCode.UnsupportedMediaType
                                              or HttpStatusCode.NotFound)
            {
                // Expired/invalid token or no RFC 6578 support: fall back to a full comparison.
                logger.LogDebug("sync-collection on {Url} failed ({Status}); comparing ETags instead", collection, (int)ex.Status);
                if (state is not null && ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
                {
                    try
                    {
                        return await SyncCollectionAsync(collection, null, known, cancellationToken);
                    }
                    catch (DavException)
                    {
                        // Server does not do sync-collection at all.
                    }
                }
            }
        }

        return await CompareETagsAsync(collection, state, known, cancellationToken);
    }

    /// <summary>Creates (no <paramref name="remoteId"/>) or updates a resource; returns its id, ETag and stored data.</summary>
    /// <exception cref="RemoteConflictException">ETag mismatch or the resource already exists.</exception>
    public async Task<DavItem> PutAsync(Uri collection, string? remoteId, string? etag, string data, string uid, CancellationToken cancellationToken)
    {
        var create = string.IsNullOrEmpty(remoteId);
        var url = create ? new Uri(EnsureTrailingSlash(collection), SafeFileName(uid) + flavor.Extension) : new Uri(collection, remoteId);

        var response = await client.PutAsync(url, data, flavor.ContentType, etag, create, cancellationToken);
        if (response.Status == HttpStatusCode.PreconditionFailed)
        {
            throw new RemoteConflictException();
        }

        if ((int)response.Status is < 200 or >= 300)
        {
            throw new DavException(response.Status, $"Speichern fehlgeschlagen: {(int)response.Status} {response.Status}. {Truncate(response.Body)}");
        }

        // Without a strong ETag the server may have rewritten the data (allowed by RFC 4791 §5.3.4): read it back.
        if (response.ETag is { } newTag && !newTag.StartsWith("W/", StringComparison.Ordinal))
        {
            return new DavItem(url.AbsolutePath, newTag, data);
        }

        var stored = await client.GetAsync(url, cancellationToken);
        return new DavItem(url.AbsolutePath, stored.ETag, stored.Status == HttpStatusCode.OK ? stored.Body : data);
    }

    /// <exception cref="RemoteConflictException">The resource changed on the server.</exception>
    public async Task DeleteAsync(Uri collection, string remoteId, string? etag, CancellationToken cancellationToken)
    {
        var response = await client.DeleteAsync(new Uri(collection, remoteId), etag, cancellationToken);
        if (response.Status == HttpStatusCode.PreconditionFailed)
        {
            throw new RemoteConflictException();
        }

        if ((int)response.Status is < 200 or >= 300 && response.Status != HttpStatusCode.NotFound)
        {
            throw new DavException(response.Status, F("Löschen fehlgeschlagen: {0} {1}.", (int)response.Status, response.Status));
        }
    }

    private IEnumerable<Uri> Candidates()
    {
        yield return startUrl;
        var wellKnown = new Uri(startUrl, "/.well-known/" + flavor.WellKnown);
        if (wellKnown != startUrl)
        {
            yield return wellKnown;
        }
    }

    private async Task<Uri?> FindHomeAsync(DavResource resource, CancellationToken cancellationToken)
    {
        if (HrefOf(resource, flavor.HomeSet) is { } home)
        {
            return home;
        }

        if (HrefOf(resource, CurrentUserPrincipal) is not { } principal)
        {
            return null;
        }

        try
        {
            var principalProps = await client.PropfindAsync(principal, 0, [flavor.HomeSet], cancellationToken);
            return principalProps.Resources.Select(r => HrefOf(r, flavor.HomeSet)).FirstOrDefault(h => h is not null);
        }
        catch (DavException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<DavCollection>> ListAsync(Uri url, int depth, CancellationToken cancellationToken)
    {
        // Listing a home (depth 1) identifies as Thunderbird-compatible, so SOGo includes subscribed calendars.
        var listing = await client.PropfindAsync(url, depth, [ResourceType, DisplayName, CalendarColor, SupportedComponents, Privileges], cancellationToken,
            depth == 1 ? DavClient.ListingUserAgent : null);

        return listing.Resources
            .Where(r => r.HasResourceType(flavor.CollectionType))
            .Select(r => new DavCollection(
                EnsureTrailingSlash(r.Href),
                r.Text(DisplayName) ?? Uri.UnescapeDataString(r.Href.Segments.Last().TrimEnd('/')),
                ParseColor(r.Text(CalendarColor)),
                IsReadOnly(r),
                ContentOf(r)))
            .ToList();
    }

    private async Task<DavSyncResult> SyncCollectionAsync(Uri collection, string? token, IReadOnlyDictionary<string, string?> known, CancellationToken cancellationToken)
    {
        var initial = string.IsNullOrEmpty(token);
        var seen = new Dictionary<string, string?>(StringComparer.Ordinal);
        var removed = new List<string>();

        // Servers may truncate large answers (507 on the collection); then continue with the returned token.
        for (var round = 0; round < 100; round++)
        {
            var body = new XElement(DavNames.Dav + "sync-collection",
                new XElement(DavNames.Dav + "sync-token", token ?? string.Empty),
                new XElement(DavNames.Dav + "sync-level", "1"),
                new XElement(DavNames.Dav + "prop", new XElement(GetETag)));

            var result = await client.ReportAsync(collection, 0, body, cancellationToken);
            var truncated = false;

            foreach (var resource in result.Resources)
            {
                if (SamePath(resource.Href, collection))
                {
                    truncated |= resource.Status == HttpStatusCode.InsufficientStorage;
                    continue;
                }

                var id = resource.Href.AbsolutePath;
                if (resource.Status == HttpStatusCode.NotFound)
                {
                    removed.Add(id);
                    seen.Remove(id);
                }
                else if (!id.EndsWith('/'))
                {
                    seen[id] = resource.Text(GetETag);
                }
            }

            token = result.SyncToken ?? token;
            if (!truncated)
            {
                break;
            }
        }

        // An initial sync lists all members: anything else stored locally is gone.
        if (initial)
        {
            removed.AddRange(known.Keys.Where(id => !seen.ContainsKey(id)));
        }

        var toFetch = seen.Where(s => !known.TryGetValue(s.Key, out var etag) || etag is null || etag != s.Value).Select(s => s.Key).ToList();
        var changed = await FetchAsync(collection, toFetch, cancellationToken);

        return new DavSyncResult("sync:" + token, changed, removed.Distinct().ToList());
    }

    private async Task<DavSyncResult> CompareETagsAsync(Uri collection, string? state, IReadOnlyDictionary<string, string?> known, CancellationToken cancellationToken)
    {
        var listing = await client.PropfindAsync(collection, 1, [GetETag, ResourceType, CTag], cancellationToken);

        var self = listing.Resources.FirstOrDefault(r => SamePath(r.Href, collection));
        var ctag = self?.Text(CTag);
        if (ctag is not null && state == "ctag:" + ctag)
        {
            return new DavSyncResult(state, [], []);
        }

        var members = listing.Resources
            .Where(r => !SamePath(r.Href, collection) && r[ResourceType]?.HasElements != true)
            .ToDictionary(r => r.Href.AbsolutePath, r => r.Text(GetETag), StringComparer.Ordinal);

        var removed = known.Keys.Where(id => !members.ContainsKey(id)).ToList();
        var toFetch = members.Where(m => !known.TryGetValue(m.Key, out var etag) || etag is null || etag != m.Value).Select(m => m.Key).ToList();
        var changed = await FetchAsync(collection, toFetch, cancellationToken);

        var newState = ctag is not null
            ? "ctag:" + ctag
            : "etags:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', members.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + " " + m.Value)))));
        return new DavSyncResult(newState, changed, removed);
    }

    private async Task<List<DavItem>> FetchAsync(Uri collection, List<string> remoteIds, CancellationToken cancellationToken)
    {
        var items = new List<DavItem>(remoteIds.Count);
        foreach (var batch in remoteIds.Chunk(MultigetBatchSize))
        {
            var body = new XElement(flavor.Multiget,
                new XElement(DavNames.Dav + "prop", new XElement(GetETag), new XElement(flavor.Data)),
                batch.Select(id => new XElement(DavNames.Dav + "href", id)));

            Multistatus result;
            try
            {
                result = await client.ReportAsync(collection, 1, body, cancellationToken);
            }
            catch (DavException ex) when (ex.Status is HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest)
            {
                // Rare: no multiget support. Fetch one by one.
                foreach (var id in batch)
                {
                    var single = await client.GetAsync(new Uri(collection, id), cancellationToken);
                    if (single.Status == HttpStatusCode.OK)
                    {
                        items.Add(new DavItem(id, single.ETag, single.Body));
                    }
                }

                continue;
            }

            foreach (var resource in result.Resources)
            {
                if (resource.Text(flavor.Data) is { } data)
                {
                    items.Add(new DavItem(resource.Href.AbsolutePath, resource.Text(GetETag), data));
                }
            }
        }

        return items;
    }

    private static Uri? HrefOf(DavResource resource, XName property) =>
        resource[property]?.Element(DavNames.Dav + "href")?.Value.Trim() is { Length: > 0 } href ? new Uri(resource.Href, href) : null;

    private static bool IsReadOnly(DavResource resource)
    {
        var privileges = resource[Privileges];
        if (privileges is null)
        {
            return false;
        }

        var granted = privileges.Elements(DavNames.Dav + "privilege").SelectMany(p => p.Elements()).Select(e => e.Name.LocalName).ToHashSet();
        return !granted.Overlaps(["all", "write", "write-content", "bind"]);
    }

    // Without supported-calendar-component-set a calendar may hold anything (RFC 4791): events and tasks.
    private static CalendarContent ContentOf(DavResource resource)
    {
        var components = resource[SupportedComponents]?.Elements(DavNames.CalDav + "comp").Select(c => (string?)c.Attribute("name")).ToList();
        if (components is null || components.Count == 0)
        {
            return CalendarContent.Events | CalendarContent.Tasks;
        }

        var content = CalendarContent.None;
        if (components.Contains("VEVENT", StringComparer.OrdinalIgnoreCase))
        {
            content |= CalendarContent.Events;
        }

        if (components.Contains("VTODO", StringComparer.OrdinalIgnoreCase))
        {
            content |= CalendarContent.Tasks;
        }

        return content;
    }

    // Apple writes #RRGGBBAA; Avalonia and most UIs want #RRGGBB.
    private static string? ParseColor(string? value) =>
        value is { Length: >= 7 } && value[0] == '#' ? value[..7] : null;

    private static bool SamePath(Uri a, Uri b) =>
        string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    private static Uri EnsureTrailingSlash(Uri url) =>
        url.AbsolutePath.EndsWith('/') ? url : new UriBuilder(url) { Path = url.AbsolutePath + "/" }.Uri;

    private static string SafeFileName(string uid)
    {
        var safe = new string(uid.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return safe.Length is > 0 and <= 100 ? safe : Guid.NewGuid().ToString("D");
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + " …";
}
