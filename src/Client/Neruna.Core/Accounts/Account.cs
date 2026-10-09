namespace Neruna.Core.Accounts;

/// <summary>
/// What the user perceives as "an account": an identity with any number of connected services.
/// A typical SOGo account has one mail, one calendar and one contacts connection; an "Internet calendars"
/// account may only hold ICS subscriptions.
/// </summary>
/// <param name="DisplayName">The sender name in mails ("Anna Muster").</param>
/// <param name="Label">What Neruna calls the account (folder tree, lists), e.g. "Privat"; null = the e-mail address.</param>
/// <param name="Aliases">Further sender addresses of the same mailbox (the mail server has to allow them).</param>
/// <param name="CloudId">Set up by the organisation in Neruna Cloud (its id there): server settings and aliases come
/// from there and are read-only here; the account goes when it is no longer assigned.</param>
public sealed record Account(
    Guid Id,
    string DisplayName,
    string? EmailAddress,
    IReadOnlyList<ServiceConnection> Connections,
    string? Label = null,
    IReadOnlyList<MailIdentity>? Aliases = null,
    string? CloudId = null)
{
    public bool IsFromCloud => CloudId is not null;

    /// <summary>Who can send from this account: the main address first, then the aliases.</summary>
    public IReadOnlyList<MailIdentity> Identities =>
        [.. (EmailAddress is null ? [] : new[] { new MailIdentity(EmailAddress, DisplayName) }), .. Aliases ?? []];

    /// <summary>Whether <paramref name="address"/> is one of this account's own (main address or alias).</summary>
    public bool Owns(string? address) => address is not null && Identities.Any(i => string.Equals(i.Email, address, StringComparison.OrdinalIgnoreCase));

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

/// <summary>A sender: address and the name shown with it ("Anna Muster", "Example AG Support").</summary>
public sealed record MailIdentity(string Email, string DisplayName)
{
    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Email : $"{DisplayName} <{Email}>";
}
