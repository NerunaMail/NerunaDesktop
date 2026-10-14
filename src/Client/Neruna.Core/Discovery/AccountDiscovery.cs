using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Neruna.Contracts;
using Neruna.Contracts.Discovery;

namespace Neruna.Core.Discovery;

/// <param name="Source">Where the configuration came from, shown to the user (e.g. "Neruna Cloud", "autoconfig.example.com").</param>
/// <param name="OrganizationName">Set when a Neruna server of the user's organization answered.</param>
public sealed record DiscoveryResult(MailProviderConfig Config, string Source, string? OrganizationName);

/// <summary>
/// Finds server settings for an email address; see docs/architecture.md, "Konto-Erkennung".
/// Steps not implemented yet: DNS TXT lookup of the Neruna server, DNS SRV (RFC 6186/6764), guessing.
/// </summary>
public sealed class AccountDiscovery(HttpClient http, ILogger<AccountDiscovery> logger)
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Neruna's autoconfig directory: Mozilla's ISPDB plus CalDAV/CardDAV/EWS and providers Mozilla does not know, also
    /// found through the domain's mail servers (MX). Learns only the domain.
    /// </summary>
    public Uri Directory { get; set; } = new("https://autoconfig.neruna.cloud/v1/");

    /// <summary>The organization's Neruna Cloud/Control server, if the client is enrolled.</summary>
    public Uri? OrganizationServer { get; set; }

    public async Task<DiscoveryResult?> DiscoverAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        var (_, domain) = DiscoveryPlaceholders.SplitAddress(emailAddress);
        var email = Uri.EscapeDataString(emailAddress);

        if (OrganizationServer is { } server)
        {
            var neruna = await TryAsync(token => FetchNerunaAsync(new Uri(server, $"api/v1/discovery?email={email}"), token), server.Host, cancellationToken);
            if (neruna is not null)
            {
                return neruna;
            }
        }

        string[] autoconfigUrls =
        [
            $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={email}",
            $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress={email}",
            // Neruna's directory and – if it cannot be reached – Mozilla's ISP database only learn the domain, never the address.
            new Uri(Directory, Uri.EscapeDataString(domain)).AbsoluteUri,
            $"https://autoconfig.thunderbird.net/v1.1/{domain}",
        ];

        foreach (var url in autoconfigUrls)
        {
            var uri = new Uri(url);
            var result = await TryAsync(token => FetchAutoconfigAsync(uri, token), uri.Host, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task<DiscoveryResult> FetchNerunaAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(url, cancellationToken);
        var discovery = await response.Content.ReadFromJsonAsync<DiscoveryResponse>(NerunaJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Empty discovery response.");
        return new DiscoveryResult(discovery.Provider, discovery.OrganizationName, discovery.OrganizationName);
    }

    private async Task<DiscoveryResult> FetchAutoconfigAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(url, cancellationToken);
        return new DiscoveryResult(AutoconfigXml.Parse(await response.Content.ReadAsStringAsync(cancellationToken)), url.Host, null);
    }

    // With Neruna's user agent: the directory tells Neruna's lookups from other programs'.
    private async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", Cloud.DeviceInfo.UserAgent);
        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"HTTP {(int)status}", null, status);
        }

        return response;
    }

    // Each step gets its own time limit, so a server that does not answer does not hold up the next one.
    private async Task<DiscoveryResult?> TryAsync(Func<CancellationToken, Task<DiscoveryResult>> step, string source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StepTimeout);
        try
        {
            return await step(timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or FormatException or System.Xml.XmlException or System.Text.Json.JsonException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Discovery via {Source} failed", source);
            return null;
        }
    }
}
