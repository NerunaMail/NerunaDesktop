namespace Neruna.Core.Accounts;

/// <summary>
/// What the user perceives as "an account": an identity with any number of connected services.
/// A typical SOGo account has one mail, one calendar and one contacts connection; an "Internet calendars"
/// account may only hold ICS subscriptions.
/// </summary>
public sealed record Account(
    Guid Id,
    string DisplayName,
    string? EmailAddress,
    IReadOnlyList<ServiceConnection> Connections)
{
    public IEnumerable<ServiceConnection> ConnectionsOf(ServiceKind kind) => Connections.Where(c => c.Kind == kind);
}

public enum ServiceKind
{
    Mail,
    Calendar,
    Contacts,
}

/// <summary>
/// One service of an account, handled by the provider registered under <see cref="ProviderId"/>.
/// </summary>
/// <param name="ProviderId">E.g. <c>imap</c>, <c>caldav</c>, <c>ics</c>, <c>carddav</c>; see <see cref="Providers.ProviderIds"/>.</param>
/// <param name="Settings">Provider-specific, non-secret settings (hosts, ports, URLs, usernames).
/// Secrets live in <see cref="Security.ICredentialStore"/> under the connection id.</param>
public sealed record ServiceConnection(
    Guid Id,
    ServiceKind Kind,
    string ProviderId,
    IReadOnlyDictionary<string, string> Settings);
