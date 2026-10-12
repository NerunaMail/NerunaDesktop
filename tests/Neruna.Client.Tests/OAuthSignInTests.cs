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

/// <summary>A provider with dynamic registration and OpenID Connect (like "Sign in with ChatGPT").</summary>
public class OAuthDynamicRegistrationTests
{
    private sealed class Browser(Func<Uri, Task> visit) : IBrowserLauncher
    {
        public Task? Visits { get; private set; }

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Visits = Task.Run(() => visit(uri), cancellationToken);
            return Task.CompletedTask;
        }
    }

    private static string IdToken(string audience, string? nonce, string issuer = "https://auth.example") =>
        "e30." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["iss"] = issuer, ["aud"] = audience, ["nonce"] = nonce, ["email"] = "anna@example.com",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    private static OAuthEndpoints Endpoints(string clientId, bool registering) =>
        new(new Uri("https://auth.example/authorize"), new Uri("https://auth.example/token"), clientId, ["openid", "email", "offline_access", "use.plan"])
        {
            RedirectHost = "127.0.0.1",
            RedirectPath = "/auth/callback",
            SelectAccount = false,
            Resource = "https://api.example/v1",
            AuthorizeParameters = new Dictionary<string, string> { ["host_id"] = "urn:uuid:1" },
            RegistrationParameters = registering ? new Dictionary<string, string> { ["app_name"] = "Neruna" } : null,
            IssuesClientId = true,
            Issuer = "https://auth.example",
            ScopeOnTokenRequests = false,
        };

    [Fact]
    public async Task The_issued_client_id_is_used_from_then_on_and_the_id_token_must_fit()
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubHttpHandler();
        using var tokenHttp = new HttpClient(stub);
        using var local = new HttpClient();
        string? nonce = null;
        var browser = new Browser(async authorize =>
        {
            var query = HttpUtility.ParseQueryString(authorize.Query);
            Assert.StartsWith("http://127.0.0.1:", query["redirect_uri"], StringComparison.Ordinal);
            Assert.EndsWith("/auth/callback", query["redirect_uri"], StringComparison.Ordinal);
            Assert.Equal(("dynamic", "Neruna", "urn:uuid:1", "https://api.example/v1", null), (query["client_id"], query["app_name"], query["host_id"], query["resource"], query["prompt"]));
            nonce = query["nonce"];
            stub.Responses["https://auth.example/token"] = (HttpStatusCode.OK,
                $$"""{"access_token":"A","refresh_token":"R","expires_in":3600,"scope":"openid email offline_access use.plan","id_token":"{{IdToken("app_42", nonce)}}"}""");
            await local.GetAsync($"{query["redirect_uri"]}?code=C0DE&state={query["state"]}&client_id=app_42", ct);
        });

        var tokens = await new OAuthClient(tokenHttp).SignInAsync(Endpoints("dynamic", registering: true), null, browser, ct);
        await browser.Visits!;
        Assert.Equal(("app_42", "anna@example.com"), (tokens.ClientId, tokens.Account));
        var exchange = Assert.Single(stub.Sent).Body;
        Assert.Contains("client_id=app_42", exchange, StringComparison.Ordinal);
        Assert.Contains("resource=https%3A%2F%2Fapi.example%2Fv1", exchange, StringComparison.Ordinal);
        Assert.DoesNotContain("scope=", exchange, StringComparison.Ordinal);

        // Refreshing: with the issued client id, no scope.
        stub.Responses["https://auth.example/token"] = (HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2","expires_in":3600,"scope":"openid email offline_access use.plan"}""");
        var renewed = await new OAuthClient(tokenHttp).RefreshAsync(Endpoints("dynamic", registering: true), tokens, ct);
        Assert.Equal(("A2", "R2", "app_42", "anna@example.com"), (renewed.AccessToken, renewed.RefreshToken, renewed.ClientId, renewed.Account));
        Assert.Contains("client_id=app_42", stub.Sent[^1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("scope=", stub.Sent[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_nonce_or_a_foreign_client_id_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubHttpHandler();
        using var tokenHttp = new HttpClient(stub);
        using var local = new HttpClient();

        var wrongNonce = new Browser(async authorize =>
        {
            var query = HttpUtility.ParseQueryString(authorize.Query);
            stub.Responses["https://auth.example/token"] = (HttpStatusCode.OK,
                $$"""{"access_token":"A","refresh_token":"R","expires_in":3600,"id_token":"{{IdToken("app_42", "other")}}"}""");
            await local.GetAsync($"{query["redirect_uri"]}?code=C0DE&state={query["state"]}&client_id=app_42", ct);
        });
        await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(tokenHttp).SignInAsync(Endpoints("dynamic", registering: true), null, wrongNonce, ct));

        // Signing in again with the issued id: a callback naming another client is not taken.
        var foreign = new Browser(async authorize =>
        {
            var query = HttpUtility.ParseQueryString(authorize.Query);
            Assert.Null(query["app_name"]);
            await local.GetAsync($"{query["redirect_uri"]}?code=C0DE&state={query["state"]}&client_id=app_other", ct);
        });
        await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(tokenHttp).SignInAsync(Endpoints("app_42", registering: false), null, foreign, ct));
    }
}
