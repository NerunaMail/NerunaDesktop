using Neruna.Core.Auth;

namespace Neruna.Core.Ai;

/// <summary>How Neruna is connected to an AI provider: with the user's own subscription (sign-in) or an API key.</summary>
public enum AiAuthMethod
{
    SignIn,
    ApiKey,
}

/// <summary>A model the user may choose (id as the provider names it, name for the list).</summary>
public sealed record AiModel(string Id, string Name);

/// <param name="Role">"user" or "assistant".</param>
public sealed record AiMessage(string Role, string Text);

/// <summary>One task for the model: what it should do (instructions) and the material (input).</summary>
public sealed record AiRequest(string Instructions, IReadOnlyList<AiMessage> Input);

/// <summary>A message for the user; <see cref="SignInRequired"/> when the connection has to be made again.</summary>
public sealed class AiException(string message, bool signInRequired = false, Exception? inner = null) : Exception(message, inner)
{
    public bool SignInRequired { get; } = signInRequired;
}

/// <summary>
/// An AI provider (OpenAI, later others). It connects the user – with a sign-in that uses their subscription and/or
/// an API key – and keeps the secrets itself (keychain only); the controller keeps which one is used. Requests go
/// straight from this device to the provider; Neruna's servers are never involved.
/// </summary>
public interface IAiProvider
{
    /// <summary>Stable id, stored in the settings (e.g. "openai").</summary>
    string Id { get; }

    /// <summary>Shown in the list of providers (e.g. "OpenAI").</summary>
    string Name { get; }

    /// <summary>Name of the service whose subscription a sign-in uses (e.g. "ChatGPT"); null: no sign-in.</summary>
    string? SignInName { get; }

    /// <summary>Whether an API key can be entered.</summary>
    bool SupportsApiKey { get; }

    /// <summary>Where the user gets an API key.</summary>
    Uri? ApiKeyHelp { get; }

    /// <returns>The account signed in (e.g. its e-mail address), if known.</returns>
    /// <exception cref="AiException">The account cannot be used (e.g. its plan does not allow other apps).</exception>
    Task<string?> SignInAsync(IBrowserLauncher browser, CancellationToken cancellationToken = default);

    /// <summary>Checks the key with the provider and keeps it.</summary>
    /// <exception cref="AiException">The key was refused.</exception>
    Task ConnectApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>Forgets the secret of <paramref name="method"/> (and revokes a sign-in where possible).</summary>
    Task DisconnectAsync(AiAuthMethod method, CancellationToken cancellationToken = default);

    /// <summary>The models available with this connection, in the provider's order (the first is the default).</summary>
    Task<IReadOnlyList<AiModel>> ListModelsAsync(AiAuthMethod method, CancellationToken cancellationToken = default);

    /// <summary>The answer, as it is written (text pieces); ends only when the provider confirmed it complete.</summary>
    /// <exception cref="AiException">Refused, limit reached, interrupted …</exception>
    IAsyncEnumerable<string> StreamAsync(AiAuthMethod method, string model, AiRequest request, CancellationToken cancellationToken = default);
}
