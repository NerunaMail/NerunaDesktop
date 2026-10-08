using System.Text.Json;
using Neruna.Contracts;
using Neruna.Contracts.Discovery;

namespace Neruna.Shared.Tests;

/// <summary>
/// The Laravel server asserts that it produces exactly api/fixtures/discovery-example.json;
/// here we assert the desktop client reads that same document correctly.
/// </summary>
public class DiscoveryContractTests
{
    [Fact]
    public void Client_reads_server_discovery_response()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "discovery-example.json"));

        var response = JsonSerializer.Deserialize<DiscoveryResponse>(json, NerunaJson.Options)!;

        Assert.Equal("Example AG", response.OrganizationName);
        Assert.Equal(new Uri("https://neruna.example/"), response.ServerUrl);
        Assert.Equal(new MailServerSettings(ServerProtocol.Imap, "mail.example.com", 993, SocketSecurity.SslOnConnect, AuthScheme.PasswordCleartext, "%EMAILADDRESS%"), response.Provider.PreferredIncoming);
        Assert.Equal(new MailServerSettings(ServerProtocol.Smtp, "mail.example.com", 587, SocketSecurity.StartTls, AuthScheme.PasswordCleartext, "%EMAILADDRESS%"), response.Provider.PreferredOutgoing);
        Assert.Equal(new Uri("https://mail.example.com/SOGo/dav/"), response.Provider.CalDav?.ServerUrl);
        Assert.Equal(new Uri("https://mail.example.com/SOGo/dav/"), response.Provider.CardDav?.ServerUrl);
    }

    [Fact]
    public void Client_serialization_matches_server_wire_format()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "discovery-example.json"));
        var response = JsonSerializer.Deserialize<DiscoveryResponse>(json, NerunaJson.Options)!;

        var roundTripped = JsonSerializer.Serialize(response, NerunaJson.Options);

        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(json).RootElement, JsonDocument.Parse(roundTripped).RootElement), roundTripped);
    }
}
