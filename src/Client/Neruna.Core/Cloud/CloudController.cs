using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Core.Security;

namespace Neruna.Core.Cloud;

/// <summary>This computer's connection to a Neruna Cloud/Control server (kept in the settings; the key in the keychain).</summary>
public sealed record CloudConnection(Uri Server, string DeviceId, string OrganizationName, string MemberName, DateTimeOffset ConnectedAt);

/// <summary>The server refused or could not be reached; <see cref="Exception.Message"/> is for the user (German).</summary>
public sealed class CloudException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Neruna Cloud/Control for the UI: connect with the one-time code and PIN from the portal, read the own profile,
/// disconnect. The device's private key never leaves this computer; the server knows only its public half.
/// </summary>
public sealed class CloudController(HttpClient http, ISettingsStore settings, ICredentialStore credentials, ILogger<CloudController> logger)
{
    /// <summary>Where the device key is kept in the credential store (not a mail/DAV connection).</summary>
    public static readonly Guid KeyId = new("6e657275-6e61-436c-6f75-640000000001");

    /// <summary>Where this device's encryption key for certificates is kept (P-256 ECDH, PKCS#8).</summary>
    public static readonly Guid EncryptionKeyId = new("6e657275-6e61-436c-6f75-640000000002");

    /// <summary>Language of the server's messages: the app's UI language.</summary>
    public const string UiLanguage = "de-CH";

    private string? _token;
    private DateTimeOffset _tokenExpires;

    public async Task<CloudConnection?> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        var json = await settings.GetAsync(SettingKeys.CloudConnection, cancellationToken);
        try
        {
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<CloudConnection>(json, NerunaJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored cloud connection unreadable");
            return null;
        }
    }

    /// <summary>The server address as typed: https required (http only for this computer, for development).</summary>
    public static Uri ParseServer(string text)
    {
        var value = (text ?? string.Empty).Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new CloudException("invalid_server", "Bitte die Serveradresse prüfen (https://…).");
        }

        return uri;
    }

    /// <summary>Redeems code and PIN: creates the device key, registers it, remembers the connection.</summary>
    public async Task<CloudConnection> ConnectAsync(Uri server, string code, string pin, DeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(device);
        using var key = DeviceKey.Create();
        var request = new EnrollmentRequest(code.Trim(), pin.Trim(), key.PublicKeyPem, device.Name, device.AppVersion, device.OsName, device.OsVersion, device.OsUser);
        var response = await SendAsync<EnrollmentResponse>(() => new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/v1/enrollment"))
        {
            Content = JsonContent.Create(request, options: NerunaJson.Options),
        }, cancellationToken);

        // The key is only kept once the server has accepted it.
        await credentials.SetSecretAsync(KeyId, key.ExportPrivateKey(), cancellationToken);
        var connection = new CloudConnection(server, response.DeviceId, response.OrganizationName, response.MemberName, DateTimeOffset.Now);
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(connection, NerunaJson.Options), cancellationToken);
        _token = null;
        logger.LogInformation("Connected to {Server} as device {Device}", server.Host, response.DeviceId);
        return connection;
    }

    /// <summary>The connected person, organisation and add-ons.</summary>
    public async Task<MeResponse> GetProfileAsync(CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<MeResponse>("api/v1/me", cancellationToken);

    /// <summary>The organisation's central signatures, filled in for this person.</summary>
    public async Task<SignaturesResponse> GetSignaturesAsync(CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<SignaturesResponse>("api/v1/signatures", cancellationToken);

    /// <summary>The organisation's text templates, filled in for this person.</summary>
    public async Task<TextTemplatesResponse> GetTextTemplatesAsync(CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<TextTemplatesResponse>("api/v1/text-templates", cancellationToken);

    /// <summary>Chat: everything newer than message <paramref name="after"/>; also keeps this person online.</summary>
    public async Task<ChatResponse> GetChatAsync(long after, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<ChatResponse>($"api/v1/chat?after={after}", cancellationToken);

    /// <summary>Sends a chat message to a room or one person; returns it as stored (with its id).</summary>
    public async Task<CloudChatMessage> SendChatMessageAsync(ChatSendRequest request, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<CloudChatMessage>(HttpMethod.Post, "api/v1/chat/messages", request, cancellationToken);

    /// <summary>Marks a chat conversation read up to a message.</summary>
    public async Task MarkChatReadAsync(ChatReadRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await RequireConnectionAsync(cancellationToken);
        using var response = await SendAuthorizedAsync(connection, HttpMethod.Post, "api/v1/chat/read", cancellationToken, request);
        if (!response.IsSuccessStatusCode)
        {
            await ReadAsync<CloudError>(response, cancellationToken);
        }
    }

    /// <summary>Sets the online status others see in the chat.</summary>
    public async Task SetPresenceAsync(string presence, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<PresenceRequest>(HttpMethod.Put, "api/v1/presence", new PresenceRequest(presence), cancellationToken);

    /// <summary>Registers this device's encryption key for certificates; true when an admin has approved it.</summary>
    public async Task<bool> SetDeviceEncryptionKeyAsync(string publicKey, CancellationToken cancellationToken = default) =>
        (await AuthorizedAsync<JsonElement>(HttpMethod.Put, "api/v1/device/encryption-key", new DeviceEncryptionKeyRequest(publicKey), cancellationToken))
        .TryGetProperty("approved", out var approved) && approved.GetBoolean();

    /// <summary>The S/MIME certificates assigned to this person, encrypted for this device.</summary>
    public async Task<CertificatesResponse> GetCertificatesAsync(CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<CertificatesResponse>("api/v1/certificates", cancellationToken);

    /// <summary>The person's photo from the portal, if there is one.</summary>
    public async Task<byte[]?> GetPhotoAsync(CancellationToken cancellationToken = default)
    {
        var connection = await RequireConnectionAsync(cancellationToken);
        using var response = await SendAuthorizedAsync(connection, HttpMethod.Get, "api/v1/me/photo", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await response.EnsureSuccessStatusCode().Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Ends the connection on the server (when reachable) and forgets key and connection here.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (await GetConnectionAsync(cancellationToken) is { } connection)
        {
            try
            {
                using var response = await SendAuthorizedAsync(connection, HttpMethod.Delete, "api/v1/device", cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or CloudException or TaskCanceledException)
            {
                // Unreachable or already blocked in the portal: forgetting it here is what matters.
                logger.LogWarning(ex, "Disconnecting on the server failed");
            }
        }

        await credentials.DeleteSecretAsync(KeyId, cancellationToken);
        await credentials.DeleteSecretAsync(EncryptionKeyId, cancellationToken);
        await settings.SetAsync(SettingKeys.CloudConnection, null, cancellationToken);
        await settings.SetAsync(SettingKeys.CloudOrganizationSigningKey, null, cancellationToken);
        _token = null;
    }

    private async Task<T> AuthorizedAsync<T>(string path, CancellationToken cancellationToken)
    {
        var connection = await RequireConnectionAsync(cancellationToken);
        using var response = await SendAuthorizedAsync(connection, HttpMethod.Get, path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> AuthorizedAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        var connection = await RequireConnectionAsync(cancellationToken);
        using var response = await SendAuthorizedAsync(connection, method, path, cancellationToken, body);
        return await ReadAsync<T>(response, cancellationToken);
    }

    // Bearer token from a signed assertion; renewed shortly before it expires, and once more if the server refuses it.
    private async Task<HttpResponseMessage> SendAuthorizedAsync(CloudConnection connection, HttpMethod method, string path, CancellationToken cancellationToken, object? body = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(connection, cancellationToken);
            var request = new HttpRequestMessage(method, new Uri(connection.Server, path));
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: NerunaJson.Options);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await SendRawAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return response;
            }

            response.Dispose();
            _token = null;
        }
    }

    private async Task<string> TokenAsync(CloudConnection connection, CancellationToken cancellationToken)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires)
        {
            return _token;
        }

        var secret = await credentials.GetSecretAsync(KeyId, cancellationToken)
                     ?? throw new CloudException("no_key", "Der Geräteschlüssel fehlt. Bitte die Cloud-Verbindung neu einrichten.");
        using var key = DeviceKey.Import(secret);
        var tokenUrl = new Uri(connection.Server, "api/v1/token");
        var assertion = key.CreateAssertion(connection.DeviceId, tokenUrl, DateTimeOffset.UtcNow);
        var issued = await SendAsync<TokenResponse>(() => new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = JsonContent.Create(new TokenRequest(assertion), options: NerunaJson.Options),
        }, cancellationToken);

        _token = issued.AccessToken;
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, issued.ExpiresIn - 60));
        return _token;
    }

    private async Task<CloudConnection> RequireConnectionAsync(CancellationToken cancellationToken) =>
        await GetConnectionAsync(cancellationToken) ?? throw new CloudException("not_connected", "Neruna ist nicht mit einer Cloud verbunden.");

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> request, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(request(), cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // The server answers in the language of the UI (German for now; later the UI culture).
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(UiLanguage));
            try
            {
                return await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new CloudException("unreachable", $"Der Server {request.RequestUri?.Host} ist nicht erreichbar ({ex.Message}).", ex);
            }
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(NerunaJson.Options, cancellationToken)
                   ?? throw new CloudException("invalid_response", "Unerwartete Antwort des Servers.");
        }

        CloudError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<CloudError>(NerunaJson.Options, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Not a Neruna error document (proxy page, wrong address …).
        }

        throw error is { Message.Length: > 0 }
            ? new CloudException(error.Error ?? "invalid_request", error.Message)
            : new CloudException("http_" + (int)response.StatusCode, $"Der Server antwortet mit Fehler {(int)response.StatusCode}. Stimmt die Serveradresse?");
    }
}
