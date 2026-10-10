using System.Net;
using Neruna.Providers.Dav;

namespace Neruna.Client.Tests;

public sealed class DavProbeTests
{
    /// <summary>Answers by URL; records whether a password was ever sent.</summary>
    private sealed class FakeServer(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
    {
        public bool SawCredentials { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SawCredentials |= request.Headers.Authorization is not null;
            return Task.FromResult(routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private static HttpResponseMessage Challenge() => new(HttpStatusCode.Unauthorized);

    private static async Task<(DavProbeResult? Result, bool SawCredentials)> ProbeAsync(Dictionary<string, Func<HttpResponseMessage>> routes, params string[] hosts)
    {
        var server = new FakeServer(routes);
        using var http = new HttpClient(server);
        return (await DavProbe.FindAsync(http, hosts, "caldav", TestContext.Current.CancellationToken), server.SawCredentials);
    }

    [Fact]
    public async Task A_redirect_on_the_same_host_keeps_the_host()
    {
        var (result, _) = await ProbeAsync(new()
        {
            ["https://example.com/.well-known/caldav"] = () => Redirect("/remote.php/dav/"),
            ["https://example.com/remote.php/dav/"] = Challenge,
        }, "example.com");

        Assert.Equal("https://example.com/", result!.Url.AbsoluteUri);
        Assert.False(result.IsElsewhere);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_reported_with_its_target_and_no_password_goes_there()
    {
        var (result, sawCredentials) = await ProbeAsync(new()
        {
            ["https://example.com/.well-known/caldav"] = () => Redirect("https://dav.example.net/remote.php/dav/"),
            ["https://dav.example.net/remote.php/dav/"] = Challenge,
        }, "example.com");

        Assert.Equal("https://dav.example.net/remote.php/dav/", result!.Url.AbsoluteUri);
        Assert.Equal("example.com", result.ProbedHost);
        Assert.True(result.IsElsewhere);
        Assert.False(sawCredentials);
    }

    [Fact]
    public async Task A_redirect_to_plain_http_or_nowhere_is_not_used()
    {
        var (toHttp, _) = await ProbeAsync(new()
        {
            ["https://example.com/.well-known/caldav"] = () => Redirect("http://dav.example.com/"),
            ["http://dav.example.com/"] = Challenge,
        }, "example.com");
        var (toNothing, _) = await ProbeAsync(new()
        {
            ["https://example.com/.well-known/caldav"] = () => Redirect("https://dav.example.com/gone/"),
        }, "example.com");

        Assert.Null(toHttp);
        Assert.Null(toNothing);
    }

    [Fact]
    public async Task The_next_host_is_asked_when_the_first_has_nothing()
    {
        var (result, _) = await ProbeAsync(new()
        {
            ["https://mail.example.com/.well-known/caldav"] = () => new HttpResponseMessage((HttpStatusCode)207),
        }, "example.com", "mail.example.com");

        Assert.Equal("https://mail.example.com/", result!.Url.AbsoluteUri);
        Assert.Equal("mail.example.com", result.ProbedHost);
    }
}
