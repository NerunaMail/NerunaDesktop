using System.Net;
using Microsoft.Extensions.Logging;

namespace Neruna.Core.Diagnostics;

/// <summary>
/// In every HTTP client of the app: requests to Neruna's own servers (neruna.org, neruna.cloud) say who they are
/// ("Neruna-Desktop/0.13"), so they can be told apart from scanners in the server logs. Other hosts – a mail's
/// pictures, the user's servers – get no user agent, as before: nothing that tells a sender which program reads.
/// With a logger (desktop), a "too many requests" or "unavailable" answer is logged in detail – who answered
/// (Server, Via, Retry-After …) and the start of the body – to find where such limits come from.
/// </summary>
public sealed partial class NerunaHttpHandler(string userAgent, ILogger? logger = null) : DelegatingHandler
{
    private static readonly string[] OwnHosts = ["neruna.org", "neruna.cloud"];

    // Response headers worth knowing (no cookies, nothing secret).
    private static readonly string[] Telling = ["Server", "Via", "Retry-After", "X-Powered-By", "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-Cache", "CF-Ray", "X-Served-By"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsOwn(request.RequestUri) && request.Headers.UserAgent.Count == 0)
        {
            request.Headers.UserAgent.ParseAdd(userAgent);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (logger is not null && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            await LogAsync(logger, request, response, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static bool IsOwn(Uri? uri) =>
        uri is not null && OwnHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    private static async Task LogAsync(ILogger logger, HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var headers = string.Join("; ", Telling
            .Select(name => response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? $"{name}: {string.Join(", ", values)}" : null)
            .OfType<string>());
        string body;
        try
        {
            // Buffered, so the caller can still read it.
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).ReplaceLineEndings(" ");
            body = body.Length > 300 ? body[..300] + "…" : body;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            body = "(not readable: " + ex.Message + ")";
        }

        // Path without query: it may carry ids, never needed to tell who answered.
        var target = request.RequestUri is { } uri ? uri.GetLeftPart(UriPartial.Path) : "?";
        Log429(logger, (int)response.StatusCode, request.Method.Method, target, headers.Length > 0 ? headers : "(none)", body);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP {Status} for {Method} {Target} – headers: {Headers} – body: {Body}")]
    private static partial void Log429(ILogger logger, int status, string method, string target, string headers, string body);
}
