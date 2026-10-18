using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Auth;

/// <summary>
/// Where and as which app to sign in (OAuth 2.0 for a public client – a desktop app has no secret, RFC 8252).
/// The client id is not secret: security comes from the user's own sign-in, PKCE and the redirect to this computer.
/// </summary>
/// <param name="AddedLater">Scopes added to <paramref name="Scopes"/> after the first release: a sign-in made before
/// lacks them, so its refresh must not ask for them (the provider would refuse and the account would break).</param>
public sealed record OAuthEndpoints(Uri Authorize, Uri Token, string ClientId, IReadOnlyList<string> Scopes, IReadOnlyList<string>? AddedLater = null)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Loopback host of the redirect: "localhost", or "127.0.0.1" where a provider requires it.</summary>
    public string RedirectHost { get; init; } = "localhost";

    /// <summary>Path of the redirect, e.g. "/auth/callback".</summary>
    public string RedirectPath { get; init; } = "/";

    /// <summary>Ask the provider to let the user pick an account (<c>prompt=select_account</c>).</summary>
    public bool SelectAccount { get; init; } = true;

    /// <summary>The API the tokens are for (resource indicator, RFC 8707), sent with every request.</summary>
    public string? Resource { get; init; }

    /// <summary>Further parameters of the authorization request (e.g. an id for this installation).</summary>
    public IReadOnlyDictionary<string, string>? AuthorizeParameters { get; init; }

    /// <summary>
    /// The provider registers the app on its first sign-in and returns a client id of its own with the code
    /// (<see cref="OAuthTokens.ClientId"/>), used from then on; these parameters go along only then (e.g. the app name).
    /// Null: <see cref="ClientId"/> is already the issued one.
    /// </summary>
    public IReadOnlyDictionary<string, string>? RegistrationParameters { get; init; }

    /// <summary>The callback may carry an issued client id (dynamic registration, see <see cref="RegistrationParameters"/>).</summary>
    public bool IssuesClientId { get; init; }

    /// <summary>OpenID Connect: a nonce goes along and is checked in the returned ID token, with audience, issuer and expiry.</summary>
    public string? Issuer { get; init; }

    /// <summary>Whether token requests name the scopes (some providers keep the grant and refuse them on refresh).</summary>
    public bool ScopeOnTokenRequests { get; init; } = true;

    /// <summary>
    /// The app's own redirect address registered with the provider (a custom scheme, e.g. "msal…://auth"), for an
    /// <see cref="IAppRedirectBrowser"/> on a phone. Null: only the loopback listener.
    /// </summary>
    public Uri? AppRedirect { get; init; }
}

/// <summary>What the sign-in yields; kept only in the system keychain (never in the database or logs).</summary>
public sealed record OAuthTokens(
    [property: JsonPropertyName("access")] string AccessToken,
    [property: JsonPropertyName("refresh")] string RefreshToken,
    [property: JsonPropertyName("expires")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("account")] string? Account = null,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("client")] string? ClientId = null)
{
    /// <summary>Whether the provider granted <paramref name="scope"/> (also as full resource URI); unknown counts as no.</summary>
    public bool Grants(string scope) =>
        Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => string.Equals(s, scope, StringComparison.OrdinalIgnoreCase) || s.EndsWith("/" + scope, StringComparison.OrdinalIgnoreCase)) == true;
}

public sealed class OAuthException(string message, bool signInRequired = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The refresh token is no longer valid (revoked, expired, password changed): sign in again.</summary>
    public bool SignInRequired { get; } = signInRequired;
}

/// <summary>Opens a page in the system browser (the desktop app provides it).</summary>
public interface IBrowserLauncher
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

/// <summary>
/// A browser that brings the provider's answer back to the app itself (a phone: the redirect to the app's own scheme
/// opens the app again). Used instead of the loopback listener where the provider has an <see cref="OAuthEndpoints.AppRedirect"/>
/// – a phone may freeze the app while the browser is in front, so a listener in it would not answer.
/// </summary>
public interface IAppRedirectBrowser : IBrowserLauncher
{
    /// <summary>Opens <paramref name="authorize"/>; returns the parameters the redirect to <paramref name="redirect"/> carried.</summary>
    /// <exception cref="OperationCanceledException">The user closed the browser.</exception>
    Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri authorize, Uri redirect, CancellationToken cancellationToken = default);
}

/// <summary>
/// The sign-in itself: authorization code with PKCE in the system browser, redirect to a short-lived listener on this
/// computer (<c>http://localhost:{port}/</c> or as the provider requires), then code for tokens. Refreshing uses the
/// refresh token.
/// </summary>
public sealed class OAuthClient(HttpClient http)
{
    /// <param name="loginHint">The address the user typed, so the provider's page starts with it.</param>
    public async Task<OAuthTokens> SignInAsync(OAuthEndpoints endpoints, string? loginHint, IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(browser);
        if (!endpoints.IsConfigured)
        {
            throw new OAuthException(T("Diese Anmeldung ist in dieser Version von Neruna noch nicht eingerichtet."));
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var nonce = endpoints.Issuer is null ? null : Base64Url(RandomNumberGenerator.GetBytes(16));
        var app = browser is IAppRedirectBrowser appBrowser && endpoints.AppRedirect is { } appRedirect ? (appBrowser, appRedirect) : default;
        using var listener = app.appBrowser is null ? LoopbackListener.Start(endpoints.RedirectHost) : null;
        var redirect = listener is null ? app.appRedirect.OriginalString : $"http://{endpoints.RedirectHost}:{listener.Port}{endpoints.RedirectPath}";

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = endpoints.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            // Without scopes the provider grants what the app is registered for (Zoom).
            ["scope"] = endpoints.Scopes.Count > 0 ? string.Join(' ', endpoints.Scopes) : null,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = nonce,
            ["resource"] = endpoints.Resource,
            ["prompt"] = endpoints.SelectAccount ? "select_account" : null,
            ["login_hint"] = loginHint,
        };
        foreach (var (key, value) in (endpoints.AuthorizeParameters ?? new Dictionary<string, string>()).Concat(endpoints.RegistrationParameters ?? new Dictionary<string, string>()))
        {
            query[key] = value;
        }

        var authorize = new Uri(endpoints.Authorize + "?" + Form(query));
        if (listener is null)
        {
            // The app gets the answer through its own scheme: no listener, no page to answer.
            System.Collections.Specialized.NameValueCollection answer = [];
            try
            {
                foreach (var (key, value) in await app.appBrowser!.AuthenticateAsync(authorize, app.appRedirect, cancellationToken))
                {
                    answer[key] = value;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OAuthException(T("Die Anmeldung wurde abgebrochen."));
            }

            var (appOk, appClient) = Check(answer);
            return await FinishAsync(answer, appOk, appClient);
        }

        await browser.OpenAsync(authorize, cancellationToken);

        // The browser comes back here once the user signed in (or cancelled); give up after 5 minutes.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        // Only the answer of the sign-in counts (code or error on the redirect path); anything else on the port – a
        // favicon request, another program – is turned away and does not end the sign-in.
        LoopbackListener.Request request;
        while (true)
        {
            try
            {
                request = await listener.NextAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OAuthException(T("Die Anmeldung wurde nicht abgeschlossen."));
            }

            if (request.Path == endpoints.RedirectPath && (request.Query["code"] is not null || request.Query["error"] is not null))
            {
                break;
            }

            await request.RespondAsync(404);
            request.Dispose();
        }

        using (request)
        {
            var (ok, clientId) = Check(request.Query);
            await RespondAsync(request, ok);
            return await FinishAsync(request.Query, ok, clientId);
        }

        // The answer: code and state, and – with dynamic registration – the client id.
        (bool Ok, string ClientId) Check(System.Collections.Specialized.NameValueCollection result)
        {
            var ok = result["code"] is { Length: > 0 } && result["state"] == state;
            // A registering sign-in gets the app's own client id back; a later one must not get another.
            var clientId = endpoints.ClientId;
            if (ok && endpoints.IssuesClientId)
            {
                var issued = result["client_id"];
                if (endpoints.RegistrationParameters is not null)
                {
                    ok = !string.IsNullOrEmpty(issued);
                    clientId = issued ?? clientId;
                }
                else if (issued is not null && issued != endpoints.ClientId)
                {
                    ok = false;
                }
            }

            return (ok, clientId);
        }

        Task<OAuthTokens> FinishAsync(System.Collections.Specialized.NameValueCollection result, bool ok, string clientId)
        {
            if (!ok)
            {
                throw new OAuthException(result["error_description"] ?? result["error"] ?? T("Die Anmeldung wurde abgebrochen."));
            }

            return RequestTokensAsync(endpoints, clientId, new Dictionary<string, string?>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = result["code"],
                ["redirect_uri"] = redirect,
                ["code_verifier"] = verifier,
            }, null, nonce, cancellationToken);
        }
    }

    /// <exception cref="OAuthException">With <see cref="OAuthException.SignInRequired"/> when the user has to sign in again.</exception>
    public Task<OAuthTokens> RefreshAsync(OAuthEndpoints endpoints, OAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return RequestTokensAsync(endpoints, tokens.ClientId ?? endpoints.ClientId, new Dictionary<string, string?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
        }, tokens, null, cancellationToken);
    }

    private async Task<OAuthTokens> RequestTokensAsync(OAuthEndpoints endpoints, string clientId, Dictionary<string, string?> grant, OAuthTokens? previous, string? nonce, CancellationToken cancellationToken)
    {
        grant["client_id"] = clientId;
        grant["resource"] = endpoints.Resource;
        if (endpoints.ScopeOnTokenRequests)
        {
            grant["scope"] = string.Join(' ', ScopesFor(endpoints, previous));
        }

        using var content = new FormUrlEncodedContent(grant.Where(g => g.Value is not null).Select(g => new KeyValuePair<string, string>(g.Key, g.Value!)));
        using var response = await http.PostAsync(endpoints.Token, content, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (!response.IsSuccessStatusCode || body?.AccessToken is null)
        {
            // Refreshing: a refused grant means signing in again. Signing in: nothing has expired – the provider's
            // own reason says more (e.g. an account that may not use this).
            var invalidGrant = body?.Error is "invalid_grant" or "interaction_required";
            var reason = body?.ErrorDescription ?? body?.Error ?? response.StatusCode.ToString();
            throw new OAuthException(
                invalidGrant && previous is not null ? T("Die Anmeldung ist abgelaufen – bitte erneut anmelden.") : F("Anmeldung fehlgeschlagen: {0}", reason),
                invalidGrant);
        }

        var account = previous?.Account;
        if (endpoints.Issuer is not null && body.IdToken is not null)
        {
            account = CheckIdToken(body.IdToken, endpoints.Issuer, clientId, nonce) ?? account;
        }

        return new OAuthTokens(
            body.AccessToken,
            body.RefreshToken ?? previous?.RefreshToken ?? throw new OAuthException(T("Der Anbieter hat kein dauerhaftes Anmelde-Token geliefert.")),
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, body.ExpiresIn)),
            account,
            body.Scope ?? previous?.Scope ?? string.Join(' ', ScopesFor(endpoints, previous)),
            endpoints.IssuesClientId ? clientId : previous?.ClientId);
    }

    /// <summary>
    /// The ID token came straight from the token endpoint over TLS (OpenID Connect Core 3.1.3.7: its signature need not
    /// be checked then); issuer, audience, expiry and – at sign-in – the nonce must match.
    /// </summary>
    /// <returns>The signed-in address, if the token has one.</returns>
    private static string? CheckIdToken(string idToken, string issuer, string clientId, string? nonce)
    {
        JsonElement claims;
        try
        {
            var payload = idToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
            claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '='))).RootElement;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException)
        {
            throw new OAuthException(T("Die Anmeldung lieferte eine ungültige Antwort."), inner: ex);
        }

        string? Claim(string name) => claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var audience = claims.TryGetProperty("aud", out var aud)
            ? aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Select(a => a.GetString()).ToList() : [aud.GetString()]
            : [];
        var expired = claims.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds) && DateTimeOffset.FromUnixTimeSeconds(seconds) < DateTimeOffset.UtcNow.AddMinutes(-5);
        if (Claim("iss") != issuer || !audience.Contains(clientId) || expired || (nonce is not null && Claim("nonce") != nonce))
        {
            throw new OAuthException(T("Die Anmeldung lieferte eine ungültige Antwort."));
        }

        return Claim("email");
    }

    // Sign-in: everything. Refresh: what was granted (a refresh may not ask for more); for sign-ins from before the
    // grant was recorded, the scopes of that time.
    private static IEnumerable<string> ScopesFor(OAuthEndpoints endpoints, OAuthTokens? previous)
    {
        if (previous is null)
        {
            return endpoints.Scopes;
        }

        var granted = previous.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      ?? endpoints.Scopes.Except(endpoints.AddedLater ?? [], StringComparer.OrdinalIgnoreCase);
        return granted.Append("offline_access").Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Task RespondAsync(LoopbackListener.Request request, bool ok)
    {
        var text = ok ? T("Angemeldet – Sie können dieses Fenster schliessen und zu Neruna zurückkehren.") : T("Die Anmeldung wurde abgebrochen.");
        return request.RespondAsync(200, $"<!doctype html><meta charset=\"utf-8\"><title>Neruna</title><body style=\"font-family:sans-serif;padding:3em\"><h2>Neruna</h2><p>{WebUtility.HtmlEncode(text)}</p></body>");
    }

    private static string Form(IReadOnlyDictionary<string, string?> values) =>
        string.Join('&', values.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value!)}"));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}

/// <summary>
/// A connection's tokens in the keychain: hands out a valid access token, refreshing (and storing the rotated refresh
/// token) shortly before it expires. One refresh at a time per secret, so parallel syncs do not race.
/// </summary>
public sealed class OAuthTokenSource(OAuthClient client, OAuthEndpoints endpoints, ICredentialStore credentials, Guid secretId)
{
    private static readonly Dictionary<Guid, SemaphoreSlim> Gates = [];

    // The short-lived access token stays in memory only; the keychain keeps the refresh token (smaller, and nothing
    // that works on its own for long).
    private static readonly Dictionary<Guid, OAuthTokens> Current = [];

    public static async Task StoreAsync(ICredentialStore credentials, Guid secretId, OAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(tokens);
        lock (Current)
        {
            Current[secretId] = tokens;
        }

        await credentials.SetSecretAsync(secretId, JsonSerializer.Serialize(tokens with { AccessToken = string.Empty, ExpiresAt = DateTimeOffset.MinValue }), cancellationToken);
    }

    /// <summary>Whether the stored sign-in includes <paramref name="scope"/> (e.g. tasks, added later).</summary>
    public async Task<bool> GrantsAsync(string scope, CancellationToken cancellationToken = default)
    {
        OAuthTokens? stored;
        lock (Current)
        {
            stored = Current.GetValueOrDefault(secretId);
        }

        stored ??= await credentials.GetSecretAsync(secretId, cancellationToken) is { Length: > 0 } json ? JsonSerializer.Deserialize<OAuthTokens>(json) : null;
        return stored?.Grants(scope) == true;
    }

    /// <exception cref="OAuthException">Not signed in, or the sign-in expired.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate;
        lock (Gates)
        {
            if (!Gates.TryGetValue(secretId, out gate!))
            {
                gate = Gates[secretId] = new SemaphoreSlim(1, 1);
            }
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            OAuthTokens? stored;
            lock (Current)
            {
                stored = Current.GetValueOrDefault(secretId);
            }

            stored ??= await credentials.GetSecretAsync(secretId, cancellationToken) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<OAuthTokens>(json)
                : null;
            if (stored is null)
            {
                throw new OAuthException(T("Nicht angemeldet – bitte das Konto erneut verbinden."), signInRequired: true);
            }

            if (stored.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return stored.AccessToken;
            }

            var renewed = await client.RefreshAsync(endpoints, stored, cancellationToken);
            await StoreAsync(credentials, secretId, renewed, cancellationToken);
            return renewed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }
}
