using System.Net;

namespace Neruna.Providers.Dav;

/// <summary>Quick check whether a host offers CalDAV/CardDAV via /.well-known (RFC 6764), used to prefill account setup.</summary>
public static class DavProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    /// <param name="service">"caldav" or "carddav".</param>
    /// <returns>The base URL to configure (https://host/), or null if no host answered like a DAV server.</returns>
    public static async Task<Uri?> FindAsync(HttpClient http, IEnumerable<string> hosts, string service, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(hosts);

        foreach (var host in hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"https://{host}/.well-known/{service}");
                request.Headers.Add("Depth", "0");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                // A redirect, an auth challenge or a multistatus all mean "something DAV-like lives here".
                if (response.StatusCode is HttpStatusCode.MultiStatus or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    or HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    return new Uri($"https://{host}/");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Next candidate.
            }
        }

        return null;
    }
}
