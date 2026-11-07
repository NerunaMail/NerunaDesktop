using Neruna.Core.Cloud;

namespace Neruna.Client.Tests;

/// <summary>The button in the mail with the connection code: server and code come in, nothing else.</summary>
public class CloudConnectLinkTests
{
    [Fact]
    public void Reads_server_and_code()
    {
        var link = CloudConnectLink.TryParse("neruna://connect?server=https%3A%2F%2Fportal.example.ch&code=k7q2-abcd-efgh-jkmn-pqrs-tvwx-yz01-2345");

        Assert.NotNull(link);
        Assert.Equal("https://portal.example.ch", link.ServerText);
        Assert.Equal("K7Q2-ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345", link.Code);
        Assert.Equal(link, CloudConnectLink.TryParse(link.ToLink()));
    }

    [Theory]
    [InlineData("neruna://connect/?server=https%3A%2F%2Fneruna.cloud%2F", "https://neruna.cloud")]
    [InlineData("NERUNA://CONNECT?server=portal.example.ch%2Fneruna", "https://portal.example.ch/neruna")]
    [InlineData("neruna://connect?server=http%3A%2F%2Flocalhost%3A8080", "http://localhost:8080")]
    public void Accepts_server_forms(string item, string server) => Assert.Equal(server, CloudConnectLink.TryParse(item)?.ServerText);

    [Theory]
    [InlineData("neruna://connect?code=K7Q2")]                                          // no server
    [InlineData("neruna://connect?server=http%3A%2F%2Fevil.example")]                  // plain http elsewhere
    [InlineData("neruna://connect?server=ftp%3A%2F%2Fexample.ch")]
    [InlineData("neruna://contact?uid=1&server=https%3A%2F%2Fexample.ch")]            // a note link, not this
    [InlineData("neruna://connector?server=https%3A%2F%2Fexample.ch")]
    [InlineData("mailto:anna@example.ch")]
    public void Rejects_anything_else(string item) => Assert.Null(CloudConnectLink.TryParse(item));

    [Fact]
    public void Leaves_out_a_code_that_is_none()
    {
        Assert.Null(CloudConnectLink.TryParse("neruna://connect?server=https%3A%2F%2Fexample.ch&code=%3Cscript%3E")?.Code);
        Assert.NotNull(CloudConnectLink.TryParse("neruna://connect?server=https%3A%2F%2Fexample.ch&code=%3Cscript%3E"));
    }
}
