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
}

/// <summary>What the sign-in yields; kept only in the system keychain (never in the database or logs).</summary>
public sealed record OAuthTokens(
    [property: JsonPropertyName("access")] string AccessToken,
    [property: JsonPropertyName("refresh")] string RefreshToken,
    [property: JsonPropertyName("expires")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("account")] string? Account = null,
    [property: JsonPropertyName("scope")] string? Scope = null)
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
/// The sign-in itself: authorization code with PKCE in the system browser, redirect to a short-lived listener on
/// <c>http://localhost:{port}/</c>, then code for tokens. Refreshing uses the refresh token.
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
        var port = FreePort();
        var redirect = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = endpoints.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["scope"] = string.Join(' ', endpoints.Scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["prompt"] = "select_account",
            ["login_hint"] = loginHint,
        };
        await browser.OpenAsync(new Uri(endpoints.Authorize + "?" + Form(query)), cancellationToken);

        // The browser comes back here once the user signed in (or cancelled); give up after 5 minutes.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException(T("Die Anmeldung wurde nicht abgeschlossen."));
        }

        var result = context.Request.QueryString;
        var ok = result["code"] is { Length: > 0 } && result["state"] == state;
        await RespondAsync(context, ok);
        if (!ok)
        {
            throw new OAuthException(result["error_description"] ?? result["error"] ?? T("Die Anmeldung wurde abgebrochen."));
        }

        return await RequestTokensAsync(endpoints, new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = result["code"],
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        }, null, cancellationToken);
    }

    /// <exception cref="OAuthException">With <see cref="OAuthException.SignInRequired"/> when the user has to sign in again.</exception>
    public Task<OAuthTokens> RefreshAsync(OAuthEndpoints endpoints, OAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return RequestTokensAsync(endpoints, new Dictionary<string, string?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
        }, tokens, cancellationToken);
    }

    private async Task<OAuthTokens> RequestTokensAsync(OAuthEndpoints endpoints, Dictionary<string, string?> grant, OAuthTokens? previous, CancellationToken cancellationToken)
    {
        grant["client_id"] = endpoints.ClientId;
        grant["scope"] = string.Join(' ', ScopesFor(endpoints, previous));
        using var content = new FormUrlEncodedContent(grant.Where(g => g.Value is not null).Select(g => new KeyValuePair<string, string>(g.Key, g.Value!)));
        using var response = await http.PostAsync(endpoints.Token, content, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (!response.IsSuccessStatusCode || body?.AccessToken is null)
        {
            var invalidGrant = body?.Error is "invalid_grant" or "interaction_required";
            throw new OAuthException(
                invalidGrant ? T("Die Anmeldung ist abgelaufen – bitte erneut anmelden.") : F("Anmeldung fehlgeschlagen: {0}", body?.ErrorDescription ?? response.StatusCode.ToString()),
                invalidGrant);
        }

        return new OAuthTokens(
            body.AccessToken,
            body.RefreshToken ?? previous?.RefreshToken ?? throw new OAuthException(T("Der Anbieter hat kein dauerhaftes Anmelde-Token geliefert.")),
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, body.ExpiresIn)),
            previous?.Account,
            body.Scope ?? previous?.Scope ?? string.Join(' ', ScopesFor(endpoints, previous)));
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

    private static async Task RespondAsync(HttpListenerContext context, bool ok)
    {
        var text = ok ? T("Angemeldet – Sie können dieses Fenster schliessen und zu Neruna zurückkehren.") : T("Die Anmeldung wurde abgebrochen.");
        var page = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>Neruna</title><body style=\"font-family:sans-serif;padding:3em\"><h2>Neruna</h2><p>{WebUtility.HtmlEncode(text)}</p></body>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page);
        context.Response.Close();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Form(IReadOnlyDictionary<string, string?> values) =>
        string.Join('&', values.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value!)}"));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope,
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
