using System.Net;

namespace Neruna.Providers.Dav;

/// <param name="Url">The base URL to configure.</param>
/// <param name="ProbedHost">The host that was asked; differs from <paramref name="Url"/>'s host when it redirected elsewhere.</param>
public sealed record DavProbeResult(Uri Url, string ProbedHost)
{
    /// <summary>The service lives on another host than the one asked: the user decides whether to send the password there.</summary>
    public bool IsElsewhere => !Url.Host.Equals(ProbedHost, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Quick check whether a host offers CalDAV/CardDAV via /.well-known (RFC 6764), used to prefill account setup.
/// Redirects are followed here, without credentials, so that a redirect to another host can be shown to the user –
/// the DAV client itself never sends the password to a host it was redirected to.
/// </summary>
public static class DavProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    private const int MaxRedirects = 5;

    /// <param name="http">A client that does not follow redirects itself.</param>
    /// <param name="service">"caldav" or "carddav".</param>
    /// <returns>Where the service is, or null if no host answered like a DAV server.</returns>
    public static async Task<DavProbeResult?> FindAsync(HttpClient http, IEnumerable<string> hosts, string service, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(hosts);

        foreach (var host in hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                if (await FollowAsync(http, new Uri($"https://{host}/.well-known/{service}"), timeout.Token) is { } found)
                {
                    // On the same host the DAV client finds its way via /.well-known again; elsewhere it needs the target itself.
                    return new DavProbeResult(found.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ? new Uri($"https://{host}/") : found, host);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Next candidate.
            }
        }

        return null;
    }

    /// <returns>The URL that answered like a DAV server (after redirects), or null.</returns>
    private static async Task<Uri?> FollowAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), current);
            request.Headers.Add("Depth", "0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme != Uri.UriSchemeHttps)
                {
                    // Never downgrade: the password would travel unencrypted.
                    return null;
                }

                current = next;
                continue;
            }

            // An auth challenge or a multistatus means "something DAV-like lives here".
            return response.StatusCode is HttpStatusCode.MultiStatus or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? current
                : null;
        }

        return null;
    }
}
