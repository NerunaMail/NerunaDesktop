namespace Neruna.Contracts.Discovery;

/// <summary>Response of <c>GET /api/v1/discovery?email=…</c> on a Neruna Cloud/Control server.</summary>
/// <param name="OrganizationName">Name of the tenant that owns the domain.</param>
/// <param name="ServerUrl">Base URL of the Neruna server responsible for this domain.</param>
public sealed record DiscoveryResponse(
    string OrganizationName,
    Uri ServerUrl,
    MailProviderConfig Provider);
