using Neruna.Contracts.Discovery;

namespace Neruna.Shared.Tests;

public class AutoconfigXmlTests
{
    // The same fixture is asserted by the Laravel server tests, so both sides agree on the format.
    private static string Fixture => ContractFixtures.Read("autoconfig-example.xml");

    [Fact]
    public void Parse_reads_mail_and_dav_servers_from_contract_fixture()
    {
        var config = AutoconfigXml.Parse(Fixture);

        Assert.Equal("example.com", config.Domain);
        Assert.Equal("Example AG", config.DisplayName);
        Assert.Equal(new MailServerSettings(ServerProtocol.Imap, "mail.example.com", 993, SocketSecurity.SslOnConnect, AuthScheme.PasswordCleartext, "%EMAILADDRESS%"), config.PreferredIncoming);
        Assert.Equal(new MailServerSettings(ServerProtocol.Smtp, "mail.example.com", 587, SocketSecurity.StartTls, AuthScheme.PasswordCleartext, "%EMAILADDRESS%"), config.PreferredOutgoing);
        Assert.Equal(new Uri("https://mail.example.com/SOGo/dav/"), config.CalDav?.ServerUrl);
        Assert.Equal(new Uri("https://mail.example.com/SOGo/dav/"), config.CardDav?.ServerUrl);
    }

    [Fact]
    public void The_directorys_entries_bring_calendar_contacts_and_exchange()
    {
        // Served by autoconfig.neruna.cloud: Mozilla's file with Neruna's additions (also asserted by the server tests).
        var config = AutoconfigXml.Parse(ContractFixtures.Read("autoconfig-directory-example.xml"));

        Assert.Equal(("example-hosting.net", "Example Hosting"), (config.Domain, config.DisplayName));
        Assert.Equal(new MailServerSettings(ServerProtocol.Imap, "imap.example-hosting.net", 993, SocketSecurity.SslOnConnect, AuthScheme.PasswordCleartext, "%EMAILADDRESS%"), config.PreferredIncoming);
        Assert.Equal(new MailServerSettings(ServerProtocol.Smtp, "smtp.example-hosting.net", 465, SocketSecurity.SslOnConnect, AuthScheme.PasswordCleartext, "%EMAILADDRESS%"), config.PreferredOutgoing);
        Assert.Equal(new Uri("https://dav.example-hosting.net/calendars/"), config.CalDav?.ServerUrl);
        Assert.Equal(new Uri("https://dav.example-hosting.net/addressbooks/"), config.CardDav?.ServerUrl);
        Assert.Equal(new DavServerSettings(ServerProtocol.Ews, new Uri("https://exchange.example-hosting.net/EWS/Exchange.asmx"), "%EMAILADDRESS%"), config.Ews);
        Assert.Single(config.IncomingServers); // the exchange server is no IMAP/POP server
    }

    [Fact]
    public void Write_then_parse_round_trips()
    {
        var original = AutoconfigXml.Parse(Fixture);

        var reparsed = AutoconfigXml.Parse(AutoconfigXml.Write(original));

        Assert.Equal(original.Domain, reparsed.Domain);
        Assert.Equal(original.IncomingServers, reparsed.IncomingServers);
        Assert.Equal(original.OutgoingServers, reparsed.OutgoingServers);
        Assert.Equal(original.DavServers, reparsed.DavServers);
    }

    [Fact]
    public void Parse_skips_unknown_server_types_and_prefers_imap()
    {
        const string xml = """
            <clientConfig version="1.1">
              <emailProvider id="Example.org">
                <incomingServer type="exchange"><hostname>ews.example.org</hostname><port>443</port></incomingServer>
                <incomingServer type="pop3"><hostname>pop.example.org</hostname><port>995</port><socketType>SSL</socketType></incomingServer>
                <incomingServer type="imap"><hostname>imap.example.org</hostname><port>143</port><socketType>STARTTLS</socketType>
                  <authentication>GSSAPI</authentication><authentication>password-encrypted</authentication></incomingServer>
              </emailProvider>
            </clientConfig>
            """;

        var config = AutoconfigXml.Parse(xml);

        Assert.Equal("example.org", config.Domain);
        Assert.Equal(2, config.IncomingServers.Count);
        Assert.Equal("imap.example.org", config.PreferredIncoming?.Host);
        Assert.Equal(AuthScheme.PasswordEncrypted, config.PreferredIncoming?.Authentication);
    }

    [Fact]
    public void Parse_rejects_dtd()
    {
        const string xml = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e "boom">]><clientConfig><emailProvider id="a.b"/></clientConfig>""";

        Assert.ThrowsAny<System.Xml.XmlException>(() => AutoconfigXml.Parse(xml));
    }

    [Theory]
    [InlineData("%EMAILADDRESS%", "Anna.Muster@Example.com")]
    [InlineData("%EMAILLOCALPART%", "Anna.Muster")]
    [InlineData("%EMAILLOCALPART%@%EMAILDOMAIN%", "Anna.Muster@example.com")]
    public void Placeholders_expand(string template, string expected)
    {
        Assert.Equal(expected, DiscoveryPlaceholders.Expand(template, "Anna.Muster@Example.com"));
    }
}
