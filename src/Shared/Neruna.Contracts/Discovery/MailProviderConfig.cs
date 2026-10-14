using System.Text.Json.Serialization;

namespace Neruna.Contracts.Discovery;

public enum ServerProtocol
{
    Imap,
    Pop3,
    Smtp,
    CalDav,
    CardDav,

    /// <summary>Exchange Web Services: one URL for mail, calendar and contacts (listed under <see cref="MailProviderConfig.DavServers"/>).</summary>
    Ews,
}

public enum SocketSecurity
{
    None,
    SslOnConnect,
    StartTls,
}

public enum AuthScheme
{
    PasswordCleartext,
    PasswordEncrypted,
    OAuth2,
    None,
}

/// <summary>A mail server (IMAP, POP3 or SMTP) as announced by autoconfig or a Neruna server.</summary>
/// <param name="UsernameTemplate">May contain the placeholders from <see cref="DiscoveryPlaceholders"/>.</param>
public sealed record MailServerSettings(
    ServerProtocol Protocol,
    string Host,
    int Port,
    SocketSecurity Security,
    AuthScheme Authentication,
    string UsernameTemplate);

/// <summary>
/// An endpoint addressed by URL: CalDAV, CardDAV or Exchange Web Services. Neruna only points at it, the data stays on
/// the customer's server.
/// </summary>
public sealed record DavServerSettings(
    ServerProtocol Protocol,
    Uri ServerUrl,
    string UsernameTemplate);

/// <summary>
/// A link of the provider for the user: instructions, support, or what to switch on first (e.g. IMAP access in the
/// webmail). <paramref name="Text"/>: the description per language ("de", "en" …), as the provider wrote it.
/// </summary>
public sealed record ProviderLink(Uri Url, IReadOnlyDictionary<string, string> Text)
{
    /// <summary>The description in <paramref name="language"/>, else English, else any; null without one.</summary>
    public string? TextFor(string language) =>
        Text.TryGetValue(language, out var text) || Text.TryGetValue("en", out text) ? text : Text.Values.FirstOrDefault();
}

/// <summary>Everything a client needs to set up the accounts for one mail domain.</summary>
public sealed record MailProviderConfig(
    string Domain,
    string? DisplayName,
    IReadOnlyList<MailServerSettings> IncomingServers,
    IReadOnlyList<MailServerSettings> OutgoingServers,
    IReadOnlyList<DavServerSettings> DavServers)
{
    [JsonIgnore]
    public MailServerSettings? PreferredIncoming =>
        IncomingServers.FirstOrDefault(s => s.Protocol == ServerProtocol.Imap) ?? (IncomingServers.Count > 0 ? IncomingServers[0] : null);

    [JsonIgnore]
    public MailServerSettings? PreferredOutgoing => OutgoingServers.Count > 0 ? OutgoingServers[0] : null;

    [JsonIgnore]
    public DavServerSettings? CalDav => DavServers.FirstOrDefault(s => s.Protocol == ServerProtocol.CalDav);

    [JsonIgnore]
    public DavServerSettings? CardDav => DavServers.FirstOrDefault(s => s.Protocol == ServerProtocol.CardDav);

    /// <summary>The provider's webmail, to open it from the account settings.</summary>
    public Uri? WebmailUrl { get; init; }

    /// <summary>Instructions and support of the provider (shown when setting up, and with the account).</summary>
    [JsonIgnore]
    public IReadOnlyList<ProviderLink> Documentation { get; init; } = [];

    /// <summary>On the wire only when there is some (as the server leaves it out).</summary>
    [JsonPropertyName("documentation")]
    public IReadOnlyList<ProviderLink>? DocumentationOnWire
    {
        get => Documentation.Count == 0 ? null : Documentation;
        init => Documentation = value ?? [];
    }

    /// <summary>What the user has to switch on before a mail program can connect (e.g. IMAP in the webmail settings).</summary>
    public ProviderLink? Enable { get; init; }

    [JsonIgnore]
    public DavServerSettings? Ews => DavServers.FirstOrDefault(s => s.Protocol == ServerProtocol.Ews);
}
