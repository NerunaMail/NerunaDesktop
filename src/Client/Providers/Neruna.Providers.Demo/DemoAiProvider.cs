using System.Runtime.CompilerServices;
using Neruna.Core.Ai;
using Neruna.Core.Auth;

namespace Neruna.Providers.Demo;

/// <summary>
/// AI for the demo (only with <c>--demo</c>): connects without an account and writes a fixed sample summary, piece by
/// piece like a real provider – to show and try the AI functions without a subscription or key.
/// </summary>
public sealed class DemoAiProvider : IAiProvider
{
    public string Id => "demo";

    public string Name => "Demo";

    public string? SignInName => "Demo";

    public bool SupportsApiKey => false;

    public Uri? ApiKeyHelp => null;

    public Task<string?> SignInAsync(IBrowserLauncher browser, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task ConnectApiKeyAsync(string apiKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DisconnectAsync(AiAuthMethod method, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AiModel>> ListModelsAsync(AiAuthMethod method, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AiModel>>([new AiModel("demo", "Demo")]);

    public async IAsyncEnumerable<string> StreamAsync(AiAuthMethod method, string model, AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const string sample = "Marco Bernasconi schickt die Offerte für die Erneuerung des Netzwerks im vierten Quartal. "
                              + "Sie umfasst neue Switches, WLAN in allen Etagen und die Umstellung an einem Wochenende.\n\n"
                              + "• Offerte prüfen und bis Freitag bestätigen\n• Termin für die Umstellung vorschlagen";
        foreach (var word in sample.Split(' '))
        {
            await Task.Delay(15, cancellationToken);
            yield return word + " ";
        }
    }
}
