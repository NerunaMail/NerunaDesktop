using Neruna.Core;
using Neruna.Core.Privacy;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// The privacy settings as they apply right now (Einstellungen → Datenschutz), and what the phishing check compares
/// with: the own addresses and the contacts (kept for a few minutes, like the recipient suggestions).
/// </summary>
internal sealed class PrivacyService(ISettingsStore settings, IAccountStore accounts, RecipientDirectory recipients)
{
    private PrivacyOptions? _options;

    /// <summary>The settings changed (the open mail is shown again with them).</summary>
    public event EventHandler? Changed;

    public async Task<PrivacyOptions> GetAsync() => _options ??= await PrivacyOptions.LoadAsync(settings);

    public async Task SetAsync(PrivacyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        await options.SaveAsync(settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Bilder immer laden" for a sender (address) or a whole domain ("@example.ch").</summary>
    public async Task TrustAsync(string addressOrDomain)
    {
        var options = await GetAsync();
        if (options.TrustedSenders.Contains(addressOrDomain, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        await SetAsync(options with { TrustedSenders = [.. options.TrustedSenders, addressOrDomain.ToLowerInvariant()] });
    }

    public async Task<AnalysisContext> ContextAsync()
    {
        try
        {
            return AnalysisContext.From(await accounts.GetAccountsAsync(), await recipients.GetAllAsync());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AnalysisContext.Empty;
        }
    }
}
