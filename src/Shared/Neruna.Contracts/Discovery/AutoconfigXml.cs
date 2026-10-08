using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Neruna.Contracts.Discovery;

/// <summary>
/// Reads and writes the Thunderbird autoconfig format (clientConfig v1.1).
/// Neruna servers publish it so that any client benefits; Neruna Desktop consumes it from any provider.
/// </summary>
/// <seealso href="https://wiki.mozilla.org/Thunderbird:Autoconfiguration:ConfigFileFormat"/>
public static class AutoconfigXml
{
    public static MailProviderConfig Parse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return Parse(XDocument.Load(reader));
    }

    public static MailProviderConfig Parse(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "clientConfig")
        {
            throw new FormatException("Not an autoconfig document: missing <clientConfig>.");
        }

        var provider = root.Element("emailProvider")
            ?? throw new FormatException("Autoconfig document has no <emailProvider>.");

        var domain = provider.Element("domain")?.Value.Trim()
            ?? provider.Attribute("id")?.Value.Trim()
            ?? throw new FormatException("Autoconfig document has no domain.");

        var incoming = provider.Elements("incomingServer").Select(ParseMailServer).OfType<MailServerSettings>().ToList();
        var outgoing = provider.Elements("outgoingServer").Select(ParseMailServer).OfType<MailServerSettings>().ToList();

        var dav = root.Elements("calendar").Concat(root.Elements("addressBook"))
            .Select(ParseDavServer)
            .OfType<DavServerSettings>()
            .ToList();

        return new MailProviderConfig(
            domain.ToLowerInvariant(),
            provider.Element("displayName")?.Value.Trim(),
            incoming,
            outgoing,
            dav);
    }

    public static string Write(MailProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var provider = new XElement("emailProvider",
            new XAttribute("id", config.Domain),
            new XElement("domain", config.Domain));

        if (config.DisplayName is { } displayName)
        {
            provider.Add(new XElement("displayName", displayName));
            provider.Add(new XElement("displayShortName", displayName));
        }

        provider.Add(config.IncomingServers.Select(s => WriteMailServer("incomingServer", s)));
        provider.Add(config.OutgoingServers.Select(s => WriteMailServer("outgoingServer", s)));

        var root = new XElement("clientConfig", new XAttribute("version", "1.1"), provider);
        root.Add(config.DavServers.Select(WriteDavServer));

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + root;
    }

    private static MailServerSettings? ParseMailServer(XElement element)
    {
        var protocol = element.Attribute("type")?.Value.ToLowerInvariant() switch
        {
            "imap" => ServerProtocol.Imap,
            "pop3" => ServerProtocol.Pop3,
            "smtp" => ServerProtocol.Smtp,
            _ => (ServerProtocol?)null,
        };
        var host = element.Element("hostname")?.Value.Trim();
        if (protocol is null || string.IsNullOrEmpty(host)
            || !int.TryParse(element.Element("port")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            // Unknown server types (e.g. "exchange") are skipped, not fatal.
            return null;
        }

        var security = element.Element("socketType")?.Value.Trim().ToUpperInvariant() switch
        {
            "SSL" => SocketSecurity.SslOnConnect,
            "STARTTLS" => SocketSecurity.StartTls,
            _ => SocketSecurity.None,
        };

        // A server may list several methods; the first one we understand wins.
        var auth = element.Elements("authentication")
            .Select(a => ParseAuth(a.Value.Trim()))
            .FirstOrDefault(a => a is not null) ?? AuthScheme.PasswordCleartext;

        return new MailServerSettings(
            protocol.Value,
            host,
            port,
            security,
            auth,
            element.Element("username")?.Value.Trim() ?? DiscoveryPlaceholders.EmailAddress);
    }

    private static DavServerSettings? ParseDavServer(XElement element)
    {
        var protocol = element.Attribute("type")?.Value.ToLowerInvariant() switch
        {
            "caldav" => ServerProtocol.CalDav,
            "carddav" => ServerProtocol.CardDav,
            _ => (ServerProtocol?)null,
        };
        if (protocol is null || !Uri.TryCreate(element.Element("serverURL")?.Value.Trim(), UriKind.Absolute, out var url))
        {
            return null;
        }

        return new DavServerSettings(
            protocol.Value,
            url,
            element.Element("username")?.Value.Trim() ?? DiscoveryPlaceholders.EmailAddress);
    }

    private static AuthScheme? ParseAuth(string value) => value switch
    {
        "password-cleartext" or "plain" => AuthScheme.PasswordCleartext,
        "password-encrypted" or "secure" => AuthScheme.PasswordEncrypted,
        "OAuth2" => AuthScheme.OAuth2,
        "none" => AuthScheme.None,
        _ => null,
    };

    private static XElement WriteMailServer(string elementName, MailServerSettings server) =>
        new(elementName,
            new XAttribute("type", server.Protocol.ToString().ToLowerInvariant()),
            new XElement("hostname", server.Host),
            new XElement("port", server.Port.ToString(CultureInfo.InvariantCulture)),
            new XElement("socketType", server.Security switch
            {
                SocketSecurity.SslOnConnect => "SSL",
                SocketSecurity.StartTls => "STARTTLS",
                _ => "plain",
            }),
            new XElement("authentication", server.Authentication switch
            {
                AuthScheme.PasswordEncrypted => "password-encrypted",
                AuthScheme.OAuth2 => "OAuth2",
                AuthScheme.None => "none",
                _ => "password-cleartext",
            }),
            new XElement("username", server.UsernameTemplate));

    private static XElement WriteDavServer(DavServerSettings server) =>
        new(server.Protocol == ServerProtocol.CalDav ? "calendar" : "addressBook",
            new XAttribute("type", server.Protocol.ToString().ToLowerInvariant()),
            new XElement("username", server.UsernameTemplate),
            new XElement("authentication", "http-basic"),
            new XElement("serverURL", server.ServerUrl.AbsoluteUri));
}
