namespace Neruna.Core.Accounts;

/// <summary>
/// What the user perceives as "an account": an identity with any number of connected services.
/// A typical SOGo account has one mail, one calendar and one contacts connection; an "Internet calendars"
/// account may only hold ICS subscriptions.
/// </summary>
/// <param name="DisplayName">The sender name in mails ("Anna Muster").</param>
/// <param name="Label">What Neruna calls the account (folder tree, lists), e.g. "Privat"; null = the e-mail address.</param>
public sealed record Account(
    Guid Id,
    string DisplayName,
    string? EmailAddress,
    IReadOnlyList<ServiceConnection> Connections,
    string? Label = null)
{
    /// <summary>The account's name in Neruna: the label, else the e-mail address, else the sender name.</summary>
    public string Title => string.IsNullOrWhiteSpace(Label) ? EmailAddress ?? DisplayName : Label;

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
