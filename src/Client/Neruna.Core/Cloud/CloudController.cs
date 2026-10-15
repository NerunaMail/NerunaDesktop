using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Cloud;

/// <summary>
/// One of this computer's connections to a Neruna Cloud/Control server – one per organisation (kept in the settings; the
/// keys in the keychain). The first connection made before there could be several keeps its places (<see cref="FirstId"/>):
/// nothing had to be moved. Further ones keep theirs under their own <see cref="Id"/>.
/// </summary>
public sealed record CloudConnection(Uri Server, string DeviceId, string OrganizationName, string MemberName, DateTimeOffset ConnectedAt, string Id = CloudConnection.FirstId)
{
    /// <summary>The connection from <see cref="SettingKeys.CloudConnection"/> (single-connection versions).</summary>
    public const string FirstId = "default";

    public bool IsFirst => Id == FirstId;

    /// <summary>The device key (signs the token assertions).</summary>
    public Guid DeviceKeyId => IsFirst ? CloudController.KeyId : Derived("device-key");

    /// <summary>This device's encryption key for the organisation's certificates and accounts.</summary>
    public Guid EncryptionKeyId => IsFirst ? CloudController.EncryptionKeyId : Derived("encryption-key");

    /// <summary>A setting of this connection ("cloud.accounts" → "cloud.accounts.&lt;id&gt;").</summary>
    public string SettingKey(string key) => IsFirst ? key : $"{key}.{Id}";

    /// <summary>Source of the signatures and text templates that came from this organisation.</summary>
    public string Source => IsFirst ? CloudSources.Cloud : $"{CloudSources.Cloud}:{Id}";

    /// <summary>The local id of something of this organisation (accounts: <c>Account.CloudId</c>).</summary>
    public string LocalId(string remoteId) => IsFirst ? remoteId : $"{Id}:{remoteId}";

    /// <summary>Whether a local id (see <see cref="LocalId"/>) belongs to this connection.</summary>
    public bool Owns(string localId) => IsFirst ? !localId.Contains(':', StringComparison.Ordinal) : localId.StartsWith(Id + ":", StringComparison.Ordinal);

    private Guid Derived(string purpose) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"neruna-cloud|{purpose}|{Id}")).AsSpan(0, 16));
}

/// <summary>Where a signature or text template came from: "cloud" (first connection) or "cloud:&lt;connection id&gt;".</summary>
public static class CloudSources
{
    public const string Cloud = "cloud";

    public static bool IsCloud(string? source) =>
        source is not null && (source == Cloud || source.StartsWith(Cloud + ":", StringComparison.Ordinal));
}

/// <summary>The server refused or could not be reached; <see cref="Exception.Message"/> is for the user (German).</summary>
public sealed class CloudException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Neruna Cloud/Control for the UI: connect with the one-time code and PIN from the portal – also to several
/// organisations, each as a device of its own –, read the own profile, disconnect. The device's private keys never
/// leave this computer; the servers know only their public halves.
/// </summary>
public sealed class CloudController(HttpClient http, ISettingsStore settings, ICredentialStore credentials, ILogger<CloudController> logger)
{
    /// <summary>Where the first connection's device key is kept in the credential store (not a mail/DAV connection).</summary>
    public static readonly Guid KeyId = new("6e657275-6e61-436c-6f75-640000000001");

    /// <summary>Where the first connection's encryption key for certificates is kept (P-256 ECDH, PKCS#8).</summary>
    public static readonly Guid EncryptionKeyId = new("6e657275-6e61-436c-6f75-640000000002");

    /// <summary>Language of the server's messages: the app's UI language.</summary>
    public static string UiLanguage => Localization.Texts.Culture.Name;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new();

    /// <summary>A connection was added or removed (or the chat's or backups' organisation changed).</summary>
    public event EventHandler? ConnectionsChanged;

    /// <summary>All connections, the first one made first.</summary>
    public async Task<IReadOnlyList<CloudConnection>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CloudConnection>();
        if (Read<CloudConnection>(await settings.GetAsync(SettingKeys.CloudConnection, cancellationToken)) is { } first)
        {
            result.Add(first with { Id = CloudConnection.FirstId });
        }

        result.AddRange(Read<List<CloudConnection>>(await settings.GetAsync(SettingKeys.CloudConnections, cancellationToken)) ?? []);
        return result;
    }

    /// <summary>The first connection (the only one on the phone), or null.</summary>
    public async Task<CloudConnection?> GetConnectionAsync(CancellationToken cancellationToken = default) =>
        (await GetConnectionsAsync(cancellationToken)).FirstOrDefault();

    public async Task<CloudConnection?> GetConnectionAsync(string id, CancellationToken cancellationToken = default) =>
        (await GetConnectionsAsync(cancellationToken)).FirstOrDefault(c => c.Id == id);

    /// <summary>The organisation whose chat is shown (Einstellungen → Cloud); without a choice the first one.</summary>
    public Task<CloudConnection?> GetChatConnectionAsync(CancellationToken cancellationToken = default) =>
        ChosenAsync(SettingKeys.CloudChatConnection, cancellationToken);

    /// <summary>The organisation that keeps the personal backups (Einstellungen → Cloud); without a choice the first one.</summary>
    public Task<CloudConnection?> GetBackupConnectionAsync(CancellationToken cancellationToken = default) =>
        ChosenAsync(SettingKeys.CloudBackupConnection, cancellationToken);

    public Task SetChatConnectionAsync(string id, CancellationToken cancellationToken = default) => ChooseAsync(SettingKeys.CloudChatConnection, id, cancellationToken);

    public Task SetBackupConnectionAsync(string id, CancellationToken cancellationToken = default) => ChooseAsync(SettingKeys.CloudBackupConnection, id, cancellationToken);

    private async Task<CloudConnection?> ChosenAsync(string key, CancellationToken cancellationToken)
    {
        var connections = await GetConnectionsAsync(cancellationToken);
        var chosen = await settings.GetAsync(key, cancellationToken);
        return connections.FirstOrDefault(c => c.Id == chosen) ?? connections.FirstOrDefault();
    }

    private async Task ChooseAsync(string key, string id, CancellationToken cancellationToken)
    {
        await settings.SetAsync(key, id, cancellationToken);
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private T? Read<T>(string? json)
        where T : class
    {
        try
        {
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, NerunaJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored cloud connection unreadable");
            return null;
        }
    }

    // The first connection stays where single-connection versions keep it; further ones go into the list.
    private async Task SaveAsync(IReadOnlyList<CloudConnection> connections, CancellationToken cancellationToken)
    {
        var first = connections.FirstOrDefault(c => c.IsFirst);
        await settings.SetAsync(SettingKeys.CloudConnection, first is null ? null : JsonSerializer.Serialize(first, NerunaJson.Options), cancellationToken);
        var others = connections.Where(c => !c.IsFirst).ToList();
        await settings.SetAsync(SettingKeys.CloudConnections, others.Count == 0 ? null : JsonSerializer.Serialize(others, NerunaJson.Options), cancellationToken);
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
            throw new CloudException("invalid_server", T("Bitte die Serveradresse prüfen (https://…)."));
        }

        return uri;
    }

    /// <summary>
    /// Redeems code and PIN: creates the device key, registers it, remembers the connection – next to the others. Connected
    /// again to the same organisation, the new connection takes the old one's place (what came from it stays).
    /// </summary>
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

        var connections = (await GetConnectionsAsync(cancellationToken)).ToList();
        var previous = connections.FirstOrDefault(c => c.Server == server && c.OrganizationName == response.OrganizationName);
        if (previous is not null)
        {
            await EndOnServerAsync(previous, cancellationToken);
            connections.Remove(previous);
        }

        // The first connection ever keeps the places single-connection versions use.
        var id = previous?.Id ?? (connections.Count == 0 ? CloudConnection.FirstId : Guid.NewGuid().ToString("N"));
        var connection = new CloudConnection(server, response.DeviceId, response.OrganizationName, response.MemberName, DateTimeOffset.Now, id);

        // The key is only kept once the server has accepted it.
        await credentials.SetSecretAsync(connection.DeviceKeyId, key.ExportPrivateKey(), cancellationToken);
        connections.Add(connection);
        await SaveAsync(connections, cancellationToken);
        _tokens.TryRemove(id, out _);
        logger.LogInformation("Connected to {Server} as device {Device}", server.Host, response.DeviceId);
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        return connection;
    }

    /// <summary>The connected person, organisation and add-ons.</summary>
    public async Task<MeResponse> GetProfileAsync(CloudConnection connection, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<MeResponse>(connection, "api/v1/me", cancellationToken);

    /// <summary>The organisation's central signatures, filled in for this person.</summary>
    public async Task<SignaturesResponse> GetSignaturesAsync(CloudConnection connection, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<SignaturesResponse>(connection, "api/v1/signatures", cancellationToken);

    /// <summary>The organisation's text templates, filled in for this person.</summary>
    public async Task<TextTemplatesResponse> GetTextTemplatesAsync(CloudConnection connection, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<TextTemplatesResponse>(connection, "api/v1/text-templates", cancellationToken);

    /// <summary>Chat: everything newer than message <paramref name="after"/>; also keeps this person online.</summary>
    public async Task<ChatResponse> GetChatAsync(CloudConnection connection, long after, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<ChatResponse>(connection, $"api/v1/chat?after={after}", cancellationToken);

    /// <summary>Sends a chat message to a room or one person; returns it as stored (with its id).</summary>
    public async Task<CloudChatMessage> SendChatMessageAsync(CloudConnection connection, ChatSendRequest request, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<CloudChatMessage>(connection, HttpMethod.Post, "api/v1/chat/messages", request, cancellationToken);

    /// <summary>Marks a chat conversation read up to a message.</summary>
    public async Task MarkChatReadAsync(CloudConnection connection, ChatReadRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(connection, HttpMethod.Post, "api/v1/chat/read", cancellationToken, request);
        if (!response.IsSuccessStatusCode)
        {
            await ReadAsync<CloudError>(response, cancellationToken);
        }
    }

    /// <summary>Sets the online status others see in the chat.</summary>
    public async Task SetPresenceAsync(CloudConnection connection, string presence, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<PresenceRequest>(connection, HttpMethod.Put, "api/v1/presence", new PresenceRequest(presence), cancellationToken);

    /// <summary>
    /// Registers this device's encryption key for certificates; true when it is approved (by an admin or, a phone, by
    /// the desktop it was paired from). A paired phone sends its proof with it (see PairingInvitation).
    /// </summary>
    public async Task<bool> SetDeviceEncryptionKeyAsync(CloudConnection connection, string publicKey, string? pairingProof = null, CancellationToken cancellationToken = default) =>
        (await AuthorizedAsync<JsonElement>(connection, HttpMethod.Put, "api/v1/device/encryption-key", new DeviceEncryptionKeyRequest(publicKey, pairingProof), cancellationToken))
        .TryGetProperty("approved", out var approved) && approved.GetBoolean();

    /// <summary>A code + PIN for pairing a phone from this desktop (only as many phones as an admin allowed).</summary>
    public async Task<PairingResponse> StartPairingAsync(CloudConnection connection, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<PairingResponse>(connection, HttpMethod.Post, "api/v1/device/pairings", new { }, cancellationToken);

    public async Task<PairingStatusResponse> GetPairingAsync(CloudConnection connection, string pairingId, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<PairingStatusResponse>(connection, $"api/v1/device/pairings/{Uri.EscapeDataString(pairingId)}", cancellationToken);

    /// <summary>The phones paired from this desktop, with what they still miss.</summary>
    public async Task<PairedDevicesResponse> GetPairedDevicesAsync(CloudConnection connection, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<PairedDevicesResponse>(connection, "api/v1/device/paired", cancellationToken);

    public async Task<PairedDevice> DeliverToPairedDeviceAsync(CloudConnection connection, string deviceId, PairedEnvelopesRequest envelopes, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<PairedDevice>(connection, HttpMethod.Put, $"api/v1/device/paired/{Uri.EscapeDataString(deviceId)}/envelopes", envelopes, cancellationToken);

    /// <summary>The S/MIME certificates assigned to this person, encrypted for this device.</summary>
    public async Task<CertificatesResponse> GetCertificatesAsync(CloudConnection connection, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<CertificatesResponse>(connection, "api/v1/certificates", cancellationToken);

    /// <summary>The mail accounts the organisation set up for this person, encrypted for this device.</summary>
    public async Task<MailAccountsResponse> GetMailAccountsAsync(CloudConnection connection, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<MailAccountsResponse>(connection, "api/v1/mail-accounts", cancellationToken);

    /// <summary>The config vault (wrapped keys) and the list of backups.</summary>
    public async Task<VaultResponse> GetVaultAsync(CloudConnection connection, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<VaultResponse>(connection, "api/v1/vault", cancellationToken);

    /// <summary>Sets up the vault or replaces its wrapped keys.</summary>
    public async Task<CloudVault> SetVaultAsync(CloudConnection connection, VaultUpdateRequest request, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<CloudVault>(connection, HttpMethod.Put, "api/v1/vault", request, cancellationToken);

    /// <summary>Deletes the vault and all backups (start over).</summary>
    public Task DeleteVaultAsync(CloudConnection connection, CancellationToken cancellationToken = default) => SendWithoutAnswerAsync(connection, HttpMethod.Delete, "api/v1/vault", cancellationToken);

    public async Task<VaultBackupSummary> CreateVaultBackupAsync(CloudConnection connection, VaultBackupRequest request, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<VaultBackupSummary>(connection, HttpMethod.Post, "api/v1/vault/backups", request, cancellationToken);

    public async Task<VaultBackupResponse> GetVaultBackupAsync(CloudConnection connection, string id, CancellationToken cancellationToken = default) =>
        await AuthorizedAsync<VaultBackupResponse>(connection, $"api/v1/vault/backups/{Uri.EscapeDataString(id)}", cancellationToken);

    public Task DeleteVaultBackupAsync(CloudConnection connection, string id, CancellationToken cancellationToken = default) =>
        SendWithoutAnswerAsync(connection, HttpMethod.Delete, $"api/v1/vault/backups/{Uri.EscapeDataString(id)}", cancellationToken);

    private async Task SendWithoutAnswerAsync(CloudConnection connection, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(connection, method, path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ReadAsync<CloudError>(response, cancellationToken);
        }
    }

    /// <summary>Where crash reports go when this computer is not connected to a cloud.</summary>
    public static readonly Uri DefaultServer = new("https://neruna.cloud/");

    /// <summary>
    /// A crash report (only with the user's consent): connected, with the device token, so the organisation and device
    /// are known; otherwise – or if the token fails – anonymous.
    /// </summary>
    /// <exception cref="CloudException">Code "invalid": the server refused the report (it won't take it later either).</exception>
    public async Task SendCrashReportAsync(CrashReportRequest report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        const string path = "api/v1/crash-reports";
        var connection = await GetChatConnectionAsync(cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            if (connection is not null)
            {
                try
                {
                    response = await SendAuthorizedAsync(connection, HttpMethod.Post, path, cancellationToken, report);
                }
                catch (CloudException ex)
                {
                    logger.LogInformation(ex, "No device token for the crash report; sending it anonymously");
                }
            }

            if (response is null || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response?.Dispose();
                response = await SendRawAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(connection?.Server ?? DefaultServer, path))
                {
                    Content = JsonContent.Create(report, options: NerunaJson.Options),
                }, cancellationToken);
            }

            if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest)
            {
                throw new CloudException("invalid", F("Der Server antwortet mit Fehler {0}. Stimmt die Serveradresse?", (int)response.StatusCode));
            }

            response.EnsureSuccessStatusCode();
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>The person's photo from the portal, if there is one.</summary>
    public async Task<byte[]?> GetPhotoAsync(CloudConnection connection, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(connection, HttpMethod.Get, "api/v1/me/photo", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await response.EnsureSuccessStatusCode().Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Ends the connection on the server (when reachable) and forgets its keys and the connection here.</summary>
    public async Task DisconnectAsync(CloudConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await EndOnServerAsync(connection, cancellationToken);
        await credentials.DeleteSecretAsync(connection.DeviceKeyId, cancellationToken);
        await credentials.DeleteSecretAsync(connection.EncryptionKeyId, cancellationToken);
        foreach (var key in new[] { SettingKeys.CloudOrganizationSigningKey, SettingKeys.CloudCertificates, SettingKeys.CloudAccountsState })
        {
            await settings.SetAsync(connection.SettingKey(key), null, cancellationToken);
        }

        await SaveAsync([.. (await GetConnectionsAsync(cancellationToken)).Where(c => c.Id != connection.Id)], cancellationToken);
        _tokens.TryRemove(connection.Id, out _);
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task EndOnServerAsync(CloudConnection connection, CancellationToken cancellationToken)
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

    private async Task<T> AuthorizedAsync<T>(CloudConnection connection, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(connection, HttpMethod.Get, path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> AuthorizedAsync<T>(CloudConnection connection, HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
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
                // Buffered, so it goes with a Content-Length: a chunked body larger than the hosting's post_max_size
                // would arrive empty, and the server could not tell why.
                request.Content = JsonContent.Create(body, body.GetType(), options: NerunaJson.Options);
                await request.Content.LoadIntoBufferAsync(cancellationToken);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await SendRawAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return response;
            }

            response.Dispose();
            _tokens.TryRemove(connection.Id, out _);
        }
    }

    private async Task<string> TokenAsync(CloudConnection connection, CancellationToken cancellationToken)
    {
        if (_tokens.TryGetValue(connection.Id, out var cached) && DateTimeOffset.UtcNow < cached.Expires)
        {
            return cached.Token;
        }

        var secret = await credentials.GetSecretAsync(connection.DeviceKeyId, cancellationToken)
                     ?? throw new CloudException("no_key", T("Der Geräteschlüssel fehlt. Bitte die Cloud-Verbindung neu einrichten."));
        using var key = DeviceKey.Import(secret);
        var tokenUrl = new Uri(connection.Server, "api/v1/token");
        var assertion = key.CreateAssertion(connection.DeviceId, tokenUrl, DateTimeOffset.UtcNow);
        var issued = await SendAsync<TokenResponse>(() => new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = JsonContent.Create(new TokenRequest(assertion), options: NerunaJson.Options),
        }, cancellationToken);

        _tokens[connection.Id] = (issued.AccessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, issued.ExpiresIn - 60)));
        return issued.AccessToken;
    }

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
                throw new CloudException("unreachable", F("Der Server {0} ist nicht erreichbar ({1}).", request.RequestUri?.Host, ex.Message), ex);
            }
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(NerunaJson.Options, cancellationToken)
                   ?? throw new CloudException("invalid_response", Localization.Texts.T("Unerwartete Antwort des Servers."));
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
            : new CloudException("http_" + (int)response.StatusCode, F("Der Server antwortet mit Fehler {0}. Stimmt die Serveradresse?", (int)response.StatusCode));
    }
}
