using System.Text.Json.Serialization;

namespace Neruna.Contracts.Discovery;

public enum ServerProtocol
{
    Imap,
    Pop3,
    Smtp,
    CalDav,
    CardDav,
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

/// <summary>A CalDAV or CardDAV endpoint. Neruna only points at it, the data stays on the customer's server.</summary>
public sealed record DavServerSettings(
    ServerProtocol Protocol,
    Uri ServerUrl,
    string UsernameTemplate);

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
}
