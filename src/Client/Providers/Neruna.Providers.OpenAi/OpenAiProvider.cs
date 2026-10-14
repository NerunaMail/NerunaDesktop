using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neruna.Core;
using Neruna.Core.Ai;
using Neruna.Core.Auth;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.OpenAi;

/// <summary>
/// OpenAI: "Sign in with ChatGPT" uses the user's ChatGPT plan (Plus/Pro) – the open-source flow, where each
/// installation registers itself on its first sign-in and keeps the client id it gets
/// (https://developers.openai.com/siwc/token-sharing-open-source) – or an API key (billed to the key's account).
/// Both go to the Responses API with <c>store: false</c> (nothing kept at OpenAI) and streaming.
/// </summary>
public sealed class OpenAiProvider(HttpClient http, ICredentialStore credentials, ISettingsStore settings) : IAiProvider
{
    // Keychain entries of this provider (fixed ids: one connection per installation).
    internal static readonly Guid SignInSecret = new("4e7c1f62-9a3b-4d0e-8f21-6b5a0c3d9e01");
    internal static readonly Guid ApiKeySecret = new("4e7c1f62-9a3b-4d0e-8f21-6b5a0c3d9e02");

    // Not secret (settings, this device only): the installation's id towards OpenAI, and the client id it was issued.
    internal const string HostIdKey = "ai.openai.hostId";
    internal const string ClientIdKey = "ai.openai.clientId";

    private const string Api = "https://api.openai.com/v1";
    private const string PlanScope = "chatgpt.tokens.use.direct";
    private const string DynamicClient = "dynamic_agent_client";

    public string Id => "openai";

    public string Name => "OpenAI";

    public string? SignInName => "ChatGPT";

    public bool SupportsApiKey => true;

    public Uri? ApiKeyHelp => new("https://platform.openai.com/api-keys");

    internal static OAuthEndpoints Endpoints(string clientId, string hostId, bool registering) =>
        new(new Uri("https://auth.openai.com/api/accounts/authorize"), new Uri("https://auth.openai.com/api/accounts/oauth/token"), clientId,
            ["openid", "profile", "email", "offline_access", "resource.invoke", PlanScope])
        {
            RedirectHost = "127.0.0.1",
            RedirectPath = "/auth/callback",
            SelectAccount = false,
            Resource = Api,
            AuthorizeParameters = new Dictionary<string, string> { ["ext_agent_host_id"] = hostId },
            RegistrationParameters = registering ? new Dictionary<string, string> { ["agent_name_hint"] = "Neruna" } : null,
            IssuesClientId = true,
            Issuer = "https://auth.openai.com",
            ScopeOnTokenRequests = false,
        };

    public async Task<string?> SignInAsync(IBrowserLauncher browser, CancellationToken cancellationToken = default)
    {
        var hostId = await HostIdAsync(cancellationToken);
        var issued = await settings.GetAsync(ClientIdKey, cancellationToken);
        OAuthTokens tokens;
        try
        {
            tokens = await new OAuthClient(http).SignInAsync(Endpoints(issued ?? DynamicClient, hostId, registering: issued is null), null, browser, cancellationToken);
        }
        catch (OAuthException ex) when (issued is not null)
        {
            // The app id OpenAI issued earlier may be gone (app removed in ChatGPT, disconnected): the next attempt
            // registers Neruna anew.
            await settings.SetAsync(ClientIdKey, null, cancellationToken);
            throw new AiException(ex.Message + " " + T("Bitte nochmals «Mit ChatGPT anmelden» – Neruna meldet sich dann neu bei OpenAI an."), false, ex);
        }
        if (tokens.ClientId is { } clientId)
        {
            await settings.SetAsync(ClientIdKey, clientId, cancellationToken);
        }

        // Signing in proves who it is; using the plan is a separate grant (Plus/Pro, and the user agreed to it).
        if (!tokens.Grants(PlanScope))
        {
            throw new AiException(T("Dieses ChatGPT-Konto kann sein Abo nicht in anderen Apps nutzen (nötig ist ChatGPT Plus oder Pro). Alternativ einen API-Schlüssel verwenden."));
        }

        await OAuthTokenSource.StoreAsync(credentials, SignInSecret, tokens, cancellationToken);
        return tokens.Account;
    }

    public async Task ConnectApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        // Checked by listing the models: a wrong key is refused at once, not at the first task.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AiException(T("Der API-Schlüssel wurde abgelehnt."));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorOfAsync(response, AiAuthMethod.ApiKey, cancellationToken);
        }

        await credentials.SetSecretAsync(ApiKeySecret, apiKey, cancellationToken);
    }

    public async Task DisconnectAsync(AiAuthMethod method, CancellationToken cancellationToken = default)
    {
        if (method == AiAuthMethod.SignIn)
        {
            await RevokeAsync(cancellationToken);
        }

        await credentials.DeleteSecretAsync(method == AiAuthMethod.SignIn ? SignInSecret : ApiKeySecret, cancellationToken);
    }

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(AiAuthMethod method, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(method, cancellationToken));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorOfAsync(response, method, cancellationToken);
        }

        return ParseModels(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// With a ChatGPT sign-in: "models" with slug, display name and visibility, in the order to offer them. With an API
    /// key: all of the account's models ("data") – only the text ones, newest first.
    /// </summary>
    internal static IReadOnlyList<AiModel> ParseModels(string json)
    {
        var root = JsonNode.Parse(json);
        if (root?["models"] is JsonArray plan)
        {
            return [.. plan.Where(m => (string?)m?["visibility"] is null or "list")
                .Select(m => new AiModel((string)m!["slug"]!, (string?)m["display_name"] ?? (string)m["slug"]!))];
        }

        string[] notForText = ["audio", "realtime", "transcribe", "tts", "image", "search", "embedding", "moderation", "instruct", "dall-e", "whisper", "computer-use"];
        return [.. (root?["data"] as JsonArray ?? [])
            .Select(m => (string?)m?["id"])
            .OfType<string>()
            .Where(id => (id.StartsWith("gpt-", StringComparison.Ordinal) || (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1])))
                         && !notForText.Any(word => id.Contains(word, StringComparison.Ordinal)))
            .OrderByDescending(id => id, StringComparer.Ordinal)
            .Select(id => new AiModel(id, id))];
    }

    public async IAsyncEnumerable<string> StreamAsync(AiAuthMethod method, string model, AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = request.Instructions,
            ["input"] = new JsonArray([.. request.Input.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray(new JsonObject { ["type"] = m.Role == "assistant" ? "output_text" : "input_text", ["text"] = m.Text }),
            })]),
            ["store"] = false,
            ["stream"] = true,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Api}/responses") { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(method, cancellationToken));
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorOfAsync(response, method, cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var piece in ReadEventsAsync(stream, method, cancellationToken))
        {
            yield return piece;
        }
    }

    /// <summary>
    /// The answer's server-sent events: text deltas until <c>response.completed</c> – the only sign of success; a failed
    /// or incomplete response (even after text came) is an error.
    /// </summary>
    internal static async IAsyncEnumerable<string> ReadEventsAsync(Stream stream, AiAuthMethod method, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data is "" or "[DONE]")
            {
                continue;
            }

            var json = JsonNode.Parse(data);
            switch ((string?)json?["type"])
            {
                case "response.output_text.delta":
                    yield return (string?)json!["delta"] ?? string.Empty;
                    break;
                case "response.completed":
                    yield break;
                case "response.failed":
                    throw ErrorOf((string?)json!["response"]?["error"]?["code"], (string?)json["response"]?["error"]?["message"], method);
                case "error":
                    throw ErrorOf((string?)json!["code"] ?? (string?)json["error"]?["code"], (string?)json["message"] ?? (string?)json["error"]?["message"], method);
                case "response.incomplete":
                    throw new AiException(T("Die Antwort der KI wurde unvollständig abgebrochen."));
            }
        }

        throw new AiException(T("Die Verbindung zur KI brach ab, bevor die Antwort vollständig war."));
    }

    private async Task<string> AccessTokenAsync(AiAuthMethod method, CancellationToken cancellationToken)
    {
        if (method == AiAuthMethod.ApiKey)
        {
            return await credentials.GetSecretAsync(ApiKeySecret, cancellationToken)
                   ?? throw new AiException(T("Kein API-Schlüssel gespeichert – bitte unter Einstellungen → KI neu verbinden."), signInRequired: true);
        }

        var issued = await settings.GetAsync(ClientIdKey, cancellationToken) ?? DynamicClient;
        try
        {
            return await new OAuthTokenSource(new OAuthClient(http), Endpoints(issued, await HostIdAsync(cancellationToken), registering: false), credentials, SignInSecret)
                .GetAccessTokenAsync(cancellationToken);
        }
        catch (OAuthException ex)
        {
            throw new AiException(ex.SignInRequired ? T("Die Verbindung zu ChatGPT ist abgelaufen oder wurde getrennt – bitte unter Einstellungen → KI erneut anmelden.") : ex.Message, ex.SignInRequired, ex);
        }
    }

    // The installation's id towards OpenAI: random, kept on this device (never an e-mail address or user id).
    private async Task<string> HostIdAsync(CancellationToken cancellationToken)
    {
        if (await settings.GetAsync(HostIdKey, cancellationToken) is { Length: > 0 } existing)
        {
            return existing;
        }

        var created = $"urn:uuid:{Guid.NewGuid()}";
        await settings.SetAsync(HostIdKey, created, cancellationToken);
        return created;
    }

    // Revokes the refresh token (endpoint from OpenAI's discovery document); best effort – disconnecting works anyway,
    // and the user can also remove the app in ChatGPT's settings.
    private async Task RevokeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stored = await credentials.GetSecretAsync(SignInSecret, cancellationToken) is { Length: > 0 } json ? JsonSerializer.Deserialize<OAuthTokens>(json) : null;
            if (stored?.ClientId is null)
            {
                return;
            }

            var discovery = await http.GetFromJsonAsync<JsonObject>("https://auth.openai.com/.well-known/openid-configuration", cancellationToken);
            if ((string?)discovery?["revocation_endpoint"] is { } endpoint)
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = stored.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = stored.ClientId,
                });
                using var _ = await http.PostAsync(endpoint, form, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Not reachable: the local secret is deleted all the same.
        }
    }

    private static async Task<AiException> ErrorOfAsync(HttpResponseMessage response, AiAuthMethod method, CancellationToken cancellationToken)
    {
        JsonNode? error = null;
        try
        {
            error = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["error"];
        }
        catch (JsonException)
        {
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized && (string?)error?["code"] is null)
        {
            return method == AiAuthMethod.ApiKey
                ? new AiException(T("Der API-Schlüssel wurde abgelehnt."), signInRequired: true)
                : new AiException(T("Die Verbindung zu ChatGPT ist abgelaufen oder wurde getrennt – bitte unter Einstellungen → KI erneut anmelden."), signInRequired: true);
        }

        return ErrorOf((string?)error?["code"] ?? (response.StatusCode == HttpStatusCode.TooManyRequests ? "rate_limit_exceeded" : null),
            (string?)error?["message"] ?? F("Die KI antwortete mit Fehler {0}.", (int)response.StatusCode), method);
    }

    internal static AiException ErrorOf(string? code, string? message, AiAuthMethod method) => code switch
    {
        "subscription_sharing_usage_limit_exceeded" => new AiException(T("Das Kontingent Ihres ChatGPT-Abos für Neruna ist aufgebraucht. Unter chatgpt.com → Einstellungen → Nutzung lässt sich das Limit pro App anpassen.")),
        "subscription_sharing_usage_unavailable" => new AiException(T("Ihr ChatGPT-Abo kann gerade nicht genutzt werden. Bitte später nochmals versuchen.")),
        "subscription_sharing_user_not_eligible" => new AiException(T("Dieses ChatGPT-Konto kann sein Abo nicht in anderen Apps nutzen (nötig ist ChatGPT Plus oder Pro). Alternativ einen API-Schlüssel verwenden.")),
        "subscription_sharing_invalid_user" => new AiException(T("Die Verbindung zu ChatGPT ist abgelaufen oder wurde getrennt – bitte unter Einstellungen → KI erneut anmelden."), signInRequired: true),
        "insufficient_quota" => new AiException(T("Das Guthaben des API-Kontos ist aufgebraucht.")),
        "rate_limit_exceeded" => new AiException(T("Zu viele Anfragen an die KI – bitte kurz warten und nochmals versuchen.")),
        "invalid_api_key" => new AiException(T("Der API-Schlüssel wurde abgelehnt."), signInRequired: method == AiAuthMethod.ApiKey),
        _ => new AiException(message ?? T("Die KI hat die Anfrage abgelehnt.")),
    };
}
