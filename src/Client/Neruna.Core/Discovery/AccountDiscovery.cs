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

    /// <summary>The organization's Neruna Cloud/Control server, if the client is enrolled.</summary>
    public Uri? OrganizationServer { get; set; }

    public async Task<DiscoveryResult?> DiscoverAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        var (_, domain) = DiscoveryPlaceholders.SplitAddress(emailAddress);
        var email = Uri.EscapeDataString(emailAddress);

        if (OrganizationServer is { } server)
        {
            var neruna = await TryAsync(() => FetchNerunaAsync(new Uri(server, $"api/v1/discovery?email={email}"), cancellationToken), server.Host, cancellationToken);
            if (neruna is not null)
            {
                return neruna;
            }
        }

        string[] autoconfigUrls =
        [
            $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={email}",
            $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress={email}",
            // Mozilla's ISP database only learns the domain, never the full address.
            $"https://autoconfig.thunderbird.net/v1.1/{domain}",
        ];

        foreach (var url in autoconfigUrls)
        {
            var uri = new Uri(url);
            var result = await TryAsync(() => FetchAutoconfigAsync(uri, cancellationToken), uri.Host, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task<DiscoveryResult> FetchNerunaAsync(Uri url, CancellationToken cancellationToken)
    {
        var response = await http.GetFromJsonAsync<DiscoveryResponse>(url, NerunaJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Empty discovery response.");
        return new DiscoveryResult(response.Provider, response.OrganizationName, response.OrganizationName);
    }

    private async Task<DiscoveryResult> FetchAutoconfigAsync(Uri url, CancellationToken cancellationToken)
    {
        var xml = await http.GetStringAsync(url, cancellationToken);
        return new DiscoveryResult(AutoconfigXml.Parse(xml), url.Host, null);
    }

    private async Task<DiscoveryResult?> TryAsync(Func<Task<DiscoveryResult>> step, string source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StepTimeout);
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or FormatException or System.Xml.XmlException or System.Text.Json.JsonException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Discovery via {Source} failed", source);
            return null;
        }
    }
}
