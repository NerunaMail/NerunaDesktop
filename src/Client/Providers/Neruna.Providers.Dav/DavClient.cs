using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Dav;

internal static class DavNames
{
    public static readonly XNamespace Dav = "DAV:";
    public static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";
    public static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";
    public static readonly XNamespace Apple = "http://apple.com/ns/ical/";
}

/// <summary>A non-success WebDAV response that callers may want to react to (404, 405, 412 …).</summary>
internal sealed class DavException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <param name="Dav">The DAV header (compliance classes such as "calendar-auto-schedule"), if sent.</param>
internal sealed record DavHttpResponse(HttpStatusCode Status, Uri Url, string? ETag, string Body, string? Dav = null);

/// <summary>One <c>&lt;response&gt;</c> of a multistatus. Only properties reported with status 200 are included.</summary>
internal sealed record DavResource(Uri Href, HttpStatusCode Status, IReadOnlyDictionary<XName, XElement> Properties)
{
    public XElement? this[XName name] => Properties.GetValueOrDefault(name);

    public string? Text(XName name) => this[name]?.Value.Trim() is { Length: > 0 } value ? value : null;

    public bool HasResourceType(XName type) => this[DavNames.Dav + "resourcetype"]?.Element(type) is not null;
}

internal sealed record Multistatus(IReadOnlyList<DavResource> Resources, string? SyncToken);

/// <summary>
/// Minimal WebDAV client: Basic auth, PROPFIND/REPORT with multistatus parsing, conditional PUT/DELETE.
/// Follows redirects itself because HttpClient drops the Authorization header on redirects
/// (and /.well-known/caldav nearly always redirects); credentials are only re-sent to the same host.
/// </summary>
internal sealed class DavClient(HttpClient http, string username, string password, ILogger logger)
{
    private const int MaxRedirects = 5;
    private const int MaxAttempts = 3;

    /// <summary>Sent with every request.</summary>
    public const string UserAgent = "Neruna/0.1";

    /// <summary>
    /// For listing collection homes only: SOGo includes calendars subscribed in its web interface only for clients
    /// that identify as Thunderbird (verified against a SOGo server; without it only own calendars are listed).
    /// </summary>
    public const string ListingUserAgent = "Neruna/0.1 (compatible; Thunderbird/128.0)";

    private readonly AuthenticationHeaderValue _auth = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    public async Task<Multistatus> PropfindAsync(Uri url, int depth, IEnumerable<XName> properties, CancellationToken cancellationToken, string? userAgent = null)
    {
        var body = new XElement(DavNames.Dav + "propfind", new XElement(DavNames.Dav + "prop", properties.Select(p => new XElement(p))));
        return await MultistatusAsync(new HttpMethod("PROPFIND"), url, depth, body, cancellationToken, userAgent);
    }

    public Task<Multistatus> ReportAsync(Uri url, int depth, XElement body, CancellationToken cancellationToken) =>
        MultistatusAsync(new HttpMethod("REPORT"), url, depth, body, cancellationToken);

    public Task<DavHttpResponse> GetAsync(Uri url, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, url, null, null, null, null, cancellationToken);

    /// <param name="ifMatch">ETag the resource must still have; null with <paramref name="create"/> = must not exist.</param>
    public Task<DavHttpResponse> PutAsync(Uri url, string content, string contentType, string? ifMatch, bool create, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, url, content, contentType, null, request =>
        {
            if (create)
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            }
            else if (ifMatch is not null && EntityTagHeaderValue.TryParse(ifMatch, out var tag))
            {
                request.Headers.IfMatch.Add(tag);
            }
        }, cancellationToken);

    public Task<DavHttpResponse> DeleteAsync(Uri url, string? ifMatch, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, url, null, null, null, request =>
        {
            if (ifMatch is not null && EntityTagHeaderValue.TryParse(ifMatch, out var tag))
            {
                request.Headers.IfMatch.Add(tag);
            }
        }, cancellationToken);

    public async Task<DavHttpResponse> SendAsync(
        HttpMethod method,
        Uri url,
        string? content,
        string? contentType,
        int? depth,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, current);
            if (current.Host.Equals(url.Host, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = _auth;
            }

            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (depth is { } d)
            {
                request.Headers.Add("Depth", d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (content is not null)
            {
                request.Content = new StringContent(content, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/xml; charset=utf-8");
            }

            configure?.Invoke(request);

            using var response = await SendWithRetryAsync(request, method, cancellationToken);
            var status = response.StatusCode;
            logger.LogDebug("DAV {Method} {Url} → {Status}", method, current, (int)status);

            if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && response.Headers.Location is { } location && hop < MaxRedirects)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (status == HttpStatusCode.Unauthorized)
            {
                throw new AuthenticationException(F("Anmeldung am DAV-Server {0} fehlgeschlagen (Benutzername/Passwort prüfen).", current.Host));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("DAV response body from {Url}:\n{Body}", current, body);
            }

            var dav = response.Headers.TryGetValues("DAV", out var values) ? string.Join(", ", values) : null;
            return new DavHttpResponse(status, current, response.Headers.ETag?.ToString(), body, dav);
        }
    }

    /// <summary>
    /// Retries reads and deletes when the connection drops mid-response (flaky networks, small WSGI servers).
    /// PUT is not retried: a retried create could report a false conflict after the first attempt succeeded.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, HttpMethod method, CancellationToken cancellationToken)
    {
        var retryable = method != HttpMethod.Put && method != HttpMethod.Post;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await http.SendAsync(attempt == 1 ? request : await CloneAsync(request), HttpCompletionOption.ResponseContentRead, cancellationToken);
            }
            catch (HttpRequestException ex) when (retryable && attempt < MaxAttempts && ex.InnerException is IOException)
            {
                logger.LogDebug(ex, "DAV {Method} {Url}: connection dropped, retrying ({Attempt}/{Max})", method, request.RequestUri, attempt, MaxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }
    }

    // A request message can only be sent once.
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            var body = await original.Content.ReadAsStringAsync();
            clone.Content = new StringContent(body, Encoding.UTF8);
            clone.Content.Headers.ContentType = original.Content.Headers.ContentType;
        }

        return clone;
    }

    private async Task<Multistatus> MultistatusAsync(HttpMethod method, Uri url, int depth, XElement body, CancellationToken cancellationToken, string? userAgent = null)
    {
        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), body).ToString(SaveOptions.DisableFormatting);
        var configure = userAgent is null ? null : new Action<HttpRequestMessage>(request =>
        {
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        });
        var response = await SendAsync(method, url, xml, "application/xml; charset=utf-8", depth, configure, cancellationToken);
        if (response.Status != HttpStatusCode.MultiStatus)
        {
            throw new DavException(response.Status, $"{method} {response.Url} returned {(int)response.Status} {response.Status}.");
        }

        return ParseMultistatus(response.Body, response.Url);
    }

    internal static Multistatus ParseMultistatus(string xml, Uri baseUrl)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var root = XDocument.Load(reader).Root ?? throw new FormatException("Empty multistatus.");

        var resources = new List<DavResource>();
        foreach (var response in root.Elements(DavNames.Dav + "response"))
        {
            var href = response.Element(DavNames.Dav + "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var properties = new Dictionary<XName, XElement>();
            foreach (var propstat in response.Elements(DavNames.Dav + "propstat"))
            {
                if (ParseStatus(propstat.Element(DavNames.Dav + "status")?.Value) != HttpStatusCode.OK)
                {
                    continue;
                }

                foreach (var property in propstat.Element(DavNames.Dav + "prop")?.Elements() ?? [])
                {
                    properties[property.Name] = property;
                }
            }

            var status = ParseStatus(response.Element(DavNames.Dav + "status")?.Value) ?? HttpStatusCode.OK;
            resources.Add(new DavResource(new Uri(baseUrl, href), status, properties));
        }

        return new Multistatus(resources, root.Element(DavNames.Dav + "sync-token")?.Value.Trim());
    }

    // "HTTP/1.1 200 OK" → 200
    private static HttpStatusCode? ParseStatus(string? statusLine)
    {
        var parts = statusLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 2 } && int.TryParse(parts[1], out var code) ? (HttpStatusCode)code : null;
    }
}
