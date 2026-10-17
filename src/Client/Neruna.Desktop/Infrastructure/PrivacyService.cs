using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Privacy;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// The privacy settings as they apply right now (Einstellungen → Datenschutz), and what the phishing check compares
/// with: the own addresses and the contacts (kept for a few minutes, like the recipient suggestions).
/// </summary>
internal sealed class PrivacyService(ISettingsStore settings, IAccountStore accounts, RecipientDirectory recipients, HttpClient http, AppOptions app,
    ILogger<PrivacyService> logger)
{
    private static readonly TimeSpan PhishingInterval = TimeSpan.FromHours(12);
    private PrivacyOptions? _options;
    private PhishingListUpdater? _phishing;
    private Avalonia.Threading.DispatcherTimer? _phishingTimer;

    /// <summary>The known phishing hosts were loaded or updated (Einstellungen → Datenschutz shows their state).</summary>
    public event EventHandler? PhishingListChanged;

    public PhishingListUpdater PhishingUpdater => _phishing ??= new PhishingListUpdater(http, Path.Combine(app.DataDirectory, "phishing"));

    /// <summary>
    /// At start: the stored list at once, then a look at neruna.cloud (every 12 hours, checked every 3) – if the setting
    /// is on. The demo asks no server.
    /// </summary>
    public async Task StartPhishingListAsync()
    {
        if ((await GetAsync()).PhishingList)
        {
            await Task.Run(PhishingUpdater.LoadStored);
            PhishingListChanged?.Invoke(this, EventArgs.Empty);
        }

        if (app.Demo)
        {
            return;
        }

        _phishingTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(3) };
        _phishingTimer.Tick += async (_, _) => await UpdatePhishingListAsync(force: false);
        _phishingTimer.Start();

        // Not at once: in an office all computers start at eight – spread over a few minutes, the server stays calm.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(30, 600)));
        await UpdatePhishingListAsync(force: false);
    }

    /// <summary>Asks neruna.cloud for what changed (or, with <paramref name="force"/>, now); off: the list is dropped.</summary>
    /// <returns>An error text, or null.</returns>
    public async Task<string?> UpdatePhishingListAsync(bool force)
    {
        if (!(await GetAsync()).PhishingList)
        {
            PhishingList.Shared.Clear();
            PhishingListChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }

        try
        {
            if (force)
            {
                await PhishingUpdater.UpdateAsync();
            }
            else
            {
                await PhishingUpdater.UpdateIfDueAsync(PhishingInterval);
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or FormatException or UnauthorizedAccessException)
        {
            logger.LogInformation("Phishing list not updated: {Message}", ex.Message);
            return ex.Message;
        }
        finally
        {
            PhishingListChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The settings changed (the open mail is shown again with them).</summary>
    public event EventHandler? Changed;

    public async Task<PrivacyOptions> GetAsync() => _options ??= await PrivacyOptions.LoadAsync(settings);

    public async Task SetAsync(PrivacyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var phishingBefore = _options?.PhishingList;
        _options = options;
        await options.SaveAsync(settings);
        if (phishingBefore is { } before && before != options.PhishingList)
        {
            // Turned on: the stored copy at once, then the changes; turned off: dropped.
            if (options.PhishingList)
            {
                await Task.Run(PhishingUpdater.LoadStored);
            }

            _ = UpdatePhishingListAsync(force: false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Ausblenden" at the spam note of the check: kept quietly, the open mail stays as it is.</summary>
    public async Task HideSpamNoteAsync()
    {
        var options = await GetAsync() with { HideSpamNote = true };
        _options = options;
        await options.SaveAsync(settings);
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
