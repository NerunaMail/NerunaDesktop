using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neruna.Core.Auth;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Ai;

/// <summary>Which provider, how connected, as whom, and the chosen model (settings "ai.connection").</summary>
public sealed record AiConnection(
    [property: JsonPropertyName("provider")] string ProviderId,
    [property: JsonPropertyName("method")] AiAuthMethod Method,
    [property: JsonPropertyName("account")] string? Account,
    [property: JsonPropertyName("model")] string? Model);

/// <summary>
/// The UI's way to AI: connect a provider (Settings → KI), and the tasks Neruna offers (summarise …). One provider is
/// connected at a time; the AI functions show only then. What a task sends goes from this device to the provider.
/// </summary>
public sealed class AiController(IEnumerable<IAiProvider> providers, ISettingsStore settings, IBrowserLauncher browser)
{
    public const string ConnectionKey = "ai.connection";

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public IReadOnlyList<IAiProvider> Providers { get; } = [.. providers];

    public AiConnection? Connection { get; private set; }

    public bool IsConnected => Connection is not null && Provider(Connection.ProviderId) is not null;

    /// <summary>Connected, disconnected, model changed.</summary>
    public event EventHandler? Changed;

    public IAiProvider? Provider(string id) => Providers.FirstOrDefault(p => p.Id == id);

    private const string ConfirmWholeMailKey = "ai.confirmWholeMail";

    /// <summary>
    /// Ask before the whole mail goes to the AI (nothing marked, or "Schreiben" with text or a quoted message). On by
    /// default; whoever turns it off under Einstellungen → KI does so knowingly.
    /// </summary>
    public bool ConfirmWholeMail { get; private set; } = true;

    public async Task SetConfirmWholeMailAsync(bool confirm, CancellationToken cancellationToken = default)
    {
        ConfirmWholeMail = confirm;
        await settings.SetBoolAsync(ConfirmWholeMailKey, confirm, cancellationToken);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ConfirmWholeMail = await settings.GetBoolAsync(ConfirmWholeMailKey, fallback: true, cancellationToken);
        var stored = await settings.GetAsync(ConnectionKey, cancellationToken);
        try
        {
            Connection = string.IsNullOrEmpty(stored) ? null : JsonSerializer.Deserialize<AiConnection>(stored, Json);
        }
        catch (JsonException)
        {
            Connection = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sign in with the provider's subscription (browser); replaces another connection.</summary>
    /// <param name="via">Opens the sign-in page instead of the default browser launcher (e.g. one that also shows the address).</param>
    public async Task SignInAsync(string providerId, IBrowserLauncher? via = null, CancellationToken cancellationToken = default)
    {
        var provider = Provider(providerId) ?? throw new ArgumentException("Unknown AI provider.", nameof(providerId));
        var account = await provider.SignInAsync(via ?? browser, cancellationToken);
        await ConnectAsync(provider, AiAuthMethod.SignIn, account, cancellationToken);
    }

    public async Task ConnectApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken = default)
    {
        var provider = Provider(providerId) ?? throw new ArgumentException("Unknown AI provider.", nameof(providerId));
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiException(T("Bitte einen API-Schlüssel eingeben."));
        }

        await provider.ConnectApiKeyAsync(apiKey.Trim(), cancellationToken);
        await ConnectAsync(provider, AiAuthMethod.ApiKey, null, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Connection is { } connection && Provider(connection.ProviderId) is { } provider)
        {
            await provider.DisconnectAsync(connection.Method, cancellationToken);
        }

        await SaveAsync(null, cancellationToken);
    }

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var (provider, connection) = Current();
        return await provider.ListModelsAsync(connection.Method, cancellationToken);
    }

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        Connection is { } connection ? SaveAsync(connection with { Model = model }, cancellationToken) : Task.CompletedTask;

    /// <summary>A task with the connected provider and model, the answer as it is written.</summary>
    public async IAsyncEnumerable<string> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (provider, connection) = Current();
        var model = connection.Model;
        if (string.IsNullOrEmpty(model))
        {
            model = (await provider.ListModelsAsync(connection.Method, cancellationToken)).FirstOrDefault()?.Id
                    ?? throw new AiException(T("Der KI-Anbieter bietet keine Modelle an."));
            await SetModelAsync(model, cancellationToken);
        }

        await foreach (var piece in provider.StreamAsync(connection.Method, model, request, cancellationToken))
        {
            yield return piece;
        }
    }

    /// <summary>A short summary of a message, in the language of the user interface.</summary>
    public IAsyncEnumerable<string> SummarizeAsync(string? subject, string? from, string text, CancellationToken cancellationToken = default) =>
        StreamAsync(AiTasks.Summarize(subject, from, text), cancellationToken);

    /// <summary>The text of a new mail or a reply (see <see cref="AiTasks.Draft"/>).</summary>
    public IAsyncEnumerable<string> DraftAsync(string? instructions, string? subject, string? to, string? from, string ownText, string quote, CancellationToken cancellationToken = default) =>
        StreamAsync(AiTasks.Draft(instructions, subject, to, from, ownText, quote), cancellationToken);

    /// <summary>"Mit KI prüfen": the AI's verdict on a mail (see <see cref="AiTasks.CheckMail"/>).</summary>
    public IAsyncEnumerable<string> CheckMailAsync(string headers, string? subject, string text, IReadOnlyList<Privacy.HtmlLink> links, CancellationToken cancellationToken = default) =>
        StreamAsync(AiTasks.CheckMail(headers, subject, text, links), cancellationToken);

    /// <summary>The connected provider's name ("OpenAI" …), or null.</summary>
    public string? ProviderName => Connection is { } connection ? Provider(connection.ProviderId)?.Name : null;

    public IAsyncEnumerable<string> RewriteAsync(string text, AiRewrite how, string? instructions = null, CancellationToken cancellationToken = default) =>
        StreamAsync(AiTasks.Rewrite(text, how, instructions), cancellationToken);

    private (IAiProvider Provider, AiConnection Connection) Current() =>
        Connection is { } connection && Provider(connection.ProviderId) is { } provider
            ? (provider, connection)
            : throw new AiException(T("Kein KI-Anbieter verbunden – unter Einstellungen → KI verbinden."));

    private async Task ConnectAsync(IAiProvider provider, AiAuthMethod method, string? account, CancellationToken cancellationToken)
    {
        // Another connection (other provider or way) is let go first: its secret should not stay behind.
        if (Connection is { } previous && (previous.ProviderId != provider.Id || previous.Method != method) && Provider(previous.ProviderId) is { } old)
        {
            await old.DisconnectAsync(previous.Method, cancellationToken);
        }

        await SaveAsync(new AiConnection(provider.Id, method, account, null), cancellationToken);
    }

    private async Task SaveAsync(AiConnection? connection, CancellationToken cancellationToken)
    {
        Connection = connection;
        await settings.SetAsync(ConnectionKey, connection is null ? null : JsonSerializer.Serialize(connection, Json), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
