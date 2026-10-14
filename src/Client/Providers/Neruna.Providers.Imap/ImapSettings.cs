using System.Globalization;
using Neruna.Contracts.Discovery;

namespace Neruna.Providers.Imap;

/// <summary>Typed view of an IMAP connection's <c>Settings</c> dictionary.</summary>
public sealed record ImapSettings(
    string ImapHost,
    int ImapPort,
    SocketSecurity ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    SocketSecurity SmtpSecurity,
    string Username,
    bool AcceptAnyCertificate = false)
{
    private const string ImapHostKey = "imap.host";
    private const string ImapPortKey = "imap.port";
    private const string ImapSecurityKey = "imap.security";
    private const string SmtpHostKey = "smtp.host";
    private const string SmtpPortKey = "smtp.port";
    private const string SmtpSecurityKey = "smtp.security";
    private const string UsernameKey = "username";

    /// <summary>"true": the server files sent mail in "Sent" itself (Proton Mail Bridge) – Neruna adds no copy.</summary>
    public const string ServerSavesSentKey = "imap.serverSavesSent";

    /// <summary>"proton-bridge": set up through Proton Mail Bridge on this computer (the account dialog shows that step).</summary>
    public const string KindKey = "imap.kind";

    public const string ProtonBridgeKind = "proton-bridge";

    /// <summary>All settings of the connection (TLS options, kind …), for what this record does not type.</summary>
    public IReadOnlyDictionary<string, string> Raw { get; init; } = new Dictionary<string, string>();

    public bool ServerSavesSent => Raw.TryGetValue(ServerSavesSentKey, out var value) && value == "true";

    // Equal are the same servers and user; Raw is only carried along.
    public bool Equals(ImapSettings? other) =>
        other is not null && (ImapHost, ImapPort, ImapSecurity, SmtpHost, SmtpPort, SmtpSecurity, Username, AcceptAnyCertificate)
            == (other.ImapHost, other.ImapPort, other.ImapSecurity, other.SmtpHost, other.SmtpPort, other.SmtpSecurity, other.Username, other.AcceptAnyCertificate);

    public override int GetHashCode() => HashCode.Combine(ImapHost, ImapPort, ImapSecurity, SmtpHost, SmtpPort, SmtpSecurity, Username, AcceptAnyCertificate);

    public IReadOnlyDictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        [ImapHostKey] = ImapHost,
        [ImapPortKey] = ImapPort.ToString(CultureInfo.InvariantCulture),
        [ImapSecurityKey] = ImapSecurity.ToString(),
        [SmtpHostKey] = SmtpHost,
        [SmtpPortKey] = SmtpPort.ToString(CultureInfo.InvariantCulture),
        [SmtpSecurityKey] = SmtpSecurity.ToString(),
        [UsernameKey] = Username,
    }.Concat(AcceptAnyCertificate ? [new KeyValuePair<string, string>(Neruna.Core.Security.TlsCertificates.AcceptAnyCertificateKey, "true")] : [])
     .ToDictionary(p => p.Key, p => p.Value);

    public static ImapSettings FromDictionary(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ImapSettings(
            Required(settings, ImapHostKey),
            int.Parse(Required(settings, ImapPortKey), CultureInfo.InvariantCulture),
            Enum.Parse<SocketSecurity>(Required(settings, ImapSecurityKey)),
            Required(settings, SmtpHostKey),
            int.Parse(Required(settings, SmtpPortKey), CultureInfo.InvariantCulture),
            Enum.Parse<SocketSecurity>(Required(settings, SmtpSecurityKey)),
            Required(settings, UsernameKey),
            Neruna.Core.Security.TlsCertificates.AcceptsAnyCertificate(settings)) { Raw = settings };
    }

    /// <summary>Null unless the configuration offers both an IMAP and an SMTP server.</summary>
    public static ImapSettings? FromDiscovery(MailProviderConfig config, string emailAddress)
    {
        ArgumentNullException.ThrowIfNull(config);
        var imap = config.IncomingServers.FirstOrDefault(s => s.Protocol == ServerProtocol.Imap);
        var smtp = config.PreferredOutgoing;
        if (imap is null || smtp is null)
        {
            return null;
        }

        return new ImapSettings(
            imap.Host,
            imap.Port,
            imap.Security,
            smtp.Host,
            smtp.Port,
            smtp.Security,
            DiscoveryPlaceholders.Expand(imap.UsernameTemplate, emailAddress));
    }

    private static string Required(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"IMAP setting '{key}' is missing.");
}
