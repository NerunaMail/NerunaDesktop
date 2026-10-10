using System.Net;
using System.Web;
using Neruna.Core.Auth;

namespace Neruna.Client.Tests;

/// <summary>The browser sign-in: PKCE and state, and other requests on the local port do not end it.</summary>
public class OAuthSignInTests
{
    private sealed class Browser(Func<Uri, Task> visit) : IBrowserLauncher
    {
        public Task? Visits { get; private set; }

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Visits = Task.Run(() => visit(uri), cancellationToken); // the user signs in while Neruna waits
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Other_requests_on_the_port_do_not_end_the_sign_in_and_the_state_must_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoints = new OAuthEndpoints(new Uri("https://login.example/authorize"), new Uri("https://login.example/token"), "client-1", ["Mail.ReadWrite", "offline_access"]);
        var stub = new StubHttpHandler();
        stub.Responses["https://login.example/token"] = (HttpStatusCode.OK, """{"access_token":"A","refresh_token":"R","expires_in":3600,"scope":"Mail.ReadWrite"}""");
        using var tokenHttp = new HttpClient(stub);
        using var local = new HttpClient();

        var browser = new Browser(async authorize =>
        {
            var query = HttpUtility.ParseQueryString(authorize.Query);
            Assert.Equal("S256", query["code_challenge_method"]);
            var redirect = query["redirect_uri"]!;
            // Something else asks first (a favicon, another program) …
            Assert.Equal(HttpStatusCode.NotFound, (await local.GetAsync(redirect + "favicon.ico", ct)).StatusCode);
            // … then the provider's answer arrives.
            await local.GetAsync($"{redirect}?code=C0DE&state={query["state"]}", ct);
        });

        var tokens = await new OAuthClient(tokenHttp).SignInAsync(endpoints, null, browser, ct);
        await browser.Visits!;
        Assert.Equal(("A", "R"), (tokens.AccessToken, tokens.RefreshToken));
        Assert.Contains("code_verifier=", Assert.Single(stub.Sent).Body, StringComparison.Ordinal);

        // A forged answer (wrong state) is refused.
        var forged = new Browser(async authorize =>
        {
            var redirect = HttpUtility.ParseQueryString(authorize.Query)["redirect_uri"]!;
            await local.GetAsync($"{redirect}?code=C0DE&state=forged", ct);
        });
        await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(tokenHttp).SignInAsync(endpoints, null, forged, ct));
    }
}
