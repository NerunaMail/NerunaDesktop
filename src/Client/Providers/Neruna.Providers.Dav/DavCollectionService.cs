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

/// <param name="SharedBy">Found in another principal's home (delegation, group): whose; null for the user's own.</param>
internal sealed record DavCollection(Uri Url, string Name, string? Color, bool IsReadOnly, CalendarContent Content, string? SharedBy = null)
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
    private static readonly XName Principal = DavNames.Dav + "principal";
    private static readonly XName GroupMembership = DavNames.Dav + "group-membership";
    private static readonly XName ProxyReadFor = DavNames.CalendarServer + "calendar-proxy-read-for";
    private static readonly XName ProxyWriteFor = DavNames.CalendarServer + "calendar-proxy-write-for";
    private static readonly XName ProxyRead = DavNames.CalendarServer + "calendar-proxy-read";
    private static readonly XName ProxyWrite = DavNames.CalendarServer + "calendar-proxy-write";

    /// <summary>Other principals asked for delegated home sets, at most (groups can be large).</summary>
    private const int MaxRelatedPrincipals = 25;

    /// <summary>A collection home: the user's own (<paramref name="SharedBy"/> null) or another principal's.</summary>
    private sealed record HomeSet(Uri Url, string? SharedBy);

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
        var answers = new List<string>();
        var blocked = false;
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
                answers.Add($"{candidate.AbsoluteUri} → {(int)ex.Status} {ex.Status}");

                // Firewalls and proxies with web filtering often answer WebDAV requests with 403/405/501 or a web page.
                blocked |= ex.Status is HttpStatusCode.OK or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;
                continue;
            }

            var resource = self.Resources.FirstOrDefault();
            if (resource is null)
            {
                answers.Add($"{candidate.AbsoluteUri} → 207 ({T("leer")})");
                continue;
            }

            // The URL is a single calendar / address book (Thunderbird wants e.g. SOGo's …/Calendar/personal/). The
            // user's home still lists everything else – shared and subscribed calendars included – so look there first.
            if (resource.HasResourceType(flavor.CollectionType))
            {
                var configured = await ListAsync(resource.Href, depth: 0, cancellationToken);
                var userHomes = await HomesAsync(resource, cancellationToken);
                var all = await ListHomesAsync(userHomes, cancellationToken);
                if (all.Count == 0)
                {
                    return configured;
                }

                Home = userHomes.FirstOrDefault(h => h.SharedBy is null)?.Url;
                return all.Any(c => c.Url == configured.FirstOrDefault()?.Url) ? all : [.. configured, .. all];
            }

            // Some servers are configured with the home URL directly and expose no principal.
            var homes = await HomesAsync(resource, cancellationToken);
            var foundHome = homes.Any(h => h.SharedBy is null);
            if (!foundHome)
            {
                homes.Insert(0, new HomeSet(resource.Href, null));
            }

            var collections = await ListHomesAsync(homes, cancellationToken);
            Home = homes[0].Url;
            if (collections.Count > 0 || foundHome)
            {
                return collections;
            }

            answers.Add($"{candidate.AbsoluteUri} → 207 ({T("leer")})");
        }

        var message = F("Unter {0} wurde kein {1}-Dienst gefunden.", startUrl, flavor.WellKnown.ToUpperInvariant())
            + " " + F("Antworten des Servers: {0}", string.Join("; ", answers));
        if (blocked)
        {
            message += " " + T("Falls das Netzwerk eine Firewall oder einen Proxy mit Webfilter hat: WebDAV-Anfragen (PROPFIND) müssen dort erlaubt sein.");
        }

        throw new DavException(HttpStatusCode.NotFound, message);
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

    /// <summary>
    /// The collection homes of the user – all home sets of the principal, not only the first – and those the user may
    /// use through delegation (RFC 3744 group membership, CalendarServer calendar-proxy-read/write-for): another
    /// principal's home sets, marked with whose they are. At most two steps away, each principal asked once.
    /// </summary>
    private async Task<List<HomeSet>> HomesAsync(DavResource resource, CancellationToken cancellationToken)
    {
        var homes = HrefsOf(resource, flavor.HomeSet).Select(h => new HomeSet(EnsureTrailingSlash(h), null)).ToList();
        var principal = HrefOf(resource, CurrentUserPrincipal) ?? (resource.HasResourceType(Principal) ? resource.Href : null);
        if (principal is null)
        {
            return homes;
        }

        XName[] properties = flavor == DavFlavor.CalDav
            ? [flavor.HomeSet, DisplayName, ResourceType, GroupMembership, ProxyReadFor, ProxyWriteFor]
            : [flavor.HomeSet, DisplayName, ResourceType, GroupMembership];
        var me = await PrincipalAsync(principal, properties, cancellationToken);
        if (me is null)
        {
            return homes;
        }

        Add(homes, me, null);
        var queried = new HashSet<string>(StringComparer.Ordinal) { principal.AbsolutePath.TrimEnd('/') };
        var pending = new Queue<(Uri Url, int Level)>(Related(me).Select(u => (u, 1)));
        while (pending.TryDequeue(out var next) && queried.Count <= MaxRelatedPrincipals)
        {
            if (!queried.Add(next.Url.AbsolutePath.TrimEnd('/')))
            {
                continue;
            }

            if (await PrincipalAsync(next.Url, [flavor.HomeSet, DisplayName, ResourceType], cancellationToken) is not { } other)
            {
                continue;
            }

            Add(homes, other, other.Text(DisplayName) ?? Uri.UnescapeDataString(next.Url.Segments.Last().TrimEnd('/')));

            // Membership in another user's proxy group ("…/lea/calendar-proxy-read/"): the user behind it is its parent.
            if (next.Level < 2 && (other.HasResourceType(ProxyRead) || other.HasResourceType(ProxyWrite)))
            {
                pending.Enqueue((new Uri(next.Url, ".."), next.Level + 1));
            }
        }

        return homes;

        void Add(List<HomeSet> list, DavResource from, string? sharedBy)
        {
            foreach (var href in HrefsOf(from, flavor.HomeSet).Select(EnsureTrailingSlash))
            {
                if (list.All(h => h.Url != href))
                {
                    list.Add(new HomeSet(href, sharedBy));
                }
            }
        }
    }

    private static IEnumerable<Uri> Related(DavResource principal) =>
        HrefsOf(principal, GroupMembership).Concat(HrefsOf(principal, ProxyReadFor)).Concat(HrefsOf(principal, ProxyWriteFor));

    private async Task<DavResource?> PrincipalAsync(Uri url, XName[] properties, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.PropfindAsync(url, 0, properties, cancellationToken)).Resources.FirstOrDefault();
        }
        catch (DavException ex)
        {
            logger.LogDebug("DAV discovery: principal {Url} not readable ({Status})", url, (int)ex.Status);
            return null;
        }
    }

    /// <summary>The collections of all homes, the user's own first; a collection is listed once. Another principal's home that cannot be listed is skipped.</summary>
    private async Task<List<DavCollection>> ListHomesAsync(IReadOnlyList<HomeSet> homes, CancellationToken cancellationToken)
    {
        var result = new List<DavCollection>();
        foreach (var home in homes.OrderBy(h => h.SharedBy is not null))
        {
            IReadOnlyList<DavCollection> listed;
            try
            {
                listed = await ListAsync(home.Url, depth: 1, cancellationToken);
            }
            catch (DavException ex) when (home.SharedBy is not null || result.Count > 0)
            {
                logger.LogDebug("DAV discovery: home {Url} not listable ({Status})", home.Url, (int)ex.Status);
                continue;
            }

            result.AddRange(listed.Where(c => result.All(r => r.Url != c.Url)).Select(c => c with { SharedBy = home.SharedBy }));
        }

        return result;
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

    private static Uri? HrefOf(DavResource resource, XName property) => HrefsOf(resource, property).FirstOrDefault();

    // A property may hold several hrefs (home sets, group memberships …).
    private static IEnumerable<Uri> HrefsOf(DavResource resource, XName property) =>
        resource[property]?.Elements(DavNames.Dav + "href").Select(h => h.Value.Trim()).Where(h => h.Length > 0).Select(h => new Uri(resource.Href, h)) ?? [];

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
