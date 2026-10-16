using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Ews;

/// <summary>An answer from Exchange that is not a success: EWS's response code (e.g. ErrorItemNotFound) and message.</summary>
public sealed class EwsException(string? code, string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public string? Code { get; } = code;

    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Builds and reads EWS XML: messages (<c>m:</c>) and types (<c>t:</c>).</summary>
internal static class X
{
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";
    public static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";

    public static XElement Msg(string name, params object?[] content) => new(M + name, content);

    public static XElement Typ(string name, params object?[] content) => new(T + name, content);

    public static XElement Field(string uri) => Typ("FieldURI", new XAttribute("FieldURI", uri));

    /// <summary>A MAPI property by tag, e.g. <c>Extended(0x1081, "Integer")</c>.</summary>
    public static XElement Extended(int tag, string type) =>
        Typ("ExtendedFieldURI", new XAttribute("PropertyTag", "0x" + tag.ToString("X4", CultureInfo.InvariantCulture)), new XAttribute("PropertyType", type));

    public static XElement ItemId(string id, string? changeKey = null) =>
        Typ("ItemId", new XAttribute("Id", id), changeKey is null ? null : new XAttribute("ChangeKey", changeKey));

    /// <summary>A folder by id, or a well-known one ("inbox", "msgfolderroot", "calendar" …).</summary>
    public static XElement FolderId(string id) =>
        Distinguished.Contains(id)
            ? Typ("DistinguishedFolderId", new XAttribute("Id", id))
            : Typ("FolderId", new XAttribute("Id", id));

    /// <summary>The well-known folder names Neruna uses (EWS DistinguishedFolderIdNameType).</summary>
    public static readonly HashSet<string> Distinguished = new(StringComparer.Ordinal)
    {
        "msgfolderroot", "inbox", "sentitems", "drafts", "deleteditems", "junkemail", "calendar", "contacts",
    };

    public static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ── reading ──

    public static XElement? Get(this XElement? e, string name) => e?.Element(T + name);

    public static string? Val(this XElement? e, string name) => e?.Element(T + name)?.Value;

    public static bool Bool(this XElement? e, string name) => e.Val(name) is { } v && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    public static int Int(this XElement? e, string name) => int.TryParse(e.Val(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static DateTimeOffset? Date(this XElement? e, string name) =>
        DateTimeOffset.TryParse(e.Val(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var v) ? v : null;

    public static string? Id(this XElement? item) => item?.Element(T + "ItemId")?.Attribute("Id")?.Value;

    public static string? ChangeKey(this XElement? item) => item?.Element(T + "ItemId")?.Attribute("ChangeKey")?.Value;

    /// <summary>The value of an extended property in an item (by tag), or null.</summary>
    public static string? ExtendedValue(this XElement? item, int tag) =>
        item?.Elements(T + "ExtendedProperty")
            .FirstOrDefault(p => int.TryParse(p.Element(T + "ExtendedFieldURI")?.Attribute("PropertyTag")?.Value?.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var t) && t == tag)
            ?.Element(T + "Value")?.Value;

    /// <summary>The item (Message, CalendarItem, Contact …) inside a response message's <c>m:Items</c>.</summary>
    public static XElement? FirstItem(this XElement response) => response.Element(M + "Items")?.Elements().FirstOrDefault();

    public static bool IsSuccess(this XElement response) => response.Attribute("ResponseClass")?.Value != "Error";

    public static string? Code(this XElement response) => response.Element(M + "ResponseCode")?.Value;
}

/// <summary>
/// The SOAP transport to one EWS address: the request in an envelope, the answer's response messages back; errors as
/// <see cref="EwsException"/> (a changed item as <see cref="RemoteConflictException"/>). Authentication and the client
/// certificate are part of the <see cref="HttpClient"/> (see <see cref="EwsConnectionFactory"/>); only preemptive Basic is
/// added here. When Exchange is busy it is asked once more after the pause it names.
/// </summary>
internal sealed class EwsClient(HttpClient http, EwsSettings settings, string? password, ILogger logger)
{
    /// <summary>Exchange 2013 and newer (2016, 2019, Subscription Edition); flags and "mark all read" need it.</summary>
    public const string ServerVersion = "Exchange2013";

    public EwsSettings Settings => settings;

    /// <summary>Sends one operation; returns its response messages (successful ones and those with a tolerated error code).</summary>
    /// <exception cref="EwsException">Transport, SOAP or a response message's error not in <paramref name="tolerated"/>.</exception>
    public async Task<IReadOnlyList<XElement>> CallAsync(XElement operation, CancellationToken cancellationToken, params string[] tolerated)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var body = await SendAsync(operation, cancellationToken);
        var responses = body.Descendants(X.M + "ResponseMessages").FirstOrDefault()?.Elements().ToList() ?? [];
        foreach (var response in responses.Where(r => !r.IsSuccess()))
        {
            var code = response.Code();
            if (code is not null && tolerated.Contains(code, StringComparer.Ordinal))
            {
                continue;
            }

            if (code is "ErrorIrresolvableConflict" or "ErrorChangeKeyRequiredForWriteOperations" or "ErrorStaleObject")
            {
                throw new RemoteConflictException();
            }

            throw new EwsException(code, response.Element(X.M + "MessageText")?.Value ?? code ?? "EWS error");
        }

        return responses;
    }

    /// <summary>
    /// An operation with one response (GetUserOofSettings, SetUserOofSettings …): the whole response element, after its
    /// ResponseMessage was checked.
    /// </summary>
    public async Task<XElement> CallSingleAsync(XElement operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var body = await SendAsync(operation, cancellationToken);
        var response = body.Elements().FirstOrDefault() ?? throw new EwsException(null, "EWS: empty response");
        if ((response.Element(X.M + "ResponseMessage") ?? response) is { } message && !message.IsSuccess())
        {
            var code = message.Code();
            throw new EwsException(code, message.Element(X.M + "MessageText")?.Value ?? code ?? "EWS error");
        }

        return response;
    }

    /// <summary>The response messages of an operation on many items, in groups of <paramref name="size"/> (EWS takes about 100–250).</summary>
    public async Task<IReadOnlyList<XElement>> CallInBatchesAsync<T>(IEnumerable<T> items, int size, Func<IReadOnlyList<T>, XElement> operation, CancellationToken cancellationToken, params string[] tolerated)
    {
        var result = new List<XElement>();
        foreach (var chunk in items.Chunk(size))
        {
            result.AddRange(await CallAsync(operation(chunk), cancellationToken, tolerated));
        }

        return result;
    }

    /// <summary>The SOAP body of the answer, for operations whose answer is not a list of response messages (GetUserAvailability).</summary>
    internal async Task<XElement> SendAsync(XElement operation, CancellationToken cancellationToken)
    {
        var envelope = new XElement(X.Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", X.Soap), new XAttribute(XNamespace.Xmlns + "m", X.M), new XAttribute(XNamespace.Xmlns + "t", X.T),
            new XElement(X.Soap + "Header", X.Typ("RequestServerVersion", new XAttribute("Version", ServerVersion))),
            new XElement(X.Soap + "Body", operation));
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + envelope.ToString(SaveOptions.DisableFormatting);
        logger.LogDebug("EWS {Operation}", operation.Name.LocalName);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{X.M.NamespaceName}/{operation.Name.LocalName}\"");
            request.Headers.UserAgent.ParseAdd("Neruna/1.0");
            if (settings.Authentication == EwsAuthentication.Basic)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{password}")));
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new EwsException(null, F("Keine Verbindung zu {0}: {1}", settings.Url.Host, ex.InnerException?.Message ?? ex.Message), null, ex);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                var body = TryParse(text);
                var fault = body?.Element(X.Soap + "Fault");
                if (response.IsSuccessStatusCode && body is not null && fault is null)
                {
                    return body;
                }

                // Busy: wait as told (at most 30 s), once.
                var busy = fault?.Descendants().FirstOrDefault(d => d.Name.LocalName == "ResponseCode")?.Value == "ErrorServerBusy"
                           || response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
                if (busy && attempt == 0)
                {
                    var wait = int.TryParse(fault?.Descendants().FirstOrDefault(d => d.Name.LocalName == "Value" && d.Attribute("Name")?.Value == "BackOffMilliseconds")?.Value,
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(5);
                    await Task.Delay(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30), cancellationToken);
                    continue;
                }

                throw Failure(response, fault, text);
            }
        }
    }

    private static XElement? TryParse(string text)
    {
        try
        {
            return XDocument.Parse(text).Root?.Element(X.Soap + "Body");
        }
        catch (XmlException)
        {
            return null; // an error page (of a proxy …)
        }
    }

    private EwsException Failure(HttpResponseMessage response, XElement? fault, string text)
    {
        var status = response.StatusCode;
        if (fault is not null)
        {
            var code = fault.Descendants().FirstOrDefault(d => d.Name.LocalName == "ResponseCode")?.Value;
            var message = fault.Descendants().FirstOrDefault(d => d.Name.LocalName == "Message")?.Value ?? fault.Element("faultstring")?.Value ?? code ?? status.ToString();
            return new EwsException(code, message, status);
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => new EwsException("Unauthorized", settings.Authentication == EwsAuthentication.CertificateOnly
                ? T("Anmeldung abgelehnt: Der Server verlangt ein Passwort.")
                : T("Anmeldung abgelehnt: Benutzername oder Passwort stimmt nicht. Hinter einem Reverse Proxy hilft manchmal die Anmeldung «Basic»."), status),
            HttpStatusCode.Forbidden => new EwsException("Forbidden", T("Zugriff verweigert (HTTP 403). Verlangt der Server ein Client-Zertifikat, bitte eines wählen."), status),
            HttpStatusCode.NotFound => new EwsException("NotFound", F("Unter {0} gibt es kein EWS (HTTP 404). Üblich ist https://<server>/EWS/Exchange.asmx.", settings.Url.AbsoluteUri), status),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => new EwsException("Redirect",
                F("Der Server leitet weiter nach {0}. Bitte diese Adresse als Server eintragen.", response.Headers.Location?.AbsoluteUri ?? "?"), status),
            HttpStatusCode.BadRequest when text.Contains("certificate", StringComparison.OrdinalIgnoreCase) =>
                new EwsException("ClientCertificate", T("Der Server verlangt ein Client-Zertifikat (HTTP 400)."), status),
            _ => new EwsException(null, F("Exchange antwortet mit HTTP {0} ({1}).", (int)status, response.ReasonPhrase ?? status.ToString()), status),
        };
    }
}
