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
/// Quick check whether a domain offers CalDAV/CardDAV (RFC 6764), used to prefill account setup: first its SRV record
/// (<c>_caldavs._tcp</c> with TXT <c>path=</c>), then /.well-known on the given hosts. A server that answers but says
/// in its DAV header (OPTIONS) that it does not offer the service is passed over.
/// Redirects are followed here, without credentials, so that a redirect to another host can be shown to the user –
/// the DAV client itself never sends the password to a host it was redirected to.
/// </summary>
public static class DavProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    private const int MaxRedirects = 5;

    /// <param name="http">A client that does not follow redirects itself.</param>
    /// <param name="service">"caldav" or "carddav".</param>
    /// <param name="domain">The mail domain whose SRV records are asked first; null: only /.well-known.</param>
    /// <param name="dns">DNS lookups (tests pass their own); null: the system's.</param>
    /// <returns>Where the service is, or null if no host answered like a DAV server.</returns>
    public static async Task<DavProbeResult?> FindAsync(HttpClient http, IEnumerable<string> hosts, string service, string? domain = null, IDavDns? dns = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(hosts);

        // Announced in the DNS by the domain's owner: trusted like the domain's own autoconfig (no question about
        // "another host"), the found URL is used as it is.
        if (!string.IsNullOrWhiteSpace(domain))
        {
            IReadOnlyList<Uri> announced = [];
            try
            {
                announced = await DavServiceRecords.CandidatesAsync(dns ?? SystemDavDns.Instance, domain, service, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // DNS too slow: /.well-known still
            }

            foreach (var candidate in announced)
            {
                if (await TryAsync(http, candidate, service, cancellationToken) is { } found)
                {
                    return new DavProbeResult(found, found.Host);
                }
            }
        }

        foreach (var host in hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await TryAsync(http, new Uri($"https://{host}/.well-known/{service}"), service, cancellationToken) is { } found)
            {
                // On the same host the DAV client finds its way via /.well-known again; elsewhere it needs the target itself.
                return new DavProbeResult(found.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ? new Uri($"https://{host}/") : found, host);
            }
        }

        return null;
    }

    private static async Task<Uri?> TryAsync(HttpClient http, Uri url, string service, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            return await FollowAsync(http, url, timeout.Token) is { } found && await OffersAsync(http, found, service, timeout.Token) ? found : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null; // next candidate
        }
    }

    /// <summary>
    /// Whether the server says it offers the service (DAV header of OPTIONS: <c>calendar-access</c> / <c>addressbook</c>).
    /// No answer, a refusal or no DAV header: assumed yes – many servers answer OPTIONS only after signing in.
    /// </summary>
    internal static async Task<bool> OffersAsync(HttpClient http, Uri url, string service, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues("DAV", out var values))
        {
            return true;
        }

        var capabilities = values.SelectMany(v => v.Split(',')).Select(c => c.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return capabilities.Contains(service == "carddav" ? "addressbook" : "calendar-access");
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
